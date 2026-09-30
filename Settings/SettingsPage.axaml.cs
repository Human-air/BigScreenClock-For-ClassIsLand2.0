using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Timers;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.Settings;

[SettingsPageInfo("com.bigscreen.clock.settings", "大屏时钟", SettingsPageCategory.External)]
public partial class SettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly PluginSettings _settings;
    private List<string> _allCourseNames = new();
    private System.Timers.Timer? _previewTimer;

    private string _previewTime = DateTime.Now.ToString("HH:mm:ss");
    private string _previewCourseText = "当前课程为 晚自习   18:30 — 21:30";

    public ObservableCollection<CourseSelectionItem> SelectedCourses { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        _settings = Plugin.Settings!;
        DataContext = this;

        // 预览按当前生效主题呈现：跟随档下 CI 的亮暗就是这里的亮暗
        ClockThemeApplier.Apply(_settings, CiIsDark);
        ApplyThemeFromSettings();

        LoadAllCourseNames();
        RestoreSelectedCourses();
        LoadCameraDevices();
        StartPreviewTimer();

        // CI 主题在明亮/黑暗间切换（跟随档）→ 重新套色，预览跟着变
        ActualThemeVariantChanged += (_, _) => ApplyThemeFromSettings();
    }

    private void StartPreviewTimer()
    {
        _previewTimer = new System.Timers.Timer(1000);
        _previewTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PreviewTime = DateTime.Now.ToString("HH:mm:ss");
        });
        _previewTimer.Start();
    }

    public string PreviewTime
    {
        get => _previewTime;
        set { _previewTime = value; OnPropertyChanged(nameof(PreviewTime)); }
    }

    public string PreviewCourseText
    {
        get => _previewCourseText;
        set { _previewCourseText = value; OnPropertyChanged(nameof(PreviewCourseText)); }
    }

    public int PreviewFontSize => Math.Max(12, _settings.ClockFontSize / 5);

    // ===== 主题 =====

    /// <summary>CI 当前是不是深色主题。设置页就在 CI 的窗口里，自身生效变体即 CI 的。</summary>
    private bool CiIsDark => ActualThemeVariant != ThemeVariant.Light;

    /// <summary>CI 数据目录（data\），插件配置目录往上三级。</summary>
    private string CiDataDir => Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));

    /// <summary>主题档位：0 = 跟随 ClassIsland，1 = 明亮，2 = 黑暗。</summary>
    public int ThemeModeIndex
    {
        get => _settings.ThemeMode;
        set
        {
            if (_settings.ThemeMode == value) return;
            _settings.ThemeMode = value;
            // 切主题即套用该主题的颜色默认值（之后仍可手动微调），预览立刻跟着变
            ClockThemeApplier.Apply(_settings, CiIsDark);
            OnPropertyChanged(nameof(ThemeModeIndex));
            NotifyAllColorsChanged();
            ApplyThemeFromSettings();
        }
    }

    /// <summary>预览用的界面画刷（整批随主题替换；界面绑 PreviewChrome.Xxx）。</summary>
    public ClockBrushes PreviewChrome { get; private set; } = ClockBrushes.From(ClockTheme.Dark);

    /// <summary>预览里时钟数字的字体（跟随 CI 主界面字体）。</summary>
    public FontFamily PreviewClockFontFamily { get; private set; } = FontFamily.Parse(CiMainFont.Fallback);

    /// <summary>与全屏时钟一致：数字用表格宽度（tnum）。</summary>
    public FontFeatureCollection PreviewClockFontFeatures { get; } =
        new FontFeatureCollection { FontFeature.Parse("tnum") };

    /// <summary>预览里当前档位文字（良好）的档位色。</summary>
    public IBrush PreviewSlotTextBrush { get; private set; } = Solid("#AED581");

    /// <summary>预览里五档标签的档位色（索引 0–4），与全屏时钟同一套配色。</summary>
    public IBrush[] PreviewSlotLabelBrushes { get; private set; } =
        LevelSlotCalculator.SlotTextColors(false).Select(c => Solid("#" + c.ToString("X8"))).ToArray();

    /// <summary>预览里五档标签非当前档位的淡化程度。</summary>
    public double PreviewSlotFadeOpacity { get; private set; } = 0.35;

    /// <summary>预览里「一般」计数与「正在记录」提示的黄色。</summary>
    public IBrush PreviewCountBrush { get; private set; } = Solid("#FFFF44");

    /// <summary>预览里预警条目的等级色（预览固定用黄色预警示例）。</summary>
    public IBrush PreviewAlertBrush { get; private set; } = Solid("#FFDD44");

    /// <summary>按当前设置重新解析主题：刷新预览画刷、语义色与时钟字体。</summary>
    private void ApplyThemeFromSettings()
    {
        PreviewChrome = ClockBrushes.From(ClockTheme.Resolve((ClockThemeMode)_settings.ThemeMode, CiIsDark));
        PreviewClockFontFamily = ReadCiClockFont();

        // 语义色（档位文字/计数/预警）在两套主题下取值不同，预览要与全屏时钟一致
        var light = PreviewChrome.IsLight;
        var slotText = LevelSlotCalculator.SlotTextColors(light);
        PreviewSlotTextBrush = Solid("#" + slotText[1].ToString("X8"));
        PreviewSlotLabelBrushes = slotText.Select(c => Solid("#" + c.ToString("X8"))).ToArray();
        PreviewSlotFadeOpacity = light ? 0.5 : 0.35;
        PreviewCountBrush = Solid(SemanticColors.CountNormalHex(light));
        PreviewAlertBrush = Solid(SemanticColors.AlertHex("黄色", light) ?? SemanticColors.CountNormalHex(light));

        OnPropertyChanged(nameof(PreviewChrome));
        OnPropertyChanged(nameof(PreviewClockFontFamily));
        OnPropertyChanged(nameof(PreviewSlotTextBrush));
        OnPropertyChanged(nameof(PreviewSlotLabelBrushes));
        OnPropertyChanged(nameof(PreviewSlotFadeOpacity));
        OnPropertyChanged(nameof(PreviewCountBrush));
        OnPropertyChanged(nameof(PreviewAlertBrush));
    }

    private static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex));

    private FontFamily ReadCiClockFont()
    {
        var font = CiMainFont.ReadFontString(CiDataDir) ?? CiMainFont.Fallback;
        try { return FontFamily.Parse(font); }
        catch { return FontFamily.Parse(CiMainFont.Fallback); }
    }

    /// <summary>5 个颜色设置的值/色块/hex 全部重新求值（主题切换、颜色被改写后调用）。</summary>
    private void NotifyAllColorsChanged()
    {
        OnPropertyChanged(nameof(BackgroundColorValue));
        OnPropertyChanged(nameof(BackgroundColorBrush));
        OnPropertyChanged(nameof(BackgroundColorHex));
        OnPropertyChanged(nameof(FontColorValue));
        OnPropertyChanged(nameof(FontColorBrush));
        OnPropertyChanged(nameof(FontColorHex));
        OnPropertyChanged(nameof(ProgressColorValue));
        OnPropertyChanged(nameof(ProgressColorBrush));
        OnPropertyChanged(nameof(ProgressColorHex));
        OnPropertyChanged(nameof(CourseInfoColorValue));
        OnPropertyChanged(nameof(CourseInfoColorBrush));
        OnPropertyChanged(nameof(CourseInfoColorHex));
        OnPropertyChanged(nameof(NoiseTitleColorValue));
        OnPropertyChanged(nameof(NoiseTitleColorBrush));
        OnPropertyChanged(nameof(NoiseTitleColorHex));
        OnPropertyChanged(nameof(RainColorValue));
        OnPropertyChanged(nameof(RainColorBrush));
        OnPropertyChanged(nameof(RainColorHex));
    }

    // 滑块绑定需要 double
    public double ClockFontSize
    {
        get => _settings.ClockFontSize;
        set { _settings.ClockFontSize = (int)value; OnPropertyChanged(nameof(ClockFontSize)); OnPropertyChanged(nameof(PreviewFontSize)); OnPropertyChanged(nameof(ClockFontSizeText)); }
    }

    /// <summary>底部居中的课程信息字号（改完进大屏时钟生效）。</summary>
    public double CourseInfoFontSize
    {
        get => _settings.CourseInfoFontSize;
        set { _settings.CourseInfoFontSize = (int)value; OnPropertyChanged(nameof(CourseInfoFontSize)); OnPropertyChanged(nameof(CourseInfoFontSizeText)); }
    }

    /// <summary>左下角计数字号（改完进大屏时钟生效）。</summary>
    public double CountFontSize
    {
        get => _settings.CountFontSize;
        set { _settings.CountFontSize = (int)value; OnPropertyChanged(nameof(CountFontSize)); OnPropertyChanged(nameof(CountFontSizeText)); }
    }

    private void LoadAllCourseNames()
    {
        try
        {
            var dataDir = CiDataDir;
            var settingsPath = Path.Combine(dataDir, "Settings.json");
            var profileFileName = "7.json";
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("SelectedProfile", out var sp))
                    profileFileName = sp.GetString() ?? "7.json";
            }
            var profilePath = Path.Combine(dataDir, "Profiles", profileFileName);
            if (!File.Exists(profilePath)) return;
            using var profileDoc = JsonDocument.Parse(File.ReadAllText(profilePath));
            if (!profileDoc.RootElement.TryGetProperty("Subjects", out var subjectsEl)) return;
            foreach (var subject in subjectsEl.EnumerateObject())
                if (subject.Value.TryGetProperty("Name", out var ne) && !string.IsNullOrWhiteSpace(ne.GetString()))
                    _allCourseNames.Add(ne.GetString()!);
        }
        catch { }
    }

    private void RestoreSelectedCourses()
    {
        SelectedCourses.Clear();
        foreach (var name in _settings.TargetCourseNames)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, name, this));
        if (SelectedCourses.Count == 0)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
    }

    public void AddCourse_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
        SaveCourses();
    }

    internal void OnCourseSelectionChanged() => SaveCourses();

    internal void RemoveCourse(CourseSelectionItem item)
    {
        SelectedCourses.Remove(item);
        if (SelectedCourses.Count == 0)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
        SaveCourses();
    }

    private void SaveCourses()
    {
        var names = SelectedCourses.Where(c => !string.IsNullOrWhiteSpace(c.SelectedName))
                                   .Select(c => c.SelectedName!).Distinct().ToList();
        _settings.TargetCourseNames.Clear();
        foreach (var n in names) _settings.TargetCourseNames.Add(n);
        Plugin.SaveSettings(); // 强制写盘
    }

    // ===== 摄像头占用 =====

    /// <summary>已枚举到的摄像头（勾选状态写回设置，见 <see cref="SaveCameraSelection"/>）。</summary>
    public ObservableCollection<CameraDeviceItem> CameraDevices { get; } = new();

    private string _cameraHint = "";

    /// <summary>设备列表下方的提示（枚举出错/没找到设备时才有内容）。</summary>
    public string CameraHint
    {
        get => _cameraHint;
        private set
        {
            _cameraHint = value;
            OnPropertyChanged(nameof(CameraHint));
            OnPropertyChanged(nameof(HasCameraHint));
        }
    }

    public bool HasCameraHint => _cameraHint.Length > 0;

    private CameraActivityService? CameraService
    {
        get
        {
            try { return Plugin.ServiceProvider?.GetService<CameraActivityService>(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 枚举本机摄像头填进列表。首次使用（还没填过默认值）时全部勾上——开箱即用，
    /// 用户之后取消勾选就真的不再提醒。
    /// </summary>
    private void LoadCameraDevices()
    {
        var service = CameraService;
        var devices = service?.EnumerateDevices() ?? new List<CameraDevice>();

        CameraDevices.Clear();
        if (devices.Count == 0)
        {
            CameraHint = "没有找到摄像头设备（外接摄像头插上后点「刷新设备列表」）。";
            return;
        }

        if (!_settings.CameraListFilled)
        {
            _settings.MonitoredCameras.Clear();
            foreach (var d in devices) _settings.MonitoredCameras.Add(d.Name);
            _settings.CameraListFilled = true;
            Plugin.SaveSettings();
        }

        foreach (var d in devices)
            CameraDevices.Add(new CameraDeviceItem(d.Name, IsMonitored(d.Name), this));

        CameraHint = service?.LastError ?? "";
    }

    private bool IsMonitored(string name)
        => _settings.MonitoredCameras.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>勾选/取消勾选设备后，把当前勾选写回设置（供 <see cref="CameraDeviceItem"/> 调用）。</summary>
    internal void SaveCameraSelection()
    {
        _settings.MonitoredCameras.Clear();
        foreach (var d in CameraDevices.Where(d => d.IsSelected)) _settings.MonitoredCameras.Add(d.Name);
        _settings.CameraListFilled = true;
        Plugin.SaveSettings(); // 强制写盘
    }

    public void RefreshCameras_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => LoadCameraDevices();

    public void TestButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { Plugin.ServiceProvider?.GetRequiredService<FullScreenClockViewModel>().Show(); }
        catch { }
    }

    // ===== 分贝阈值（滑块用对数刻度 0.001~1.0，也支持直接填数字） =====

    // 对数刻度：Slider 范围 -3~0 映射阈值 0.001~1.0（阈值 = 10^index）。
    // 低值区（0.04~0.14）在线性滑块上挤成一团，对数刻度才能精细拖动。
    // 递增联动：安静 < 良好 < 一般 < 吵闹 必须依次递增——后一级滑块的最小值
    // 就是前一级的当前值（对数刻度），保证永远有序。
    private const double ThresholdSliderMin = -3.0;
    private const double ThresholdSliderMax = 0.0;
    private const double ThresholdMin = 0.001;
    private const double ThresholdMax = 1.0;

    // 动态滑块最小值（对数刻度），供 XAML 上 良好/一般/吵闹 Slider 的 Minimum 绑定。
    // 例如安静设为 0.01，良好的可拖范围就变成 0.01~1.00。
    public double GoodThresholdSliderMin => Math.Log10(_settings.DecibelQuietThreshold);
    public double NormalThresholdSliderMin => Math.Log10(_settings.DecibelGoodThreshold);
    public double NoisyThresholdSliderMin => Math.Log10(_settings.DecibelNormalThreshold);

    public double DecibelQuietThresholdIndex
    {
        get => Math.Log10(_settings.DecibelQuietThreshold);
        set
        {
            _settings.DecibelQuietThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }
    public double DecibelGoodThresholdIndex
    {
        get => Math.Log10(_settings.DecibelGoodThreshold);
        set
        {
            _settings.DecibelGoodThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }
    public double DecibelNormalThresholdIndex
    {
        get => Math.Log10(_settings.DecibelNormalThreshold);
        set
        {
            _settings.DecibelNormalThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }
    public double DecibelNoisyThresholdIndex
    {
        get => Math.Log10(_settings.DecibelNoisyThreshold);
        set
        {
            _settings.DecibelNoisyThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }

    public string DecibelQuietThresholdText
    {
        get => _settings.DecibelQuietThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelQuietThresholdText), value, v => _settings.DecibelQuietThreshold = v); }
    }
    public string DecibelGoodThresholdText
    {
        get => _settings.DecibelGoodThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelGoodThresholdText), value, v => _settings.DecibelGoodThreshold = v); }
    }
    public string DecibelNormalThresholdText
    {
        get => _settings.DecibelNormalThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelNormalThresholdText), value, v => _settings.DecibelNormalThreshold = v); }
    }
    public string DecibelNoisyThresholdText
    {
        get => _settings.DecibelNoisyThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelNoisyThresholdText), value, v => _settings.DecibelNoisyThreshold = v); }
    }

    private void SetThreshold(string propName, string value, Action<double> setter)
    {
        if (double.TryParse(value, out var v))
        {
            setter(Math.Clamp(v, ThresholdMin, ThresholdMax));
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }

    /// <summary>
    /// 链式约束：强制 安静 ≤ 良好 ≤ 一般 ≤ 吵闹。后一级若低于前一级则被钳回前一级的值，
    /// 保证拖动/填数后阈值永远递增有序。
    /// </summary>
    private void ClampThresholds()
    {
        _settings.DecibelGoodThreshold = Math.Max(_settings.DecibelGoodThreshold, _settings.DecibelQuietThreshold);
        _settings.DecibelNormalThreshold = Math.Max(_settings.DecibelNormalThreshold, _settings.DecibelGoodThreshold);
        _settings.DecibelNoisyThreshold = Math.Max(_settings.DecibelNoisyThreshold, _settings.DecibelNormalThreshold);
    }

    /// <summary>
    /// 阈值相关属性统一通知：四个文本框、四个滑块位置、以及后一级滑块的 Minimum。
    /// </summary>
    private void NotifyThresholdsChanged()
    {
        OnPropertyChanged(nameof(DecibelQuietThresholdText));
        OnPropertyChanged(nameof(DecibelGoodThresholdText));
        OnPropertyChanged(nameof(DecibelNormalThresholdText));
        OnPropertyChanged(nameof(DecibelNoisyThresholdText));
        OnPropertyChanged(nameof(DecibelQuietThresholdIndex));
        OnPropertyChanged(nameof(DecibelGoodThresholdIndex));
        OnPropertyChanged(nameof(DecibelNormalThresholdIndex));
        OnPropertyChanged(nameof(DecibelNoisyThresholdIndex));
        OnPropertyChanged(nameof(GoodThresholdSliderMin));
        OnPropertyChanged(nameof(NormalThresholdSliderMin));
        OnPropertyChanged(nameof(NoisyThresholdSliderMin));
    }

    // ===== 记录参数 =====
    public double NoisySustainSeconds
    {
        get => _settings.NoisySustainSeconds;
        set
        {
            _settings.NoisySustainSeconds = value;
            OnPropertyChanged(nameof(NoisySustainSeconds));
            OnPropertyChanged(nameof(NoisySustainSecondsText));
        }
    }
    public string NoisySustainSecondsText => $"{_settings.NoisySustainSeconds:0.#} 秒";

    public double FallWindowSeconds
    {
        get => _settings.FallWindowSeconds;
        set
        {
            _settings.FallWindowSeconds = value;
            OnPropertyChanged(nameof(FallWindowSeconds));
            OnPropertyChanged(nameof(FallWindowSecondsText));
        }
    }
    public string FallWindowSecondsText => $"{_settings.FallWindowSeconds:0.#} 秒";

    /// <summary>音量采样间隔（秒）。</summary>
    public double SamplingIntervalSeconds
    {
        get => _settings.SamplingIntervalSeconds;
        set
        {
            _settings.SamplingIntervalSeconds = value;
            OnPropertyChanged(nameof(SamplingIntervalSeconds));
            OnPropertyChanged(nameof(SamplingIntervalSecondsText));
        }
    }
    public string SamplingIntervalSecondsText => $"{_settings.SamplingIntervalSeconds:0.##} 秒";

    public bool SkipFirst3Min
    {
        get => _settings.SkipFirst3Min;
        set
        {
            _settings.SkipFirst3Min = value;
            OnPropertyChanged(nameof(SkipFirst3Min));
            OnPropertyChanged(nameof(SkipFirstMinutes));
        }
    }

    public int SkipFirstMinutes
    {
        get => _settings.SkipFirstMinutes;
        set
        {
            _settings.SkipFirstMinutes = value;
            OnPropertyChanged(nameof(SkipFirstMinutes));
            OnPropertyChanged(nameof(SkipFirstMinutesText));
        }
    }
    public string SkipFirstMinutesText => $"{_settings.SkipFirstMinutes} 分钟";

    public bool EnableNoiseDebugLog
    {
        get => _settings.EnableNoiseDebugLog;
        set
        {
            _settings.EnableNoiseDebugLog = value;
            OnPropertyChanged(nameof(EnableNoiseDebugLog));
        }
    }

    public int LogRetentionDays
    {
        get => _settings.LogRetentionDays;
        set
        {
            _settings.LogRetentionDays = value;
            OnPropertyChanged(nameof(LogRetentionDays));
            OnPropertyChanged(nameof(LogRetentionDaysText));
        }
    }
    public string LogRetentionDaysText => $"{_settings.LogRetentionDays} 天";

    // 保留规则：两个单选按钮写同一个设置的两种取值（Avalonia 的 RadioButton 只能双向绑 bool，
    // 所以把「按大小」做成「非按时长」的取反视图，点未选中的那个才真正生效）
    public bool LogKeepByDuration
    {
        get => _settings.LogKeepByDuration;
        set
        {
            if (!value) return;
            _settings.LogKeepByDuration = true;
            NotifyLogKeepChanged();
        }
    }

    public bool LogKeepBySize
    {
        get => !_settings.LogKeepByDuration;
        set
        {
            if (!value) return;
            _settings.LogKeepByDuration = false;
            NotifyLogKeepChanged();
        }
    }

    private void NotifyLogKeepChanged()
    {
        OnPropertyChanged(nameof(LogKeepByDuration));
        OnPropertyChanged(nameof(LogKeepBySize));
        OnPropertyChanged(nameof(LogKeepLabel));
        OnPropertyChanged(nameof(LogKeepValue));
        OnPropertyChanged(nameof(LogKeepMinimum));
        OnPropertyChanged(nameof(LogKeepMaximum));
        OnPropertyChanged(nameof(LogKeepTick));
        OnPropertyChanged(nameof(LogKeepValueText));
    }

    /// <summary>两种规则共用一个滑块，滑的分别是「分钟」或「KB」。</summary>
    public double LogKeepValue
    {
        get => _settings.LogKeepByDuration ? _settings.LogKeepMinutes : _settings.LogKeepMinKb;
        set
        {
            if (_settings.LogKeepByDuration) _settings.LogKeepMinutes = (int)value;
            else _settings.LogKeepMinKb = (int)value;
            OnPropertyChanged(nameof(LogKeepValue));
            OnPropertyChanged(nameof(LogKeepValueText));
        }
    }

    public string LogKeepLabel => _settings.LogKeepByDuration ? "不足（分钟）" : "小于（KB）";
    public double LogKeepMinimum => _settings.LogKeepByDuration ? 1 : 10;
    public double LogKeepMaximum => _settings.LogKeepByDuration ? 120 : 3072;
    public double LogKeepTick => _settings.LogKeepByDuration ? 1 : 16;
    public string LogKeepValueText => _settings.LogKeepByDuration
        ? $"{_settings.LogKeepMinutes} 分钟" : $"{_settings.LogKeepMinKb} KB";

    /// <summary>打开存放调试日志的目录（就是插件配置目录）。</summary>
    public void OpenLogFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var dir = Plugin.ConfigFolder;
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public bool ShowDecibelMeter
    {
        get => _settings.ShowDecibelMeter;
        set { _settings.ShowDecibelMeter = value; OnPropertyChanged(nameof(ShowDecibelMeter)); }
    }

    public bool ShowCourseInfo
    {
        get => _settings.ShowCourseInfo;
        set { _settings.ShowCourseInfo = value; OnPropertyChanged(nameof(ShowCourseInfo)); }
    }

    public bool ShowBellTime
    {
        get => _settings.ShowBellTime;
        set { _settings.ShowBellTime = value; OnPropertyChanged(nameof(ShowBellTime)); }
    }

    public bool ShowNoisyCounter
    {
        get => _settings.ShowNoisyCounter;
        set { _settings.ShowNoisyCounter = value; OnPropertyChanged(nameof(ShowNoisyCounter)); }
    }

    // ===== 提醒面板开关 =====
    public bool ShowReminderPanel
    {
        get => _settings.ShowReminderPanel;
        set { _settings.ShowReminderPanel = value; OnPropertyChanged(nameof(ShowReminderPanel)); }
    }

    public bool ShowWeatherReminder
    {
        get => _settings.ShowWeatherReminder;
        set { _settings.ShowWeatherReminder = value; OnPropertyChanged(nameof(ShowWeatherReminder)); }
    }

    public bool ShowAlertsReminder
    {
        get => _settings.ShowAlertsReminder;
        set { _settings.ShowAlertsReminder = value; OnPropertyChanged(nameof(ShowAlertsReminder)); }
    }

    public bool ShowCountdownReminder
    {
        get => _settings.ShowCountdownReminder;
        set { _settings.ShowCountdownReminder = value; OnPropertyChanged(nameof(ShowCountdownReminder)); }
    }

    public bool ShowTextReminder
    {
        get => _settings.ShowTextReminder;
        set { _settings.ShowTextReminder = value; OnPropertyChanged(nameof(ShowTextReminder)); }
    }

    public bool ShowRainReminder
    {
        get => _settings.ShowRainReminder;
        set { _settings.ShowRainReminder = value; OnPropertyChanged(nameof(ShowRainReminder)); }
    }

    public bool ShowCameraReminder
    {
        get => _settings.ShowCameraReminder;
        set { _settings.ShowCameraReminder = value; OnPropertyChanged(nameof(ShowCameraReminder)); }
    }

    public bool ShowEmojiSubtitles
    {
        get => _settings.ShowEmojiSubtitles;
        set { _settings.ShowEmojiSubtitles = value; OnPropertyChanged(nameof(ShowEmojiSubtitles)); }
    }

    /// <summary>是否在 CI 托盘右键菜单显示「进入大屏时钟」入口。</summary>
    public bool ShowTrayMenuEntry
    {
        get => _settings.ShowTrayMenuEntry;
        set { _settings.ShowTrayMenuEntry = value; OnPropertyChanged(nameof(ShowTrayMenuEntry)); }
    }

    // ===== 外观颜色（HSV 颜色选择器，Avalonia.Controls.ColorPicker 双向绑定） =====

    // ColorPicker 的 Color 属性是 Avalonia.Media.Color（默认 TwoWay），
    // 这里把它与 PluginSettings 的 hex string 互相转换；Color.ToString() 输出 #AARRGGBB，Color.Parse 可读回。

    public Color RainColorValue
    {
        get => Color.Parse(_settings.RainColor);
        set
        {
            _settings.RainColor = value.ToString();
            OnPropertyChanged(nameof(RainColorValue));
            OnPropertyChanged(nameof(RainColorBrush));
            OnPropertyChanged(nameof(RainColorHex));
        }
    }

    public Color BackgroundColorValue
    {
        get => Color.Parse(_settings.BackgroundColor);
        set
        {
            _settings.BackgroundColor = value.ToString();
            OnPropertyChanged(nameof(BackgroundColorValue));
            OnPropertyChanged(nameof(BackgroundColorBrush));
            OnPropertyChanged(nameof(BackgroundColorHex));
        }
    }

    public Color FontColorValue
    {
        get => Color.Parse(_settings.FontColor);
        set
        {
            _settings.FontColor = value.ToString();
            OnPropertyChanged(nameof(FontColorValue));
            OnPropertyChanged(nameof(FontColorBrush));
            OnPropertyChanged(nameof(FontColorHex));
        }
    }

    public Color ProgressColorValue
    {
        get => Color.Parse(_settings.ProgressColor);
        set
        {
            _settings.ProgressColor = value.ToString();
            OnPropertyChanged(nameof(ProgressColorValue));
            OnPropertyChanged(nameof(ProgressColorBrush));
            OnPropertyChanged(nameof(ProgressColorHex));
        }
    }

    public Color CourseInfoColorValue
    {
        get => Color.Parse(_settings.CourseInfoColor);
        set
        {
            _settings.CourseInfoColor = value.ToString();
            OnPropertyChanged(nameof(CourseInfoColorValue));
            OnPropertyChanged(nameof(CourseInfoColorBrush));
            OnPropertyChanged(nameof(CourseInfoColorHex));
        }
    }

    public Color NoiseTitleColorValue
    {
        get => Color.Parse(_settings.NoiseTitleColor);
        set
        {
            _settings.NoiseTitleColor = value.ToString();
            OnPropertyChanged(nameof(NoiseTitleColorValue));
            OnPropertyChanged(nameof(NoiseTitleColorBrush));
            OnPropertyChanged(nameof(NoiseTitleColorHex));
        }
    }

    // 供 ColorPicker 自定义预览（大色块）绑定：色块填充色 + 显示用 #RRGGBB
    public IBrush BackgroundColorBrush => new SolidColorBrush(BackgroundColorValue);
    public string BackgroundColorHex => ToHexRgb(BackgroundColorValue);
    public IBrush FontColorBrush => new SolidColorBrush(FontColorValue);
    public string FontColorHex => ToHexRgb(FontColorValue);
    public IBrush ProgressColorBrush => new SolidColorBrush(ProgressColorValue);
    public string ProgressColorHex => ToHexRgb(ProgressColorValue);
    public IBrush CourseInfoColorBrush => new SolidColorBrush(CourseInfoColorValue);
    public string CourseInfoColorHex => ToHexRgb(CourseInfoColorValue);
    public IBrush NoiseTitleColorBrush => new SolidColorBrush(NoiseTitleColorValue);
    public string NoiseTitleColorHex => ToHexRgb(NoiseTitleColorValue);

    /// <summary>降水提醒色：预览里的降雨行与设置页色块都用它。</summary>
    public IBrush RainColorBrush => new SolidColorBrush(RainColorValue);
    public string RainColorHex => ToHexRgb(RainColorValue);

    /// <summary>Color → #RRGGBB（去掉 alpha，设置界面显示友好）。</summary>
    private static string ToHexRgb(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public string ClockFontSizeText => $"{_settings.ClockFontSize}px";

    public string CourseInfoFontSizeText => $"{_settings.CourseInfoFontSize}px";

    public string CountFontSizeText => $"{_settings.CountFontSize}px";

    public new event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>设置页里一个摄像头的勾选项。勾选变化立刻写回设置（大屏时钟那边每秒会重算提醒）。</summary>
public class CameraDeviceItem : INotifyPropertyChanged
{
    private readonly SettingsPage _parent;
    private bool _isSelected;

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _parent.SaveCameraSelection();
        }
    }

    public CameraDeviceItem(string name, bool isSelected, SettingsPage parent)
    {
        Name = name;
        _isSelected = isSelected;
        _parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class CourseSelectionItem : INotifyPropertyChanged
{
    private readonly SettingsPage _parent;
    private string? _selectedName;
    public List<string> AllCourses { get; }
    public ICommand RemoveCommand { get; }

    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            if (_selectedName == value) return;
            _selectedName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedName)));
            if (!string.IsNullOrWhiteSpace(value)) _parent.OnCourseSelectionChanged();
        }
    }

    public CourseSelectionItem(List<string> allCourses, string? initial, SettingsPage parent)
    {
        _parent = parent; AllCourses = allCourses; _selectedName = initial;
        RemoveCommand = new RelayCommand(() => parent.RemoveCourse(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

