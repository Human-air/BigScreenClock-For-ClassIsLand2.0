using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Timers;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.ViewModels;
using EveningSelfStudyClock.Views;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.Settings;

[SettingsPageInfo("com.bigscreen.clock.settings", "大屏时钟", SettingsPageCategory.External)]
// 全宽页：不加这个，CI 会把页面标题限制在 960 宽并居中，窗口一变大标题就往中间跑，
// 和下面铺满的设置卡片对不齐。加了这个标题固定靠左（左距与卡片一致），缩放窗口不再移动。
[FullWidthPage]
public partial class SettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly PluginSettings _settings;
    private List<string> _allCourseNames = new();

    public ObservableCollection<CourseSelectionItem> SelectedCourses { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        _settings = Plugin.Settings!;
        _earthquakePluginAvailable = CheckEarthquakePlugin();
        try { _clockViewModel = Plugin.ServiceProvider?.GetService<FullScreenClockViewModel>(); } catch { }

        // 这几项订阅的都是**页面外面**的对象（设置、ViewModel），页面一收起就必须摘掉：
        // CI 每打开一次设置页都可能新建一个页面，不摘的话旧页面永远活着，还接着干 UI 活。
        Loaded += (_, _) => { HookPositionLimits(); HookLayoutSliders(); SubscribeSharedEvents(); };
        Unloaded += (_, _) => { UnhookPositionLimits(); StopLayoutAdjust(); UnsubscribeSharedEvents(); };

        DataContext = this;

        // 预览按当前生效主题呈现：跟随档下 CI 的亮暗就是这里的亮暗
        ClockThemeApplier.Apply(_settings, CiIsDark);
        ApplyThemeFromSettings();
        // 上面这一步刚换过颜色，而 DataContext 早一步就赋好了、绑定已经按旧颜色求过一次值：
        // 不在这里补通知，几个色块会停在切换前那套颜色上（色块与右边 hex 对不上）
        NotifyAllColorsChanged();

        LoadAllCourseNames();
        RestoreSelectedCourses();
        LoadCameraDevices();
        StartPreviewSync();

        // CI 主题在明亮/黑暗间切换（跟随档）→ 重新套色，预览跟着变
        ActualThemeVariantChanged += (_, _) => ApplyThemeFromSettings();
    }

    /// <summary>
    /// 挂 / 摘「页面外面那些对象」的订阅。配对调用的地方在 Loaded / Unloaded 里。
    /// 先减后加，免得 CI 复用页面时（Loaded 来了两遍）叠成两份。
    /// </summary>
    private void SubscribeSharedEvents()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void UnsubscribeSharedEvents()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        // 页面都收起来了，没必要再为「设置变了」去刷新看不见的预览
        _previewSyncTimer?.Stop();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 时钟/音量条的位置会被大屏那边按限位改写（写回实际位置），滑块要跟着动
        if (e.PropertyName is nameof(PluginSettings.ClockPositionPercent))
        {
            OnPropertyChanged(nameof(ClockPositionPercent));
            OnPropertyChanged(nameof(ClockPositionPercentText));
        }
        else if (e.PropertyName is nameof(PluginSettings.VolumePositionPercent))
        {
            OnPropertyChanged(nameof(VolumePositionPercent));
            OnPropertyChanged(nameof(VolumePositionPercentText));
        }

        SchedulePreviewSync();
    }

    // ===== 右栏的实时预览 =====
    //
    // 预览放的是大屏那面墙本身（ClockSurface），只是等比缩小塞进预览框 —— 布局、字号、
    // 颜色、时钟与音量条的位置全出自同一份 XAML，不存在「预览一套、大屏另一套」。
    // 尺寸按大屏那块屏幕取，预览框整体缩放，所以看到的就是真实比例的观感。

    /// <summary>预览绑的大屏 ViewModel：与大屏用的是 DI 里的同一个单例，
    /// 所以预览里的数据、颜色、字号就是大屏当时真正会显示的那一套。</summary>
    public FullScreenClockViewModel? PreviewVm => _clockViewModel;

    private Size _previewSize = new(1920, 1080);

    /// <summary>预览用的逻辑宽度（= 大屏那块屏幕的逻辑宽度；全屏窗口的 Bounds 就是这个值）。</summary>
    public double PreviewWidth => _previewSize.Width;

    /// <summary>预览用的逻辑高度。</summary>
    public double PreviewHeight => _previewSize.Height;

    /// <summary>
    /// 按大屏所在显示器的长宽比定预览尺寸。用逻辑尺寸（物理 ÷ 缩放）而不是物理像素：
    /// 大屏窗口的实测宽度就是逻辑值，字号、边距全是按它算的，预览要跟它同一把尺子。
    /// </summary>
    private void UpdatePreviewSize()
    {
        try
        {
            // 大屏是开在 CI 主窗口所在那块屏幕上的，跟着它取，多屏时预览才对得上
            var top = TopLevel.GetTopLevel(this);
            var screens = top?.Screens;
            var screen = screens?.Primary;
            if (top is Window window && screens?.ScreenFromWindow(window) is { } own)
                screen = own;

            if (screen is { } s && s.Bounds.Width > 0 && s.Bounds.Height > 0)
            {
                var scale = s.Scaling > 0 ? s.Scaling : 1.0;
                _previewSize = new Size(s.Bounds.Width / scale, s.Bounds.Height / scale);
            }
        }
        catch
        {
            _previewSize = new Size(1920, 1080);   // 取不到就按最常见的班级大屏来
        }

        OnPropertyChanged(nameof(PreviewWidth));
        OnPropertyChanged(nameof(PreviewHeight));
    }

    /// <summary>
    /// 预览的刷新：尺寸取一次 + 让 ViewModel 把「平时只在进大屏时才同步一次」的那几项
    /// （字号、颜色、音量条/课程信息开关）从设置里重读一遍。
    ///
    /// 防抖 200 毫秒：拖字号/颜色滑块会连着触发很多次，每次都去读组件与天气缓存太浪费；
    /// 通知只在 UI 线程上发，所以这里用 DispatcherTimer 而不是 System.Timers.Timer。
    /// </summary>
    private void StartPreviewSync()
    {
        UpdatePreviewSize();

        _previewSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewSyncTimer.Tick += (_, _) =>
        {
            _previewSyncTimer!.Stop();
            _previewSyncing = true;
            try { _clockViewModel?.RefreshForPreview(); }
            finally { _previewSyncing = false; }
        };

        Loaded += (_, _) => UpdatePreviewSize();
        // 设置变化的订阅不在这里挂：它跟着页面的 Loaded/Unloaded 走（见 SubscribeSharedEvents）
        _clockViewModel?.RefreshForPreview();   // 首次进来先把数据填上（没进过大屏时预览也有内容）
    }

    private DispatcherTimer? _previewSyncTimer;

    /// <summary>正在刷新预览。刷新过程本身可能牵动设置（换主题那类），别让它再排一轮、来回刷。</summary>
    private bool _previewSyncing;

    private void SchedulePreviewSync()
    {
        if (_previewSyncing) return;
        if (_previewSyncTimer is not { } timer) return;
        timer.Stop();
        timer.Start();
    }


    // ===== 主题 =====

    /// <summary>CI 当前是不是深色主题。设置页就在 CI 的窗口里，自身生效变体即 CI 的。</summary>
    private bool CiIsDark => ActualThemeVariant != ThemeVariant.Light;

    /// <summary>CI 数据目录（data\），插件配置目录往上三级。</summary>
    private string CiDataDir => Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));

    /// <summary>外观主题档位：0 = 跟随 ClassIsland，1 = 明亮，2 = 黑暗。背景开关打开时整项失效。</summary>
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

    /// <summary>背景开关打开后，外观里那项「主题」就不起作用了，置灰。</summary>
    public bool IsAppearanceThemeEnabled => !_settings.BackgroundEnabled;

    /// <summary>
    /// 背景总开关：关着一整组背景/玻璃设置都不生效，外观与没有这套功能时逐像素一致。
    /// 默认关闭 —— 升级上来的配置没这个字段，默认开着会在用户没点过任何东西的情况下换掉他的主题。
    /// </summary>
    public bool BackgroundEnabled
    {
        get => _settings.BackgroundEnabled;
        set
        {
            if (_settings.BackgroundEnabled == value) return;
            _settings.BackgroundEnabled = value;
            OnPropertyChanged(nameof(BackgroundEnabled));
            OnPropertyChanged(nameof(IsAppearanceThemeEnabled));
            OnPropertyChanged(nameof(BackgroundSectionOpacity));
            // 打开就把这一组展开，省得再点一下
            if (value) IsBackgroundSectionExpanded = true;
            RefreshThemeFromBackground();
        }
    }

    /// <summary>
    /// 「背景与玻璃」那一组在开关关着时整体压暗用的透明度。
    /// Expander 上的 IsEnabled=false 只会让下拉、按钮这些模板自带禁用态的控件变灰，
    /// 那几行说明文字各自写了 Foreground，不跟着变 —— 只靠 IsEnabled 看着像还能调。
    /// </summary>
    public double BackgroundSectionOpacity => _settings.BackgroundEnabled ? 1.0 : 0.4;

    /// <summary>「背景与玻璃」那一组是不是展开着。默认收起，拨开上面的开关会自动展开。</summary>
    public bool IsBackgroundSectionExpanded
    {
        get => _settings.BackgroundSectionExpanded;
        set
        {
            if (_settings.BackgroundSectionExpanded == value) return;
            _settings.BackgroundSectionExpanded = value;
            OnPropertyChanged(nameof(IsBackgroundSectionExpanded));
        }
    }

    /// <summary>
    /// 背景主题档位：0 = 自动（跟着背景图），1 = 暗底白字，2 = 亮底黑字。
    /// 只在背景开关打开时生效——名字按看到的玻璃和字来叫，免得「黑暗」在亮壁纸上反而调出一屏白字。
    /// </summary>
    public int BackgroundThemeModeIndex
    {
        get => _settings.BackgroundThemeMode;
        set
        {
            if (_settings.BackgroundThemeMode == value) return;
            _settings.BackgroundThemeMode = value;
            OnPropertyChanged(nameof(BackgroundThemeModeIndex));
            RefreshThemeFromBackground();
        }
    }

    /// <summary>
    /// 背景图 / 明暗遮罩变了：「自动」主题档判的就是它俩，可能整批翻主题。
    /// 没翻就只刷新预览（不然拖遮罩滑块时每格都要重读一次字体等无关东西）。
    /// </summary>
    private void RefreshThemeFromBackground()
    {
        if (ClockThemeApplier.Apply(_settings, CiIsDark))
        {
            NotifyAllColorsChanged();
            ApplyThemeFromSettings();   // 里面会再刷一次预览
            return;
        }
        SchedulePreviewSync();
    }

    // ===== 背景图片 =====

    /// <summary>当前背景图的文件名；没选就是空。</summary>
    public string BackgroundImageSummary
        => BackgroundImageStore.ResolvePath(_settings.BackgroundImageFile) is null
            ? "未选择（纯色底）"
            : _settings.BackgroundImageFile;

    public string BackgroundImageHint { get; private set; } = "选中的图片会复制一份到插件配置目录，原图之后挪走也不影响";

    /// <summary>填充方式：0 铺满裁切 / 1 完整显示 / 2 拉伸。</summary>
    public int BackgroundImageStretchIndex
    {
        get => _settings.BackgroundImageStretch;
        set
        {
            if (_settings.BackgroundImageStretch == value) return;
            _settings.BackgroundImageStretch = value;
            OnPropertyChanged(nameof(BackgroundImageStretchIndex));
            SchedulePreviewSync();
        }
    }

    /// <summary>背景明暗：负数把整张背景压暗、正数提亮（0 = 不调），保证背景再花字也读得清。</summary>
    public double BackgroundDim
    {
        get => _settings.BackgroundDim;
        set
        {
            if (Math.Abs(_settings.BackgroundDim - value) < 0.01) return;
            _settings.BackgroundDim = value;
            OnPropertyChanged(nameof(BackgroundDim));
            OnPropertyChanged(nameof(BackgroundDimText));
            RefreshThemeFromBackground();
        }
    }

    public string BackgroundDimText => _settings.BackgroundDim switch
    {
        > 0.5 => $"提亮 {_settings.BackgroundDim:0}%",
        < -0.5 => $"压暗 {-_settings.BackgroundDim:0}%",
        _ => "不调",
    };

    private async void PickBackgroundImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择背景图片",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.gif"],
                    },
                ],
            });

            if (files.Count == 0) return;
            var source = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;

            BackgroundImageStore.Replace(source, _settings);
            BackgroundImageHint = $"已复制到插件配置目录：{Path.GetFileName(source)}";
            NotifyBackgroundChanged();
        }
        catch (Exception ex)
        {
            BackgroundImageHint = "选择失败：" + ex.Message;
            OnPropertyChanged(nameof(BackgroundImageHint));
        }
    }

    private void ClearBackgroundImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        BackgroundImageStore.Clear(_settings);
        BackgroundImageHint = "已清除，回到纯色底";
        NotifyBackgroundChanged();
    }

    private void NotifyBackgroundChanged()
    {
        OnPropertyChanged(nameof(BackgroundImageSummary));
        OnPropertyChanged(nameof(BackgroundImageHint));
        RefreshThemeFromBackground();   // 「自动」主题档要按新图重判一次
    }

    // ===== 玻璃底 =====

    /// <summary>玻璃档：0 关闭 / 1 半透明 / 2 磨砂 / 3 真•高光折射。</summary>
    public int GlassModeIndex
    {
        get => _settings.GlassMode;
        set
        {
            if (_settings.GlassMode == value) return;
            _settings.GlassMode = value;
            OnPropertyChanged(nameof(GlassModeIndex));
            // 每档各露各的旋钮，换档时三个可见性都得重新通知一遍
            OnPropertyChanged(nameof(IsOpacityRowVisible));
            OnPropertyChanged(nameof(IsGrainRowVisible));
            OnPropertyChanged(nameof(IsLiquidRowVisible));
            SchedulePreviewSync();
        }
    }

    // 每档只露自己用得上的旋钮，省得「磨砂颗粒度」和「雾面浓度」摆在一起分不清谁是谁。
    // 唯独「透明度」三档都露：它管的是玻璃底色压多实，半透明档它就是那层底色本身，
    // 磨砂档是压在糊底图上那层，折射档烘进折射图里 —— 哪一档都用得上，
    // 藏起来只会让人换档以后找不到它。剩下的：磨砂档另加颗粒度；折射档是折射强度 + 雾面浓度。

    public bool IsOpacityRowVisible => _settings.GlassMode != (int)GlassMode.Off;
    public bool IsGrainRowVisible => _settings.GlassMode == (int)GlassMode.Frosted;
    public bool IsLiquidRowVisible => _settings.GlassMode == (int)GlassMode.Liquid;

    /// <summary>折射强度：边缘把背后画面推弯的力度。</summary>
    public double GlassRefraction
    {
        get => _settings.GlassRefraction;
        set
        {
            if (Math.Abs(_settings.GlassRefraction - value) < 0.01) return;
            _settings.GlassRefraction = value;
            OnPropertyChanged(nameof(GlassRefraction));
            OnPropertyChanged(nameof(GlassRefractionText));
            SchedulePreviewSync();
        }
    }

    public string GlassRefractionText => $"{_settings.GlassRefraction:0}";

    /// <summary>雾面浓度：清晰画面里掺多少糊底图，给文字可读性兜底。</summary>
    public double GlassFrost
    {
        get => _settings.GlassFrost;
        set
        {
            if (Math.Abs(_settings.GlassFrost - value) < 0.01) return;
            _settings.GlassFrost = value;
            OnPropertyChanged(nameof(GlassFrost));
            OnPropertyChanged(nameof(GlassFrostText));
            SchedulePreviewSync();
        }
    }

    public string GlassFrostText => $"{_settings.GlassFrost:0}%";

    /// <summary>玻璃浓度：玻璃底色（黑暗主题=黑、明亮主题=白）的不透明度百分比。</summary>
    public double GlassOpacity
    {
        get => _settings.GlassOpacity;
        set
        {
            if (Math.Abs(_settings.GlassOpacity - value) < 0.01) return;
            _settings.GlassOpacity = value;
            OnPropertyChanged(nameof(GlassOpacity));
            OnPropertyChanged(nameof(GlassOpacityText));
            SchedulePreviewSync();
        }
    }

    public string GlassOpacityText => $"{_settings.GlassOpacity:0}%";

    /// <summary>磨砂颗粒度（模糊半径，单位按屏幕像素算）。</summary>
    public double GlassBlurRadius
    {
        get => _settings.GlassBlurRadius;
        set
        {
            if (Math.Abs(_settings.GlassBlurRadius - value) < 0.01) return;
            _settings.GlassBlurRadius = value;
            OnPropertyChanged(nameof(GlassBlurRadius));
            OnPropertyChanged(nameof(GlassBlurRadiusText));
            SchedulePreviewSync();
        }
    }

    public string GlassBlurRadiusText => $"{_settings.GlassBlurRadius:0}";

    /// <summary>
    /// 换主题后设置页这半边要跟着翻（色块、hex），预览那半边由 ClockSurface 自己盯着 ViewModel 走，
    /// 不用在这里另刷一遍。
    /// </summary>
    private void ApplyThemeFromSettings()
    {
        NotifyAllColorsChanged();
        SchedulePreviewSync();
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
        set { BeginLayoutAdjust(); _settings.ClockFontSize = (int)value; OnPropertyChanged(nameof(ClockFontSize)); OnPropertyChanged(nameof(ClockFontSizeText)); }
    }

    /// <summary>底部居中的课程信息字号（改完进大屏时钟生效）。</summary>
    public double CourseInfoFontSize
    {
        get => _settings.CourseInfoFontSize;
        set { BeginLayoutAdjust(); _settings.CourseInfoFontSize = (int)value; OnPropertyChanged(nameof(CourseInfoFontSize)); OnPropertyChanged(nameof(CourseInfoFontSizeText)); }
    }

    /// <summary>左下角计数字号（改完进大屏时钟生效）。</summary>
    public double CountFontSize
    {
        get => _settings.CountFontSize;
        set { BeginLayoutAdjust(); _settings.CountFontSize = (int)value; OnPropertyChanged(nameof(CountFontSize)); OnPropertyChanged(nameof(CountFontSizeText)); }
    }

    /// <summary>时钟垂直位置：中心落在主体区高度的百分之几（大屏时钟开着时拖动即时生效）。</summary>
    public double ClockPositionPercent
    {
        get => _settings.ClockPositionPercent;
        set { BeginLayoutAdjust(); _settings.ClockPositionPercent = value; OnPropertyChanged(nameof(ClockPositionPercent)); OnPropertyChanged(nameof(ClockPositionPercentText)); }
    }

    /// <summary>音量条垂直位置：同上。</summary>
    public double VolumePositionPercent
    {
        get => _settings.VolumePositionPercent;
        set { BeginLayoutAdjust(); _settings.VolumePositionPercent = value; OnPropertyChanged(nameof(VolumePositionPercent)); OnPropertyChanged(nameof(VolumePositionPercentText)); }
    }

    // ===== 调位置/字号的「让玻璃歇一会儿」=====
    //
    // 这几个滑块一动，大屏和预览每一帧都要重排；而玻璃那一层每帧要画十二块圆角投影 + 各块折射图 +
    // 整屏清晰图，Skia 每帧出的好几个大中间面排着队释放不掉，内存顶着 CI 的看门狗上限走
    // （现象就是「一调位置大小就闪退」），画面本身也只是一片拖影。
    //
    // 「什么时候把手松开」以前是拿 300 毫秒没有新值来猜的，于是**按住不动停 300 毫秒**就被当成松手、
    // 玻璃按中间态烘了出来，接着手一拖又是错的 —— 用户在大屏上看到的就是「玻璃比文字慢一拍」。
    // 现在按的是真事件：按下置 pin，松开/丢捕获/页面收起清 pin，外加一条兜底轮询。

    private DispatcherTimer? _layoutAdjustTimer;

    /// <summary>手还按在位置/字号滑块的手柄上。</summary>
    private bool _layoutPinned;

    /// <summary>按下去那一下的指针，兜底轮询靠它问「还抓着没」（见 <see cref="IsLayoutPointerBusy"/>）。</summary>
    private IPointer? _layoutPointer;

    private readonly List<Slider> _layoutSliders = new();
    private DispatcherTimer? _layoutPinWatch;

    private void BeginLayoutAdjust()
    {
        _settings.AdjustingLayout = true;
        _layoutAdjustTimer ??= CreateLayoutAdjustTimer();
        _layoutAdjustTimer.Stop();
        _layoutAdjustTimer.Start();
    }

    private DispatcherTimer CreateLayoutAdjustTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            // 手还没松开就什么都不做（也不停表，下一拍再看一眼）
            if (_layoutPinned)
            {
                StartLayoutPinWatch();
                return;
            }

            timer.Stop();
            _settings.AdjustingLayout = false;
        };
        return timer;
    }

    /// <summary>
    /// 给「改布局」的那几根滑块接上真按下/松开。
    ///
    /// 必须走**隧道**且 <c>handledEventsToo</c>：Slider 模板里的 Thumb 会把 PointerPressed 标成
    /// Handled，冒泡那一遍根本收不到；而且事件源是 Thumb 不是 Slider，按在滑块空白处更是什么都不发。
    /// </summary>
    private void HookLayoutSliders()
    {
        if (_layoutSliders.Count > 0) return;

        _layoutSliders.AddRange(new[]
        {
            ClockFontSlider, CourseInfoFontSlider, CountFontSlider,
            ClockPositionSlider, VolumePositionSlider, CameraAlertFontSlider,
        });

        foreach (var slider in _layoutSliders)
        {
            slider.AddHandler(PointerPressedEvent, OnLayoutSliderPressed, RoutingStrategies.Tunnel, true);
            slider.AddHandler(PointerReleasedEvent, OnLayoutSliderReleased, RoutingStrategies.Tunnel, true);
            slider.AddHandler(PointerCaptureLostEvent, OnLayoutSliderReleased, RoutingStrategies.Tunnel, true);
        }
    }

    private void OnLayoutSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _layoutPointer = e.Pointer;
        _layoutPinned = true;
        BeginLayoutAdjust();
        StartLayoutPinWatch();
    }

    private void OnLayoutSliderReleased(object? sender, RoutedEventArgs e)
    {
        if (!_layoutPinned) return;
        ReleaseLayoutPin();
        BeginLayoutAdjust();   // 松手：再等 300 毫秒让最后的布局落定，然后放玻璃出来
    }

    /// <summary>
    /// 兜底轮询：手明明松了、松手事件却没送到（鼠标在窗口外松开、拖到别的窗口上、窗口失焦……），
    /// pin 就会一直挂着，大屏和预览的玻璃会**永久**不画。所以按着期间每 300 毫秒核一次。
    /// </summary>
    private void StartLayoutPinWatch()
    {
        if (!_layoutPinned) return;

        _layoutPinWatch ??= CreateLayoutPinWatch();
        if (!_layoutPinWatch.IsEnabled) _layoutPinWatch.Start();
    }

    private DispatcherTimer CreateLayoutPinWatch()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            if (IsLayoutPointerBusy()) return;

            ReleaseLayoutPin();
            BeginLayoutAdjust();
        };
        return timer;
    }

    /// <summary>
    /// 指针是不是还被这几根滑块（或它们模板里的手柄）抓着。滑块按下手柄时会 Capture 住指针、
    /// 松手就还回来，所以「还抓着」等价于「这一拖还没结束」。
    ///
    /// 只认这几根滑块：被别的控件抓着跟这次拖动无关，那就当它已经结束了 ——
    /// 兜底轮询宁可早放不能晚放，晚放就是玻璃永久不画。
    /// </summary>
    private bool IsLayoutPointerBusy()
    {
        if (_layoutPointer?.Captured is not Visual captured) return false;

        foreach (var slider in _layoutSliders)
            if (ReferenceEquals(captured, slider) || slider.GetVisualDescendants().Contains(captured))
                return true;

        return false;
    }

    /// <summary>清 pin 并停下兜底轮询。</summary>
    private void ReleaseLayoutPin()
    {
        _layoutPinned = false;
        _layoutPointer = null;
        _layoutPinWatch?.Stop();
    }

    /// <summary>
    /// 页面收起：pin 与两个定时器一起收掉，并把冻结点解开 —— 定时器是 DispatcherTimer，
    /// 攥着这一页（连带里面的预览、绑定）不放就是一份泄漏；而冻结不解开的话，
    /// 下次进设置页预览的玻璃会一直不画。
    /// </summary>
    private void StopLayoutAdjust()
    {
        ReleaseLayoutPin();
        _layoutAdjustTimer?.Stop();
        _settings.AdjustingLayout = false;
    }

    // 两个位置滑块能拖到哪儿，由大屏那边按当前限位（地震提醒下沿、两块互顶、屏幕边缘）算出来。
    // 直接把它当 Slider 的 Minimum/Maximum：否则滑块能一路拖进「屏幕上早就顶住、数字却不变」的死区。
    public double ClockPositionMin => _clockViewModel?.ClockPositionMin ?? 0;
    public double ClockPositionMax => _clockViewModel?.ClockPositionMax ?? 100;
    public double VolumePositionMin => _clockViewModel?.VolumePositionMin ?? 0;
    public double VolumePositionMax => _clockViewModel?.VolumePositionMax ?? 100;

    /// <summary>大屏时钟的 ViewModel（全屏时钟窗口和这里是同一个实例），拿它的限位。</summary>
    private readonly FullScreenClockViewModel? _clockViewModel;

    private void HookPositionLimits()
    {
        if (_clockViewModel is null) return;
        _clockViewModel.PropertyChanged += OnClockViewModelChanged;
        OnClockViewModelChanged(null, new PropertyChangedEventArgs(nameof(FullScreenClockViewModel.ClockPositionMin)));
        OnClockViewModelChanged(null, new PropertyChangedEventArgs(nameof(FullScreenClockViewModel.VolumePositionMin)));
        OnClockViewModelChanged(null, new PropertyChangedEventArgs(nameof(FullScreenClockViewModel.ClockPositionMax)));
        OnClockViewModelChanged(null, new PropertyChangedEventArgs(nameof(FullScreenClockViewModel.VolumePositionMax)));
    }

    private void UnhookPositionLimits()
    {
        if (_clockViewModel is null) return;
        _clockViewModel.PropertyChanged -= OnClockViewModelChanged;
    }

    private void OnClockViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FullScreenClockViewModel.ClockPositionMin)
            or nameof(FullScreenClockViewModel.ClockPositionMax))
        {
            OnPropertyChanged(nameof(ClockPositionMin));
            OnPropertyChanged(nameof(ClockPositionMax));
        }
        else if (e.PropertyName is nameof(FullScreenClockViewModel.VolumePositionMin)
            or nameof(FullScreenClockViewModel.VolumePositionMax))
        {
            OnPropertyChanged(nameof(VolumePositionMin));
            OnPropertyChanged(nameof(VolumePositionMax));
        }
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
            foreach (var d in devices)
                _settings.MonitoredCameras.Add(new MonitoredCamera(d.Name, d.SymbolicLink).ToSettingValue());
            _settings.CameraListFilled = true;
            Plugin.SaveSettings();
        }

        var displayNames = CameraDeviceNaming.DisplayNames(devices);
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            CameraDevices.Add(new CameraDeviceItem(d, displayNames[i], IsMonitored(d.Name, d.SymbolicLink), this));
        }

        CameraHint = service?.LastError ?? "";
    }

    /// <summary>设置里勾选的项里有没有这一台（按路径认，认不出才退回按名字，见 <see cref="MonitoredCamera.Matches"/>）。</summary>
    private bool IsMonitored(string name, string link)
        => _settings.MonitoredCameras
            .Select(MonitoredCamera.Parse)
            .Any(m => m.Matches(name, link));

    /// <summary>勾选/取消勾选设备后，把当前勾选写回设置（供 <see cref="CameraDeviceItem"/> 调用）。</summary>
    internal void SaveCameraSelection()
    {
        _settings.MonitoredCameras.Clear();
        foreach (var d in CameraDevices.Where(d => d.IsSelected))
            _settings.MonitoredCameras.Add(d.SettingValue);
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

    /// <summary>「摄像头使用中」那一条的字号。范围就是「默认 ~ 天气提醒字号」。</summary>
    public double CameraAlertFontSize
    {
        get => _settings.CameraAlertFontSize;
        set
        {
            if (Math.Abs(_settings.CameraAlertFontSize - value) < 0.01) return;
            BeginLayoutAdjust();
            _settings.CameraAlertFontSize = value;
            OnPropertyChanged(nameof(CameraAlertFontSize));
            OnPropertyChanged(nameof(CameraAlertFontSizeText));
        }
    }

    public string CameraAlertFontSizeText => $"{_settings.CameraAlertFontSize:0}px";

    /// <summary>地震提醒（读「地震预警」插件的实时状态，在时钟正上方弹出）。</summary>
    public bool ShowEarthquakeReminder
    {
        get => _settings.ShowEarthquakeReminder;
        set { _settings.ShowEarthquakeReminder = value; OnPropertyChanged(nameof(ShowEarthquakeReminder)); }
    }

    /// <summary>
    /// 「地震预警」插件装了没。没装时地震提醒那一项置灰、下方给出提示——
    /// 它只是个通知提供方，没装的话这个开关调了也没有用。
    /// 设置页是每次打开新建的，构造函数里查一次即可。
    /// </summary>
    public bool IsEarthquakePluginAvailable => _earthquakePluginAvailable;

    /// <summary>没装「地震预警」插件时，在那一项下方给出原因。</summary>
    public bool ShowEarthquakePluginHint => !_earthquakePluginAvailable;

    private readonly bool _earthquakePluginAvailable;

    private static bool CheckEarthquakePlugin()
    {
        try
        {
            return Plugin.ServiceProvider is { } sp && new EarthquakeReader(sp).IsAvailable();
        }
        catch
        {
            return false;
        }
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
        get => DisplayColor(_settings.RainColor);
        set
        {
            _settings.RainColor = value.ToString();
            OnPropertyChanged(nameof(RainColorValue));
            OnPropertyChanged(nameof(RainColorBrush));
            OnPropertyChanged(nameof(RainColorHex));
        }
    }

    /// <summary>色块上显示的值（就是设置里存的那个；解析不了就按黑色显示）。</summary>
    private static Color DisplayColor(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Black; }
    }

    public Color BackgroundColorValue
    {
        get => DisplayColor(_settings.BackgroundColor);
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
        get => DisplayColor(_settings.FontColor);
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
        get => DisplayColor(_settings.ProgressColor);
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
        get => DisplayColor(_settings.CourseInfoColor);
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
        get => DisplayColor(_settings.NoiseTitleColor);
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

    public string ClockPositionPercentText => $"{_settings.ClockPositionPercent:0}%";

    public string VolumePositionPercentText => $"{_settings.VolumePositionPercent:0}%";

    public new event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>设置页里一个摄像头的勾选项。勾选变化立刻写回设置（大屏时钟那边每秒会重算提醒）。</summary>
public class CameraDeviceItem : INotifyPropertyChanged
{
    private readonly SettingsPage _parent;
    private readonly CameraDevice _device;
    private bool _isSelected;

    /// <summary>列表上显示的名字。同名设备（一体机 + 展台）各带一小段设备路径，
    /// 免得两行同样文字分不出哪个是哪个。</summary>
    public string Name { get; }

    /// <summary>悬停提示：设备名 + 完整设备路径。</summary>
    public string PathTip { get; }

    /// <summary>写回设置的一行（名字 + 接口路径，见 <see cref="MonitoredCamera.ToSettingValue"/>）。</summary>
    internal string SettingValue => new MonitoredCamera(_device.Name, _device.SymbolicLink).ToSettingValue();

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

    public CameraDeviceItem(CameraDevice device, string displayName, bool isSelected, SettingsPage parent)
    {
        _device = device;
        Name = displayName;
        PathTip = device.SymbolicLink.Length == 0 ? device.Name : device.SymbolicLink;
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

