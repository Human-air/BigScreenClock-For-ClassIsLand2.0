using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Views;

public partial class FullScreenClockWindow : Window
{
    private CancellationTokenSource? _hideCursorCts;
    private AboutPopupWindow? _aboutWindow;

    /// <summary>右上角日期连点计数（间隔超过 3 秒视为中断）</summary>
    private int _dateClickCount;
    private DateTime _lastDateClick = DateTime.MinValue;

    public FullScreenClockWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        PointerMoved += OnPointerMoved;
        RulesPopup.PlacementTarget = CounterAreaBorder; // 规则浮层锚定在计数区域上方
        SizeChanged += (_, _) => UpdateReminderPanelWidth();
        DataContextChanged += (_, _) => UpdateReminderPanelWidth();
    }

    /// <summary>提醒面板最大宽 = 屏幕宽 × 2/3；第一行提醒合并阈值 = 屏幕宽 × 1/3。</summary>
    private void UpdateReminderPanelWidth()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;
        vm.ReminderPanelMaxWidth = Bounds.Width * 2.0 / 3.0;
        vm.MergeThreshold = Bounds.Width / 3.0;
    }

    /// <summary>鼠标悬停某条预警：在顶部弹幕带显示其详情（自动切换，不需要先收起旧条）。</summary>
    private void AlertPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not AlertDisplayItem item) return;
        if (DataContext is not FullScreenClockViewModel vm) return;

        vm.ExpandedAlertDetail = item.Detail;
        // 顶部只有一个弹幕 ScrollViewer。先停旧弹幕再重新启动：悬停另一条时 Text 已变，
        // 不能靠 SizeChanged 触发，显式重启。
        StopMarquee(AlertDetailScroll);
        StartMarquee(AlertDetailScroll);
    }

    /// <summary>鼠标移出该条预警：收起顶部弹幕。</summary>
    private void AlertPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is not FullScreenClockViewModel vm) return;
        vm.ExpandedAlertDetail = null;
        StopMarquee(AlertDetailScroll);
    }

    /// <summary>停止弹幕：停定时器、清平移、清启动标记（下次 StartMarquee 才能重新启动）。</summary>
    private static void StopMarquee(ScrollViewer sv)
    {
        if (sv.Tag is DispatcherTimer timer) timer.Stop();
        if (sv.Content is Control content) content.RenderTransform = null;
        sv.Tag = null;
    }

    /// <summary>预警详情弹幕：文字超宽时自动横向滚动（从右向左），不用手动拖动滚动条。</summary>
    private void DetailScroll_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.IsVisible)
            StartMarquee(sv);
    }

    private static void StartMarquee(ScrollViewer sv)
    {
        if (!sv.IsEffectivelyVisible) return;   // 单行/双行只有当前布局那个可见，隐藏版跳过
        if (sv.Tag is not null) return;         // 已启动过，防反复触发重复动画

        // 同步挂平移、先把文字推到视口右侧外（布局未完成前 viewport 未知，用大偏移占位）：
        // 这样「重开弹幕的瞬间」不会有一帧把原文整段占满显示框（此前 translate.X 默认 0，
        // 首个 Tick 之前会闪一下完整开头）。
        var translate = new TranslateTransform { X = 1e6 };
        if (sv.Content is Control c0) c0.RenderTransform = translate;
        sv.Tag = translate;               // 占位标记；StopMarquee 会清掉，挂起的回调据此退出
        StartMarqueeWhenReady(sv, translate, 0);
    }

    /// <summary>等布局完成（ScrollViewer.Extent 就绪）后再初始化弹幕；未就绪则下一帧重试。</summary>
    private static void StartMarqueeWhenReady(ScrollViewer sv, TranslateTransform translate, int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sv.Tag, translate)) return;   // 鼠标已移开被 StopMarquee 取消

            // 不用手动测量文字宽度：ScrollViewer 的 Extent 是框架根据 NoWrap 内容算出的真实宽度，
            // 绝不会像 TextBlock 那样被容器压成视口宽（此前 textWidth≈0 导致只滚窗口宽度就循环）。
            var extent = sv.Extent.Width;      // 文字真实宽度（框架测量）
            var viewport = sv.Viewport.Width;  // 可见区宽度
            var hasText = sv.Content is TextBlock tb && !string.IsNullOrEmpty(tb.Text);

            // 文本已设但 Extent 还没更新 → 布局尚未完成，下一帧再试（最多 5 次）。
            if (extent <= 0 && hasText && attempt < 5)
            {
                StartMarqueeWhenReady(sv, translate, attempt + 1);
                return;
            }

            if (extent <= viewport)
            {
                if (sv.Content is Control cc) cc.RenderTransform = null;   // 没超宽：还原正常显示
                sv.Tag = null;
                return;
            }

            // 经典弹幕：文字整体从「视口右侧外」进场，一路向左，滚出左侧后再从右侧重新进场。
            // 位移 p = viewport → -extent，用 (elapsed*speed) 对 (extent+viewport) 取模实现无限循环重播，
            // 这样最左边的字一开始也在视口外右侧，不会一开场就被裁掉。
            translate.X = viewport;
            var total = extent + viewport;
            const double speed = 100.0;                      // 像素/秒
            var start = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                translate.X = viewport - (elapsed * speed) % total;
            };
            timer.Start();
            sv.Tag = timer;   // 持有引用防 GC，同时作为「已启动」标记
        });
    }

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

    private void ShowAboutPopup()
    {
        if (_aboutWindow != null && _aboutWindow.IsVisible) return;
        _aboutWindow = new AboutPopupWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show(this); // 以全屏窗口为 Owner（置顶、随主窗口关闭）
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

    /// <summary>鼠标离开计数区域：隐藏记录规则，仅留免责声明</summary>
    private void CounterArea_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm) vm.ShowCountRules = false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            (DataContext as FullScreenClockViewModel)?.ExitFullScreen();
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Cursor = Cursor.Default;
        _hideCursorCts?.Cancel();
        _hideCursorCts = new CancellationTokenSource();
        var token = _hideCursorCts.Token;
        Task.Delay(10000, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => Cursor = null);
        }, TaskScheduler.Default);
    }
}
