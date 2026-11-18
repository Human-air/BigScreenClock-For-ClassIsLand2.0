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
    private readonly DispatcherTimer _hideCursorTimer;

    internal Models.GlassMode EffectiveGlassMode => Surface.EffectiveGlassMode;
    internal (int Batches, int Panels) GlassBakeTotals => Surface.GlassBakeTotals;

    internal void ObserveUiStall(TimeSpan now, double milliseconds) => Surface.ObserveUiStall(now, milliseconds);

    public FullScreenClockWindow()
    {
        InitializeComponent();
        // 鼠标每移动一次只重置同一支定时器，避免每秒创建数十个延迟任务和 CTS。
        _hideCursorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _hideCursorTimer.Tick += OnHideCursor;
        KeyDown += OnKeyDown;
        PointerMoved += OnPointerMoved;

        // 窗口收起时没有限位在起作用了，把滑块范围放回整段（不然上次地震留下的下限会一直卡着设置页）
        PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty && e.Property != WindowStateProperty) return;
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                Surface.ResumeVisualWork();
                return;
            }

            _hideCursorTimer.Stop();
            Surface.SuspendVisualWork();
            (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
        };
        Closed += (_, _) =>
        {
            Plugin.Settings?.SetRuntimeGlassAppearance(null);
            _hideCursorTimer.Stop();
            _hideCursorTimer.Tick -= OnHideCursor;
            Surface.SuspendVisualWork();
            (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
        };
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
        _hideCursorTimer.Stop();
        // 快速设置开着、窗口隐藏或最小化时不安排隐藏鼠标的工作。
        if (Surface.IsQuickSettingsOpen || !IsVisible || WindowState == WindowState.Minimized) return;
        _hideCursorTimer.Start();
    }

    private void OnHideCursor(object? sender, EventArgs e)
    {
        _hideCursorTimer.Stop();
        if (IsVisible && WindowState != WindowState.Minimized && !Surface.IsQuickSettingsOpen)
            Cursor = null;
    }
}
