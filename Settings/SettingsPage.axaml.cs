using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Timers;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
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
        LoadAllCourseNames();
        RestoreSelectedCourses();
        StartPreviewTimer();
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

    // 滑块绑定需要 double
    public double ClockFontSize
    {
        get => _settings.ClockFontSize;
        set { _settings.ClockFontSize = (int)value; OnPropertyChanged(nameof(ClockFontSize)); OnPropertyChanged(nameof(PreviewFontSize)); OnPropertyChanged(nameof(ClockFontSizeText)); }
    }

    private void LoadAllCourseNames()
    {
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

    public int LogSizeLimitKb
    {
        get => _settings.LogSizeLimitKb;
        set
        {
            _settings.LogSizeLimitKb = value;
            OnPropertyChanged(nameof(LogSizeLimitKb));
            OnPropertyChanged(nameof(LogSizeLimitKbText));
        }
    }
    public string LogSizeLimitKbText => $"{_settings.LogSizeLimitKb} KB";

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

    public Color AccentColorValue
    {
        get => Color.Parse(_settings.AccentColor);
        set
        {
            _settings.AccentColor = value.ToString();
            OnPropertyChanged(nameof(AccentColorValue));
            OnPropertyChanged(nameof(AccentColorBrush));
            OnPropertyChanged(nameof(AccentColorHex));
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
    public IBrush AccentColorBrush => new SolidColorBrush(AccentColorValue);
    public string AccentColorHex => ToHexRgb(AccentColorValue);
    public IBrush ProgressColorBrush => new SolidColorBrush(ProgressColorValue);
    public string ProgressColorHex => ToHexRgb(ProgressColorValue);
    public IBrush CourseInfoColorBrush => new SolidColorBrush(CourseInfoColorValue);
    public string CourseInfoColorHex => ToHexRgb(CourseInfoColorValue);
    public IBrush NoiseTitleColorBrush => new SolidColorBrush(NoiseTitleColorValue);
    public string NoiseTitleColorHex => ToHexRgb(NoiseTitleColorValue);

    /// <summary>Color → #RRGGBB（去掉 alpha，设置界面显示友好）。</summary>
    private static string ToHexRgb(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public string ClockFontSizeText => $"{_settings.ClockFontSize}px";

    public new event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

