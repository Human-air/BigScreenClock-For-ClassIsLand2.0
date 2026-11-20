using System.Net.Http;
using System.Text;
using System.Text.Json;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 计数算法选择上报：只在用户勾选、且配置了统计接口地址时发送。
/// 只上传所选的算法和插件版本，不含课程、音量、设备名或任何课堂数据。
/// </summary>
public static class AlgorithmChoiceReporter
{
    /// <summary>统计接口地址。留空表示不上传；填入后需能接收 JSON POST。</summary>
    private const string Endpoint = "";

    /// <summary>
    /// 数据收集截止日期（含当天）。之后不再上传，弹窗也不再出现。
    /// 2027-03-22 之后发布的第一个版本删除：本类、NoiseAlgorithmChoiceWindow、
    /// Plugin 与本类调用、SettingsPage 的上传勾选与弹窗触发，以及 PluginSettings 的
    /// ShareAlgorithmChoice / AlgorithmChoicePrompted（NoiseAlgorithmVersion 保留）。
    /// </summary>
    public static readonly DateTimeOffset CollectionEnd = new(2027, 3, 22, 23, 59, 59, TimeSpan.Zero);

    public static bool IsCollectionOpen => DateTimeOffset.UtcNow <= CollectionEnd;

    public static void TrySend(PluginSettings settings)
    {
        if (!IsCollectionOpen) return;
        if (!settings.ShareAlgorithmChoice) return;
        if (string.IsNullOrWhiteSpace(Endpoint)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var json = JsonSerializer.Serialize(new
                {
                    algorithm = settings.NoiseAlgorithmVersion,
                    pluginVersion = PluginVersion.Current,
                    at = DateTimeOffset.UtcNow
                });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await client.PostAsync(Endpoint, content).ConfigureAwait(false);
            }
            catch
            {
                // 统计失败不影响使用，静默忽略
            }
        });
    }
}
