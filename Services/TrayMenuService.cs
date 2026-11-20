using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock.Services;

/// <summary>跟随托盘菜单生命周期注册入口，兼容启动延迟及其他插件重建菜单。</summary>
public class TrayMenuService(
    FullScreenClockViewModel viewModel,
    IServiceProvider serviceProvider,
    PluginSettings settings) : IHostedService
{
    private NativeMenuItem? _menuItem;
    private NativeMenu? _menu;
    private TrayIcon? _icon;
    private DispatcherTimer? _retryTimer;
    private bool _stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_retryTimer is not null || cancellationToken.IsCancellationRequested) return;
            _stopped = false;
            settings.PropertyChanged += OnSettingsChanged;
            _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _retryTimer.Tick += OnRetry;
            _retryTimer.Start();
            EnsureRegistered();
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _stopped = true;
            settings.PropertyChanged -= OnSettingsChanged;
            if (_retryTimer is not null)
            {
                _retryTimer.Stop();
                _retryTimer.Tick -= OnRetry;
                _retryTimer = null;
            }
            DetachMenu();
            if (_icon is not null) _icon.PropertyChanged -= OnIconChanged;
            _icon = null;
        });
    }

    private void OnIconChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TrayIcon.MenuProperty) EnsureRegistered();
    }

    private void OnRetry(object? sender, EventArgs e) => EnsureRegistered();
    private void OnMenuNeedsUpdate(object? sender, EventArgs e) => EnsureRegistered();
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginSettings.ShowTrayMenuEntry))
            Dispatcher.UIThread.Post(EnsureRegistered);
    }

    private void DetachMenu()
    {
        if (_menu is null) return;
        _menu.NeedsUpdate -= OnMenuNeedsUpdate;
        if (_menuItem is not null) _menu.Items.Remove(_menuItem);
        _menu = null;
    }

    public void EnsureRegistered()
    {
        if (_stopped) return;
        try
        {
            var icon = serviceProvider.GetService<ITaskBarIconService>()?.MainTaskBarIcon;
            if (!ReferenceEquals(icon, _icon))
            {
                if (_icon is not null) _icon.PropertyChanged -= OnIconChanged;
                _icon = icon;
                if (_icon is not null) _icon.PropertyChanged += OnIconChanged;
            }
            var current = _icon?.Menu;
            if (!ReferenceEquals(current, _menu))
            {
                DetachMenu();
                _menu = current;
                if (_menu is not null) _menu.NeedsUpdate += OnMenuNeedsUpdate;
            }
            if (_menu is null) return;
            if (!settings.ShowTrayMenuEntry)
            {
                if (_menuItem is not null) _menu.Items.Remove(_menuItem);
                return;
            }
            _menuItem ??= new NativeMenuItem
            {
                Header = "进入大屏时钟",
                Command = new RelayCommand(viewModel.Show)
            };
            if (!_menu.Items.Contains(_menuItem)) _menu.Items.Add(_menuItem);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayMenu] 注册托盘菜单失败: {ex}");
        }
    }
}
