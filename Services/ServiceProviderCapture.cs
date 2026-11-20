using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 在应用启动时立即捕获 IServiceProvider，供 SettingsPage 等使用。
/// 这必须在 AutoTriggerService 之前运行，确保 SettingsPage 能获取到服务。
/// </summary>
public class ServiceProviderCapture : IHostedService
{
    public ServiceProviderCapture(IServiceProvider serviceProvider)
    {
        Plugin.ServiceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
