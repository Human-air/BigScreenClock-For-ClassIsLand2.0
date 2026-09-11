using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// CI 托盘右键菜单加「进入大屏时钟」入口。
///
/// 扩展点说明：ITaskBarIconService.MoreOptionsMenuItems 只是普通内容列表，CI 很可能只在
/// 启动构建菜单时读一次，插件之后（HostedService 阶段）往里面加不会反映到已建好的菜单上。
/// 这里改为直接往真正会弹出的 NativeMenu 里加：托盘图标右键菜单 = MainTaskBarIcon.Menu。
/// 并挂 NativeMenu.NeedsUpdate（Avalonia 官方在每次菜单弹出前触发的同步钩子）幂等补注册，
/// 保证无论 CI 何时/以何种顺序重建菜单，打开右键菜单前我们的项都会在。
/// </summary>
public class TrayMenuService : IHostedService
{
    private readonly FullScreenClockViewModel _viewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly PluginSettings _settings;
    private NativeMenuItem? _menuItem;
    private bool _hooked;

    public TrayMenuService(
        FullScreenClockViewModel viewModel,
        IServiceProvider serviceProvider,
        PluginSettings settings)
    {
        _viewModel = viewModel;
        _serviceProvider = serviceProvider;
        _settings = settings;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 不阻塞启动：等一小段让 CI 托盘就绪，再在 UI 线程注册（Avalonia 对象要贴 UI 线程）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, cancellationToken);
                Dispatcher.UIThread.Post(() => EnsureRegistered());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayMenu] 注册托盘菜单失败: {ex}");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 幂等注册：把「进入大屏时钟」放进真正弹出的托盘菜单（缺哪个就补哪个）。
    /// 每次菜单弹出前（NeedsUpdate）会再走一遍，防止被 CI 重建冲掉。
    /// </summary>
    public void EnsureRegistered()
    {
        try
        {
            var tray = _serviceProvider.GetService<ITaskBarIconService>();
            if (tray is null) return;
            var menu = tray.MainTaskBarIcon?.Menu;   // 托盘右键真正弹出的菜单
            if (menu is null) return;

            if (!_hooked)
            {
                menu.NeedsUpdate += (_, _) => EnsureRegistered();
                _hooked = true;
            }

            // 开关关闭：把入口从菜单里撤掉（每次弹出前都会走一遍，设置改完下次打开菜单即生效）
            if (!_settings.ShowTrayMenuEntry)
            {
                if (_menuItem is not null && menu.Items.Contains(_menuItem))
                    menu.Items.Remove(_menuItem);
                return;
            }

            if (_menuItem is null)
            {
                _menuItem = new NativeMenuItem
                {
                    Header = "进入大屏时钟",
                    Command = new RelayCommand(() => _viewModel.Show()),
                };
            }
            if (!menu.Items.Contains(_menuItem))
                menu.Items.Add(_menuItem);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayMenu] 添加托盘菜单项失败: {ex}");
        }
    }
}
