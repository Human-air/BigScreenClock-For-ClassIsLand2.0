using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using EveningSelfStudyClock.Helpers;
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

        var settingsPath = Path.Combine(ConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(settingsPath);

        // 已经有配置文件 = 老版本升级上来：把原有那套颜色原样留在黑暗主题下，
        // 别让「跟随 ClassIsland」在首次启动时就把外观换成另一套（新装用户没有这个问题，默认跟随）
        if (File.Exists(settingsPath))
            ClockThemeApplier.MigrateLegacySettings(Settings);

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
