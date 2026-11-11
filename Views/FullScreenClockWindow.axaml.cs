using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 全屏窗口。窗口本身只管窗口的事（全屏、躲鼠标、Esc/F11 退出），
/// 画面全在 <see cref="ClockSurface"/> 里 —— 设置页的预览放的也是它。
/// </summary>
public partial class FullScreenClockWindow : Window
{
    private CancellationTokenSource? _hideCursorCts;

    public FullScreenClockWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        PointerMoved += OnPointerMoved;

        // 窗口收起时没有限位在起作用了，把滑块范围放回整段（不然上次地震留下的下限会一直卡着设置页）
        PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty || IsVisible) return;
            // 关于窗口是主窗口的从属窗口，主窗口藏起来时它会跟着藏、却不会触发 Closed，
            // 弹出时给大屏套的蒙版就会一直留在 ClockSurface 上（重开大屏还是糊的）。这里主动收掉。
            Surface.DismissAboutPopup();
            (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
        };
        Closed += (_, _) => (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // 快速设置开着时，Esc 先关面板，不退大屏
        if (e.Key == Key.Escape && Surface.CloseQuickSettings())
        {
            e.Handled = true;
            return;
        }

        // 关于窗口开着、键却落在大屏这边（窗口没拿到焦点）时，Esc 只关关于，不要连大屏一起退
        if (e.Key == Key.Escape && Surface.DismissAboutPopup())
        {
            e.Handled = true;
            return;
        }

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

        // 快速设置开着时别把鼠标藏起来：面板里要拖滑块、点下拉框
        if (Surface.IsQuickSettingsOpen) return;

        _hideCursorCts = new CancellationTokenSource();
        var token = _hideCursorCts.Token;
        Task.Delay(10000, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => Cursor = null);
        }, TaskScheduler.Default);
    }
}
