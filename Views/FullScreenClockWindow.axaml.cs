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

    /// <summary>「点击后滚完这一遍就收」已生效：这期间重复点击不生效。</summary>
    private bool _alertMarqueeOncePlaying;

    /// <summary>把正在循环的弹幕改成「接着当前进度滚，滚完这一遍就收」（点击触发）。
    /// 只在弹幕真的在滚时非空；null 表示没在播或已经在收尾状态。</summary>
    private Action? _marqueeStopAfterThisPass;

    /// <summary>鼠标悬停某条预警：在顶部弹幕带显示其详情并循环滚动（自动切换，不需要先收起旧条）。</summary>
    private void AlertPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not AlertDisplayItem item) return;
        if (DataContext is not FullScreenClockViewModel vm) return;

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
        if (_alertMarqueeOncePlaying) return;   // 已经在收尾的那一遍里，忽略
        if (sender is not Control c || c.DataContext is not AlertDisplayItem item) return;
        if (DataContext is not FullScreenClockViewModel vm) return;

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

            // 不用手动测量文字宽度：ScrollViewer 的 Extent 是框架根据 NoWrap 内容算出的真实宽度，
            // 绝不会像 TextBlock 那样被容器压成视口宽（此前 textWidth≈0 导致只滚窗口宽度就循环）。
            var extent = sv.Extent.Width;      // 文字真实宽度（框架测量）
            var viewport = sv.Viewport.Width;  // 可见区宽度
            var hasText = sv.Content is TextBlock tb && !string.IsNullOrEmpty(tb.Text);

            // 文本已设但 Extent 还没更新 → 布局尚未完成，下一帧再试（最多 5 次）。
            if (extent <= 0 && hasText && attempt < 5)
            {
                StartMarqueeWhenReady(sv, translate, attempt + 1, once);
                return;
            }

            // 经典弹幕（和 B 站一样）：文字整体从「视口右侧外」进场，一路匀速向左，滚出左侧后
            // 再从右侧重新进场，中途不停顿。不管文字长短一律这么滚——短预警（如高温预警全文没占
            // 满一屏）以前会退化成「整段直接显示」，与弹幕的观感不一致。
            const double speed = 100.0;                       // 像素/秒
            var total = (extent + viewport) / speed;          // 滚一整遍的时长

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
