using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Settings;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 大屏时钟的全部内容。全屏窗口（<see cref="FullScreenClockWindow"/>）与设置页的预览放的都是这一份，
/// 预览只是把它等比缩小塞进预览框 —— 两边的字号、颜色、位置出自同一份布局，不可能对不上。
///
/// <see cref="IsPreview"/> = true 的那份（设置页里的）只负责「长得一样」：不往设置里写回位置、
/// 不起弹幕定时器、鼠标事件也由外层掐掉，免得预览反过来改动真实状态。
/// </summary>
public partial class ClockSurface : UserControl
{
    /// <summary>
    /// 这一份是设置页里的预览吗？
    /// 预览按真实屏幕尺寸（等比缩放到预览框里的是整个面），所以实测尺寸、行位置与大屏一模一样。
    /// </summary>
    public static readonly StyledProperty<bool> IsPreviewProperty =
        AvaloniaProperty.Register<ClockSurface, bool>(nameof(IsPreview));

    public bool IsPreview
    {
        get => GetValue(IsPreviewProperty);
        set => SetValue(IsPreviewProperty, value);
    }

    /// <summary>
    /// 背景层的圆角。大屏全屏时是 0；设置页的预览是一张圆角卡片，要设成和卡片一样的圆角，
    /// 否则背景图会在卡片四角露出方角（Border 的 ClipToBounds 只裁矩形，不裁圆角）。
    /// </summary>
    public CornerRadius BackdropCornerRadius
    {
        get => Backdrop.CornerRadius;
        set => Backdrop.CornerRadius = value;
    }

    public ClockSurface()
    {
        InitializeComponent();

        RulesPopup.PlacementTarget = CounterAreaBorder; // 规则浮层锚定在计数区域上方

        // 半屏模式下点黑着的那一边三下 → 回到全屏（黑板挡住时看不见，但平时可以这样退出来）
        AddHandler(PointerPressedEvent, OnEmptyAreaPressed, RoutingStrategies.Tunnel, true);

        SizeChanged += (_, _) => ApplyDisplayRegion();
        DataContextChanged += (_, _) =>
        {
            ApplyDisplayRegion();
            SetupBackdrop();
            UpdatePreviewPlaceholders();
        };

        // 时钟/音量条的上下位置：主体区或两块自身的尺寸一变（换字号、开关注音条、内容行数变化）就重算
        // 地震提醒的「左右绽开」：横向从中间展开，不从下往上冒。
        // 显示与否在外层那格上（整屏正中的覆盖层），卡片本身一直存在，所以盯的是外层。
        EarthquakeAlertBlock.RenderTransformOrigin = RelativePoint.Center;
        EarthquakeAlertLayer.PropertyChanged += OnEarthquakeAlertPropertyChanged;

        BodyArea.SizeChanged += (_, _) => ApplyBodyPositions();

        // 顶栏 / 按钮行的高度一变（半屏紧凑排版、字号、按钮增减），快速设置面板的上下边界跟着重算
        HeaderGrid.SizeChanged += (_, _) => UpdateQuickSettingsBounds();
        ButtonBar.SizeChanged += (_, _) => UpdateQuickSettingsBounds();
        ClockBox.SizeChanged += (_, _) => ApplyBodyPositions();
        VolumePanel.SizeChanged += (_, _) => ApplyBodyPositions();
        Loaded += (_, _) => ApplyBodyPositions();
        Loaded += (_, _) => UpdatePreviewPlaceholders();
    }

    // ===== 生命周期：挂上/摘掉那几根「全局事件」的线 =====

    // Plugin.Settings 与 ViewModel 都是长命的单例，挂上去的处理器会让它们反过来攥住这一整棵
    // 视觉树。设置页每打开一次就新建一个 ClockSurface（预览那份），不摘线的话每开一次就漏一整份
    // 控件树 + 背景层的位图，而且之后每次改设置都要多跑一遍 —— 内存一路涨、界面一路钝。
    // 所以挂线只在真正进了视觉树时做，离开时全部摘掉（大屏窗口只是最小化、不脱离树，不受影响）。

    private bool _settingsHooked;
    private FullScreenClockViewModel? _hookedVm;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookSignals();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnhookSignals();
    }

    private void HookSignals()
    {
        if (!_settingsHooked && Plugin.Settings is { } settings)
        {
            settings.PropertyChanged += OnPluginSettingsChanged;
            _settingsHooked = true;
        }

        if (_hookedVm is null && DataContext is FullScreenClockViewModel vm)
        {
            vm.PropertyChanged += OnClockViewModelPropertyChanged;
            vm.NoisyDisplaySegments.CollectionChanged += OnNoisySegmentsChanged;
            _hookedVm = vm;
        }
    }

    private void UnhookSignals()
    {
        if (_settingsHooked && Plugin.Settings is { } settings)
            settings.PropertyChanged -= OnPluginSettingsChanged;
        _settingsHooked = false;

        if (_hookedVm is { } vm)
        {
            vm.PropertyChanged -= OnClockViewModelPropertyChanged;
            vm.NoisyDisplaySegments.CollectionChanged -= OnNoisySegmentsChanged;
        }
        _hookedVm = null;
    }

    private void OnNoisySegmentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdatePreviewPlaceholders();

    private void OnPluginSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 按住设置项（滑块把手）时快速设置面板淡下去，好看清后面被改的那块大屏；
        // 不 return：AdjustingLayout 同时也是背景层「先别重烘」的闸，下面照样要处理。
        if (e.PropertyName is nameof(PluginSettings.AdjustingLayout))
            UpdateQuickSettingsOpacity();

        // 显示区域：全屏 ↔ 半屏、可见宽度变化，立刻重排（背景层跟着 Shell 变窄，会自己重烘）
        if (e.PropertyName is nameof(PluginSettings.DisplayRegion)
            or nameof(PluginSettings.VisibleWidthPercent))
        {
            ApplyDisplayRegion();
            return;
        }

        // 设置页拖滑块改的是同一个 PluginSettings 实例 → 立刻重排，不用等重开窗口
        if (e.PropertyName is nameof(PluginSettings.ClockPositionPercent)
            or nameof(PluginSettings.VolumePositionPercent)
            or nameof(PluginSettings.ShowDecibelMeter))
        {
            ApplyBodyPositions();
            UpdatePreviewPlaceholders();
            return;
        }

        // 预览里的假内容跟着这两项开关走；它们不进玻璃，不用重烘
        if (e.PropertyName is nameof(PluginSettings.ShowNoisyCounter)
            or nameof(PluginSettings.ShowCourseInfo))
        {
            UpdatePreviewPlaceholders();
            return;
        }

        OnBackdropSettingsChanged(sender, e);
    }

    // ===== 背景图 + 组件玻璃底 =====

    /// <summary>玻璃层只初始化一次（DataContext 可能来不止一次）。</summary>
    private bool _backdropBound;

    /// <summary>
    /// 把要垫玻璃的组件登记给背景层，并接上「什么时候要重画玻璃」的几根线。
    ///
    /// 玻璃是从底下一整层画的，不参与布局，所以这里只登记控件本身，不碰它们的 Margin / 尺寸——
    /// ApplyBodyPositions 那套定位与写回一个字节都不用改。
    /// </summary>
    private void SetupBackdrop()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;

        if (!_backdropBound)
        {
            _backdropBound = true;

            // 全部独立组件各垫一块；地震提醒块刻意不垫 —— 它在整屏正中，底下正好压着时钟，
            // 登记进来磨砂会跑到时钟下面、时钟的字反而浮在磨砂上面。
            // 它自己从背景层那张模糊图上裁一块（见 FrostedCardBackdrop）。
            // 底部全宽进度带不垫（语义色带，垫了反而糊）。
            EqFrost.Layer = Backdrop;
            QuakeSheet.Layer = Backdrop;   // 整屏那层磨砂，同一张模糊图
            Backdrop.GlassTargets = new GlassTarget[]
            {
                new() { Visual = WeatherBlock },
                new() { Visual = RainBlock },
                new() { Visual = CountdownBlock },
                new() { Visual = ReminderTextBlock },
                new() { Visual = AlertDetailScroll },
                new() { Visual = DateBlock },
                new() { Visual = ClockBox, InsetX = 16, InsetY = 8, Radius = 16 },
                new() { Visual = VolumePanel, InsetX = 16, InsetY = 8, Radius = 16 },
                new() { Visual = CounterStrip },
                new() { Visual = CourseInfoBox },
                new() { Visual = ButtonBar },
                new() { Visual = ReminderPanel },
            };

            // 组件挪了/隐了就得重画玻璃。Avalonia 是保留式渲染，兄弟控件动了不会叫醒背景层，
            // 所以直接在各个目标身上盯 Bounds 与 IsVisible——位置或尺寸一变必然走这两条。
            foreach (var target in new Control[]
                     {
                         WeatherBlock, RainBlock, CountdownBlock, ReminderTextBlock,
                         AlertDetailScroll, DateBlock, ClockBox, VolumePanel,
                         CounterStrip, CourseInfoBox, ButtonBar, ReminderPanel,
                     })
            {
                target.PropertyChanged += OnGlassTargetPropertyChanged;
            }

            // 设置页里改背景/玻璃相关项 → 立刻生效，不用重开窗口。
            // 挂线统一走 HookSignals（进视觉树时挂、离开时摘），在那儿挂的是一根
            // OnPluginSettingsChanged，位置/音量条与背景/玻璃两类通知都从它转发过来。
            HookSignals();

            Loaded += (_, _) => Backdrop.InvalidateGlass();
        }

        ApplyButtonHalo();
        ApplyBackdrop();
    }

    private void OnGlassTargetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty || e.Property == Visual.IsVisibleProperty)
            Backdrop.InvalidateGlass();
    }

    private void OnClockViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 换主题 / 自动配色下换背景色 → 玻璃的底色、描边、留白色整批跟着变
        if (e.PropertyName is nameof(FullScreenClockViewModel.Chrome)
            or nameof(FullScreenClockViewModel.BackgroundColor))
        {
            ApplyButtonHalo();
            ApplyBackdrop();
        }

        // 预览占位内容跟着这些状态走（上课了就把假内容撤掉）
        if (e.PropertyName is nameof(FullScreenClockViewModel.CourseInfoText)
            or nameof(FullScreenClockViewModel.IsRecordingVisible)
            or nameof(FullScreenClockViewModel.ShowDecibelMeter))
        {
            UpdatePreviewPlaceholders();
        }
    }

    /// <summary>
    /// 设置页预览专供：补一份「假内容」。
    ///
    /// 预览不是另画的小样，而是把大屏这面墙原样缩小 —— 所以没在上课时，课程信息、左下角计数、
    /// 「正在记录」本来就是空的，想调这几项字号，预览里什么也看不到。这里在**预览那一份**里
    /// 补一行示意文字，只在真实内容为空时显示。
    /// 不碰 ViewModel：大屏与预览共用同一个实例，往 VM 里塞假数据会污染真实大屏。
    /// </summary>
    private void UpdatePreviewPlaceholders()
    {
        var vm = DataContext as FullScreenClockViewModel;
        var preview = IsPreview && vm is not null;

        var counterOn = Plugin.Settings?.ShowNoisyCounter ?? true;
        PreviewCounterPlaceholder.IsVisible = preview && counterOn && vm!.NoisyDisplaySegments.Count == 0;
        PreviewCoursePlaceholder.IsVisible = preview && string.IsNullOrWhiteSpace(vm!.CourseInfoText);
        PreviewRecordingPlaceholder.IsVisible = preview && vm!.ShowDecibelMeter && !vm.IsRecordingVisible;
    }

    /// <summary>
    /// 给「最小化 / 退出」两个按钮的字描一圈同色系的柔光。
    ///
    /// 这两个键以前靠一块很实的黑/白底片保证可读，底片一淡就糊、够实又成了一堆亮色里杵一块砖。
    /// 可读性改由这圈柔光扛：深色主题白字配黑晕、明亮主题黑字配白晕，
    /// 底片于是可以只留一点同色系的淡彩，看着是「同一家族里挑出来的一个」，而不是贴上去的黑块。
    /// </summary>
    private void ApplyButtonHalo()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;

        // 两个控件不能共用一个 Effect 实例，各建一份
        foreach (var button in new[] { MinimizeButton, ExitButton })
        {
            if (button.Content is not Control content) continue;
            content.Effect = new DropShadowEffect
            {
                Color = vm.Chrome.IsLight ? Colors.White : Colors.Black,
                OffsetX = 0,
                OffsetY = 0,
                BlurRadius = 4,
                Opacity = 0.9,
            };
        }
    }

    private void OnBackdropSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.BackgroundEnabled)
            or nameof(PluginSettings.BackgroundImageFile)
            or nameof(PluginSettings.BackgroundImageStretch)
            or nameof(PluginSettings.BackgroundDim)
            or nameof(PluginSettings.GlassMode)
            or nameof(PluginSettings.GlassOpacity)
            or nameof(PluginSettings.GlassBlurRadius)
            or nameof(PluginSettings.GlassRefraction)
            or nameof(PluginSettings.GlassFrost)
            or nameof(PluginSettings.AdjustingLayout))
            ApplyBackdrop();
    }

    /// <summary>把当前的背景/玻璃设置推给背景层。</summary>
    private void ApplyBackdrop()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;
        if (Plugin.Settings is not { } settings) return;

        // 总开关关着 = 背景图与玻璃一并收掉，哪怕设置里选过图/开过玻璃
        var on = settings.BackgroundEnabled;

        // 半屏模式下填充方式定死「铺满整屏（裁掉多余）」：窄条里留白或拉伸都不好看
        var stretch = settings.DisplayRegion != 0
            ? (int)BackgroundImageStretch.FillCrop
            : settings.BackgroundImageStretch;

        Backdrop.SetPalette(vm.Chrome, ParseColorOrDefault(vm.BackgroundColor, Colors.Black));
        ApplyQuakeFrost(vm);
        Backdrop.Apply(on ? BackgroundImageStore.ResolvePath(settings.BackgroundImageFile) : null,
                       stretch, settings.BackgroundDim,
                       on ? settings.GlassMode : (int)GlassMode.Off,
                       settings.GlassOpacity, settings.GlassBlurRadius, settings.GlassRefraction,
                       settings.GlassFrost, settings.AdjustingLayout);
    }

    /// <summary>
    /// 地震预警那层「整屏磨砂」的压色：磨砂本身用背景层早烘好的模糊图（现成的，只多画一遍），
    /// 这里只负责在它上面按亮/暗主题压一档，卡片（不压）才跳得出来。
    /// QuakeVeil 是没有背景图时的兜底：近乎不透明，把别的都盖住。
    /// </summary>
    private void ApplyQuakeFrost(FullScreenClockViewModel vm)
    {
        var light = vm.Chrome.IsLight;

        QuakeSheet.Tint = new SolidColorBrush(light
            ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x59, 0x00, 0x00, 0x00));

        QuakeVeil.Background = new SolidColorBrush(light
            ? Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE8, 0x08, 0x08, 0x0C));
    }

    private static Color ParseColorOrDefault(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : Color.Parse(hex); }
        catch { return fallback; }
    }

    // ===== 显示区域：全屏 / 左半屏 / 右半屏 =====

    /// <summary>
    /// 当前可见区域的实际宽度（逻辑像素）。全屏时等于整个 ClockSurface 的宽度；
    /// 左/右半屏时是其中一条。尺寸相关的宽度都按它算，不能再用整窗宽。
    /// </summary>
    private double _visibleWidth;

    /// <summary>上一次套用的是不是紧凑排版；null = 还没套过（第一次一定要套一遍）。</summary>
    private bool? _compactApplied;

    /// <summary>
    /// 按设置把画面收进可见区域：全屏铺满；左/右半屏时 Shell 只占宽度的一条。
    /// 窗口本身不动——Esc/F11、防休眠、焦点仍然是整个全屏窗口的事。
    /// </summary>
    private void ApplyDisplayRegion()
    {
        var full = Bounds.Width;
        if (Plugin.Settings is not { } settings)
        {
            UpdateSizeDependentLayout();
            return;
        }

        var region = settings.DisplayRegion;
        var percent = Math.Clamp(settings.VisibleWidthPercent, 30, 70);

        var narrowed = region != 0 && full > 0;

        if (!narrowed)
        {
            Shell.Width = double.NaN;
            Shell.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _visibleWidth = full;
        }
        else
        {
            _visibleWidth = Math.Round(full * percent / 100.0);
            Shell.Width = _visibleWidth;
            Shell.HorizontalAlignment = region == 1
                ? Avalonia.Layout.HorizontalAlignment.Left
                : Avalonia.Layout.HorizontalAlignment.Right;
        }

        // 收窄到七成以下才换紧凑排版：再宽一点一行放得下，按原来那套排，省得在两套边界上来回跳
        ApplyCompactLayout(narrowed && percent < 70);

        // 日期、天气、提醒面板贴可见的那一侧：左半屏靠左，右半屏靠右
        ApplyRegionAlignment(narrowed && region == 2);

        // 半屏时背景填充方式被锁成「铺满整屏」，这里让背景层重新取一次参数
        ApplyBackdrop();

        UpdateSizeDependentLayout();
    }

    /// <summary>
    /// 半屏（窄）排版。宽屏那套是「顶栏一行（左天气 / 中弹幕 / 右日期）+ 底部一行（计数 / 课程信息 / 按钮）」；
    /// 窄了放不下，改成：天气一行 → 日期与弹幕一行；课程信息一行 → 计数与按钮一行。
    /// 时钟字号不在这里动：外面那层 Viewbox 会按可用宽度自己缩。
    /// </summary>
    private void ApplyCompactLayout(bool compact)
    {
        if (_compactApplied == compact) return;
        _compactApplied = compact;

        if (compact)
        {
            MainContent.Margin = new Thickness(20, 18, 20, 12);

            // 两行之间留出 24：玻璃各往外探 6，间距得比 12 大，不然上下两块玻璃会压在一起
            HeaderGrid.RowSpacing = 24;
            FooterGrid.RowSpacing = 24;

            // 顶栏：日期在第一行（弹幕接着占同一行剩下的横向空间），天气挪到第二行
            Grid.SetRow(DateBlock, 0);
            Grid.SetColumn(DateBlock, 0);
            Grid.SetColumnSpan(DateBlock, 1);
            DateBlock.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            DateBlock.Margin = new Thickness(0);

            Grid.SetRow(AlertDetailScroll, 0);
            Grid.SetColumn(AlertDetailScroll, 1);
            Grid.SetColumnSpan(AlertDetailScroll, 2);
            AlertDetailScroll.Margin = new Thickness(16, 0, 0, 0);

            Grid.SetRow(HeaderLeft, 1);
            Grid.SetColumn(HeaderLeft, 0);
            Grid.SetColumnSpan(HeaderLeft, 3);

            // 底行：课程信息独占一行，计数和按钮挤到下一行（两边本来就都是 *，不用改列定义）
            Grid.SetRow(CourseInfoBox, 0);
            Grid.SetColumn(CourseInfoBox, 0);
            Grid.SetColumnSpan(CourseInfoBox, 3);

            Grid.SetRow(CounterAreaBorder, 1);
            Grid.SetColumn(CounterAreaBorder, 0);
            Grid.SetColumnSpan(CounterAreaBorder, 2);

            Grid.SetRow(ButtonBar, 1);
            Grid.SetColumn(ButtonBar, 2);
            Grid.SetColumnSpan(ButtonBar, 1);
        }
        else
        {
            MainContent.Margin = new Thickness(40, 30, 40, 18);
            HeaderGrid.RowSpacing = 0;
            FooterGrid.RowSpacing = 0;

            Grid.SetRow(HeaderLeft, 0);
            Grid.SetColumn(HeaderLeft, 0);
            Grid.SetColumnSpan(HeaderLeft, 1);

            Grid.SetRow(DateBlock, 0);
            Grid.SetColumn(DateBlock, 2);
            Grid.SetColumnSpan(DateBlock, 1);
            DateBlock.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            DateBlock.Margin = new Thickness(28, 0, 0, 0);

            Grid.SetRow(AlertDetailScroll, 0);
            Grid.SetColumn(AlertDetailScroll, 1);
            Grid.SetColumnSpan(AlertDetailScroll, 1);
            AlertDetailScroll.Margin = new Thickness(28, 0, 0, 0);

            Grid.SetRow(CourseInfoBox, 0);
            Grid.SetColumn(CourseInfoBox, 1);
            Grid.SetColumnSpan(CourseInfoBox, 1);

            Grid.SetRow(CounterAreaBorder, 0);
            Grid.SetColumn(CounterAreaBorder, 0);
            Grid.SetColumnSpan(CounterAreaBorder, 1);

            Grid.SetRow(ButtonBar, 0);
            Grid.SetColumn(ButtonBar, 2);
            Grid.SetColumnSpan(ButtonBar, 1);
        }
    }

    /// <summary>
    /// 内容贴哪一边：左半屏贴左、右半屏贴右（日期、天气、预警弹幕、提醒面板都跟着可见侧走）。
    /// 不是整幅镜像——时钟、音量条、课程信息始终居中，读数的方向不变。
    /// </summary>
    private void ApplyRegionAlignment(bool right)
    {
        // 快速设置面板跟着可见侧走（要先于下面的提前返回：宽屏那套也要贴对边）
        ApplyQuickSettingsAppearance();

        var side = right
            ? Avalonia.Layout.HorizontalAlignment.Right
            : Avalonia.Layout.HorizontalAlignment.Left;

        HeaderLeft.HorizontalAlignment = side;
        ReminderPanel.HorizontalAlignment = side;

        if (_compactApplied != true) return;   // 宽屏那套日期本来就在右侧，不用动

        DateBlock.HorizontalAlignment = side;

        if (right)
        {
            // 右半屏：日期贴右，弹幕占左边剩下的横向空间
            Grid.SetColumn(DateBlock, 2);
            Grid.SetColumn(AlertDetailScroll, 0);
            Grid.SetColumnSpan(AlertDetailScroll, 2);
            AlertDetailScroll.Margin = new Thickness(0, 0, 16, 0);
        }
        else
        {
            Grid.SetColumn(DateBlock, 0);
            Grid.SetColumn(AlertDetailScroll, 1);
            Grid.SetColumnSpan(AlertDetailScroll, 2);
            AlertDetailScroll.Margin = new Thickness(16, 0, 0, 0);
        }
    }

    /// <summary>
    /// 半屏模式下点黑着的那一边三下 → 回到全屏。
    /// 用隧道挂：黑屏区没有控件，但点在可见区里的按钮/滑块上也会先经过这里，按坐标排掉即可。
    /// </summary>
    private void OnEmptyAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsPreview) return;
        if (IsQuickSettingsOpen) return;   // 面板开着时点黑屏区是关面板，不算「三击退出半屏」
        if (Plugin.Settings is not { } settings || settings.DisplayRegion == 0) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 点在看得见的那一条里就不算（Shell 靠右时，屏幕左半边是负坐标）
        var point = e.GetPosition(Shell);
        if (point.X >= 0 && point.X <= Shell.Bounds.Width
            && point.Y >= 0 && point.Y <= Shell.Bounds.Height)
            return;

        var now = DateTime.Now;

        // 触摸屏（希沃）上一次触摸会同时送来触摸与模拟鼠标两个 PointerPressed：
        // 不去重的话双击就成了三下，半屏会被误退出。120 毫秒内的重复事件按同一次点击算。
        if ((now - _lastEmptyAreaTap).TotalMilliseconds < 120) return;

        if ((now - _lastEmptyAreaTap).TotalMilliseconds > 1000) _emptyAreaTapCount = 0;
        _lastEmptyAreaTap = now;

        if (++_emptyAreaTapCount < 3) return;
        _emptyAreaTapCount = 0;
        settings.DisplayRegion = 0;   // 设置变更通知会接着走 ApplyDisplayRegion
    }

    private int _emptyAreaTapCount;
    private DateTime _lastEmptyAreaTap = DateTime.MinValue;

    /// <summary>
    /// 随尺寸变化的几个宽度：提醒面板最大宽 = 可见区域宽 × 2/3；第一行提醒合并阈值 = 可见区域宽 × 1/3；
    /// 底部课程信息最大宽 = 可见区域宽（去掉页面左右各 40 的边距）× 1/2，超了由 Viewbox 缩字而不是裁掉。
    /// </summary>
    private void UpdateSizeDependentLayout()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;
        if (Bounds.Width <= 0) return;  // 还没量出尺寸，别把课程信息压成 0 宽

        // 半屏时这些宽度按「看得见的那一条」算，不是整窗
        var width = _visibleWidth > 0 ? _visibleWidth : Bounds.Width;
        vm.ReminderPanelMaxWidth = width * 2.0 / 3.0;
        vm.MergeThreshold = width / 3.0;
        vm.CourseInfoMaxWidth = Math.Max(160, (width - 80) / 2.0);
        // 地震提醒块铺的是整窗（半屏时仍然整屏弹），这条仍按整窗宽算
        vm.EarthquakeAlertMaxWidth = Math.Max(240, Bounds.Width - 80 - 72);
        ApplyBodyPositions();
    }

    /// <summary>
    /// 地震提醒弹出动画：整块横向从中间向两侧绽开。
    /// 地震预警插件在 CI 里是往下摊开的，跟大屏这面墙不搭，这里改成左右展开。
    /// 用的是 Avalonia 对 RenderTransform 的原生插值（TransformOperations），缩放中心在正中。
    /// </summary>
    private static readonly Animation QuakeRevealAnimation = new()
    {
        Duration = TimeSpan.FromMilliseconds(380),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.RenderTransformProperty, QuakeScale(0.04)) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.RenderTransformProperty, QuakeScale(1)) } },
        },
    };

    /// <summary>只做横向缩放的变换（1 = 原样，越接近 0 越窄）。</summary>
    private static TransformOperations QuakeScale(double scaleX)
    {
        var builder = new TransformOperations.Builder(1);
        builder.AppendScale(scaleX, 1);
        return builder.Build();
    }

    private void OnEarthquakeAlertPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;

        if (!EarthquakeAlertLayer.IsVisible) return;

        EarthquakeAlertBlock.RenderTransform = QuakeScale(0.04);   // 先归零，免得动画起来前闪一帧整块
        try
        {
            _ = QuakeRevealAnimation.RunAsync(EarthquakeAlertBlock);
        }
        catch
        {
            EarthquakeAlertBlock.RenderTransform = QuakeScale(1);  // 动画起不来就直接显示，别把提醒吞了
        }
    }

    /// <summary>时钟与音量条之间至少留出的距离（音量条顶上来时的联动依据）。</summary>
    private const double ClockVolumeGap = 24;

    /// <summary>正在套用布局（也是往设置里写回实际位置的时候），用来挡住写回引发的重入。</summary>
    private bool _applyingLayout;

    /// <summary>
    /// 时钟、音量条各自的上下位置。设置里存的是「该块中心落在主体区高度的百分之几」（0 最上、100 最下），
    /// 这里按主体区的实测高度换算成上边距，再钳进这一轮算出来的可落范围（滑块的范围也是同一个）。
    ///
    /// 两块从上到下依次是：时钟、音量条，谁都不许越界：
    /// - 时钟不出主体区（顶不出上沿、底不出下沿）；
    /// - 音量条上不了时钟头顶，下不出主体区——再往下就压到「最小化 / 退出」那一行了。
    ///
    /// 音量条的下限是固定的，时钟的下限跟着音量条走——所以音量条往上顶，时钟的可落范围就收紧、
    /// 被一并顶上去；反过来往下拖时钟，时钟停在音量条上沿，不会把音量条挤下去。
    /// 不用另记「这次拖的是哪一个」。
    ///
    /// 钳完的位置会写回设置，滑块范围也同步过去：滑块显示的、能拖的就是屏幕上的实际情况。
    /// 预览那一份（<see cref="IsPreview"/>）只照这套摆位，既不改设置、也不动滑块范围——
    /// 预览的尺寸和大屏一致，摆出来的位置本就一样，但它不该有「改设置」这份副作用。
    /// </summary>
    private void ApplyBodyPositions()
    {
        if (Plugin.Settings is not { } settings) return;
        if (_applyingLayout) return;                   // 写回设置引发的重入，一次就够
        var areaHeight = BodyArea.Bounds.Height;
        if (areaHeight <= 0) return;   // 布局还没跑，等下一轮 SizeChanged

        _applyingLayout = true;
        try
        {
            // 地震提醒是整屏正中的浮层，不参与这里的限位：它来去都不该把时钟顶走
            const double clockMinTop = 0.0;

            // 用外层的 Viewbox：半屏窄条里它会被缩到放得下，摆位要按缩放后的实际尺寸算
            var clockHeight = ClockBox.Bounds.Height;
            var volumeVisible = VolumePanel.IsVisible;
            var volumeHeight = VolumePanel.Bounds.Height;

            // 音量条：上不了时钟头顶，下不出主体区
            // （再往下就压到「最小化 / 退出」那一行去了，这条优先守住）
            var volumeMinTop = clockMinTop + clockHeight + ClockVolumeGap;
            var volumeMaxTop = Math.Max(0, areaHeight - volumeHeight);
            var volumeTop = volumeVisible
                ? Math.Min(Math.Max(TopFor(settings.VolumePositionPercent, areaHeight, volumeHeight), volumeMinTop), volumeMaxTop)
                : 0.0;

            // 时钟：上不出主体区，下压不到音量条上。装不下时宁可两块挨上也别越界
            var clockRoomTop = volumeVisible ? volumeTop - ClockVolumeGap - clockHeight : areaHeight - clockHeight;
            var clockMaxTop = Math.Max(clockMinTop, clockRoomTop);
            var clockTop = Math.Clamp(TopFor(settings.ClockPositionPercent, areaHeight, clockHeight), clockMinTop, clockMaxTop);

            if (volumeVisible) SetTop(VolumePanel, volumeTop);
            SetTop(ClockBox, clockTop);

            if (IsPreview) return;   // 预览到此为止：位置摆好了，剩下的都是真实大屏才该做的事

            if (DataContext is FullScreenClockViewModel vm)
            {
                vm.SetPositionLimits(
                    PercentOf(clockMinTop, areaHeight, clockHeight),
                    PercentOf(clockMaxTop, areaHeight, clockHeight),
                    volumeVisible ? PercentOf(Math.Min(clockHeight + ClockVolumeGap, volumeMaxTop), areaHeight, volumeHeight) : 0,
                    volumeVisible ? PercentOf(volumeMaxTop, areaHeight, volumeHeight) : 100);
            }

            WriteBack(settings, areaHeight, clockTop, clockHeight, volumeTop, volumeHeight, volumeVisible);
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    /// <summary>把钳完的实际位置写回设置（差值太小就不写，免得每轮布局都惊动一遍设置）。</summary>
    private static void WriteBack(PluginSettings settings, double areaHeight,
                                  double clockTop, double clockHeight,
                                  double volumeTop, double volumeHeight, bool volumeVisible)
    {
        var clockPercent = PercentOf(clockTop, areaHeight, clockHeight);
        if (Math.Abs(clockPercent - settings.ClockPositionPercent) > 0.05)
            settings.ClockPositionPercent = clockPercent;

        if (!volumeVisible) return;
        var volumePercent = PercentOf(volumeTop, areaHeight, volumeHeight);
        if (Math.Abs(volumePercent - settings.VolumePositionPercent) > 0.05)
            settings.VolumePositionPercent = volumePercent;
    }

    /// <summary>按「中心落在区域高度的百分之几」算出该块的上边距（未钳制，钳制在各调用处按优先级做）。</summary>
    private static double TopFor(double percent, double areaHeight, double height)
        => percent / 100.0 * areaHeight - height / 2.0;

    /// <summary>上边距换算回「中心落在区域高度的百分之几」，与 <see cref="TopFor"/> 互逆。</summary>
    private static double PercentOf(double top, double areaHeight, double height)
        => Math.Clamp((top + height / 2.0) / areaHeight * 100, 0, 100);

    /// <summary>摆好一块的上边距。只在真的变了才写：写过边距还会再跑一轮 SizeChanged，不比较会来回抖。</summary>
    private static void SetTop(Control block, double top)
    {
        if (Math.Abs(block.Margin.Top - top) > 0.5)
            block.Margin = new Thickness(0, top, 0, 0);
    }

    /// <summary>「点击后滚完这一遍就收」已生效：这期间重复点击不生效。</summary>
    private bool _alertMarqueeOncePlaying;

    /// <summary>把正在循环的弹幕改成「接着当前进度滚，滚完这一遍就收」（点击触发）。
    /// 只在弹幕真的在滚时非空；null 表示没在播或已经在收尾状态。</summary>
    private Action? _marqueeStopAfterThisPass;

    /// <summary>触屏/触控笔按住中：这段时间按悬停算（一直循环），抬手才当「点了一下」收尾。</summary>
    private bool _touchPressActive;

    /// <summary>鼠标悬停某条预警：在顶部弹幕带显示其详情并循环滚动（自动切换，不需要先收起旧条）。</summary>
    private void AlertPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not AlertDisplayItem item) return;
        if (DataContext is not FullScreenClockViewModel vm) return;

        if (e.Pointer.Type == PointerType.Mouse) _touchPressActive = false;   // 换成鼠标了，清掉触屏状态

        vm.ExpandedAlertDetail = item.Detail;
        // 顶部只有一个弹幕 ScrollViewer。先停旧弹幕再重新启动：悬停另一条时 Text 已变，
        // 不能靠 SizeChanged 触发，显式重启。悬停态 = 一直循环滚。
        StopMarquee(AlertDetailScroll);
        StartMarquee(AlertDetailScroll, once: false);
    }

    /// <summary>点击某条预警：详情弹幕滚完一整遍后自动收起。
    /// 一遍没滚完时重复点击不生效。</summary>
    private void AlertPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not AlertDisplayItem item) return;
        if (_alertMarqueeOncePlaying) return;   // 已经在收尾的那一遍里，忽略
        if (DataContext is not FullScreenClockViewModel vm) return;

        // 触屏/触控笔按下不是「点击」：按下时也会触发 PointerEntered，那是「手指/笔尖放在上面」，
        // 该按悬停处理（一直循环滚）。若当成点击，就会变成「按住不动只滚一遍」——
        // 教室大屏正是触屏，抬手时（PointerExited）再按点击收尾。
        if (e.Pointer.Type != PointerType.Mouse)
        {
            _touchPressActive = true;
            return;
        }

        vm.ExpandedAlertDetail = item.Detail;

        // 鼠标点之前必然已经悬停，弹幕早就在循环滚了：接着当前进度往下滚，滚完这一遍再收，
        // 不重头开始播（用户 2026-09-19 反馈：点头重播很出戏）。
        if (_marqueeStopAfterThisPass is { } stopAfterThisPass)
        {
            _alertMarqueeOncePlaying = true;
            stopAfterThisPass();
            return;
        }

        // 没在播（触屏上直接点，没有悬停态）：从头滚一遍。
        StopMarquee(AlertDetailScroll);
        StartMarquee(AlertDetailScroll, once: true);
    }

    /// <summary>鼠标移出该条预警：收起顶部弹幕。点击触发的单次播放不受影响——
    /// 触屏抬手也会产生 PointerExited，不能让它把「滚完一遍」打断。</summary>
    private void AlertPointerExited(object? sender, PointerEventArgs e)
    {
        if (_alertMarqueeOncePlaying) return;

        // 触屏抬手 = 轻点了一下：别在抬手瞬间把弹幕掐掉，让它滚完当前这一遍再收（等同鼠标点击）。
        if (_touchPressActive)
        {
            _touchPressActive = false;
            if (_marqueeStopAfterThisPass is { } stopAfterThisPass)
            {
                _alertMarqueeOncePlaying = true;
                stopAfterThisPass();
                return;
            }

            StopMarquee(AlertDetailScroll);
            StartMarquee(AlertDetailScroll, once: true);
            return;
        }

        if (DataContext is not FullScreenClockViewModel vm) return;
        vm.ExpandedAlertDetail = null;
        StopMarquee(AlertDetailScroll);
    }

    /// <summary>停止弹幕：停定时器、清平移、清启动标记（下次 StartMarquee 才能重新启动）。</summary>
    private void StopMarquee(ScrollViewer sv)
    {
        if (sv.Tag is DispatcherTimer timer) timer.Stop();
        if (sv.Content is Control content) content.RenderTransform = null;
        sv.Tag = null;
        _marqueeStopAfterThisPass = null;
        _alertMarqueeOncePlaying = false;
    }

    /// <summary>预警详情弹幕：文字超宽时自动横向滚动（从右向左），不用手动拖动滚动条。</summary>
    private void DetailScroll_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.IsVisible)
            StartMarquee(sv, once: false);
    }

    private void StartMarquee(ScrollViewer sv, bool once)
    {
        // 预览那份不起弹幕：设置页里没必要为了看一眼排版常驻一个 16 毫秒的定时器
        if (IsPreview) return;

        if (!sv.IsEffectivelyVisible) return;   // 单行/双行只有当前布局那个可见，隐藏版跳过
        if (sv.Tag is not null) return;         // 已启动过，防反复触发重复动画

        _alertMarqueeOncePlaying = once;        // 先立标记：布局就绪前的这几帧也挡住重复点击
        // 同步挂平移、先把文字推到视口右侧外（布局未完成前 viewport 未知，用大偏移占位）：
        // 这样「重开弹幕的瞬间」不会有一帧把原文整段占满显示框（此前 translate.X 默认 0，
        // 首个 Tick 之前会闪一下完整开头）。
        var translate = new TranslateTransform { X = 1e6 };
        if (sv.Content is Control c0) c0.RenderTransform = translate;
        sv.Tag = translate;               // 占位标记；StopMarquee 会清掉，挂起的回调据此退出
        StartMarqueeWhenReady(sv, translate, 0, once);
    }

    /// <summary>等布局完成（ScrollViewer.Extent 就绪）后再初始化弹幕；未就绪则下一帧重试。</summary>
    private void StartMarqueeWhenReady(ScrollViewer sv, TranslateTransform translate, int attempt, bool once)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sv.Tag, translate)) return;   // 鼠标已移开被 StopMarquee 取消

            // 文字宽度取 TextBlock 在无限宽约束下量出的 DesiredSize（NoWrap → 就是文字真实宽度）。
            // 不能用 sv.Extent：ScrollViewer 的 Extent 有「不小于视口宽」的下限，短文字会被抬到视口宽，
            // 按它算一遍的行程就会在文字离场后多空跑 (视口宽 − 文字宽)/speed 秒——
            // 天气预警详情长（超过视口）所以看不出来，摄像头那条详情短，正好暴露出这个空档。
            var extent = sv.Extent.Width;
            var viewport = sv.Viewport.Width;  // 可见区宽度
            var tb = sv.Content as TextBlock;
            var hasText = tb != null && !string.IsNullOrEmpty(tb.Text);
            var desired = tb?.DesiredSize.Width ?? 0;   // 无限宽约束下量出的文字自然宽度
            var textWidth = desired > 0
                ? Math.Min(desired, extent)            // 兜底：量不出或量得比 Extent 还大就用 Extent
                : extent;

            // 文本已设但宽度还没量出来 → 布局尚未完成，下一帧再试（最多 5 次）。
            // Extent 有「不小于视口宽」的下限，短文字时它一上来就非 0，所以这里必须看 DesiredSize。
            if (hasText && (extent <= 0 || desired <= 0) && attempt < 5)
            {
                StartMarqueeWhenReady(sv, translate, attempt + 1, once);
                return;
            }

            // 经典弹幕（和 B 站一样）：文字整体从「视口右侧外」进场，一路匀速向左，滚出左侧后
            // 再从右侧重新进场，中途不停顿（行程 = 视口宽 + 文字宽，尾部一离场就立刻循环重播）。
            const double speed = 100.0;                        // 像素/秒
            var total = (textWidth + viewport) / speed;        // 滚一整遍的时长

            translate.X = viewport;                           // 起步位置：视口右侧外
            var start = DateTime.UtcNow;

            // 收尾截止时刻（相对 start 的秒数）：PositiveInfinity = 一直循环滚。
            // 点击预警时由 _marqueeStopAfterThisPass 改成「滚完当前这一遍就收」——
            // 取当前这一遍的结尾，所以是接着往下滚、不重头播。
            var stopAfter = once ? total : double.PositiveInfinity;
            _marqueeStopAfterThisPass = () =>
            {
                var e = (DateTime.UtcNow - start).TotalSeconds;
                var phase = e % total;                       // 当前这一遍已经走了多少秒
                // 距离这一遍结束不到 2 秒就再多滚一遍，免得点完立刻就收、像没反应
                stopAfter = e - phase + (total - phase < 2.0 ? 2 : 1) * total;
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var elapsed = (DateTime.UtcNow - start).TotalSeconds;

                // 一遍滚完（文字从右侧外进场、滚到左侧外）就收起详情，并放开重复点击的限制
                if (elapsed >= stopAfter)
                {
                    timer.Stop();
                    if (sv.Content is Control cc) cc.RenderTransform = null;
                    sv.Tag = null;
                    _marqueeStopAfterThisPass = null;
                    _alertMarqueeOncePlaying = false;
                    if (DataContext is FullScreenClockViewModel vm) vm.ExpandedAlertDetail = null;
                    return;
                }

                // 用 elapsed 对总循环时长取模实现无限循环重播，
                // 这样最左边的字一开始也在视口外右侧，不会一开场就被裁掉。
                translate.X = viewport - (elapsed % total) * speed;
            };
            timer.Start();
            sv.Tag = timer;   // 持有引用防 GC，同时作为「已启动」标记
        });
    }

    /// <summary>右上角日期连点计数（间隔超过 3 秒视为中断）</summary>
    private int _dateClickCount;
    private DateTime _lastDateClick = DateTime.MinValue;

    private void DateText_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastDateClick).TotalSeconds > 3) _dateClickCount = 0;
        _lastDateClick = now;
        _dateClickCount++;

        if (_dateClickCount >= 7)
        {
            _dateClickCount = 0;
            ShowAboutPopup();
        }
    }

    private AboutPopupWindow? _aboutWindow;

    private void ShowAboutPopup()
    {
        if (_aboutWindow is { IsVisible: true }) return;   // 已经开着，不要再叠一个

        // 旧窗口可能只是跟着主窗口一起藏了起来、没走过 Closed：这种窗口不会再触发 Closed，
        // 弹出时套上的蒙版就会一直留在 ClockSurface 上，重开大屏还是糊的。先收干净再开新的。
        DismissAboutPopup();
        if (TopLevel.GetTopLevel(this) is not Window owner) return;   // 预览那份不在窗口里，点了也不弹

        var vm = DataContext as FullScreenClockViewModel;
        var settings = Plugin.Settings;

        // 有烘好的模糊底图（磨砂 / 真•◯◯玻璃档）→ 把整面墙实时糊掉，大屏内容还看得见轮廓，
        // 关于窗口浮在上面。就糊这一阵子（弹出期间），关掉立刻撤掉 Effect，不留常驻开销。
        var frosted = Backdrop.HasBlurredBackdrop;
        if (frosted)
        {
            var radius = settings?.GlassBlurRadius ?? 24;
            Effect = new BlurEffect { Radius = Math.Clamp(radius, 4, 48) };
        }

        // 窗口底色跟**背景图明暗**走：暗图黑底白字、亮图白底黑字（不是跟界面主题走——
        // 暗色界面压在一张很亮的壁上时，深色方片会突兀得像贴上去的）。
        // 没有背景图时才退回界面主题。
        var light = vm?.Chrome.IsLight ?? false;
        if (settings?.BackgroundEnabled == true && ClockThemeApplier.BackgroundIsDark(settings) is { } imageDark)
            light = !imageDark;

        var window = new AboutPopupWindow(light, frosted);
        _aboutWindow = window;
        window.Closed += (_, _) => ClearAboutMask(window);
        try
        {
            window.Show(owner); // 以全屏窗口为 Owner（置顶、随主窗口关闭）
            PlaceAboutWindow(window, owner);
        }
        catch (Exception ex)
        {
            // 窗口没开出来也得把蒙版撤掉，否则整面墙会一直糊着
            System.Diagnostics.Debug.WriteLine($"[About] 显示关于窗口失败: {ex}");
            ClearAboutMask(window);
        }
    }

    /// <summary>
    /// 半屏模式下把关于窗口摆到看得见的那一条正中。
    /// 全屏时 CenterOwner 本来就居中于整窗，不用动。
    /// </summary>
    private void PlaceAboutWindow(Window window, Window owner)
    {
        if (Plugin.Settings is not { DisplayRegion: > 0 }) return;
        if (Shell.Bounds.Width <= 0 || Shell.Bounds.Height <= 0) return;

        try
        {
            var scale = owner.RenderScaling;
            var origin = Shell.PointToScreen(new Point(0, 0));
            var width = (int)Math.Round(window.Width * scale);
            var height = (int)Math.Round(window.Height * scale);

            window.Position = new PixelPoint(
                origin.X + (int)Math.Round((Shell.Bounds.Width * scale - width) / 2),
                origin.Y + (int)Math.Round((Shell.Bounds.Height * scale - height) / 2));
        }
        catch
        {
            // 定位失败就留在整窗正中，至少还看得见
        }
    }

    /// <summary>
    /// 关掉关于窗口并撤掉蒙版（按 Esc、退出全屏、主窗口被藏起来时都要走这里）。
    /// 返回是否真的有关窗口或蒙版在。
    /// </summary>
    public bool DismissAboutPopup()
    {
        var window = _aboutWindow;
        _aboutWindow = null;
        if (window is not null)
        {
            try { window.Close(); } catch { }
        }

        // 窗口可能早就没了、Closed 却没走到（比如跟着主窗口一起被藏起来），这里兜底清一次
        var hadMask = Effect is not null;
        if (hadMask)
        {
            Effect = null;
            InvalidateVisual();
        }

        return window is not null || hadMask;
    }

    /// <summary>
    /// 撤掉关于窗口弹出时给整个大屏套的那层实时模糊。
    /// 只认当前这一个窗口：换过窗口以后，旧窗口迟到的 Closed 不能再把新窗口的蒙版撤掉。
    /// </summary>
    private void ClearAboutMask(AboutPopupWindow window)
    {
        if (!ReferenceEquals(_aboutWindow, window)) return;
        _aboutWindow = null;
        Effect = null;
        InvalidateVisual();   // Effect 走合成器，明确标一次脏，别指望下一秒的时钟刷新顺手带出来
    }

    // ===== 快速设置 =====
    //
    // 大屏上直接调设置：底部「设置」按钮打开，面板贴在可见区域那一侧。
    // 内容不另做一套——塞一份 SettingsPage（Embedded 模式，右栏预览藏掉），
    // 设置项的读写、滑块拖动标记、位置限位全跟 CI 设置窗口里那页一模一样。

    private SettingsPage? _quickSettings;

    public bool IsQuickSettingsOpen => _quickSettings is not null;

    private void SettingsButton_Click(object? sender, RoutedEventArgs e) => ToggleQuickSettings();

    public void ToggleQuickSettings()
    {
        if (_quickSettings is not null) CloseQuickSettings();
        else OpenQuickSettings();
    }

    private void OpenQuickSettings()
    {
        if (_quickSettings is not null || IsPreview) return;

        var page = new SettingsPage(embedded: true);
        _quickSettings = page;
        QuickSettingsHost.Children.Add(page);
        QuickSettingsOverlay.IsVisible = true;
        ApplyQuickSettingsAppearance();
        UpdateQuickSettingsBounds();
    }

    /// <summary>关掉面板；本来就没开返回 false（Esc 用它决定要不要接着退出全屏）。</summary>
    public bool CloseQuickSettings()
    {
        if (_quickSettings is not { } page) return false;

        _quickSettings = null;
        QuickSettingsHost.Children.Remove(page);   // 真从树里摘掉才会走 Unloaded，把订阅摘干净
        QuickSettingsOverlay.IsVisible = false;
        ClearQuickSettingsBlur();
        return true;
    }

    /// <summary>
    /// 面板贴哪一侧、多宽、透不透明：半屏时收进看得见的那一条；
    /// 拖动设置项（AdjustingLayout）时淡到只剩一点轮廓。
    /// </summary>
    private void ApplyQuickSettingsAppearance()
    {
        if (_quickSettings is null) return;

        var region = Plugin.Settings?.DisplayRegion ?? 0;

        // 左半屏贴左、右半屏贴右；全屏统一贴右（与 CI 设置窗口里平时的位置一致）
        QuickSettingsCard.HorizontalAlignment = region == 1
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;

        // 宽度不超过可见区域，窄到 30% 也放得下
        var available = _visibleWidth > 0 ? _visibleWidth : Bounds.Width;
        if (available > 0)
            QuickSettingsCard.MaxWidth = Math.Max(300, available - 40);

        UpdateQuickSettingsBounds();
        UpdateQuickSettingsOpacity();
    }

    /// <summary>
    /// 面板只占「顶栏以下、按钮行以上」那一段：不压日期，也不盖住最小化/退出那排按钮。
    /// 上下留白按这两块的实际落位算，换半屏、换紧凑排版、改字号都不会错位。
    /// </summary>
    private void UpdateQuickSettingsBounds()
    {
        if (_quickSettings is null) return;

        var top = 20.0;
        var bottom = 20.0;

        if (HeaderGrid.TranslatePoint(new Point(0, HeaderGrid.Bounds.Height), this) is { } header)
            top = header.Y + 12;

        if (ButtonBar.TranslatePoint(default, this) is { } bar)
            bottom = Math.Max(12, Bounds.Height - bar.Y + 12);

        QuickSettingsCard.Margin = new Thickness(0, top, 20, bottom);
    }

    private void UpdateQuickSettingsOpacity()
    {
        if (_quickSettings is null) return;

        // 按住设置项拖动：面板淡下去、整屏模糊也撤掉——要看的正是后面被调的那块大屏。
        var adjusting = Plugin.Settings?.AdjustingLayout == true;
        QuickSettingsCard.Opacity = adjusting ? 0.35 : 1.0;
        QuickSettingsVeil.Opacity = adjusting ? 0.35 : 1.0;

        if (adjusting) ClearQuickSettingsBlur();
        else ApplyQuickSettingsBlur();
    }

    /// <summary>
    /// 面板打开时，把面板背后的整面墙**实时**糊掉（与关于窗口同一套做法、同一个半径）。
    /// 糊的是 <see cref="Shell"/>：面板的浮层在它外面，不会被自己糊住；后台的时钟、计数照常实时刷新。
    /// 没有模糊底图（纯色背景 / 玻璃关着）就不套——省一次整屏重绘。
    /// </summary>
    private void ApplyQuickSettingsBlur()
    {
        if (_quickSettings is null || Shell.Effect is not null) return;
        if (!Backdrop.HasBlurredBackdrop) return;

        var radius = Plugin.Settings?.GlassBlurRadius ?? 24;
        Shell.Effect = new BlurEffect { Radius = Math.Clamp(radius, 4, 48) };
        Shell.InvalidateVisual();
    }

    private void ClearQuickSettingsBlur()
    {
        if (Shell.Effect is null) return;
        Shell.Effect = null;
        Shell.InvalidateVisual();
    }

    /// <summary>点遮罩空白处关；点在面板（或面板里的控件）上不关。</summary>
    private void QuickSettingsOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, QuickSettingsCard))
                return;

        CloseQuickSettings();
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as FullScreenClockViewModel)?.ExitFullScreen();
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as FullScreenClockViewModel)?.Minimize();
    }

    /// <summary>鼠标进入计数区域：显示底部记录规则</summary>
    private void CounterArea_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm) vm.ShowCountRules = true;
    }

    /// <summary>鼠标离开计数区域：隐藏记录规则</summary>
    private void CounterArea_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm) vm.ShowCountRules = false;
    }
}
