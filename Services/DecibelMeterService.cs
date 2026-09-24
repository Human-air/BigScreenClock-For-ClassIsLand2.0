using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.NoiseDetection;
using NAudio.Wave;

namespace EveningSelfStudyClock.Services;

/// <summary>持续事件已触发（后台音频线程引发，调用方需自行调度到 UI 线程）。</summary>
public sealed class NoiseEventRaisedEventArgs : EventArgs
{
    public NoiseLevel Level { get; }

    public NoiseEventRaisedEventArgs(NoiseLevel level)
    {
        Level = level;
    }
}

/// <summary>
/// 麦克风音量检测服务。使用 NAudio 采集音频，走 V2 检测管线：
/// 原始 RMS/峰值 → EMA 平滑 → 迟滞分级 → 持续判定 → 显示等级 + 持续事件。
/// 检测逻辑全部在音频采样线程执行，不依赖任何 UI Timer。
/// </summary>
public class DecibelMeterService : INotifyPropertyChanged, IDisposable
{
    private readonly PluginSettings _settings;
    private readonly NoiseEventDetector _detector = new();
    private WaveInEvent? _waveIn;
    private DateTime _lastDataReceived;
    private System.Timers.Timer? _watchdogTimer;
    /// <summary>「应当正在监听」的意图标记：StartMonitoring/StopMonitoring 维护，
    /// 不受设备自身 RecordingStopped 影响。系统睡眠唤醒后设备失效时，看门狗据此才能重启
    /// （此前看门狗只看 _isMonitoring，而设备失效会把它置 false，于是永远不再重启）。</summary>
    private bool _shouldMonitor;
    private double _currentRms;
    private int _displayLevel; // 0-100 正数显示
    private string _noiseLevelText = "等待检测...";
    private double _noiseLevelProgress;
    private bool _isMonitoring;
    private string _statusMessage = "";
    private NoiseLevel? _currentSegmentLevel;
    private NoiseLevel? _lastSegmentLevel;

    // 调试日志
    private StreamWriter? _logWriter;
    private string? _logPath;
    private int _logLines;
    /// <summary>本文件第一行/最后一行的时刻，用于「记录跨度不足 N 分钟就删」的判定。</summary>
    private DateTime? _logFirstSample;
    private DateTime? _logLastSample;
    /// <summary>单个日志文件的硬上限（KB），超了就滚动新文件，防止一次开一晚上写出个巨无霸。
    /// 「按大小」的保留线比它高时以保留线为准，否则滚出来的文件会被当成不达标当场删掉。</summary>
    private const int LogRollSizeKb = 2048;

    private int LogRollThresholdKb =>
        _settings.LogKeepByDuration ? LogRollSizeKb : Math.Max(LogRollSizeKb, _settings.LogKeepMinKb);

    public DecibelMeterService(PluginSettings settings)
    {
        _settings = settings;
    }

    /// <summary>持续事件（一般/吵闹）已触发。Level 取值 Normal 或 Noisy。</summary>
    public event EventHandler<NoiseEventRaisedEventArgs>? NoiseEventRaised;

    public double CurrentRms
    {
        get => _currentRms;
        private set { _currentRms = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 正数音量级别 0-100（方便阅读）
    /// </summary>
    public int DisplayLevel
    {
        get => _displayLevel;
        private set { _displayLevel = value; OnPropertyChanged(); }
    }

    public string NoiseLevelText
    {
        get => _noiseLevelText;
        private set { _noiseLevelText = value; OnPropertyChanged(); }
    }

    public double NoiseLevelProgress
    {
        get => _noiseLevelProgress;
        private set { _noiseLevelProgress = value; OnPropertyChanged(); }
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set { _isMonitoring = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 当前段起算后的实时归属（一般/吵闹），供「正在记录」提示显示；无段/未起算为 null。
    /// </summary>
    public NoiseLevel? CurrentSegmentLevel
    {
        get => _currentSegmentLevel;
        private set { _currentSegmentLevel = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 开始监听。固定使用默认麦克风（设备 0），失败则自动搜索可用设备。
    /// </summary>
    public void StartMonitoring()
    {
        if (_isMonitoring) return;
        _shouldMonitor = true;
        _detector.Reset();
        _lastSegmentLevel = null;
        CurrentSegmentLevel = null;
        EnsureLogWriter();
        StartMonitoringInternal();
        StartWatchdog();
    }

    private void StartMonitoringInternal()
    {
        try
        {
            int count = WaveInEvent.DeviceCount;
            if (count == 0)
            {
                NoiseLevelText = "未检测到麦克风";
                return;
            }

            // 固定用默认设备（0）；打开失败自动搜其他可用设备（如设备休眠失效后看门狗重启）。
            // 缓冲时长 = 采样间隔：块越大采样越稀，越省性能、越不拖日志与大屏时钟。
            var bufferMs = (int)Math.Clamp(_settings.SamplingIntervalSeconds * 1000, 100, 1000);
            foreach (var idx in Enumerable.Range(0, count))
            {
                try
                {
                    var wi = new WaveInEvent
                    {
                        DeviceNumber = idx,
                        WaveFormat = new WaveFormat(44100, 16, 1),
                        BufferMilliseconds = bufferMs
                    };
                    wi.DataAvailable += OnDataAvailable;
                    wi.RecordingStopped += OnRecordingStopped;
                    wi.StartRecording();
                    _waveIn = wi;
                    // 走属性而不是字段：界面靠 IsMonitoring 的通知才把「等待检测...」
                    // 换成实时档位文字（刚进大屏时钟一直显示等待检测就是漏了这个通知）
                    IsMonitoring = true;
                    _lastDataReceived = DateTime.Now;
                    return;
                }
                catch { }
            }

            NoiseLevelText = "无法打开任何麦克风";
        }
        catch (Exception ex)
        {
            IsMonitoring = false;
            NoiseLevelText = $"麦克风错误: {ex.Message}";
        }
    }

    /// <summary>
    /// 看门狗：只要还处于「应当监听」状态，一旦超过 5 秒没收到音频数据就整条链路重启。
    /// 睡眠唤醒后设备句柄失效是典型场景——此时必须重建 WaveInEvent，光对老实例
    /// StartRecording 是救不回来的；重启失败（设备还没醒）就下一个周期继续试，直到有数据为止。
    /// </summary>
    private void StartWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = new System.Timers.Timer(5000);
        _watchdogTimer.Elapsed += (_, _) =>
        {
            if (!_shouldMonitor) return;
            if (_isMonitoring && (DateTime.Now - _lastDataReceived).TotalSeconds <= 5) return;

            System.Diagnostics.Debug.WriteLine("[DecibelMeter] 看门狗检测到数据中断，重启音频");
            Dispatcher.UIThread.Post(() =>
            {
                StopMonitoringInternal();
                StartMonitoringInternal();
            });
        };
        _watchdogTimer.Start();
    }

    private void StopWatchdog()
    {
        _watchdogTimer?.Stop();
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    public void StopMonitoring()
    {
        _shouldMonitor = false;
        StopWatchdog();
        StopMonitoringInternal();
        CloseLogWriter();
    }

    private void StopMonitoringInternal()
    {
        // 先把引用摘掉再停：这样本实例异步回来的 RecordingStopped 会被当成「过期事件」忽略掉
        // （见 OnRecordingStopped 的判断），不会把紧接着新建的那次监听状态带坏。
        var wi = _waveIn;
        _waveIn = null;
        IsMonitoring = false;
        if (wi == null) return;

        try
        {
            wi.StopRecording();
            wi.DataAvailable -= OnDataAvailable;
            wi.RecordingStopped -= OnRecordingStopped;
            wi.Dispose();
        }
        catch { }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // 只认当前这个实例：重启时旧设备的 RecordingStopped 是异步回来的，
        // 晚到时会把新设备的监听状态误置成「未在监听」，看门狗又据此不再重启——
        // 这正是「睡眠唤醒后音量条短暂恢复一下再彻底哑掉」的原因。
        if (!ReferenceEquals(sender, _waveIn)) return;

        _isMonitoring = false;   // 字段直写：看门狗在别的线程上立刻要读它
        // 音频线程不能直接发通知（绑定会在非 UI 线程上刷新），Post 回 UI 线程再发
        Dispatcher.UIThread.Post(() => IsMonitoring = false);
        if (e.Exception != null)
        {
            Dispatcher.UIThread.Post(() => StatusMessage = $"录音异常: {e.Exception.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _lastDataReceived = DateTime.Now;
        if (e.BytesRecorded == 0) return;

        try
        {
            int samples = e.BytesRecorded / 2;
            double sum = 0;
            double peak = 0;

            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample16 = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                double abs = Math.Abs(sample16 / 32768.0);
                sum += abs * abs;
                if (abs > peak) peak = abs;
            }

            double rawRms = Math.Sqrt(sum / samples);

            // 从设置实时同步检测器配置
            _detector.QuietThreshold = _settings.DecibelQuietThreshold;
            _detector.GoodThreshold = _settings.DecibelGoodThreshold;
            _detector.NormalThreshold = _settings.DecibelNormalThreshold;
            _detector.NoisyThreshold = _settings.DecibelNoisyThreshold;
            _detector.SustainSeconds = _settings.NoisySustainSeconds;
            _detector.FallWindowSeconds = _settings.FallWindowSeconds;

            // 按真实音频块时长传 dt（samples / 采样率），不假定固定 100ms，
            // 这样「持续判定时长」在现实时间上是准的
            double dtSeconds = _waveIn?.WaveFormat is { SampleRate: > 0 } wf
                ? samples / (double)wf.SampleRate
                : 0.1;
            var result = _detector.Process(rawRms, dtSeconds);

            // 显示：进度按 NoiseLevelDisplay 从检测器分级算出（与记录判定同源）。
            // 采样频率已由缓冲时长控制（默认每秒 5 次），每次采样直接把目标进度发给界面，
            // 条在两个采样点之间的滑动由 VM 的动画补平滑（这里不再节流，避免条看着一顿一顿）。
            var progress = NoiseLevelDisplay.ProgressOf(result.Level, result.SmoothedRms,
                _settings.DecibelGoodThreshold, _settings.DecibelNormalThreshold, _settings.DecibelNoisyThreshold);
            Dispatcher.UIThread.Post(() => NoiseLevelProgress = progress);

            // 计数：段结束结算事件（事件等级已由探测器按段内占比判定）
            if (result.EventFired && result.EventLevel.HasValue)
            {
                NoiseEventRaised?.Invoke(this,
                    new NoiseEventRaisedEventArgs(result.EventLevel.Value));
            }

            // 「正在记录」实时状态：段归属变化（起算/切换/结束）时通知 UI
            if (result.SegmentLevel != _lastSegmentLevel)
            {
                _lastSegmentLevel = result.SegmentLevel;
                var level = result.SegmentLevel;
                Dispatcher.UIThread.Post(() => CurrentSegmentLevel = level);
            }

            WriteDebugLog(rawRms, peak, result);
        }
        catch { }
    }

    // ===== 调试日志（开发工具，设置项开关） =====

    private void EnsureLogWriter()
    {
        if (_logWriter != null) return;
        if (!_settings.EnableNoiseDebugLog) return;
        try
        {
            var dir = Plugin.ConfigFolder;
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);

            // 清理过期日志：删除超过保留天数（默认 3 天）的旧 NoiseDebugLog-*.csv，防止开了忘关堆积
            try
            {
                var cutoff = DateTime.Now.AddDays(-_settings.LogRetentionDays);
                foreach (var old in Directory.EnumerateFiles(dir, "NoiseDebugLog-*.csv"))
                {
                    try { if (File.GetLastWriteTime(old) < cutoff) File.Delete(old); } catch { }
                }
            }
            catch { }

            // 每次开始监测 = 一个新文件，命名规则与 ClassIsland 自身日志一致：
            // 开始记录的 年-月-日-时-分-秒（同秒冲突时追加 -1/-2…）。
            var timestamp = DateTime.Now.ToString("y-M-d-HH-mm-ss");
            var path = Path.Combine(dir, $"NoiseDebugLog-{timestamp}.csv");
            for (int i = 1; File.Exists(path); i++)
                path = Path.Combine(dir, $"NoiseDebugLog-{timestamp}-{i}.csv");

            _logPath = path;
            _logWriter = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
            _logLines = 0;
            _logFirstSample = null;
            _logLastSample = null;
            WriteLogHeader();
        }
        catch { }
    }

    /// <summary>
    /// 文件开头记下这一轮用的各项参数。日志是隔几天才回看的，
    /// 没有这段就得回头猜当时阈值填的是多少，数据没法比对。
    /// </summary>
    private void WriteLogHeader()
    {
        if (_logWriter == null) return;
        var s = _settings;
        _logWriter.WriteLine($"# 记录开始,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _logWriter.WriteLine($"# 阈值 RMS,安静 {s.DecibelQuietThreshold:F4},良好 {s.DecibelGoodThreshold:F4}," +
                             $"一般 {s.DecibelNormalThreshold:F4},吵闹 {s.DecibelNoisyThreshold:F4}");
        _logWriter.WriteLine($"# 起算底线 {s.NoisySustainSeconds:F1}s,回落窗口 {s.FallWindowSeconds:F1}s," +
                             $"采样间隔 {s.SamplingIntervalSeconds:F2}s,保护时长 {s.SkipFirstMinutes}分钟");
        _logWriter.WriteLine("Time,RawRMS,Peak,SmoothRMS,Level,EpisodeLevel,EventState,Fired");
    }

    /// <summary>关闭当前日志文件。judge = true 时按保留规则决定去留（见 TryDeleteUnqualifiedLog）。</summary>
    private void CloseLogWriter(bool judge = true)
    {
        try { _logWriter?.Flush(); _logWriter?.Dispose(); } catch { }
        _logWriter = null;
        if (judge && _logPath != null) TryDeleteUnqualifiedLog(_logPath);
        _logPath = null;
        _logFirstSample = null;
        _logLastSample = null;
    }

    /// <summary>
    /// 保留规则：按设置里的「记录时长」或「文件大小」判断这份记录够不够格。
    /// 不够格（开着开关进出大屏时钟顺手录了几秒）就删掉，省得日志目录堆满废数据。
    /// 一行数据都没采到的文件不删——那往往是麦克风本身出了问题，留着有线索价值。
    /// </summary>
    private void TryDeleteUnqualifiedLog(string path)
    {
        if (_logFirstSample == null) return;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            bool tooShort;
            if (_settings.LogKeepByDuration)
            {
                var span = (_logLastSample ?? _logFirstSample.Value) - _logFirstSample.Value;
                tooShort = span.TotalMinutes < _settings.LogKeepMinutes;
            }
            else
            {
                tooShort = info.Length < _settings.LogKeepMinKb * 1024L;
            }

            if (tooShort) info.Delete();
        }
        catch { }
    }

    private void WriteDebugLog(double rawRms, double peak, SampleResult result)
    {
        // 开关中途拨动也即时生效：刚打开就补建文件，刚关闭就把这份结算掉（不必重进大屏时钟）
        if (_logWriter == null) EnsureLogWriter();
        if (_logWriter == null) return;
        if (!_settings.EnableNoiseDebugLog) { CloseLogWriter(); return; }

        string eventState = !_detector.IsSegmentActive ? "无段"
            : _detector.SegmentLevel.HasValue ? "已起算" : "记录中";
        string episodeLevel = _detector.SegmentLevel?.ToString() ?? "";

        var now = DateTime.Now;
        _logFirstSample ??= now;
        _logLastSample = now;

        _logWriter.WriteLine(
            $"{now:HH:mm:ss.fff},{rawRms:F4},{peak:F4},{result.SmoothedRms:F4}," +
            $"{result.Level},{episodeLevel},{eventState},{result.EventFired}");
        if (++_logLines >= 20)
        {
            _logWriter.Flush();
            _logLines = 0;
            // 到这个硬上限就换新文件（换掉的这一份同样过一遍保留规则）
            if (_logPath != null)
            {
                try
                {
                    if (new FileInfo(_logPath).Length > LogRollThresholdKb * 1024L)
                    {
                        CloseLogWriter();
                        EnsureLogWriter();
                    }
                }
                catch { }
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}
