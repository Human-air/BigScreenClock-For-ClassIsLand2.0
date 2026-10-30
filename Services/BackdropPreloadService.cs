using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 课前预烘：目标课程开始前两分钟，把背景图的解码 / 模糊 / 像素快照先在后台做好，
/// 放进 <see cref="BackdropPreloadCache"/>。大屏到点点亮时直接取现成的。
///
/// 为什么要卡在课前两分钟：这段活（4K 图解码 + 整张模糊 + 两份像素快照）在一瞬间要吃掉
/// 几百兆临时内存，正好撞上 CI 每次开屏那一刻 —— 也就是学校那台机器被内存看门狗
/// （1.40 GiB）强杀的那几秒。提前做完，GC 有两分钟慢慢回收，开屏那一刻只剩「建窗口」。
/// 平时不常驻，免得整天白白占着一两百兆。
///
/// 平时上课日没有目标课程，什么也不做；手动打开大屏时取不到预烘的，照旧当场烘（行为不变）。
/// </summary>
public class BackdropPreloadService : BackgroundService
{
    /// <summary>提前多久开烘。慢机做完 4K 解码 + 模糊也就十几秒，两分钟足够，又不会太早白占内存。</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(2);

    /// <summary>多久看一眼课表。20 秒的误差相对于两分钟的提前量无所谓。</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(20);

    private readonly PluginSettings _settings;
    private readonly FullScreenClockViewModel _viewModel;

    public BackdropPreloadService(PluginSettings settings, FullScreenClockViewModel viewModel)
    {
        _settings = settings;
        _viewModel = viewModel;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.BackgroundEnabled)
            or nameof(PluginSettings.BackgroundImageFile)
            or nameof(PluginSettings.BackgroundImageStretch)
            or nameof(PluginSettings.GlassBlurRadius)
            or nameof(PluginSettings.GlassMode))
        {
            BackdropPreloadCache.Clear();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 换图 / 换填充方式 / 改模糊半径 / 改玻璃档：预烘那份就作废了。
        // 不立刻重烘，等下一轮 Poll 到了、且还在课前两分钟窗口里再烘。
        _settings.PropertyChanged += OnSettingsChanged;

        try
        {
            // 等 CI 起来、课表服务也就位了再说
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Debug.WriteLine($"[预烘] 检查失败: {ex}"); }

                await Task.Delay(Poll, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        finally
        {
            _settings.PropertyChanged -= OnSettingsChanged;
            BackdropPreloadCache.Clear();
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        // 烘好了却一直没开屏（课取消了 / 换了课）：放掉，别攥着上百兆占一整天
        if (BackdropPreloadCache.IsStale(TimeSpan.FromMinutes(10))) BackdropPreloadCache.Clear();

        // 大屏开着就不用烘：那一套已经在窗口手里了
        if (_viewModel.IsWindowVisible) return;

        // 课表与屏幕信息都得在 UI 线程上问
        var (next, now, pixels) = await Dispatcher.UIThread.InvokeAsync(() =>
            (_viewModel.GetNextTargetCourseStart(), _viewModel.VirtualNow, MainScreenPixelSize()));

        if (next is not { } start || pixels is not { } size) return;

        var lead = start - now;
        if (lead > Lead || lead <= TimeSpan.Zero) return;   // 还没到两分钟 / 已经上课了

        // 背景总开关关着 / 没设图：大屏那边根本不画背景，烘了也用不上
        var path = _settings.BackgroundEnabled
            ? BackgroundImageStore.ResolvePath(_settings.BackgroundImageFile)
            : null;
        if (path is null) return;

        var stretch = _settings.BackgroundImageStretch;
        var radius = _settings.GlassBlurRadius;
        // 与 BackdropLayer 的 WantBlurNow() 保持一致：磨砂 / 折射两档要模糊，「完整显示」的补边也要
        var wantBlur = _settings.GlassMode is (int)GlassMode.Frosted or (int)GlassMode.Liquid
                       || stretch == (int)BackgroundImageStretch.Fit;

        if (BackdropPreloadCache.IsReadyFor(path, size, stretch, radius, wantBlur)) return;
        if (!BackdropPreloadCache.TryBeginBuild()) return;   // 上一份还在烘
        if (token.IsCancellationRequested) { BackdropPreloadCache.EndBuild(); return; }

        try
        {
            var set = await Task.Run(() => BackdropImageSet.Build(path, size, stretch, radius, wantBlur), token);

            if (set.IsEmpty)
            {
                // 图读不了（被删了 / 格式不支持）：不缓存，开屏时按老路走（那边同样会失败并退回纯色）
                set.Dispose();
                BackdropPreloadCache.EndBuild();
                Log($"没烘成：{Path.GetFileName(path)} 读不出来");
                return;
            }

            BackdropPreloadCache.Publish(set);

            // 这一下造了几百兆临时数组。趁现在（离上课还有一两分钟）强制回收一遍，
            // 把内存还给系统 —— 等 19:00 大屏点亮时再回收，看门狗可能已经先采样到峰值了。
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

            Log($"已就绪：{Path.GetFileName(path)} @ {size.Width}×{size.Height}，" +
                $"模糊半径 {radius:0.#}，距上课 {lead.TotalSeconds:0} 秒");
        }
        catch (OperationCanceledException)
        {
            BackdropPreloadCache.EndBuild();
        }
    }

    /// <summary>
    /// 记一行到配置目录的 preload.log。只在开了「噪声调试日志」时写 —— 那本来就是
    /// 「回学校排查问题」用的开关；平时不写，免得白白留个文件。
    /// </summary>
    private void Log(string message)
    {
        if (!_settings.EnableNoiseDebugLog) return;
        try
        {
            var dir = Plugin.ConfigFolder;
            if (string.IsNullOrEmpty(dir)) return;

            var path = Path.Combine(dir, "preload.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不了不影响功能
        }
    }

    /// <summary>
    /// 大屏会开在哪块屏幕上，就按那块屏幕的物理分辨率烘。
    ///
    /// 大屏是 CI 主窗口所在的那块屏幕（跟设置页预览同一套判断）；主窗口还没建好时退回主屏。
    /// 估出来的尺寸跟真开窗时差一两个像素没关系 —— <see cref="BackdropImageSet.Matches"/> 有容差。
    /// </summary>
    private static PixelSize? MainScreenPixelSize()
    {
        try
        {
            var main = AppBase.Current?.MainWindow;
            var screens = main?.Screens;
            if (main is null || screens is null) return null;

            var screen = screens.Primary;
            if (main is not null && screens.ScreenFromWindow(main) is { } own) screen = own;
            if (screen is null) return null;

            var bounds = screen.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            return new PixelSize(bounds.Width, bounds.Height);
        }
        catch
        {
            return null;
        }
    }
}
