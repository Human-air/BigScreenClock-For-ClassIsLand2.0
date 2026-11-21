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
        BackgroundDiagnostics.DestinationFolder = ConfigFolder;
        BackgroundDiagnostics.Write("session-start", $"pluginVersion={PluginVersion.Current}");

        var settingsPath = Path.Combine(ConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(settingsPath);
        try
        {
            Settings = SettingsMigration.ApplyPending(settingsPath, Settings);
        }
        catch (Exception ex)
        {
            BackgroundDiagnostics.Write("settings-import-failed", ex.GetType().Name);
        }

        // 「正在拖位置/字号」纯界面状态，绝不该跨会话留着：万一哪次拖到一半崩了把它存了下来，
        // 下次启动玻璃就一直是收起的。这里强制归零。
        Settings.AdjustingLayout = false;

        // 已经有配置文件 = 老版本升级上来：把原有那套颜色原样留在黑暗主题下，
        // 别让「跟随 ClassIsland」在首次启动时就把外观换成另一套（新装用户没有这个问题，默认跟随）
        if (File.Exists(settingsPath))
            ClockThemeApplier.MigrateLegacySettings(Settings);

        // 勾选了上传算法选择时，每次启动 ClassIsland 最多发送一次
        AlgorithmChoiceReporter.TrySend(Settings);

        // 设置保存防抖：拖滑条/连续修改不会狂写盘，停顿 400ms 后统一保存
        SettingsSaveDebouncer.Elapsed += (_, _) =>
        {
            try { SaveSettings(); } catch { }
        };
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PluginSettings.RuntimeGlassAppearance)) return;
            SettingsSaveDebouncer.Stop();
            SettingsSaveDebouncer.Start();
        };

        services.AddSingleton(Settings);

        services.AddSingleton<DecibelMeterService>();
        services.AddSingleton<CameraActivityService>();
        services.AddSingleton<FeishuNotifier>();
        services.AddSingleton<FullScreenClockViewModel>();
        services.AddSingleton<AutoTriggerService>();
        services.AddSingleton<SnitchHotkeyService>();

        services.AddHostedService<ServiceProviderCapture>();
        services.AddHostedService<AutoTriggerService>();
        services.AddHostedService<BackdropPreloadService>();
        services.AddHostedService<TrayMenuService>();
        // 用同一个实例当 HostedService：AddHostedService<T> 会另建一个对象，
        // 变成两个实例各自注册一遍全局热键（第二个必然注册失败）
        services.AddHostedService(sp => sp.GetRequiredService<SnitchHotkeyService>());

        // CI 按 DefaultGroupToMenu 自动建组时图标为空；先放一个带时钟图标的分组，
        // AddAction 会复用同名组，左侧「大屏时钟」前面就有图标了。
        if (!ClassIsland.Core.Abstractions.Services.IActionService.ActionMenuTree
                .TryGetValue("大屏时钟", out _))
        {
            ClassIsland.Core.Abstractions.Services.IActionService.ActionMenuTree.Add(
                new ClassIsland.Core.Models.Automation.ActionMenuTreeGroup("大屏时钟", "\ue4c4"));
        }

        services.AddAction<EveningSelfStudyClock.Automation.ShowClockAction>();
        services.AddAction<EveningSelfStudyClock.Automation.HideClockAction>();

        services.AddSettingsPage<EveningSelfStudyClock.Settings.SettingsPage>();
    }
}
