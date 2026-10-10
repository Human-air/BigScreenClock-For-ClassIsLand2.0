using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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
    private string _progressColor = "#4FC3F7";
    private string _courseInfoColor = "#88ccff";
    private string _noiseTitleColor = "#bbffffff";
    private string _rainColor = "#7FD4FF";
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
    private double _clockPositionPercent = 30;      // 时钟中心在主体区高度的 30% 处
    private double _volumePositionPercent = 80;     // 音量条中心在主体区高度的 80% 处
    private int _courseInfoFontSize = 28;
    private int _countFontSize = 24;
    private int _themeMode;
    private int _appliedPaletteVariant = ClockTheme.VariantUnknown;
    private bool _backgroundEnabled;
    private bool _backgroundSectionExpanded;
    private int _backgroundThemeMode;
    private string _backgroundImageFile = "";
    private int _backgroundImageStretch;
    private double _backgroundDim;
    private int _glassMode;
    private double _glassOpacity = 35;
    private double _glassBlurRadius = 24;
    private double _glassRefraction = 50;
    private double _glassFrost = 45;
    private string _windowTitle = "大屏时钟";
    private bool _showReminderPanel = true;
    private bool _showWeatherReminder = true;
    private bool _showAlertsReminder = true;
    private bool _showCountdownReminder = true;
    private bool _showTextReminder = true;
    private bool _showRainReminder = true;
    private bool _showEmojiSubtitles = true;
    private bool _showTrayMenuEntry = true;
    private bool _showCameraReminder = true;
    private double _cameraAlertFontSize = MinCameraAlertFontSize;
    private bool _showEarthquakeReminder = true;
    private bool _cameraListFilled;
    private ObservableCollection<string> _monitoredCameras = new();

    public PluginSettings()
    {
        _targetCourseNames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TargetCourseNames));
        _monitoredCameras.CollectionChanged += (_, _) => OnPropertyChanged(nameof(MonitoredCameras));
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
    /// 主题：0 = 跟随 ClassIsland，1 = 明亮，2 = 黑暗，3 = 自动（按背景图明暗，见 <see cref="ClockThemeMode"/>）。
    /// 切换主题会把颜色换成该主题那套（见 <see cref="LightColors"/> / <see cref="DarkColors"/>）。
    /// </summary>
    public int ThemeMode
    {
        get => _themeMode;
        set { _themeMode = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上面 6 个颜色当前是哪个主题那一套（<see cref="ClockTheme.VariantLight"/> / <see cref="ClockTheme.VariantDark"/>）。
    /// 用来判断「生效主题变了没有」：只有变了才换色，否则用户手改的颜色会被反复覆盖。
    /// 初值取「未知」——老配置里没有这个字段，正好用它认出「还没进过主题系统」的配置来做升级迁移
    /// （见 <see cref="EveningSelfStudyClock.Helpers.ClockThemeApplier.MigrateLegacySettings"/>）。
    /// </summary>
    public int AppliedPaletteVariant
    {
        get => _appliedPaletteVariant;
        set { _appliedPaletteVariant = value; OnPropertyChanged(); }
    }

    // ===== 背景图片与玻璃底 =====
    // 选中的图片会被复制一份到插件配置目录，这里只记副本的文件名（原图被挪走/删掉也不影响）。
    // 没选图片时下面这些项全部不生效，外观与以前逐像素一致。

    /// <summary>
    /// 背景总开关：关着的时候下面这一整组（背景图 + 玻璃 + 背景主题）全部不生效，
    /// 外观与「没有这套功能」时逐像素一致，主题也仍由外观里那项决定。
    ///
    /// 默认关闭 —— 升级上来的配置没有这个字段，默认开着的话会在用户没点过任何东西的情况下
    /// 把他的主题换成背景那套。
    /// </summary>
    public bool BackgroundEnabled
    {
        get => _backgroundEnabled;
        set { _backgroundEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 设置页「背景设置」那一组展开着没有。
    /// 纯粹是界面状态，但不存下来的话，每次重开 CI、或者跳到别的设置页再回来，
    /// 它都会自己折回去，调个参数还得重新点开一次。
    /// </summary>
    public bool BackgroundSectionExpanded
    {
        get => _backgroundSectionExpanded;
        set { _backgroundSectionExpanded = value; OnPropertyChanged(); }
    }

    private bool _adjustingLayout;

    /// <summary>
    /// 设置页正在拖「位置 / 字号」这类连着改布局的滑块（松手 300 毫秒后自动归假）。
    ///
    /// 拖动期间大屏与预览的玻璃整层收起。原因见 <see cref="Views.BackdropLayer"/>：玻璃那层每帧
    /// 要画十二块圆角投影 + 整屏折射图 + 整屏清晰图，Skia 每帧出的好几个整屏中间面排着队释放不掉，
    /// 内存顶着 CI 的看门狗上限走（现象是「一调位置大小就闪退」），画面本身也只是一片拖影。
    ///
    /// 纯界面状态，不存盘；<see cref="Plugin.Initialize"/> 里另外兜底归零一次，
    /// 免得万一被序列化下来，下次启动玻璃就一直是收起的。
    /// </summary>
    [JsonIgnore]
    public bool AdjustingLayout
    {
        get => _adjustingLayout;
        set
        {
            if (_adjustingLayout == value) return;
            _adjustingLayout = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 背景主题档位（0 自动 / 1 暗底白字 / 2 亮底黑字，见 <see cref="BackgroundThemeMode"/>）。
    /// 只在「背景」开关打开时生效，此时 <see cref="ThemeMode"/> 整项失效。
    /// </summary>
    public int BackgroundThemeMode
    {
        get => _backgroundThemeMode;
        set { _backgroundThemeMode = Math.Clamp(value, 0, 2); OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景图片文件名（相对插件配置目录）。空 = 仍用纯色底。
    /// </summary>
    public string BackgroundImageFile
    {
        get => _backgroundImageFile;
        set { _backgroundImageFile = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景图片填充方式：0 = 铺满裁切，1 = 完整显示（留白），2 = 拉伸。
    /// </summary>
    public int BackgroundImageStretch
    {
        get => _backgroundImageStretch;
        set { _backgroundImageStretch = Math.Clamp(value, 0, 2); OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景明暗遮罩：负值压暗、正值提亮（-100 ~ 100），0 = 不改。
    /// 花壁纸上文字读不清时用它压一压。
    /// </summary>
    public double BackgroundDim
    {
        get => _backgroundDim;
        set { _backgroundDim = Math.Clamp(value, -100, 100); OnPropertyChanged(); }
    }

    /// <summary>
    /// 玻璃底：0 = 关闭，1 = 半透明，2 = 磨砂模糊，3 = 真•高光折射。
    /// </summary>
    public int GlassMode
    {
        get => _glassMode;
        set { _glassMode = Math.Clamp(value, 0, 3); OnPropertyChanged(); }
    }

    /// <summary>
    /// 玻璃浓度：玻璃那层底色（黑暗主题=黑、明亮主题=白）的不透明度百分比。
    /// </summary>
    public double GlassOpacity
    {
        get => _glassOpacity;
        set { _glassOpacity = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    /// <summary>
    /// 磨砂颗粒度（模糊半径），决定糊底图本身糊得多细。仅「磨砂」档用它。
    /// </summary>
    public double GlassBlurRadius
    {
        get => _glassBlurRadius;
        set { _glassBlurRadius = Math.Clamp(value, 0, 64); OnPropertyChanged(); }
    }

    /// <summary>
    /// 折射强度，仅「真•高光折射」档生效。越大边缘把画面推得越弯。
    /// </summary>
    public double GlassRefraction
    {
        get => _glassRefraction;
        set { _glassRefraction = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    /// <summary>
    /// 雾面浓度，仅「真•高光折射」档生效。清晰画面里掺多少糊底图：0 = 纯清玻璃，100 = 全糊。
    /// 背后那张花壁纸原样透过去会把压在上面的字淹掉，这条给可读性兜底。
    /// </summary>
    public double GlassFrost
    {
        get => _glassFrost;
        set { _glassFrost = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
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
    /// 降水提醒颜色 (HEX)：有降雨提醒、或当前天气是雨雪时，相应文字与图标用这个色（默认蓝）。
    /// </summary>
    public string RainColor
    {
        get => _rainColor;
        set { _rainColor = value; OnPropertyChanged(); }
    }

    // ===== 两套主题各记一套颜色 =====
    // 切主题时上面这 6 个颜色会被换掉，所以换之前先把当前这套存回它自己的槽位、再读另一套
    // （见 ClockThemeApplier），这样切回来还是你调过的色，而不是又被打回该主题默认值。

    /// <summary>明亮主题那一套颜色（用户调过就记在这里，切回明亮时恢复）。</summary>
    public ThemeColorSet LightColors { get; set; } = ThemeColorSet.FromPalette(ClockTheme.Light);

    /// <summary>黑暗主题那一套颜色。</summary>
    public ThemeColorSet DarkColors { get; set; } = ThemeColorSet.FromPalette(ClockTheme.Dark);

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
    /// 课程信息字号 (px)：底部居中的「当前课程为 …」那行
    /// </summary>
    public int CourseInfoFontSize
    {
        get => _courseInfoFontSize;
        set { _courseInfoFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 计数字号 (px)：左下角吵闹计数与「正在记录」提示
    /// </summary>
    public int CountFontSize
    {
        get => _countFontSize;
        set { _countFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 时钟垂直位置：时钟中心落在主体区（去掉顶部提醒行与底部各行后剩下的那块）高度的百分之几。
    /// 0 = 最上，100 = 最下；实际按该块的实测高度做了钳制，滑块拉到两端也不会跑到屏幕外。
    /// </summary>
    public double ClockPositionPercent
    {
        get => _clockPositionPercent;
        set { _clockPositionPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    /// <summary>
    /// 音量条垂直位置：同 <see cref="ClockPositionPercent"/>，音量条中心在主体区高度的百分之几。
    /// 与时钟各调各的，互不牵连。
    /// </summary>
    public double VolumePositionPercent
    {
        get => _volumePositionPercent;
        set { _volumePositionPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
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

    /// <summary>是否显示「摄像头使用中」提醒（设备被程序占用时，在预警那一行出一条）。</summary>
    public bool ShowCameraReminder
    {
        get => _showCameraReminder;
        set { _showCameraReminder = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 「摄像头使用中」那一条的字号。可调范围就是「默认字号 ~ 天气提醒的字号」——
    /// 比默认小会更看不清，比天气还大就压过其它提醒了。
    /// </summary>
    public double CameraAlertFontSize
    {
        get => _cameraAlertFontSize;
        set
        {
            _cameraAlertFontSize = Math.Clamp(value, MinCameraAlertFontSize, MaxCameraAlertFontSize);
            OnPropertyChanged();
        }
    }

    /// <summary>摄像头提醒字号下限：不设这个设置项时那条的字号。</summary>
    public const double MinCameraAlertFontSize = AlertInfo.DefaultFontSize;

    /// <summary>摄像头提醒字号上限：顶部天气温度提醒的字号。</summary>
    public const double MaxCameraAlertFontSize = 19;

    /// <summary>
    /// 是否显示地震提醒（读「地震预警」插件的实时状态，在时钟正上方弹出，见 <see cref="Services.EarthquakeReader"/>）。
    /// 该插件没装 / 没启用时这一项不起作用。
    /// </summary>
    public bool ShowEarthquakeReminder
    {
        get => _showEarthquakeReminder;
        set { _showEarthquakeReminder = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 要提醒的摄像头设备名（设置页里勾的，见 <see cref="CameraAlertBuilder"/>）。空 = 不提醒。
    /// </summary>
    public ObservableCollection<string> MonitoredCameras
    {
        get => _monitoredCameras;
        set { _monitoredCameras = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 设备列表是否已经按本机摄像头「填过默认勾选」。首次打开设置页时把枚举到的设备全部勾上，
    /// 之后用户取消全部勾选就真的不提醒了（不再把空列表当成「还没选过」）。
    /// </summary>
    public bool CameraListFilled
    {
        get => _cameraListFilled;
        set { _cameraListFilled = value; OnPropertyChanged(); }
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
