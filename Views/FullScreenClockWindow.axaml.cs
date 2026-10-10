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
            if (e.Property == Visual.IsVisibleProperty && !IsVisible)
                (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
        };
        Closed += (_, _) => (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
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
