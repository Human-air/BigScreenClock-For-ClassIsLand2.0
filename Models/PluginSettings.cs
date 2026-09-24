using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClassIsland.Core.Helpers;

namespace EveningSelfStudyClock.Models;

public class PluginSettings : INotifyPropertyChanged
{
    private ObservableCollection<string> _targetCourseNames = new() { "晚自习" };
    private double _decibelQuietThreshold = 0.005;
    private double _decibelGoodThreshold = 0.015;
    private double _decibelNormalThreshold = 0.04;
    private double _decibelNoisyThreshold = 0.08;
    private string _backgroundColor = "#000000";
    private string _fontColor = "#ffffff";
    private string _accentColor = "#4CAF50";
    private string _progressColor = "#4FC3F7";
    private string _courseInfoColor = "#88ccff";
    private string _noiseTitleColor = "#bbffffff";
    private bool _showDecibelMeter = true;
    private bool _showCourseInfo = true;
    private bool _showBellTime = true;
    private bool _showNoisyCounter = true;
    private bool _skipFirst3Min = true;
    private int _skipFirstMinutes = 3;
    private double _noisySustainSeconds = 1.0;
    private double _fallWindowSeconds = 5.0;
    private double _samplingIntervalSeconds = 0.2;
    private bool _enableNoiseDebugLog;
    private int _logRetentionDays = 3;
    private bool _logKeepByDuration = true;
    private int _logKeepMinutes = 10;
    private int _logKeepMinKb = 100;
    private int _clockFontSize = 180;
    private string _windowTitle = "大屏时钟";
    private bool _showReminderPanel = true;
    private bool _showWeatherReminder = true;
    private bool _showAlertsReminder = true;
    private bool _showCountdownReminder = true;
    private bool _showTextReminder = true;
    private bool _showRainReminder = true;
    private bool _showEmojiSubtitles = true;
    private bool _showTrayMenuEntry = true;

    public PluginSettings()
    {
        _targetCourseNames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TargetCourseNames));
    }

    /// <summary>
    /// 触发全屏时钟的课程名称列表（精确匹配：名称完全相同才触发，见 <see cref="CourseMatch"/>）
    /// </summary>
    public ObservableCollection<string> TargetCourseNames
    {
        get => _targetCourseNames;
        set { _targetCourseNames = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 安静阈值 (RMS)，低于此值为"安静"
    /// </summary>
    public double DecibelQuietThreshold
    {
        get => _decibelQuietThreshold;
        set { _decibelQuietThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 良好阈值 (RMS)，低于此值为"良好"
    /// </summary>
    public double DecibelGoodThreshold
    {
        get => _decibelGoodThreshold;
        set { _decibelGoodThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 一般阈值 (RMS)，低于此值为"一般"
    /// </summary>
    public double DecibelNormalThreshold
    {
        get => _decibelNormalThreshold;
        set { _decibelNormalThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 吵闹阈值 (RMS)，高于此值为"吵闹"，以上为"嘈杂"
    /// </summary>
    public double DecibelNoisyThreshold
    {
        get => _decibelNoisyThreshold;
        set { _decibelNoisyThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景颜色 (HEX)
    /// </summary>
    public string BackgroundColor
    {
        get => _backgroundColor;
        set { _backgroundColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 文字颜色 (HEX)
    /// </summary>
    public string FontColor
    {
        get => _fontColor;
        set { _fontColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 强调色 (HEX)，用于分贝条和高亮
    /// </summary>
    public string AccentColor
    {
        get => _accentColor;
        set { _accentColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 课程进度条颜色 (HEX)。已进行部分用此色（不透明），未进行部分用半透明派生。
    /// </summary>
    public string ProgressColor
    {
        get => _progressColor;
        set { _progressColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 当前课程信息文字颜色 (HEX)。
    /// </summary>
    public string CourseInfoColor
    {
        get => _courseInfoColor;
        set { _courseInfoColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 教室音量条标题文字颜色 (HEX)。
    /// </summary>
    public string NoiseTitleColor
    {
        get => _noiseTitleColor;
        set { _noiseTitleColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示教室分贝
    /// </summary>
    public bool ShowDecibelMeter
    {
        get => _showDecibelMeter;
        set { _showDecibelMeter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示课程信息
    /// </summary>
    public bool ShowCourseInfo
    {
        get => _showCourseInfo;
        set { _showCourseInfo = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 课程信息里是否附带「实际打铃」时间（课表时间加时间偏移后的真实时间）。
    /// 关掉则只显示课表时间那一行，字短些。
    /// </summary>
    public bool ShowBellTime
    {
        get => _showBellTime;
        set { _showBellTime = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示吵闹计数
    /// </summary>
    public bool ShowNoisyCounter
    {
        get => _showNoisyCounter;
        set { _showNoisyCounter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上课前 3 分钟是否不计数
    /// </summary>
    public bool SkipFirst3Min
    {
        get => _skipFirst3Min;
        set { _skipFirst3Min = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上课初期保护时长（分钟）
    /// </summary>
    public int SkipFirstMinutes
    {
        get => _skipFirstMinutes;
        set { _skipFirstMinutes = Math.Clamp(value, 0, 10); OnPropertyChanged(); }
    }

    /// <summary>
    /// 起算底线（秒）：一段噪音内「有效时长」（处于一般/吵闹的累计时间）达到该值，
    /// 段结束才记一次；不足的短段（喷嚏/咳嗽）丢弃。默认 1 秒。
    /// </summary>
    public double NoisySustainSeconds
    {
        get => _noisySustainSeconds;
        set { _noisySustainSeconds = Math.Clamp(value, 0.5, 5.0); OnPropertyChanged(); }
    }

    /// <summary>
    /// 回落窗口（秒）：音量掉到安静/良好后，回落不超过该值则与前面并成一段（短暂停顿不断段）；
    /// 超过该值段才结束、记一次。默认 5 秒，范围 0~15。
    /// </summary>
    public double FallWindowSeconds
    {
        get => _fallWindowSeconds;
        set { _fallWindowSeconds = Math.Clamp(value, 0, 15); OnPropertyChanged(); }
    }

    /// <summary>
    /// 音量采样间隔（秒）：麦克风每采集这么长一段音频才算一次读数（也是「记录阈值」的频率）。
    /// 间隔越大采样越稀——越省性能、越不拖日志和大屏时钟，但读数越不跟手；条靠动画补平滑。
    /// 默认 0.2 秒（每秒采样 5 次）。范围 0.1~1.0 秒。
    /// </summary>
    public double SamplingIntervalSeconds
    {
        get => _samplingIntervalSeconds;
        set { _samplingIntervalSeconds = Math.Clamp(value, 0.1, 1.0); OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否输出噪音调试日志（NoiseDebugLog.csv），用于收集真实数据校准阈值
    /// </summary>
    public bool EnableNoiseDebugLog
    {
        get => _enableNoiseDebugLog;
        set { _enableNoiseDebugLog = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 调试日志保留天数：超过该天数的旧日志自动清理。默认 3 天，范围 1~5。
    /// </summary>
    public int LogRetentionDays
    {
        get => _logRetentionDays;
        set { _logRetentionDays = Math.Clamp(value, 1, 5); OnPropertyChanged(); }
    }

    /// <summary>
    /// 调试日志的保留规则：true = 按记录时长，false = 按文件大小。
    /// 打开开关后又进进出出大屏时钟会留下一堆几秒钟的废记录，两种规则都是为了把它们删掉。
    /// </summary>
    public bool LogKeepByDuration
    {
        get => _logKeepByDuration;
        set { _logKeepByDuration = value; OnPropertyChanged(); }
    }

    /// <summary>按记录时长保留（分钟）：记录跨度不足该值的日志文件在停止记录时删除。默认 10 分钟，范围 1~120。</summary>
    public int LogKeepMinutes
    {
        get => _logKeepMinutes;
        set { _logKeepMinutes = Math.Clamp(value, 1, 120); OnPropertyChanged(); }
    }

    /// <summary>按文件大小保留（KB）：小于该值的日志文件在停止记录时删除。默认 100KB，范围 10~3072。</summary>
    public int LogKeepMinKb
    {
        get => _logKeepMinKb;
        set { _logKeepMinKb = Math.Clamp(value, 10, 3072); OnPropertyChanged(); }
    }

    /// <summary>
    /// 时钟字号 (px)
    /// </summary>
    public int ClockFontSize
    {
        get => _clockFontSize;
        set { _clockFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 窗口标题
    /// </summary>
    public string WindowTitle
    {
        get => _windowTitle;
        set { _windowTitle = value; OnPropertyChanged(); }
    }

    /// <summary>是否显示左上角「提醒」面板（总开关；面板同时要求至少一个子项有内容）。</summary>
    public bool ShowReminderPanel
    {
        get => _showReminderPanel;
        set { _showReminderPanel = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示天气（跟随 CI 的天气缓存，不用 CI 的实时预报判断）。</summary>
    public bool ShowWeatherReminder
    {
        get => _showWeatherReminder;
        set { _showWeatherReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示天气预警（区级统一发布，跟随 CI 的预警缓存）。</summary>
    public bool ShowAlertsReminder
    {
        get => _showAlertsReminder;
        set { _showAlertsReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示倒计时（跟随 CI 主界面组件里的倒计时组件）。</summary>
    public bool ShowCountdownReminder
    {
        get => _showCountdownReminder;
        set { _showCountdownReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示文本框（跟随 CI 主界面组件里的文本组件）。</summary>
    public bool ShowTextReminder
    {
        get => _showTextReminder;
        set { _showTextReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示「多久下雨」降雨提醒（跟随 CI 的分钟级/逐小时降水数据）。</summary>
    public bool ShowRainReminder
    {
        get => _showRainReminder;
        set { _showRainReminder = value; OnPropertyChanged(); }
    }

    /// <summary>是否显示颜文字副标题（气温旁「记得防晒 (っ'ω')ﾉ」、日期旁「别熬啦 (｡-_-｡)」等趣味提醒；关闭则只留正文字幕）。</summary>
    public bool ShowEmojiSubtitles
    {
        get => _showEmojiSubtitles;
        set { _showEmojiSubtitles = value; OnPropertyChanged(); }
    }

    /// <summary>是否在 CI 托盘的右键菜单里显示「进入大屏时钟」入口（关闭则从菜单移除）。</summary>
    public bool ShowTrayMenuEntry
    {
        get => _showTrayMenuEntry;
        set { _showTrayMenuEntry = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 检查课程名是否在目标列表中（精确匹配，规则见 <see cref="CourseMatch"/>）
    /// </summary>
    public bool IsTargetCourse(string? courseName)
        => CourseMatch.IsMatch(courseName, TargetCourseNames);
}
