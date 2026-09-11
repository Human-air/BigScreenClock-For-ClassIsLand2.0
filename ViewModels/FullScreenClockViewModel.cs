using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Icons;
using ClassIsland.Core.Models.Components;
using ClassIsland.Shared.Enums;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.NoiseDetection;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.Views;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.ViewModels;

public class FullScreenClockViewModel : INotifyPropertyChanged
{
    private readonly PluginSettings _settings;
    private readonly DecibelMeterService _decibelService;
    private readonly IServiceProvider _serviceProvider;
    private readonly NoiseCounter _counter = new();
    private readonly Lazy<IExactTimeService?> _exactTimeService;
    private System.Timers.Timer? _updateTimer;
    private FullScreenClockWindow? _window;

    /// <summary>
    /// CI 里设置的时间偏移（秒）。正 = 打铃提前，负 = 打铃延后。
    /// </summary>
    private int _timeOffsetSeconds;

    // 计数显示颜色：一般 = 黄，吵闹 = 红
    private static readonly IBrush CountNormalBrush = new SolidColorBrush(Color.Parse("#ffff44"));
    private static readonly IBrush CountNoisyBrush = new SolidColorBrush(Color.Parse("#ff5555"));

    // 计数区「暂未记录到吵闹」前的对勾图标字形（矢量）
    private static readonly string CheckIcon = IconGlyph.Of(LucideIconKind.CircleCheck);

    // 音量条档位：档位名来自 NoiseLevelDisplay（与检测器分级同源），
    // 五档高亮、右侧状态、填充色全部以 AnimatedProgress 反推出的 CurrentSlot 为唯一源，避免不同步。
    private static readonly string[] SlotNames = NoiseLevelDisplay.SlotNames;

    /// <summary>五档画刷：静态缓存，避免每次刷新都新建 SolidColorBrush（会让控件反复失效重绘、观感发顿）。</summary>
    private static readonly IBrush[] SlotBrushes = BuildSlotBrushes();

    private static IBrush[] BuildSlotBrushes()
    {
        var brushes = new IBrush[LevelSlotCalculator.SlotColors.Length];
        for (var i = 0; i < brushes.Length; i++)
        {
            var c = LevelSlotCalculator.SlotColors[i];
            brushes[i] = new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
        }
        return brushes;
    }

    private readonly ObservableCollection<NoiseLevelSlotItem> _noiseLevelSlots = new();

    private string _currentTime = "00:00:00";
    private string _currentDate = "";
    private string _currentCourseName = "";
    private string _currentClassName = "";
    private readonly List<ClassSummary> _classSummaries = new();
    private bool _isWindowVisible;
    private bool _isInBreak;
    private DateTime _classStartTime;
    private string _courseInfoText = "";
    private List<TimeSlot> _todaySlots = new();
    private System.Timers.Timer? _antiScreensaverTimer;
    private string _noisyDisplaySignature = "";
    private bool _showCountRules;

    // ===== 提醒面板数据（跟随 CI 组件配置 + CI 天气缓存） =====
    private readonly ReminderData _reminderData = new();
    private int _reminderTick;
    private DateTime _lastWeatherRead = DateTime.MinValue;

    public FullScreenClockViewModel(
        PluginSettings settings,
        DecibelMeterService decibelService,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _decibelService = decibelService;
        _serviceProvider = serviceProvider;

        // 惰性缓存 IExactTimeService，避免每次调用查服务
        _exactTimeService = new Lazy<IExactTimeService?>(() =>
        {
            try { return _serviceProvider.GetService<IExactTimeService>(); }
            catch { return null; }
        });

        _decibelService.PropertyChanged += OnDecibelPropertyChanged;
        _decibelService.NoiseEventRaised += OnNoiseEventRaised;
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        for (var i = 0; i < SlotNames.Length; i++)
            _noiseLevelSlots.Add(new NoiseLevelSlotItem(SlotNames[i], SlotBrushes[i]));
        UpdateNoiseLevelSlots();
    }

    /// <summary>
    /// CI 的"虚拟本地时间"（含时间偏移），用于课程定位/保护计时。
    /// 大时钟显示仍用真实北京时间，学生看到的是准确时间。
    /// </summary>
    private DateTime NowVirtual
    {
        get
        {
            try { return _exactTimeService.Value?.GetCurrentLocalDateTime() ?? DateTime.Now; }
            catch { return DateTime.Now; }
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PluginSettings.SkipFirst3Min)
            or nameof(PluginSettings.SkipFirstMinutes)
            or nameof(PluginSettings.NoisySustainSeconds)
            or nameof(PluginSettings.FallWindowSeconds))
        {
            OnPropertyChanged(nameof(CountRulesText));
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowReminderPanel)
            or nameof(PluginSettings.ShowWeatherReminder)
            or nameof(PluginSettings.ShowAlertsReminder)
            or nameof(PluginSettings.ShowCountdownReminder)
            or nameof(PluginSettings.ShowTextReminder)
            or nameof(PluginSettings.ShowRainReminder))
        {
            NotifyReminderChanged();
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowEmojiSubtitles))
        {
            // 颜文字开关：全部副标题即时重算（清空/恢复）
            OnPropertyChanged(nameof(RainSubtitle));
            OnPropertyChanged(nameof(HasRainSubtitle));
            UpdateWeatherSubtitle();
            UpdateDateSubtitle();
            UpdateCountdownSubtitle();
        }
    }

    /// <summary>
    /// 分贝属性变更：仅转发显示属性给全屏界面绑定。计数由持续事件驱动（NoiseEventRaised）。
    /// </summary>
    private void OnDecibelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(DecibelMeterService.NoiseLevelProgress):
                // 采样得到新的目标进度 → 交给动画平滑逼近（文字/高亮由动画帧在跨档位时刷新）
                _targetProgress = _decibelService.NoiseLevelProgress;
                StartProgressAnimation();
                break;
            case nameof(DecibelMeterService.IsMonitoring):
            case nameof(DecibelMeterService.NoiseLevelText):
                // 监测开关 / 错误提示文字变化 → 直接刷新右侧文字（如「未检测到麦克风」「等待检测…」）
                OnPropertyChanged(nameof(NoiseLevelText));
                OnPropertyChanged(nameof(NoiseLevelFillBrush));
                break;
            case nameof(DecibelMeterService.CurrentSegmentLevel):
                UpdateRecording();
                break;
        }
    }


    /// <summary>
    /// 持续事件（一般/吵闹）→ 计数。事件在音频线程引发，这里调度到 UI 线程。
    /// 事件等级已由探测器按段内一般/吵闹占比判定，计数层只应用 冷却/保护 规则。
    /// </summary>
    private void OnNoiseEventRaised(object? sender, NoiseEventRaisedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var decision = _counter.OnEvent(e.Level, IsInProtection());
            if (decision != CountDecision.None)
                UpdateNoisyDisplay();
        });
    }

    private bool IsInProtection()
        => _settings.SkipFirst3Min
           && (NowVirtual - _classStartTime).TotalSeconds < _settings.SkipFirstMinutes * 60;

    public string CurrentTime
    {
        get => _currentTime;
        set { _currentTime = value; OnPropertyChanged(); }
    }

    public string CurrentDate
    {
        get => _currentDate;
        set { _currentDate = value; OnPropertyChanged(); }
    }

    public string CurrentCourseName
    {
        get => _currentCourseName;
        set { _currentCourseName = value; OnPropertyChanged(); }
    }

    public bool IsWindowVisible
    {
        get => _isWindowVisible;
        private set { _isWindowVisible = value; OnPropertyChanged(); }
    }

    public string BackgroundColor => _settings.BackgroundColor;
    public string FontColor => _settings.FontColor;
    public string AccentColor => _settings.AccentColor;
    public string ProgressColor => _settings.ProgressColor;
    public string CourseInfoColor => _settings.CourseInfoColor;
    public string NoiseTitleColor => _settings.NoiseTitleColor;

    /// <summary>进度条「已进行」部分颜色（深色，不透明）。</summary>
    public IBrush ProgressFillBrush => new SolidColorBrush(Color.Parse(_settings.ProgressColor));

    /// <summary>进度带「未进行」部分颜色（深蓝低饱和，固定，与音量条轨道同色系）。</summary>
    public IBrush ProgressTrackBrush => TrackBrush;

    /// <summary>
    /// 触发所有外观属性变更通知，让窗口绑定重新求值。
    /// 窗口从 Hide 退出后复用时，这些表达式体 getter 不会自动刷新，需主动通知。
    /// </summary>
    private void RefreshAppearanceBindings()
    {
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(FontColor));
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(ProgressColor));
        OnPropertyChanged(nameof(ProgressFillBrush));
        OnPropertyChanged(nameof(ProgressTrackBrush));
        OnPropertyChanged(nameof(CourseInfoColor));
        OnPropertyChanged(nameof(NoiseTitleColor));
        OnPropertyChanged(nameof(ClockFontSize));
        OnPropertyChanged(nameof(WindowTitle));
    }
    /// <summary>音量条当前显示的进度（0-100）：由动画向采样得到的目标进度平滑逼近，绑到进度条 Value。</summary>
    public double AnimatedProgress
    {
        get => _animatedProgress;
        private set { _animatedProgress = value; OnPropertyChanged(); }
    }
    private double _animatedProgress;
    private double _targetProgress;
    private DispatcherTimer? _progressAnimTimer;

    /// <summary>统一档位索引（0-4）：轨道位置、五档高亮、右侧状态文字全部由 AnimatedProgress 反推，条和字永远同源。</summary>
    private int CurrentSlot => LevelSlotCalculator.SlotFromProgress(AnimatedProgress);

    /// <summary>右侧状态文字：正常监测时按当前档位查表（与条同源）；无麦克风/出错时显示 Service 的提示文字。</summary>
    public string NoiseLevelText => _decibelService.IsMonitoring ? SlotNames[CurrentSlot] : _decibelService.NoiseLevelText;

    /// <summary>音量条五档文字集合（安静/良好/一般/吵闹/嘈杂）。</summary>
    public ObservableCollection<NoiseLevelSlotItem> NoiseLevelSlots => _noiseLevelSlots;

    /// <summary>音量条轨道填充色：随当前档位可变（绿→黄→红），取缓存的画刷实例。</summary>
    public IBrush NoiseLevelFillBrush => SlotBrushes[CurrentSlot];

    /// <summary>音量条轨道底色 / 课程进度带 track 底色（深蓝低饱和，共用同一实例）。</summary>
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#B31C3047"));

    public IBrush NoiseLevelTrackBrush => TrackBrush;

    /// <summary>档位变化时：刷新当前档位文字高亮 + 轨道填充色 + 右侧状态文字（全部同一档位源）。</summary>
    private void UpdateNoiseLevelSlots()
    {
        var slot = CurrentSlot;
        for (var i = 0; i < _noiseLevelSlots.Count; i++)
            _noiseLevelSlots[i].IsCurrent = i == slot;
        OnPropertyChanged(nameof(NoiseLevelText));
        OnPropertyChanged(nameof(NoiseLevelFillBrush));
    }

    /// <summary>收到新的采样目标进度：启动（或保持）动画，让条平滑逼近目标。</summary>
    private void StartProgressAnimation()
    {
        if (_progressAnimTimer == null)
        {
            _progressAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _progressAnimTimer.Tick += OnProgressAnimTick;
        }
        if (!_progressAnimTimer.IsEnabled)
            _progressAnimTimer.Start();
    }

    /// <summary>
    /// 动画帧：把 AnimatedProgress 向目标指数逼近（无过冲），条在两个采样点之间平滑滑动；
    /// 只在跨过档位边界时刷新文字/高亮/填充色，其余帧只动进度条。
    /// </summary>
    private void OnProgressAnimTick(object? sender, EventArgs e)
    {
        var prevSlot = CurrentSlot;
        var delta = _targetProgress - AnimatedProgress;
        if (Math.Abs(delta) < 0.2)
        {
            AnimatedProgress = _targetProgress;
            _progressAnimTimer!.Stop();
        }
        else
        {
            AnimatedProgress += delta * 0.5;   // 指数平滑，视觉上匀速滑动
        }
        if (CurrentSlot != prevSlot)
            UpdateNoiseLevelSlots();
    }

    /// <summary>
    /// 计数区域的分段显示：课程名/一般（黄）/吵闹（红）/保护提示。
    /// </summary>
    public ObservableCollection<NoisyDisplaySegment> NoisyDisplaySegments { get; } = new();

    /// <summary>
    /// 底部记录规则是否显示（鼠标悬停计数区域时）。
    /// </summary>
    public bool ShowCountRules
    {
        get => _showCountRules;
        set { _showCountRules = value; OnPropertyChanged(); }
    }

    // ===== 「正在记录」提示（段起算后实时显示，一般黄 / 吵闹红） =====

    private bool _isRecordingVisible;
    private string _recordingText = "";
    private IBrush _recordingForeground = CountNormalBrush;

    /// <summary>是否显示「正在记录」提示（有段且已起算、上课中）。</summary>
    public bool IsRecordingVisible
    {
        get => _isRecordingVisible;
        set { _isRecordingVisible = value; OnPropertyChanged(); }
    }

    /// <summary>「正在记录：一般」/「正在记录：吵闹」。</summary>
    public string RecordingText
    {
        get => _recordingText;
        set { _recordingText = value; OnPropertyChanged(); }
    }

    /// <summary>提示颜色：一般=黄，吵闹=红。</summary>
    public IBrush RecordingForeground
    {
        get => _recordingForeground;
        set { _recordingForeground = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 根据检测器的实时段归属刷新「正在记录」提示。
    /// 仅在计数可见且上课时段显示；段起算前/段结束后隐藏。
    /// </summary>
    private void UpdateRecording()
    {
        var seg = _decibelService.CurrentSegmentLevel;
        bool show = seg.HasValue && _settings.ShowNoisyCounter
            && !_isInBreak && !string.IsNullOrEmpty(_currentClassName);
        IsRecordingVisible = show;
        if (!show) return;
        var noisy = seg == NoiseLevel.Noisy;
        RecordingText = noisy ? "正在记录：吵闹" : "正在记录：一般";
        RecordingForeground = noisy ? CountNoisyBrush : CountNormalBrush;
    }

    private bool _showDecibelMeter = true;
    private bool _showCourseInfo = true;
    private int _clockFontSize = 180;

    public bool ShowDecibelMeter
    {
        get => _showDecibelMeter;
        set { _showDecibelMeter = value; OnPropertyChanged(); }
    }

    public bool ShowCourseInfo
    {
        get => _showCourseInfo;
        set { _showCourseInfo = value; OnPropertyChanged(); }
    }

    public string CourseInfoText
    {
        get => _courseInfoText;
        set { _courseInfoText = value; OnPropertyChanged(); }
    }

    private double _courseProgress;

    /// <summary>当前时段进行进度（0~100）。上课=本课进度，课间=当前休息进度。</summary>
    public double CourseProgress
    {
        get => _courseProgress;
        set { _courseProgress = value; OnPropertyChanged(); }
    }

    private bool _showCourseProgress;

    /// <summary>是否显示进度条（当前存在时段槽时显示，放学/无课隐藏）。</summary>
    public bool ShowCourseProgress
    {
        get => _showCourseProgress;
        set { _showCourseProgress = value; OnPropertyChanged(); }
    }

    public int ClockFontSize
    {
        get => _clockFontSize;
        set { _clockFontSize = value; OnPropertyChanged(); }
    }

    public string WindowTitle => _settings.WindowTitle;

    // ===== 提醒面板（左上角）：天气 / 降雨 / 预警 / 倒计时 / 文本框，跟随 CI 配置 =====

    /// <summary>天气图标字形（无数据时用温度计兜底）。</summary>
    public string WeatherIcon => _reminderData.WeatherIcon ?? IconGlyph.Of(LucideIconKind.Thermometer);

    private string _rainReminderTitle = "";

    /// <summary>未来降雨提醒主标题（如「20小时内当前地区有降雨」）；无雨为空串。副标题「记得带伞哦」界面固定。</summary>
    public string RainReminderTitle
    {
        get => _rainReminderTitle;
        set
        {
            if (_rainReminderTitle == value) return;
            _rainReminderTitle = value;
            OnPropertyChanged(nameof(RainReminderTitle));
            OnPropertyChanged(nameof(ShowRainReminder));
        }
    }

    /// <summary>降雨提醒可见性：跟随独立「多久下雨」开关，且有降雨数据。</summary>
    public bool ShowRainReminder => _settings.ShowRainReminder && !string.IsNullOrEmpty(_rainReminderTitle);
    public string WeatherText => _reminderData.WeatherText ?? "";
    public string ReminderText => _reminderData.TextContent ?? "";

    /// <summary>倒计时整行文本（如「高考   还有 30 天」）。</summary>
    public string CountdownText
    {
        get
        {
            var title = _reminderData.CountdownTitle;
            var remaining = _reminderData.CountdownRemaining;
            if (string.IsNullOrEmpty(title)) return remaining ?? "";
            return string.IsNullOrEmpty(remaining) ? title : $"{title}   {remaining}";
        }
    }

    /// <summary>预警列表（按等级着色）。</summary>
    public ObservableCollection<AlertDisplayItem> AlertItems { get; } = new();

    /// <summary>当前展开预警的详情全文（顶部弹幕带显示；点「>」展开时设置，收起或刷新时清空）。</summary>
    private string? _expandedAlertDetail;
    public string? ExpandedAlertDetail
    {
        get => _expandedAlertDetail;
        set
        {
            if (_expandedAlertDetail == value) return;
            _expandedAlertDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasExpandedAlert));
        }
    }
    public bool HasExpandedAlert => !string.IsNullOrEmpty(_expandedAlertDetail);

    // 组合可见性：子开关 && 有数据
    public bool ShowWeatherRow => _settings.ShowWeatherReminder && _reminderData.HasWeather;
    public bool ShowAlertsRow => _settings.ShowAlertsReminder && _reminderData.HasAlerts;
    public bool ShowCountdownRow => _settings.ShowCountdownReminder && _reminderData.HasCountdown;
    public bool ShowTextRow => _settings.ShowTextReminder && _reminderData.HasText;

    // 提醒面板项：未并入第一行的 降雨/倒计时/文本 才在面板显示（天气永不在面板，预警始终在面板）
    public bool ShowRainReminderPanel => ShowRainReminder && !ShowRainMergedInFirstRow;
    public bool ShowCountdownPanel => ShowCountdownRow && !ShowCountdownMergedInFirstRow;
    public bool ShowTextPanel => ShowTextRow && !ShowTextMergedInFirstRow;
    public bool ShowReminderPanel => _settings.ShowReminderPanel
        && (ShowAlertsRow || ShowRainReminderPanel || ShowCountdownPanel || ShowTextPanel);

    // ===== 提醒自动合并为同行（顶部第一行 = 主位块 + 预警详情弹幕 + 日期） =====
    // 用户定稿规则：
    // - 天气固定第一行，与弹幕同行；
    // - 与天气合并的优先级：降雨提醒 > 倒计时 > 文本，能放进屏幕 1/3 宽才合并，放不下该项留在提醒面板；
    // - 无天气时，第一行主位由倒计时顶替，再无就文本框；
    // - 天气预警始终单开一行/多行（面板）；
    // - 颜文字（副标题）显示在主提醒的正下方。

    private bool _showWeatherInFirstRow;
    private bool _showRainMergedInFirstRow;
    private bool _showCountdownMergedInFirstRow;
    private bool _showTextMergedInFirstRow;

    /// <summary>天气是否显示在第一行（有天气数据时恒为第一行主位）。</summary>
    public bool ShowWeatherInFirstRow
    {
        get => _showWeatherInFirstRow;
        private set
        {
            if (_showWeatherInFirstRow == value) return;
            _showWeatherInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>降雨提醒是否并入第一行。</summary>
    public bool ShowRainMergedInFirstRow
    {
        get => _showRainMergedInFirstRow;
        private set
        {
            if (_showRainMergedInFirstRow == value) return;
            _showRainMergedInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>倒计时是否并入第一行（无天气时作为第一行主位）。</summary>
    public bool ShowCountdownMergedInFirstRow
    {
        get => _showCountdownMergedInFirstRow;
        private set
        {
            if (_showCountdownMergedInFirstRow == value) return;
            _showCountdownMergedInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>文本是否并入第一行（无天气且无倒计时时作为第一行主位）。</summary>
    public bool ShowTextMergedInFirstRow
    {
        get => _showTextMergedInFirstRow;
        private set
        {
            if (_showTextMergedInFirstRow == value) return;
            _showTextMergedInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>合并判定阈值：主位块 + 并入项总宽不超过「屏幕宽 × 1/3」才并入（窗口 SizeChanged 设置）。</summary>
    public double MergeThreshold
    {
        get => _mergeThreshold;
        set
        {
            if (Math.Abs(_mergeThreshold - value) < 0.5) return;
            _mergeThreshold = value;
            OnPropertyChanged();
            UpdateReminderMerge();   // 阈值变化会改变是否并入
        }
    }
    private double _mergeThreshold = 640;

    /// <summary>按实际渲染宽度重算第一行合并：天气固定，降雨>倒计时>文本 逐项尝试并入（≤1/3 屏）。</summary>
    private void UpdateReminderMerge()
    {
        bool weather = ShowWeatherRow;
        bool rain = ShowRainReminder;
        bool countdown = ShowCountdownRow;
        bool text = ShowTextRow;

        bool rainMerged = false, countdownMerged = false, textMerged = false;
        const double spacing = 24;
        var threshold = MergeThreshold;

        if (weather)
        {
            var used = IconWidth(20) + 6 + TextWidth(WeatherText, 19);
            // 合并优先级：降雨提醒 > 倒计时 > 文本
            TryMerge(used, rain, rain ? IconWidth(20) + 6 + TextWidth(RainReminderTitle, 19) : 0,
                spacing, threshold, ref used, ref rainMerged);
            TryMerge(used, countdown, countdown ? IconWidth(20) + 6 + TextWidth(CountdownText, 19) : 0,
                spacing, threshold, ref used, ref countdownMerged);
            TryMerge(used, text, text ? TextWidth(ReminderText, 16) : 0,
                spacing, threshold, ref used, ref textMerged);
        }
        else if (countdown)
        {
            countdownMerged = true;   // 无天气：第一行主位由倒计时顶替
        }
        else if (text)
        {
            textMerged = true;        // 再无就文本框
        }

        ShowWeatherInFirstRow = weather;
        ShowRainMergedInFirstRow = rainMerged;
        ShowCountdownMergedInFirstRow = countdownMerged;
        ShowTextMergedInFirstRow = textMerged;

        // 面板项（未并入的）与总开关重新求值
        OnPropertyChanged(nameof(ShowRainReminderPanel));
        OnPropertyChanged(nameof(ShowCountdownPanel));
        OnPropertyChanged(nameof(ShowTextPanel));
        OnPropertyChanged(nameof(ShowReminderPanel));
    }

    /// <summary>尝试把某项并入主位块：当前已用宽度 + 间距 + 该项宽 ≤ 阈值 才并入。</summary>
    private static void TryMerge(double used, bool candidate, double candidateWidth,
        double spacing, double threshold, ref double usedOut, ref bool merged)
    {
        if (!candidate || candidateWidth <= 0) return;
        if (used + spacing + candidateWidth <= threshold)
        {
            usedOut = used + spacing + candidateWidth;
            merged = true;
        }
    }

    /// <summary>提醒面板最大宽度：由窗口按「屏幕宽 × 2/3」设置，只约束提醒面板（降雨/文本/预警）。</summary>
    public double ReminderPanelMaxWidth
    {
        get => _reminderPanelMaxWidth;
        set
        {
            if (Math.Abs(_reminderPanelMaxWidth - value) < 0.5) return;
            _reminderPanelMaxWidth = value;
            OnPropertyChanged();
        }
    }
    private double _reminderPanelMaxWidth = 520;


    // ===== 颜文字副标题（趣味提醒）：气温/天气旁、日期旁、倒计时旁、降雨旁的小字号副标题 =====
    // 由「颜文字提醒」开关统一控制；关闭时全部清空、保留正文字幕。固定文本不轮换。

    /// <summary>距最近降雨的小时数（0=正在下；null=未来无雨或未读到）。带伞副标题只在 6 小时内显示。</summary>
    private int? _rainHours;

    /// <summary>降雨提醒副标题：距离下雨 ≤6 小时才显示「记得带伞哦」（超过 6 小时只留主标题）；
    /// 开关开启带颜文字，否则纯文字。</summary>
    public string? RainSubtitle
    {
        get
        {
            if (_rainHours is not (>= 0 and <= 6)) return null;
            return _settings.ShowEmojiSubtitles ? "记得带伞哦 (ノω≦)" : "记得带伞哦";
        }
    }
    public bool HasRainSubtitle => RainSubtitle is not null;

    private string? _weatherCode;
    private double? _weatherTempC;

    /// <summary>雪/雨夹雪天气码（下雪优先显示保暖副标题，压过温度提示）。</summary>
    private static readonly HashSet<string> SnowCodes = new() { "06", "13", "14", "15", "16", "17", "26", "27", "28" };
    /// <summary>雾/霾天气码。</summary>
    private static readonly HashSet<string> FogCodes = new() { "18", "22", "32" };

    private string? _weatherSubtitle;
    public string? WeatherSubtitle
    {
        get => _weatherSubtitle;
        private set
        {
            if (_weatherSubtitle == value) return;
            _weatherSubtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWeatherSubtitle));
        }
    }
    public bool HasWeatherSubtitle => !string.IsNullOrEmpty(_weatherSubtitle);

    private string? _dateSubtitle;
    public string? DateSubtitle
    {
        get => _dateSubtitle;
        private set
        {
            if (_dateSubtitle == value) return;
            _dateSubtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDateSubtitle));
        }
    }
    public bool HasDateSubtitle => !string.IsNullOrEmpty(_dateSubtitle);

    private string? _countdownSubtitle;
    public string? CountdownSubtitle
    {
        get => _countdownSubtitle;
        private set
        {
            if (_countdownSubtitle == value) return;
            _countdownSubtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCountdownSubtitle));
        }
    }
    public bool HasCountdownSubtitle => !string.IsNullOrEmpty(_countdownSubtitle);

    /// <summary>气温旁副标题：雪 > 雾霾 > 高温 > 低温，取最先命中的一项。</summary>
    private void UpdateWeatherSubtitle()
    {
        string? s = null;
        if (_settings.ShowEmojiSubtitles)
        {
            if (_weatherCode is { } code)
            {
                if (SnowCodes.Contains(code)) s = "注意保暖 (❁´ω`❁)";
                else if (FogCodes.Contains(code)) s = "出门戴口罩 (´-ω-`)";
            }
            if (s is null && _weatherTempC is { } t)
            {
                if (t >= 36) s = "天太热，多喝水 (￣△￣;)";
                else if (t >= 33) s = "记得防晒 (っ'ω')ﾉ";
                else if (t <= 0) s = "冻手冻脚，注意保暖 (｡-_-｡)";
                else if (t <= 8) s = "天冷多穿点 (｡>ㅅ<｡)";
            }
        }
        WeatherSubtitle = s;
    }

    /// <summary>日期下方副标题：深夜 > 周五 > 月初（每秒刷新，到点自动切换）。</summary>
    private void UpdateDateSubtitle()
    {
        string? s = null;
        if (_settings.ShowEmojiSubtitles)
        {
            var now = DateTime.Now;
            if (now.DayOfWeek == DayOfWeek.Thursday) s = "撑住！明天就能回家了！ (๑•̀ㅂ•́)و";
            else if (now.Day == 1) s = "新的一个月，加油 (ง •̀_•́)ง";
        }
        DateSubtitle = s;
    }

    /// <summary>倒计时旁副标题：剩余 ≤30 天进入冲刺提示。</summary>
    private void UpdateCountdownSubtitle()
    {
        string? s = null;
        if (_settings.ShowEmojiSubtitles && _reminderData.CountdownRemaining is { } remaining)
        {
            var digits = new string(remaining.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var days) && days is > 0 and <= 30)
                s = "冲刺啦，冲鸭 (๑•̀ㅂ•́)و✧";
        }
        CountdownSubtitle = s;
    }

    /// <summary>从 CI 温度字符串（可能带 ℃/负号）解析数值，失败返回 null。</summary>
    private static double? ParseTemp(string? t)
    {
        if (string.IsNullOrEmpty(t)) return null;
        var s = new string(t.Where(c => char.IsDigit(c) || c == '-').ToArray());
        return double.TryParse(s, out var v) ? v : null;
    }

    /// <summary>用 TextLayout 独立测量文本实际渲染宽度（不依赖可视树布局，中英文混合也准确）。</summary>
    private static double TextWidth(string? s, double fontSize)
        => string.IsNullOrEmpty(s)
            ? 0
            : new TextLayout(s, new Typeface("Microsoft YaHei"), fontSize,
                null, TextAlignment.Left, TextWrapping.NoWrap).Width;

    /// <summary>
    /// 矢量图标宽度：Lucide 图标为正方形，字宽≈字号（不能用 TextWidth 量——图标字形落在
    /// 私有区，用中文字体量会得到错误的宽度，导致第一行合并判定失准）。
    /// </summary>
    private static double IconWidth(double fontSize) => fontSize;

    /// <summary>进入全屏时立即同步一次提醒数据（不等定时器）。</summary>
    private void RefreshRemindersNow()
    {
        RefreshComponents();
        RefreshWeather();
    }

    /// <summary>从 CI 组件配置读取倒计时/文本框（每 3 秒）；用户增删组件自动跟随。</summary>
    private void RefreshComponents()
    {
        try
        {
            var compService = _serviceProvider.GetService<IComponentsService>();
            var profile = compService?.CurrentComponents;
            if (profile is null) return;

            string? title = null, remaining = null;
            var texts = new List<string>();
            foreach (var line in profile.Lines)
                foreach (var child in line.Children)
                    CollectComponents(child, ref title, ref remaining, texts);

            _reminderData.CountdownTitle = title;
            _reminderData.CountdownRemaining = remaining;
            _reminderData.TextContent = texts.Count > 0 ? string.Join("\n", texts) : null;
            NotifyReminderChanged();
        }
        catch { /* CI 组件服务暂不可用时保持上次数据 */ }
    }

    /// <summary>递归收集组件设置里的倒计时/文本框（容器组件也遍历 Children）。</summary>
    /// <remarks>
    /// 不过滤 <see cref="ComponentSettings.IsActive"/>：实测 CI 布局文件里该字段对已显示组件也常为 false，
    /// 并非「是否显示」的可靠标志；用户要求「加了组件就显示」，故只按是否提取到内容判定。
    /// </remarks>
    private void CollectComponents(
        ComponentSettings c,
        ref string? title, ref string? remaining, List<string> texts)
    {
        if (c.Settings is not null)
        {
            var (t, r, txt) = CiComponentsReader.Extract(c.Settings, NowVirtual);
            if (!string.IsNullOrEmpty(t)) title ??= t;
            if (!string.IsNullOrEmpty(r)) remaining ??= r;
            if (!string.IsNullOrEmpty(txt)) texts.Add(txt!);
        }
        if (c.Children is not null)
            foreach (var child in c.Children)
                CollectComponents(child, ref title, ref remaining, texts);
    }

    /// <summary>从 CI Settings.json 缓存读取天气 + 预警（每 60 秒）。</summary>
    private void RefreshWeather()
    {
        try
        {
            var settingsPath = Path.Combine(CiDataDir, "Settings.json");
            if (!File.Exists(settingsPath)) return;
            var (code, temp, alerts) = CiWeatherReader.ReadLastWeather(settingsPath);

            // 预警内容与上次一致则不重建列表：重建会把正在悬停的预警项整链移除、
            // 触发 PointerExited/Entered，导致顶部详情弹幕每 60 秒（天气刷新周期）重置重播一次。
            var alertsChanged = alerts.Count != _reminderData.Alerts.Count
                || !alerts.Zip(_reminderData.Alerts).All(p =>
                    p.First.Title == p.Second.Title && p.First.Level == p.Second.Level
                    && p.First.Detail == p.Second.Detail);
            if (alertsChanged)
            {
                AlertItems.Clear();
                ExpandedAlertDetail = null;   // 预警内容更新，收起旧展开态（弹幕尺寸按旧文本算会错位）
                foreach (var a in alerts)
                    AlertItems.Add(new AlertDisplayItem
                    {
                        DisplayText = a.Title,
                        Icon = IconGlyph.Of(LucideIconKind.TriangleAlert),
                        Foreground = AlertBrush(a.Level),
                        Detail = a.Detail,
                    });

                _reminderData.Alerts.Clear();
                _reminderData.Alerts.AddRange(alerts);
            }

            if (string.IsNullOrEmpty(code))
            {
                _reminderData.WeatherIcon = null;
                _reminderData.WeatherText = null;
            }
            else
            {
                var desc = CiWeatherReader.GetWeatherDescription(code) ?? code;
                _reminderData.WeatherText = string.IsNullOrEmpty(temp) ? desc : $"{temp}° {desc}";

                // 天气图标：按天气码查 Lucide 图标名再转字形，用 CI 自带图标字体画成矢量图标。
                // （CI 的天气图标模板按小米天气码查表、码表在 CI 内部私有初始化，插件接不到，
                //   故这里自建 weathercn 码 → Lucide 图标映射。）
                _reminderData.WeatherIcon = CiWeatherReader.WeatherIcons.TryGetValue(code, out var iconName)
                    ? IconGlyph.Of(iconName)
                    : IconGlyph.Of(LucideIconKind.Thermometer);
            }

            _weatherCode = string.IsNullOrEmpty(code) ? null : code;
            _weatherTempC = string.IsNullOrEmpty(temp) ? null : ParseTemp(temp);
            _rainHours = CiWeatherReader.ReadRainHours(settingsPath);
            RainReminderTitle = CiWeatherReader.ReadRainReminder(settingsPath) ?? "";
            NotifyReminderChanged();
        }
        catch { }
    }

    /// <summary>预警等级 → 显示颜色（蓝/黄/橙/红）。</summary>
    private static IBrush AlertBrush(string? level)
    {
        var color = level switch
        {
            "蓝色" => "#55aaff",
            "黄色" => "#ffdd44",
            "橙色" => "#ff9933",
            "红色" => "#ff5555",
            _ => "#ffffff",
        };
        return new SolidColorBrush(Color.Parse(color));
    }

    /// <summary>触发所有提醒相关属性变更通知（数据或开关变化后调用）。</summary>
    private void NotifyReminderChanged()
    {
        OnPropertyChanged(nameof(WeatherIcon));
        OnPropertyChanged(nameof(WeatherText));
        OnPropertyChanged(nameof(RainReminderTitle));
        OnPropertyChanged(nameof(ShowRainReminder));
        OnPropertyChanged(nameof(ReminderText));
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(ShowWeatherRow));
        OnPropertyChanged(nameof(ShowAlertsRow));
        OnPropertyChanged(nameof(ShowCountdownRow));
        OnPropertyChanged(nameof(ShowTextRow));
        OnPropertyChanged(nameof(ShowReminderPanel));
        OnPropertyChanged(nameof(RainSubtitle));
        OnPropertyChanged(nameof(HasRainSubtitle));
        UpdateWeatherSubtitle();   // 天气码/温度变化后重算气温副标题
        UpdateCountdownSubtitle(); // 倒计时剩余天数变化后重算冲刺副标题
        UpdateReminderMerge();   // 内容/开关变化后重算第一行合并
        OnPropertyChanged(nameof(ShowReminderPanel));
    }

    /// <summary>CI 数据目录（data\，位于插件配置目录的上一级）。</summary>
    private string CiDataDir => Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));

    /// <summary>
    /// 记录规则（鼠标悬停计数区域时以提示显示）。
    /// </summary>
    public string CountRulesText
    {
        get
        {
            var parts = new List<string>
            {
                $"一段噪音累计满 {_settings.NoisySustainSeconds:0.#} 秒记一次",
                $"一段内回落不超过 {_settings.FallWindowSeconds:0.#} 秒并成一次",
                "一段噪音按一般/吵闹的时长占比归属：吵闹占一半及以上记「吵闹」，否则记「一般」",
            };
            if (_settings.SkipFirst3Min) parts.Add($"上课开始后 {_settings.SkipFirstMinutes} 分钟内不记录");
            return string.Join("，", parts);
        }
    }

    /// <summary>
    /// 底部固定显示的免责声明（前面的警告图标由 XAML 用矢量图标画，不在文本里）。
    /// </summary>
    public string DisclaimerText
        => "分贝仅供参考，可能受风扇、空调、开关门、桌椅移动、脚步声等环境杂音影响，不代表真实纪律状况";

    public void Show()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 必须在创建窗口前同步，否则绑定读到的是旧值
            ShowDecibelMeter = _settings.ShowDecibelMeter;
            ShowCourseInfo = _settings.ShowCourseInfo;
            ClockFontSize = _settings.ClockFontSize;
            // 外观颜色属性是直接读 _settings 的 getter，窗口从 Hide 退出后复用不自动刷新，
            // 主动触发通知让绑定重新求值（修复：改完颜色后要重启 CI 才生效的问题）
            RefreshAppearanceBindings();
            RefreshRemindersNow();

            if (_window == null)
            {
                _window = new FullScreenClockWindow { DataContext = this };
                _window.Closed += (_, _) =>
                {
                    IsWindowVisible = false;
                    StopUpdateTimer();
                    _decibelService.StopMonitoring();
                    UnsubscribeToClassEvents();
                    SetMainWindowVisible(true);
                    _window = null;
                };
                // 最小化后从任务栏点回：恢复即触发 Activated（Avalonia 11.3 无 WindowStateChanged 事件），
                // 若 OS 把全屏窗口恢复到 Normal 就在此兜底拉回全屏（守卫见 EnsureFullScreen，Minimized 态不打扰）。
                _window.Activated += (_, _) => EnsureFullScreen();
            }

            if (_window.IsVisible)
            {
                // 已最小化时再触发 Show（设置页按钮/托盘菜单）→ 先拉回全屏再刷新
                if (_window.WindowState == WindowState.Minimized)
                {
                    _window.WindowState = WindowState.Normal;
                    _window.Show();
                    _window.WindowState = WindowState.FullScreen;
                }
                RefreshAll();
                return;
            }

            // 重置新一轮记录
            _counter.StartNewClass();
            _counter.SetBreak(false);
            _currentClassName = "";
            _classSummaries.Clear();
            _isInBreak = false;
            _noisyDisplaySignature = "";
            CourseInfoText = "";
            LoadTodaySchedule();
            SubscribeToClassEvents();

            // 重新订阅分贝事件（先退订再订阅，保证不累积）
            _decibelService.PropertyChanged -= OnDecibelPropertyChanged;
            _decibelService.PropertyChanged += OnDecibelPropertyChanged;

            StartUpdateTimer();
            StartAntiScreensaver();
            _decibelService.StartMonitoring();
            RefreshAll();

            _window.WindowState = WindowState.FullScreen;
            _window.Show();
            IsWindowVisible = true;
            SetMainWindowVisible(false);
        });
    }

    /// <summary>
    /// 最小化到任务栏（收进任务栏可点回）。只收起画面，不停任何定时器/分贝/防休眠：
    /// 收起 ≠ 退出，计时、计数全程持续，点回任务栏图标即恢复全屏。
    /// </summary>
    public void Minimize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null || !IsWindowVisible) return;
            _window.WindowState = WindowState.Minimized;
        });
    }

    /// <summary>
    /// 兜底拉回全屏：窗口可见、非全屏、非最小化时（如任务栏点回落到 Normal），
    /// Show() 后设回 FullScreen。最小化/不可见/退出流程一律短路，不打扰、不自激。
    /// </summary>
    private void EnsureFullScreen()
    {
        if (_window == null || !IsWindowVisible || !_window.IsVisible) return;
        if (_window.WindowState is WindowState.FullScreen or WindowState.Minimized) return;
        _window.Show();   // 幂等：窗口已可见时再次 Show 把后台/非活动窗口置前
        _window.WindowState = WindowState.FullScreen;
    }

    public void Hide()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window?.Hide();
            StopUpdateTimer();
            StopAntiScreensaver();
            _decibelService.StopMonitoring();
            UnsubscribeToClassEvents();
            IsWindowVisible = false;
            SetMainWindowVisible(true);
        });
    }

    private void StartUpdateTimer()
    {
        if (_updateTimer != null) return;
        _updateTimer = new System.Timers.Timer(1000); // 时间只显示到秒，1s 足够
        _updateTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(RefreshAll);
        _updateTimer.AutoReset = true;
        _updateTimer.Start();
    }

    private void StopUpdateTimer()
    {
        if (_updateTimer == null) return;
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _updateTimer = null;
    }

    private void RefreshAll()
    {
        var now = DateTime.Now;
        CurrentTime = now.ToString("HH:mm:ss");
        CurrentDate = now.ToString("yyyy年M月d日 dddd");
        UpdateDateSubtitle();   // 日期下方副标题（深夜/周五/月初），每秒刷新

        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            CurrentCourseName = lessonsService.CurrentSubject?.Name ?? "";
        }
        catch { }

        // 课程定位用 CI 虚拟时间（与实际打铃对齐）
        UpdateCourseInfo(NowVirtual);

        // 刷新计数区域（脏标记：内容未变不重建；保护倒计时需要每秒刷新）
        UpdateNoisyDisplay();

        // 提醒面板：组件配置每 3 秒同步一次（用户改 CI 组件自动跟随），天气缓存每 60 秒读一次
        _reminderTick++;
        if (_reminderTick % 3 == 0) RefreshComponents();
        if ((DateTime.Now - _lastWeatherRead).TotalSeconds >= 60)
        {
            _lastWeatherRead = DateTime.Now;
            RefreshWeather();
        }
    }

    private void LoadTodaySchedule()
    {
        _todaySlots.Clear();
        try
        {
            var dataDir = Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));
            var settingsPath = Path.Combine(dataDir, "Settings.json");
            var profileFileName = "7.json";
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("SelectedProfile", out var sp))
                    profileFileName = sp.GetString() ?? "7.json";
                if (doc.RootElement.TryGetProperty("TimeOffsetSeconds", out var offset))
                    _timeOffsetSeconds = offset.GetInt32();
            }
            var profilePath = Path.Combine(dataDir, "Profiles", profileFileName);
            if (!File.Exists(profilePath)) return;

            using var profileDoc = JsonDocument.Parse(File.ReadAllText(profilePath));
            var root = profileDoc.RootElement;
            if (!root.TryGetProperty("TimeLayouts", out var layouts)) return;
            if (!root.TryGetProperty("ClassPlans", out var classPlans)) return;
            if (!root.TryGetProperty("Subjects", out var subjects)) return;

            var now = DateTime.Now;
            int todayDayOfWeek = (int)now.DayOfWeek;

            // 找今天的课表
            JsonElement? todayPlan = null;
            string? tlId = null;
            foreach (var cp in classPlans.EnumerateObject())
            {
                var v = cp.Value;
                if (v.TryGetProperty("IsOverlay", out var io) && io.GetBoolean()) continue;
                if (!v.TryGetProperty("TimeRule", out var tr)) continue;
                if (!tr.TryGetProperty("WeekDay", out var wd) || wd.GetInt32() != todayDayOfWeek) continue;
                if (!v.TryGetProperty("IsEnabled", out var ie) || !ie.GetBoolean()) continue;
                todayPlan = v;
                if (v.TryGetProperty("TimeLayoutId", out var t)) tlId = t.GetString();
                break;
            }
            if (todayPlan == null || tlId == null) return;
            if (!layouts.TryGetProperty(tlId, out var timeLayout)) return;
            if (!timeLayout.TryGetProperty("Layouts", out var items)) return;

            var classes = todayPlan.Value.GetProperty("Classes");
            int ci = 0;
            foreach (var item in items.EnumerateArray())
            {
                int timeType = item.GetProperty("TimeType").GetInt32();
                var start = TimeSpan.Parse(item.GetProperty("StartTime").GetString()!);
                var end = TimeSpan.Parse(item.GetProperty("EndTime").GetString()!);
                string? subjName = null;
                if (timeType == 0 && ci < classes.GetArrayLength())
                {
                    var ce = classes[ci];
                    if (ce.TryGetProperty("SubjectId", out var sid))
                    {
                        var sidStr = sid.GetString();
                        if (sidStr != null && subjects.TryGetProperty(sidStr, out var subj))
                            subjName = subj.GetProperty("Name").GetString();
                    }
                    ci++;
                }
                _todaySlots.Add(new TimeSlot { Start = start, End = end, IsClass = timeType == 0, SubjectName = subjName });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] 加载课表: {ex}");
        }
    }

    private void UpdateCourseInfo(DateTime now)
    {
        var time = now.TimeOfDay;
        TimeSlot? current = null;
        TimeSlot? nextClass = null;

        for (int i = 0; i < _todaySlots.Count; i++)
        {
            var s = _todaySlots[i];
            if (time >= s.Start && time < s.End)
            {
                current = s;
                for (int j = i + 1; j < _todaySlots.Count; j++)
                {
                    if (_todaySlots[j].IsClass && !string.IsNullOrEmpty(_todaySlots[j].SubjectName))
                    { nextClass = _todaySlots[j]; break; }
                }
                break;
            }
        }

        if (current != null && current.IsClass && !string.IsNullOrEmpty(current.SubjectName))
        {
            // 上课中：显示当前课程（含实际打铃时间，如有偏移）
            CourseInfoText = $"当前课程为 {current.SubjectName}   {current.Start:hh\\:mm} — {current.End:hh\\:mm}{FormatBellTime(current)}";
        }
        else if (_isInBreak && nextClass != null)
        {
            // 课间：显示下节课程
            CourseInfoText = $"下节课程为 {nextClass.SubjectName}   {nextClass.Start:hh\\:mm} — {nextClass.End:hh\\:mm}{FormatBellTime(nextClass)}";
        }
        else
        {
            CourseInfoText = "";
        }

        // 进度条：上课槽或课间槽都显示「当前时间状态」的进行进度。
        // time 与课程定位是同一时刻（NowVirtual），current 是同一时段槽 → 时间显示与进度天然一致。
        if (current != null)
        {
            CourseProgress = ProgressCalculator.Calc(time, current.Start, current.End);
            ShowCourseProgress = true;
        }
        else
        {
            ShowCourseProgress = false;
            CourseProgress = 0;
        }
    }

    /// <summary>
    /// 把课表时间换算成实际打铃时间（北京真实时间）的说明文字。
    /// 实际打铃时间 = 课表时间 - TimeOffsetSeconds。无偏移时返回空串。
    /// </summary>
    private string FormatBellTime(TimeSlot slot)
    {
        if (_timeOffsetSeconds == 0) return "";
        var offset = TimeSpan.FromSeconds(_timeOffsetSeconds);
        var bellStart = slot.Start - offset;
        var bellEnd = slot.End - offset;
        var fmt = _timeOffsetSeconds % 60 == 0 ? @"hh\:mm" : @"hh\:mm\:ss";
        return $"（实际打铃 {bellStart.ToString(fmt)} — {bellEnd.ToString(fmt)}）";
    }

    private void SetMainWindowVisible(bool visible)
    {
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime;
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win == _window) continue;
                    var typeName = win.GetType().FullName ?? "";
                    if (typeName.Contains("MainWindow"))
                    {
                        if (visible) win.Show(); else win.Hide();
                        break;
                    }
                }
            }
        }
        catch { }
    }

    private void SubscribeToClassEvents()
    {
        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            // 幂等订阅：先退订再订阅，避免 Show/Hide 多次叠加（P0 修复）
            lessonsService.CurrentTimeStateChanged -= OnTimeStateChanged;
            lessonsService.CurrentTimeStateChanged += OnTimeStateChanged;
            // 立即检查当前状态（用户可能在打开时钟时已经在上课）
            Dispatcher.UIThread.Post(() => OnTimeStateChanged(null, EventArgs.Empty));
        }
        catch { }
    }

    private void UnsubscribeToClassEvents()
    {
        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            lessonsService.CurrentTimeStateChanged -= OnTimeStateChanged;
        }
        catch { }
    }

    /// <summary>
    /// 从课表里查出当前课程的开始时间（用 CI 虚拟时间定位，与实际打铃对齐）。
    /// 如果课表没加载或找不到，返回 null，调用方降级使用虚拟时间。
    /// </summary>
    private DateTime? LookupActualClassStart(string subjectName)
    {
        if (string.IsNullOrEmpty(subjectName) || _todaySlots.Count == 0) return null;
        var now = NowVirtual;
        foreach (var slot in _todaySlots)
        {
            if (slot.IsClass && slot.SubjectName == subjectName
                && now.TimeOfDay >= slot.Start && now.TimeOfDay < slot.End)
            {
                return now.Date + slot.Start;
            }
        }
        return null;
    }

    private ClassSummary BuildSummary()
    {
        return new ClassSummary
        {
            ClassName = _currentClassName,
            Normal = _counter.NormalCount,
            Noisy = _counter.NoisyCount
        };
    }

    private void OnTimeStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
                var state = lessonsService.CurrentState;
                var subjectName = lessonsService.CurrentSubject?.Name ?? "";

                if (state == ClassIsland.Shared.Enums.TimeState.OnClass)
                {
                    // 课程切换 → 保存上一节总结
                    if (!string.IsNullOrEmpty(_currentClassName) && _currentClassName != subjectName)
                    {
                        var summary = BuildSummary();
                        if (summary.Normal > 0 || summary.Noisy > 0) _classSummaries.Add(summary);
                    }
                    _currentClassName = subjectName;
                    _counter.SetBreak(false);
                    _counter.StartNewClass();
                    _classStartTime = LookupActualClassStart(subjectName) ?? NowVirtual;
                    _isInBreak = false;
                    UpdateNoisyDisplay();
                    UpdateRecording();
                }
                else if (state == ClassIsland.Shared.Enums.TimeState.Breaking)
                {
                    if (!string.IsNullOrEmpty(_currentClassName) && (_counter.NormalCount > 0 || _counter.NoisyCount > 0))
                    {
                        _classSummaries.Add(BuildSummary());
                    }
                    _counter.SetBreak(true);
                    _isInBreak = true;
                    UpdateNoisyDisplay();
                    UpdateRecording();
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// 构建计数区域分段（课程名/一般黄/吵闹红/保护提示）。
    /// 只在上课期间显示；课间/非上课时段内部仍可记录，但显示隐藏。
    /// </summary>
    private List<NoisyDisplaySegment> BuildNoisySegments()
    {
        var list = new List<NoisyDisplaySegment>();
        if (!_settings.ShowNoisyCounter) return list;

        // 课间或非上课（无当前课程）时段：隐藏计数显示
        if (_isInBreak) return list;
        if (string.IsNullOrEmpty(_currentClassName)) return list;

        list.Add(new NoisyDisplaySegment { Text = _currentClassName });
        AddNoisyCountSegments(list);
        return list;
    }

    /// <summary>追加一般/吵闹计数段（计数为 0 时按设置显示保护提示或「暂未记录」）。</summary>
    private void AddNoisyCountSegments(List<NoisyDisplaySegment> list)
    {
        if (_counter.NormalCount > 0 || _counter.NoisyCount > 0)
        {
            if (_counter.NormalCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  一般 {_counter.NormalCount} 次", Foreground = CountNormalBrush });
            if (_counter.NoisyCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  吵闹 {_counter.NoisyCount} 次", Foreground = CountNoisyBrush });
        }
        else if (_settings.SkipFirst3Min)
        {
            var remaining = _settings.SkipFirstMinutes * 60 - (int)(NowVirtual - _classStartTime).TotalSeconds;
            if (remaining > 0)
                list.Add(new NoisyDisplaySegment
                {
                    Text = $"上课初期保护中（{remaining / 60}:{remaining % 60:D2} 后开始记录）",
                    Icon = IconGlyph.Of(LucideIconKind.Hourglass),
                });
            else
                list.Add(new NoisyDisplaySegment { Text = "暂未记录到吵闹", Icon = CheckIcon });
        }
        else
        {
            list.Add(new NoisyDisplaySegment { Text = "暂未记录到吵闹", Icon = CheckIcon });
        }
    }

    private void UpdateNoisyDisplay()
    {
        var segments = BuildNoisySegments();
        var signature = string.Join("|", segments.Select(s =>
            s.Text + ":" + s.Icon + ":" + (ReferenceEquals(s.Foreground, CountNoisyBrush) ? "R"
                : ReferenceEquals(s.Foreground, CountNormalBrush) ? "Y" : "P")));
        if (signature == _noisyDisplaySignature) return;

        _noisyDisplaySignature = signature;

        // 增量同步：复用现有片段只改文字/颜色/图标，不清空重建。
        // 清空重建会把鼠标下的 TextBlock 整链移除重建、触发 PointerExited/Entered 循环，
        // 导致上课初期保护期间每秒刷新时记录规则 Popup 一秒闪一次。
        for (var i = 0; i < NoisyDisplaySegments.Count && i < segments.Count; i++)
        {
            NoisyDisplaySegments[i].Text = segments[i].Text;
            NoisyDisplaySegments[i].Icon = segments[i].Icon;
            NoisyDisplaySegments[i].Foreground = segments[i].Foreground;
        }
        while (NoisyDisplaySegments.Count < segments.Count)
            NoisyDisplaySegments.Add(segments[NoisyDisplaySegments.Count]);
        while (NoisyDisplaySegments.Count > segments.Count)
            NoisyDisplaySegments.RemoveAt(NoisyDisplaySegments.Count - 1);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private void StartAntiScreensaver()
    {
        _antiScreensaverTimer?.Dispose();
        _antiScreensaverTimer = new System.Timers.Timer(25 * 60 * 1000); // 每 25 分钟
        _antiScreensaverTimer.Elapsed += (_, _) =>
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
        };
        _antiScreensaverTimer.Start();
        // 立即执行一次
        SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
    }

    private void StopAntiScreensaver()
    {
        _antiScreensaverTimer?.Dispose();
        _antiScreensaverTimer = null;
    }

    public void ExitFullScreen() => Hide();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal class TimeSlot
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public bool IsClass { get; set; }
    public string? SubjectName { get; set; }
}

/// <summary>
/// 一节课的吵闹记录摘要（课间/换课时显示用）。
/// </summary>
internal class ClassSummary
{
    public string ClassName { get; set; } = "";
    public int Normal { get; set; }
    public int Noisy { get; set; }
}

/// <summary>
/// 计数区域的单个显示片段（带颜色，用于一般/吵闹不同色）。
/// 实现 INotifyPropertyChanged：UpdateNoisyDisplay 增量同步时只更新已存在项的文字/颜色，
/// 不清空重建列表（重建会触发鼠标下元素的 PointerExited/Entered 循环）。
/// </summary>
public class NoisyDisplaySegment : INotifyPropertyChanged
{
    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(nameof(Text)); } }
    }

    private IBrush _foreground = new SolidColorBrush(Color.Parse("#ffff44"));
    public IBrush Foreground
    {
        get => _foreground;
        set { if (!ReferenceEquals(_foreground, value)) { _foreground = value; OnPropertyChanged(nameof(Foreground)); } }
    }

    /// <summary>图标矢量字形（空 = 该片段不带图标，如「一般 2 次」纯文字）。</summary>
    private string _icon = "";
    public string Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => !string.IsNullOrEmpty(_icon);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>提醒面板中单条预警的显示项（等级着色 + 详情全文，鼠标悬停即在顶部弹幕带显示）。</summary>
public class AlertDisplayItem
{
    public required string DisplayText { get; init; }

    /// <summary>图标矢量字形（警告三角，XAML 用 Lucide 字体渲染）。</summary>
    public string? Icon { get; init; }

    public required IBrush Foreground { get; init; }

    /// <summary>预警详情全文（alerts[].detail），鼠标悬停该条时显示。</summary>
    public string? Detail { get; init; }
}
