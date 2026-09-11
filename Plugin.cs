using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock;

[PluginEntrance]
public class Plugin : PluginBase
{
    public static PluginSettings? Settings { get; private set; }
    public static IServiceProvider? ServiceProvider { get; set; }
    public static string? ConfigFolder { get; private set; }

    private static readonly System.Timers.Timer SettingsSaveDebouncer =
        new(400) { AutoReset = false };

    /// <summary>
    /// 主动保存设置到文件
    /// </summary>
    public static void SaveSettings()
    {
        if (Settings == null || ConfigFolder == null) return;
        var path = Path.Combine(ConfigFolder, "Settings.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        ConfigureFileHelper.SaveConfig(path, Settings);
    }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        ConfigFolder = PluginConfigFolder;

        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(
            Path.Combine(ConfigFolder, "Settings.json"));

        // 设置保存防抖：拖滑条/连续修改不会狂写盘，停顿 400ms 后统一保存
        SettingsSaveDebouncer.Elapsed += (_, _) =>
        {
            try { SaveSettings(); } catch { }
        };
        Settings.PropertyChanged += (_, _) =>
        {
            SettingsSaveDebouncer.Stop();
            SettingsSaveDebouncer.Start();
        };

        services.AddSingleton(Settings);

        services.AddSingleton<DecibelMeterService>();
        services.AddSingleton<FullScreenClockViewModel>();
        services.AddSingleton<AutoTriggerService>();

        services.AddHostedService<ServiceProviderCapture>();
        services.AddHostedService<AutoTriggerService>();
        services.AddHostedService<TrayMenuService>();

        services.AddSettingsPage<EveningSelfStudyClock.Settings.SettingsPage>();
    }
}
