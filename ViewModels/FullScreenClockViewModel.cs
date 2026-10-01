using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core;
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
    private readonly CameraActivityService _cameraService;
    private readonly IServiceProvider _serviceProvider;
    private readonly EarthquakeReader _earthquakeReader;
    private readonly NoiseCounter _counter = new();
    private readonly Lazy<IExactTimeService?> _exactTimeService;
    private System.Timers.Timer? _updateTimer;
    private FullScreenClockWindow? _window;

    /// <summary>
    /// CI 里设置的时间偏移（秒）。正 = 打铃提前，负 = 打铃延后。
    /// </summary>
    private int _timeOffsetSeconds;

    // 计数显示颜色：一般 = 黄，吵闹 = 红。两套主题各一版（浅底上原色太亮看不清）
    private static readonly IBrush CountNormalBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNormalHex(false)));
    private static readonly IBrush CountNoisyBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNoisyHex(false)));
    private static readonly IBrush CountNormalLightBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNormalHex(true)));
    private static readonly IBrush CountNoisyLightBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNoisyHex(true)));

    /// <summary>当前主题下「一般」计数色。</summary>
    private IBrush CountNormal => Chrome.IsLight ? CountNormalLightBrush : CountNormalBrush;

    /// <summary>当前主题下「吵闹」计数色。</summary>
    private IBrush CountNoisy => Chrome.IsLight ? CountNoisyLightBrush : CountNoisyBrush;

    // 计数区「暂未记录到吵闹」前的对勾图标字形（矢量）
    private static readonly string CheckIcon = IconGlyph.Of(LucideIconKind.CircleCheck);

    // 音量条档位：档位名来自 NoiseLevelDisplay（与检测器分级同源），
    // 五档高亮、右侧状态、填充色全部以 AnimatedProgress 反推出的 CurrentSlot 为唯一源，避免不同步。
    private static readonly string[] SlotNames = NoiseLevelDisplay.SlotNames;

    /// <summary>五档轨道填充色：静态缓存，避免每次刷新都新建 SolidColorBrush（会让控件反复失效重绘、观感发顿）。</summary>
    private static readonly IBrush[] SlotBrushes = BuildSlotBrushes(LevelSlotCalculator.SlotColors);

    /// <summary>五档文字色（未达到该档）：深色 / 明亮两套（明亮用压暗版，白底上才看得清）。</summary>
    private static readonly IBrush[] SlotTextBrushesDark = BuildSlotBrushes(LevelSlotCalculator.SlotTextColors(false));
    private static readonly IBrush[] SlotTextBrushesLight = BuildSlotBrushes(LevelSlotCalculator.SlotTextColors(true));

    /// <summary>五档文字色（已到达该档）：只有「一般」与未达到时不同（亮黄 vs 压暗琥珀）。</summary>
    private static readonly IBrush[] SlotReachedBrushesDark = BuildReachedBrushes(false);
    private static readonly IBrush[] SlotReachedBrushesLight = BuildReachedBrushes(true);

    /// <summary>非当前档位文字的淡化程度：浅底上要比深底淡得少，否则整排字糊在背景里。</summary>
    private const double SlotFadeDark = 0.35;
    private const double SlotFadeLight = 0.5;

    /// <summary>逐档取「已达到」色（<see cref="LevelSlotCalculator.ReachedColorOf"/>）。</summary>
    private static IBrush[] BuildReachedBrushes(bool light)
    {
        var colors = new uint[SlotNames.Length];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = LevelSlotCalculator.ReachedColorOf(i, light);
        return BuildSlotBrushes(colors);
    }

    private static IBrush[] BuildSlotBrushes(uint[] colors)
    {
        var brushes = new IBrush[colors.Length];
        for (var i = 0; i < brushes.Length; i++)
        {
            var c = colors[i];
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

    // ===== 提醒面板数据（跟随 CI 组件配置 + CI 天气缓存 + 摄像头占用） =====
    private readonly ReminderData _reminderData = new();
    /// <summary>天气预警（CI 缓存读来的，60 秒刷新一次）。</summary>
    private readonly List<AlertInfo> _weatherAlerts = new();
    private int _reminderTick;
    private DateTime _lastWeatherRead = DateTime.MinValue;

    public FullScreenClockViewModel(
        PluginSettings settings,
        DecibelMeterService decibelService,
        CameraActivityService cameraService,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _decibelService = decibelService;
        _cameraService = cameraService;
        _serviceProvider = serviceProvider;
        _earthquakeReader = new EarthquakeReader(serviceProvider);

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
            _noiseLevelSlots.Add(new NoiseLevelSlotItem(SlotNames[i], SlotTextBrushesDark[i], SlotReachedBrushesDark[i]));
        ApplySlotTheme();
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
            or nameof(PluginSettings.ShowCountdownReminder)
            or nameof(PluginSettings.ShowTextReminder)
            or nameof(PluginSettings.ShowRainReminder))
        {
            NotifyReminderChanged();
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowEarthquakeReminder))
        {
            // 地震提醒开关：立刻显示/收起，不用等下一次整点刷新
            UpdateEarthquakeAlert();
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowAlertsReminder)
            or nameof(PluginSettings.ShowCameraReminder)
            or nameof(PluginSettings.MonitoredCameras))
        {
            // 预警/摄像头开关或勾选的设备变了 → 立刻重算条目（集合内容变化不会走上面那支）
            if (args.PropertyName is nameof(PluginSettings.ShowCameraReminder))
            {
                // 大屏正开着时改开关：立即开始/停止监视（没开大屏则等 Show() 时再起）
                if (_settings.ShowCameraReminder && IsWindowVisible) _cameraService.StartMonitoring();
                else if (!_settings.ShowCameraReminder) _cameraService.StopMonitoring();
            }
            UpdateAlertItems();
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
        else if (args.PropertyName is nameof(PluginSettings.ShowBellTime))
        {
            // 「显示精确打铃时间」子项：即时把末尾那段加减回去/去掉
            UpdateCourseInfo(NowVirtual);
        }
        else if (args.PropertyName is nameof(PluginSettings.ThemeMode))
        {
            // 设置页里切了主题（颜色已被设置页改写）→ 大屏时钟正开着也立刻换配色
            Dispatcher.UIThread.Post(() =>
            {
                ApplyThemePalette();
                RefreshAppearanceBindings();
            });
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
                OnPropertyChanged(nameof(NoiseLevelTextBrush));
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
    public string ProgressColor => _settings.ProgressColor;
    public string CourseInfoColor => _settings.CourseInfoColor;
    public string NoiseTitleColor => _settings.NoiseTitleColor;
    public string RainColor => _settings.RainColor;

    /// <summary>
    /// 当前主题的界面「底色 / 文字」画刷（背景白/黑时整批翻转）。
    /// 界面绑 Chrome.Xxx，换主题只需通知这一个属性。
    /// </summary>
    public ClockBrushes Chrome { get; private set; } = ClockBrushes.From(ClockTheme.Dark);

    /// <summary>
    /// 时钟数字字体：跟随 CI 主界面字体（CI 里选什么字体，大屏时钟就用什么），
    /// 换掉原来的 Consolas（数字 0 中间带斜杠）。
    /// </summary>
    public FontFamily ClockFontFamily { get; private set; } = FontFamily.Parse(DefaultClockFont);

    /// <summary>数字用表格宽度（tnum）：秒数跳动时整行不左右抖。</summary>
    public FontFeatureCollection ClockFontFeatures { get; } =
        new FontFeatureCollection { FontFeature.Parse("tnum") };

    /// <summary>读不到 CI 字体设置时的回退（原来的窗口字体）。</summary>
    private const string DefaultClockFont = "Microsoft YaHei UI, SimHei, sans-serif";

    /// <summary>进度条「已进行」部分颜色（深色，不透明）。</summary>
    public IBrush ProgressFillBrush => new SolidColorBrush(Color.Parse(_settings.ProgressColor));

    /// <summary>进度带「未进行」部分颜色（随主题：深色底上的低饱和深蓝 / 浅色底上的浅灰蓝）。</summary>
    public IBrush ProgressTrackBrush => Chrome.VolumeTrack;

    /// <summary>降水提醒色（设置项，默认蓝）：降雨提醒块用它，颜色可在设置里调。</summary>
    public IBrush RainColorBrush => new SolidColorBrush(Color.Parse(_settings.RainColor));

    /// <summary>当前天气是不是降水类（按 CI 天气码判断；雾/霾/沙尘不算）。</summary>
    private bool IsPrecipitationWeather => _weatherCode is { } code && CiWeatherReader.RainCodes.Contains(code);

    /// <summary>天气块的颜色：当前在下雨/下雪时用降水色（蓝），否则用主题正文色。</summary>
    public IBrush WeatherBrush => IsPrecipitationWeather ? RainColorBrush : Chrome.TextPrimary;

    /// <summary>
    /// 触发所有外观属性变更通知，让窗口绑定重新求值。
    /// 窗口从 Hide 退出后复用时，这些表达式体 getter 不会自动刷新，需主动通知。
    /// </summary>
    private void RefreshAppearanceBindings()
    {
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(FontColor));
        OnPropertyChanged(nameof(ProgressColor));
        OnPropertyChanged(nameof(ProgressFillBrush));
        OnPropertyChanged(nameof(ProgressTrackBrush));
        OnPropertyChanged(nameof(CourseInfoColor));
        OnPropertyChanged(nameof(NoiseTitleColor));
        OnPropertyChanged(nameof(RainColor));
        OnPropertyChanged(nameof(RainColorBrush));
        OnPropertyChanged(nameof(WeatherBrush));
        OnPropertyChanged(nameof(ClockFontSize));
        OnPropertyChanged(nameof(CourseInfoFontSize));
        OnPropertyChanged(nameof(CountFontSize));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>CI 当前是不是深色主题。取 Application 的生效变体 —— CI 的主题设置改的就是它。</summary>
    private static bool CiIsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

    /// <summary>按设置里的主题档位 + CI 当前亮暗刷新界面画刷与语义色（不改动颜色设置）。</summary>
    private void RefreshChrome()
    {
        Chrome = ClockBrushes.From(ClockTheme.Resolve((ClockThemeMode)_settings.ThemeMode, CiIsDark));
        OnPropertyChanged(nameof(Chrome));
        OnPropertyChanged(nameof(NoiseLevelTrackBrush));
        OnPropertyChanged(nameof(ProgressTrackBrush));
        OnPropertyChanged(nameof(WeatherBrush));   // 非降水天气的天气块用主题正文色，换主题要跟着变
        RefreshSemanticColors();
    }

    /// <summary>
    /// 语义色（五档文字、预警等级、吵闹计数）随底色深浅换一版：
    /// 浅底上黄绿/黄的原始档位色和预警黄几乎看不见，改用同色相的压暗版。
    /// </summary>
    private void RefreshSemanticColors()
    {
        ApplySlotTheme();

        // 预警列表：就地改色，不重建列表（重建会把正在悬停的那条弹幕重置重播）
        var light = Chrome.IsLight;
        foreach (var a in AlertItems)
            a.Foreground = AlertBrush(a.Level, light);

        // 地震提醒（等级色也分明暗两版）
        NotifyEarthquakeChanged();

        // 计数片段：签名只记 黄/红/其它，换主题后签名不变会被判成「没变化」而不刷新，
        // 故清掉签名强制重建一次（下面会各自填回新颜色）
        _noisyDisplaySignature = "";
        UpdateNoisyDisplay();
        RefreshRecordingColor();
    }

    /// <summary>档位文字：换主题时同时换「未达到/已达到」两色与淡化程度（浅底要淡得少些）。</summary>
    private void ApplySlotTheme()
    {
        var light = Chrome.IsLight;
        var brushes = light ? SlotTextBrushesLight : SlotTextBrushesDark;
        var reached = light ? SlotReachedBrushesLight : SlotReachedBrushesDark;
        var fade = light ? SlotFadeLight : SlotFadeDark;
        for (var i = 0; i < _noiseLevelSlots.Count; i++)
            _noiseLevelSlots[i].SetStyle(brushes[i], reached[i], fade);
        OnPropertyChanged(nameof(NoiseLevelTextBrush));
    }

    private static bool IsCountNormal(IBrush b) => ReferenceEquals(b, CountNormalBrush) || ReferenceEquals(b, CountNormalLightBrush);
    private static bool IsCountNoisy(IBrush b) => ReferenceEquals(b, CountNoisyBrush) || ReferenceEquals(b, CountNoisyLightBrush);

    /// <summary>
    /// 应用主题：生效主题变了就把 5 个颜色写成该主题的默认值（见 <see cref="ClockThemeApplier"/>），
    /// 再刷新画刷。进入大屏时钟时、以及跟随档下 CI 主题变化时调用。
    /// </summary>
    private void ApplyThemePalette()
    {
        var colorsChanged = ClockThemeApplier.Apply(_settings, CiIsDark);
        RefreshChrome();
        if (colorsChanged) RefreshAppearanceBindings();
    }

    /// <summary>刷新时钟字体：重新读 CI 设置里的主界面字体（用户可能在 CI 里改过）。</summary>
    private void RefreshClockFont()
    {
        var font = CiMainFont.ReadFontString(CiDataDir) ?? DefaultClockFont;
        try { ClockFontFamily = FontFamily.Parse(font); }
        catch { ClockFontFamily = FontFamily.Parse(DefaultClockFont); }
        OnPropertyChanged(nameof(ClockFontFamily));
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

    /// <summary>右侧状态文字的颜色：按当前档位取「已达到」色（随主题换），与下方五档文字同源。</summary>
    public IBrush NoiseLevelTextBrush => (Chrome.IsLight ? SlotReachedBrushesLight : SlotReachedBrushesDark)[CurrentSlot];

    /// <summary>音量条五档文字集合（安静/良好/一般/吵闹/嘈杂）。</summary>
    public ObservableCollection<NoiseLevelSlotItem> NoiseLevelSlots => _noiseLevelSlots;

    /// <summary>音量条轨道填充色：随当前档位可变（绿→黄→红），取缓存的画刷实例。</summary>
    public IBrush NoiseLevelFillBrush => SlotBrushes[CurrentSlot];

    /// <summary>音量条轨道底色 / 课程进度带 track 底色（随主题翻转，与进度带共用同一画刷实例）。</summary>
    public IBrush NoiseLevelTrackBrush => Chrome.VolumeTrack;

    /// <summary>档位变化时：刷新当前档位文字高亮 + 轨道填充色 + 右侧状态文字（全部同一档位源）。</summary>
    private void UpdateNoiseLevelSlots()
    {
        var slot = CurrentSlot;
        for (var i = 0; i < _noiseLevelSlots.Count; i++)
            _noiseLevelSlots[i].IsCurrent = i == slot;
        OnPropertyChanged(nameof(NoiseLevelText));
        OnPropertyChanged(nameof(NoiseLevelFillBrush));
        OnPropertyChanged(nameof(NoiseLevelTextBrush));
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

    /// <summary>当前主题下的「正在记录」提示色（换主题时由 <see cref="RefreshSemanticColors"/> 重算）。</summary>
    private void RefreshRecordingColor()
    {
        if (!IsRecordingVisible) return;
        RecordingForeground = RecordingText.Contains("吵闹") ? CountNoisy : CountNormal;
    }

    /// <summary>是否显示「正在记录」提示（有段且已起算、上课中）。</summary>
    public bool IsRecordingVisible
    {
        get => _isRecordingVisible;
        set
        {
            if (_isRecordingVisible == value) return;
            _isRecordingVisible = value;
            OnPropertyChanged();
            // 它一亮，「暂未记录到吵闹」就得让位（见 AddIdleSegment），左边计数片段要跟着重算
            UpdateNoisyDisplay();
        }
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
    /// 仅在计数可见、上课时段、且不在上课初期保护期内显示；段起算前/段结束后隐藏。
    /// （保护期内本来就不计数，再挂个「正在记录」会误导同学）
    /// </summary>
    private void UpdateRecording()
    {
        var seg = _decibelService.CurrentSegmentLevel;
        bool show = seg.HasValue && _settings.ShowNoisyCounter
            && !_isInBreak && !string.IsNullOrEmpty(_currentClassName)
            && !IsInProtection();
        IsRecordingVisible = show;
        if (!show) return;
        var noisy = seg == NoiseLevel.Noisy;
        RecordingText = noisy ? "正在记录：吵闹" : "正在记录：一般";
        RecordingForeground = noisy ? CountNoisy : CountNormal;
    }

    private bool _showDecibelMeter = true;
    private bool _showCourseInfo = true;
    private int _clockFontSize = 180;
    private int _courseInfoFontSize = 28;
    private int _countFontSize = 24;

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

    /// <summary>底部居中的课程信息字号（设置项，改完进大屏时钟时生效）。</summary>
    public int CourseInfoFontSize
    {
        get => _courseInfoFontSize;
        set { _courseInfoFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>左下角计数与「正在记录」提示的字号（设置项）。</summary>
    public int CountFontSize
    {
        get => _countFontSize;
        set { _countFontSize = value; OnPropertyChanged(); }
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
    // 预警开关的过滤在 UpdateAlertItems() 里做（摄像头占用那条不受天气「预警」开关影响）
    public bool ShowAlertsRow => _reminderData.HasAlerts;
    public bool ShowCountdownRow => _settings.ShowCountdownReminder && _reminderData.HasCountdown;
    public bool ShowTextRow => _settings.ShowTextReminder && _reminderData.HasText;

    // 提醒面板项：未并入第一行的 降雨/倒计时/文本 才在面板显示（天气永不在面板，预警始终在面板）
    public bool ShowRainReminderPanel => ShowRainReminder && !ShowRainMergedInFirstRow;
    public bool ShowCountdownPanel => ShowCountdownRow && !ShowCountdownMergedInFirstRow;
    public bool ShowTextPanel => ShowTextRow && !ShowTextMergedInFirstRow;
    public bool ShowReminderPanel => _settings.ShowReminderPanel
        && (ShowAlertsRow || ShowRainReminderPanel || ShowCountdownPanel || ShowTextPanel);

    // ===== 地震提醒（屏幕上方弹出，内容读自「地震预警」插件） =====
    // 每分钟的行程另有其人：数据由 EarthquakeReader 反射取，组装规则在 EarthquakeAlertBuilder（纯函数）。

    /// <summary>当前这场地震的提醒；没有正在预警的地震时为 null。</summary>
    private EarthquakeAlert? _earthquakeAlert;

    /// <summary>这场地震的横波到达时刻。一旦开始显示就守着它倒数到点，中途读不到数据也不收。</summary>
    private DateTime _earthquakeEndTime;

    /// <summary>正在显示的这场地震是谁（对方的事件标识）。用来认出「对方换了一场」。</summary>
    private string? _earthquakeEventKey;

    /// <summary>是否在时钟正上方弹出地震提醒（开关开着且有正在预警的地震）。</summary>
    public bool ShowEarthquakeAlert => _settings.ShowEarthquakeReminder && _earthquakeAlert is not null;

    /// <summary>第一行：横波还有 X 秒到达。</summary>
    public string EarthquakeLine1 => _earthquakeAlert?.Line1 ?? "";

    /// <summary>第二行：震中 + 震级。</summary>
    public string EarthquakeLine2 => _earthquakeAlert?.Line2 ?? "";

    /// <summary>等级色（本地烈度分档，与地震预警插件同一套）：两行文字、描边、底色都用它。</summary>
    public IBrush EarthquakeAlertBrush => QuakeBrush(0xFF);

    /// <summary>提醒块底色：等级色的低透明版（强调用，不盖住文字）。</summary>
    public IBrush EarthquakeAlertBackground => QuakeBrush(Chrome.IsLight ? (byte)0x22 : (byte)0x33);

    /// <summary>提醒块描边：等级色的半透明版。</summary>
    public IBrush EarthquakeAlertBorder => QuakeBrush(Chrome.IsLight ? (byte)0x88 : (byte)0x99);

    /// <summary>第一行（倒计时）字号：随时钟字号缩放，太长时由 Viewbox 再缩。</summary>
    public double EarthquakeLine1FontSize => Math.Clamp(ClockFontSize * 0.18, 26, 96);

    /// <summary>第二行（震中/震级）字号。</summary>
    public double EarthquakeLine2FontSize => Math.Clamp(ClockFontSize * 0.12, 18, 64);

    private IBrush QuakeBrush(byte alpha)
    {
        var hex = SemanticColors.EarthquakeHex(_earthquakeAlert?.Intensity ?? 0, Chrome.IsLight);
        var color = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    /// <summary>
    /// 地震提醒：每秒从「地震预警」插件读一次（横波倒计时每秒都在变）。
    /// 内容没变就不发通知，免得每秒都重排一次布局。
    ///
    /// 一旦开始显示就「粘」住：记下横波到达时刻，之后哪怕某一秒读不到数据（对方正在更新记录、
    /// 或我们这边正好在改设置重排），也照着自己的节拍把倒计时数到点才收——不会中途闪断。
    ///
    /// 但粘住的范围只限「同一场地震」：对方换了另一场（比如测试里从四川换成云南，
    /// 或前一报已经了结）就得当场收起，不能抱着上一场往下数（用户 2026-10-01 反馈）。
    /// 靠对方的事件标识分辨这两件事。
    /// </summary>
    private void UpdateEarthquakeAlert()
    {
        var now = DateTime.Now;

        if (!_settings.ShowEarthquakeReminder)
        {
            ClearEarthquakeAlert();
            return;
        }

        var reading = _earthquakeReader.Read(now);
        if (reading.Alert is { } next)
        {
            _earthquakeEventKey = reading.EventKey ?? _earthquakeEventKey;
            _earthquakeEndTime = now.AddSeconds(next.SecondsLeft);
            if (next == _earthquakeAlert) return;   // 文案没变（同一秒内被调了两次）
            _earthquakeAlert = next;
            NotifyEarthquakeChanged();
            return;
        }

        // 这一拍没有要提醒的地震。没在显示的就算了；正在显示的得先看对方是不是换了一场
        if (_earthquakeAlert is null) return;
        if (reading.EventKey is not null && reading.EventKey != _earthquakeEventKey)
        {
            ClearEarthquakeAlert();
            return;
        }

        // 还是同一场、只是这拍没读到：接着数，数到横波到达才收
        var left = (int)Math.Round((_earthquakeEndTime - now).TotalSeconds);
        if (left < 1)
        {
            ClearEarthquakeAlert();
            return;
        }

        if (left == _earthquakeAlert.SecondsLeft) return;
        _earthquakeAlert = _earthquakeAlert.WithSecondsLeft(left);
        NotifyEarthquakeChanged();
    }

    private void ClearEarthquakeAlert()
    {
        _earthquakeEventKey = null;
        if (_earthquakeAlert is null) return;
        _earthquakeAlert = null;
        NotifyEarthquakeChanged();
    }

    // ===== 时钟/音量条滑块的可拖范围（百分比） =====
    // 大屏那边按限位算出「实际能落到哪」后写进来（见 FullScreenClockWindow.ApplyBodyPositions），
    // 设置页的滑块拿它当 Minimum/Maximum。不这么做滑块能一路拖到 0，
    // 而屏幕上早就顶住不动了——「还能继续调小、右边数字却不变」（用户 2026-10-01 反馈）。

    public double ClockPositionMin => _clockPositionMin;
    public double ClockPositionMax => _clockPositionMax;
    public double VolumePositionMin => _volumePositionMin;
    public double VolumePositionMax => _volumePositionMax;

    private double _clockPositionMin;
    private double _clockPositionMax = 100;
    private double _volumePositionMin;
    private double _volumePositionMax = 100;

    /// <summary>大屏那边算完限位后同步滑块范围（全屏时钟没开时是默认的 0~100，等同不限制）。</summary>
    public void SetPositionLimits(double clockMin, double clockMax, double volumeMin, double volumeMax)
    {
        SetLimit(ref _clockPositionMin, clockMin, nameof(ClockPositionMin));
        SetLimit(ref _clockPositionMax, clockMax, nameof(ClockPositionMax));
        SetLimit(ref _volumePositionMin, volumeMin, nameof(VolumePositionMin));
        SetLimit(ref _volumePositionMax, volumeMax, nameof(VolumePositionMax));
    }

    /// <summary>全屏时钟收起时恢复成整段可拖（这时没有任何限位在起作用）。</summary>
    public void ResetPositionLimits() => SetPositionLimits(0, 100, 0, 100);

    private void SetLimit(ref double field, double value, string propertyName)
    {
        if (Math.Abs(field - value) < 0.05) return;   // 布局每轮都算一遍，没变就别惊动绑定
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void NotifyEarthquakeChanged()
    {
        OnPropertyChanged(nameof(ShowEarthquakeAlert));
        OnPropertyChanged(nameof(EarthquakeLine1));
        OnPropertyChanged(nameof(EarthquakeLine2));
        OnPropertyChanged(nameof(EarthquakeAlertBrush));
        OnPropertyChanged(nameof(EarthquakeAlertBackground));
        OnPropertyChanged(nameof(EarthquakeAlertBorder));
        OnPropertyChanged(nameof(EarthquakeLine1FontSize));
        OnPropertyChanged(nameof(EarthquakeLine2FontSize));
    }

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

    /// <summary>
    /// 底部课程信息的最大宽（窗口按「整行宽 × 1/2」设置）。超过这个宽就交给 Viewbox 等比缩字，
    /// 不再被列宽裁掉——开了「显示精确打铃时间」后这行会明显变长，正中那列又不好压缩。
    /// 默认无穷大：窗口还没量出尺寸前不设限，免得一上来被压成 0 宽。
    /// </summary>
    public double CourseInfoMaxWidth
    {
        get => _courseInfoMaxWidth;
        set
        {
            if (Math.Abs(_courseInfoMaxWidth - value) < 0.5) return;
            _courseInfoMaxWidth = value;
            OnPropertyChanged();
        }
    }
    private double _courseInfoMaxWidth = double.PositiveInfinity;

    /// <summary>
    /// 地震提醒块的最大宽（窗口按「整窗宽去掉页面左右边距」设置）。两行文字太长时交给 Viewbox 等比缩字，
    /// 保证整个提醒都塞得进屏幕。默认无穷大：窗口还没量出尺寸前不设限。
    /// </summary>
    public double EarthquakeAlertMaxWidth
    {
        get => _earthquakeAlertMaxWidth;
        set
        {
            if (Math.Abs(_earthquakeAlertMaxWidth - value) < 0.5) return;
            _earthquakeAlertMaxWidth = value;
            OnPropertyChanged();
        }
    }
    private double _earthquakeAlertMaxWidth = double.PositiveInfinity;


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

            _weatherAlerts.Clear();
            _weatherAlerts.AddRange(alerts);
            UpdateAlertItems();

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

    /// <summary>
    /// 合成预警列表并显示：天气预警（按「预警」开关过滤）+ 摄像头占用提醒（按「摄像头占用」开关过滤）。
    /// 内容与上次一致就不重建——重建会把正在悬停的条目整链移除、触发 PointerExited/Entered，
    /// 导致顶部详情弹幕（天气 60 秒一刷、摄像头每秒一算）重置重播一次。
    /// </summary>
    private void UpdateAlertItems()
    {
        // 摄像头占用快照由 CameraActivityService 在后台更新，这里取当前快照重算条目
        var cameraAlerts = _settings.ShowCameraReminder
            ? CameraAlertBuilder.Build(_settings.MonitoredCameras, _cameraService.Occupancy)
            : new List<AlertInfo>();

        var merged = new List<AlertInfo>();
        if (_settings.ShowAlertsReminder) merged.AddRange(_weatherAlerts);
        merged.AddRange(cameraAlerts);

        var changed = merged.Count != _reminderData.Alerts.Count
            || !merged.Zip(_reminderData.Alerts).All(p =>
                p.First.Title == p.Second.Title && p.First.Level == p.Second.Level
                && p.First.Detail == p.Second.Detail);
        if (!changed) return;

        AlertItems.Clear();
        ExpandedAlertDetail = null;   // 预警内容更新，收起旧展开态（弹幕尺寸按旧文本算会错位）
        foreach (var a in merged)
            AlertItems.Add(new AlertDisplayItem
            {
                DisplayText = a.Title,
                Icon = IconGlyph.Of(a.IconName),
                Level = a.Level,
                Foreground = AlertBrush(a.Level, Chrome.IsLight),
                Detail = a.Detail,
            });

        _reminderData.Alerts.Clear();
        _reminderData.Alerts.AddRange(merged);
        NotifyReminderChanged();   // 条目增减会影响面板/第一行的可见性
    }

    /// <summary>预警等级 → 显示颜色（蓝/黄/橙/红，浅底上取压暗版）。等级缺失时用主题正文色。</summary>
    private IBrush AlertBrush(string? level, bool light)
    {
        var hex = SemanticColors.AlertHex(level, light);
        return hex is null ? Chrome.TextPrimary : new SolidColorBrush(Color.Parse(hex));
    }

    /// <summary>触发所有提醒相关属性变更通知（数据或开关变化后调用）。</summary>
    private void NotifyReminderChanged()
    {
        OnPropertyChanged(nameof(WeatherIcon));
        OnPropertyChanged(nameof(WeatherText));
        OnPropertyChanged(nameof(WeatherBrush));
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
            CourseInfoFontSize = _settings.CourseInfoFontSize;
            CountFontSize = _settings.CountFontSize;
            // 主题：跟随档下 CI 主题可能在别处被改过 → 先按当前生效主题写好颜色，再刷新外观绑定
            ApplyThemePalette();
            RefreshClockFont();
            // 外观颜色属性是直接读 _settings 的 getter，窗口从 Hide 退出后复用不自动刷新，
            // 主动触发通知让绑定重新求值（修复：改完颜色后要重启 CI 才生效的问题）
            RefreshAppearanceBindings();
            RefreshRemindersNow();
            UpdateEarthquakeAlert();   // 正赶上一场地震时，开屏就有（否则等下个整秒的刷新）

            if (_window == null)
            {
                _window = new FullScreenClockWindow { DataContext = this };
                _window.Closed += (_, _) =>
                {
                    IsWindowVisible = false;
                    StopUpdateTimer();
                    _decibelService.StopMonitoring();
                    _cameraService.StopMonitoring();
                    UnsubscribeToClassEvents();
                    SetMainWindowVisible(true);
                    _window = null;
                };
                // 最小化后从任务栏点回：恢复即触发 Activated（Avalonia 11.3 无 WindowStateChanged 事件），
                // 若 OS 把全屏窗口恢复到 Normal 就在此兜底拉回全屏（守卫见 EnsureFullScreen，Minimized 态不打扰）。
                _window.Activated += (_, _) => EnsureFullScreen();
                // 跟随档：CI 主题在明亮/黑暗间切换时，大屏正开着也即时换配色
                _window.ActualThemeVariantChanged += (_, _) => ApplyThemePalette();
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
                // 最小化时把 CI 主界面拉了出来（见 Minimize），回到大屏就得收回去，
                // 否则主界面一直留在大屏时钟后面/旁边
                SetMainWindowVisible(false);
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
            // 摄像头占用检测：跟着大屏时钟的显示周期开关（用不上时不占系统资源）
            if (_settings.ShowCameraReminder) _cameraService.StartMonitoring();
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
            ShowCiMainWindow();
        });
    }

    /// <summary>
    /// 收起大屏时钟后把 ClassIsland 主界面拉出来。以前只最小化自己，屏幕一片空，
    /// 得自己去托盘里把主界面点回来。主界面若被隐藏/最小化就显示并还原，再提到最前。
    /// </summary>
    private static void ShowCiMainWindow()
    {
        if (AppBase.Current?.MainWindow is not { } main) return;
        main.Show();
        main.WindowState = WindowState.Normal;
        main.Activate();
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
        // 从任务栏点回也算「回到大屏」：最小化时拉出来的 CI 主界面同样要收回去
        SetMainWindowVisible(false);
    }

    public void Hide()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window?.Hide();
            StopUpdateTimer();
            StopAntiScreensaver();
            _decibelService.StopMonitoring();
            _cameraService.StopMonitoring();
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

        // 摄像头占用：占用快照由后台监视器事件驱动更新，这里每秒取一次并合成提醒条目
        // （内容没变时 UpdateAlertItems 直接返回，不会打断正在看的预警详情弹幕）
        UpdateAlertItems();

        // 地震提醒：横波倒计时每秒都在变，跟着这一秒的节拍刷新
        UpdateEarthquakeAlert();

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
    /// 实际打铃时间 = 课表时间 - TimeOffsetSeconds。无偏移或用户关掉该子项时返回空串。
    /// </summary>
    private string FormatBellTime(TimeSlot slot)
    {
        if (!_settings.ShowBellTime || _timeOffsetSeconds == 0) return "";
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

        // 课程名不是「数据」而是标签，用主题正文色（白/黑）。
        // 着色只留给一般/吵闹的计数，否则一整行都是荧光黄，浅底上整行读不出来。
        // 课程信息正显示着时就不再重复写一遍课名：正中那行已经写着「当前课程为 X」，
        // 左边再来一遍既啰嗦，又把大半列宽占掉（课程信息一长，计数就被列宽裁掉）。
        if (!ShowCourseInfo)
            list.Add(new NoisyDisplaySegment { Text = _currentClassName, Foreground = Chrome.TextPrimary });
        AddNoisyCountSegments(list);
        return list;
    }

    /// <summary>追加一般/吵闹计数段（计数为 0 时按设置显示保护提示或「暂未记录」）。</summary>
    private void AddNoisyCountSegments(List<NoisyDisplaySegment> list)
    {
        if (_counter.NormalCount > 0 || _counter.NoisyCount > 0)
        {
            if (_counter.NormalCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  一般 {_counter.NormalCount} 次", Foreground = CountNormal });
            if (_counter.NoisyCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  吵闹 {_counter.NoisyCount} 次", Foreground = CountNoisy });
        }
        else if (_settings.SkipFirst3Min)
        {
            var remaining = _settings.SkipFirstMinutes * 60 - (int)(NowVirtual - _classStartTime).TotalSeconds;
            if (remaining > 0)
                list.Add(new NoisyDisplaySegment
                {
                    Text = $"上课初期保护中（{remaining / 60}:{remaining % 60:D2} 后开始记录）",
                    Icon = IconGlyph.Of(LucideIconKind.Hourglass),
                    Foreground = Chrome.TextPrimary,
                });
            else
                AddIdleSegment(list);
        }
        else
        {
            AddIdleSegment(list);
        }
    }

    /// <summary>
    /// 「暂未记录到吵闹」那一段。右边的「正在记录」已经亮着时不再显示：
    /// 正记着还说「没记录到」自相矛盾，两句叠一行又把左边的课程名挤到列外被裁掉。
    /// </summary>
    private void AddIdleSegment(List<NoisyDisplaySegment> list)
    {
        if (IsRecordingVisible) return;
        list.Add(new NoisyDisplaySegment { Text = "暂未记录到吵闹", Icon = CheckIcon, Foreground = Chrome.TextPrimary });
    }

    private void UpdateNoisyDisplay()
    {
        var segments = BuildNoisySegments();
        var signature = string.Join("|", segments.Select(s =>
            s.Text + ":" + s.Icon + ":" + (IsCountNoisy(s.Foreground) ? "R"
                : IsCountNormal(s.Foreground) ? "Y" : "P")));
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

    // 兜底值：实际每个片段都由 VM 按主题显式给色（见 BuildNoisySegments）。
    // 这里以前是写死的荧光黄 #ffff44，浅底主题下漏改一处就是一整行看不见。
    private IBrush _foreground = Brushes.White;

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
public class AlertDisplayItem : INotifyPropertyChanged
{
    public required string DisplayText { get; init; }

    /// <summary>图标矢量字形（警告三角，XAML 用 Lucide 字体渲染）。</summary>
    public string? Icon { get; init; }

    /// <summary>预警等级（蓝/黄/橙/红），换主题时据此重算颜色。</summary>
    public string? Level { get; init; }

    private IBrush _foreground = Brushes.White;

    /// <summary>等级色（可写：切主题时就地换压暗版，不重建列表）。</summary>
    public required IBrush Foreground
    {
        get => _foreground;
        set
        {
            _foreground = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
        }
    }

    /// <summary>预警详情全文（alerts[].detail），鼠标悬停该条时显示。</summary>
    public string? Detail { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
