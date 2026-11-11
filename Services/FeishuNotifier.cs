using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.NoiseDetection;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 飞书推送服务（纯文本 / 卡片）。
/// 全部消息走「自定义机器人 Webhook」的交互式卡片，不依赖自建应用与 tenant_access_token，
/// 因此不需要 App ID / App Secret、也不需要群 chat_id。
/// 说明：照片与现场录音的上传通道已按需求移除，卡片只承载文字信息。
/// </summary>
public class FeishuNotifier
{
    private readonly PluginSettings _settings;

    public FeishuNotifier(PluginSettings settings)
    {
        _settings = settings;
    }

    /// <summary>噪音段结束提醒。level 为 Normal / Noisy。</summary>
    public async Task<(bool Ok, string Message)> SendNoiseAsync(
        NoiseLevel level, double smoothedRms, string course, int normalCount, int noisyCount)
    {
        if (!_settings.EnableFeishuNotify || !_settings.FeishuNotifyNoise)
            return (false, "噪音推送未启用");
        if (level == NoiseLevel.Normal && !_settings.FeishuNotifyNormal)
            return (false, "「一般」等级不推送");

        var isNoisy = level == NoiseLevel.Noisy;
        var title = isNoisy ? "🔊 教室吵闹提醒" : "🔉 教室音量偏高";
        var template = isNoisy ? "red" : "yellow";

        var fields = new List<(string Label, string Value)>
        {
            ("课程", string.IsNullOrWhiteSpace(course) ? "（未知课程）" : course),
            ("等级", isNoisy ? "吵闹" : "一般"),
            ("实时音量", smoothedRms.ToString("F4", CultureInfo.InvariantCulture)),
            ("时间", DateTime.Now.ToString("HH:mm:ss")),
            ("本节累计", $"一般 {normalCount} 次 · 吵闹 {noisyCount} 次"),
        };
        if (!string.IsNullOrWhiteSpace(_settings.DeviceName))
            fields.Add(("设备", _settings.DeviceName));

        return await SendInteractiveAsync(title, template, fields, BuildNote(), false);
    }

    /// <summary>快捷键「打小报告」：橙色卡片标注手动触发现场报告。</summary>
    public async Task<(bool Ok, string Message)> SendSnitchAsync(string course, string hotkeyText)
    {
        if (!_settings.EnableFeishuNotify)
            return (false, "未启用飞书推送");

        var fields = new List<(string Label, string Value)>
        {
            ("课程", string.IsNullOrWhiteSpace(course) ? "（未知课程）" : course),
            ("触发方式", $"快捷键 {hotkeyText}（手动上报）"),
            ("时间", DateTime.Now.ToString("HH:mm:ss")),
        };
        if (!string.IsNullOrWhiteSpace(_settings.DeviceName))
            fields.Add(("设备", _settings.DeviceName));

        return await SendInteractiveAsync("📣 教室现场报告（打小报告）", "orange", fields, BuildNote(), true);
    }

    /// <summary>进入大屏时钟通知。</summary>
    public async Task<(bool Ok, string Message)> SendEnterAsync(string course)
    {
        if (!_settings.EnableFeishuNotify || !_settings.FeishuNotifyEnterExit)
            return (false, "出入大屏推送未启用");

        var fields = new List<(string Label, string Value)>
        {
            ("课程", string.IsNullOrWhiteSpace(course) ? "（未知课程）" : course),
            ("时间", DateTime.Now.ToString("HH:mm:ss")),
        };
        if (!string.IsNullOrWhiteSpace(_settings.DeviceName))
            fields.Add(("设备", _settings.DeviceName));

        return await SendInteractiveAsync("🖥 已进入大屏时钟", "blue", fields, BuildNote(), false);
    }

    /// <summary>退出大屏时钟通知（附本次会话噪音汇总）。</summary>
    public async Task<(bool Ok, string Message)> SendExitAsync(
        string course, TimeSpan duration, int normalCount, int noisyCount)
    {
        if (!_settings.EnableFeishuNotify || !_settings.FeishuNotifyEnterExit)
            return (false, "出入大屏推送未启用");

        var durText = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分"
            : $"{duration.Minutes} 分 {duration.Seconds} 秒";

        var fields = new List<(string Label, string Value)>
        {
            ("课程", string.IsNullOrWhiteSpace(course) ? "（未知课程）" : course),
            ("时间", DateTime.Now.ToString("HH:mm:ss")),
            ("持续时长", durText),
            ("本节累计", $"一般 {normalCount} 次 · 吵闹 {noisyCount} 次"),
        };
        if (!string.IsNullOrWhiteSpace(_settings.DeviceName))
            fields.Add(("设备", _settings.DeviceName));

        return await SendInteractiveAsync("⏹ 已退出大屏时钟", "grey", fields, BuildNote(), false);
    }

    /// <summary>设置页「测试发送」：忽略启用开关，只校验 Webhook 是否可用。</summary>
    public Task<(bool Ok, string Message)> SendTestAsync()
    {
        var fields = new List<(string Label, string Value)>
        {
            ("状态", "测试消息发送成功"),
            ("时间", DateTime.Now.ToString("HH:mm:ss")),
        };

        return SendInteractiveAsync("🔔 大屏时钟 · 飞书推送测试", "green", fields, BuildNote(), true);
    }

    // ===== 内部实现 =====

    private async Task<(bool Ok, string Message)> SendInteractiveAsync(
        string title, string template, IReadOnlyList<(string Label, string Value)> fields,
        string? note, bool bypassEnabled)
    {
        if (!bypassEnabled && !_settings.EnableFeishuNotify)
            return (false, "未启用飞书推送");

        var url = (_settings.FeishuWebhookUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            return (false, "未填写 Webhook 地址");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return (false, "Webhook 地址格式不正确");

        var timeoutSeconds = Math.Clamp(_settings.FeishuTimeoutSeconds, 5, 120);
        try
        {
            // 按设置构建客户端：超时可调；默认跟随系统代理，可选直连。
            using var handler = new HttpClientHandler();
            if (_settings.FeishuDirectConnection)
                handler.UseProxy = false;
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

            return await PostCardAsync(http, uri, title, template, fields, note)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return (false,
                $"请求超时（{timeoutSeconds} 秒无响应）。常见原因：① 防火墙/校园网拦截了本程序的联网；" +
                "② 浏览器走了扩展代理（对其它程序无效），可尝试切换「直连」；③ 网络较慢，可调大超时秒数后重试。");
        }
        catch (HttpRequestException ex)
        {
            var root = ex.InnerException?.Message ?? ex.Message;
            return (false,
                $"连接失败：{root}。常见原因：① 防火墙/校园网拦截了本程序的联网；" +
                "② 需要走系统代理但系统代理未配置，可尝试切换「直连」。");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<(bool Ok, string Message)> PostCardAsync(
        HttpClient http, Uri uri, string title, string template,
        IReadOnlyList<(string Label, string Value)> fields, string? note)
    {
        var payload = new Dictionary<string, object>
        {
            ["msg_type"] = "interactive",
            ["card"] = BuildCard(title, template, fields, note),
        };

        // 若配置了签名密钥，则附加 timestamp + sign（飞书自定义机器人「签名校验」）
        var secret = (_settings.FeishuSecret ?? "").Trim();
        if (!string.IsNullOrEmpty(secret))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            payload["timestamp"] = ts;
            payload["sign"] = GenSign(ts, secret);
        }

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(uri, content).ConfigureAwait(false);
        var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        var ok = resp.IsSuccessStatusCode && ParseFeishuOk(respText);
        return ok
            ? (true, "success")
            : (false, $"HTTP {(int)resp.StatusCode} · {Truncate(respText, 200)}");
    }

    /// <summary>构建飞书交互式卡片（card v1）。</summary>
    private static Dictionary<string, object> BuildCard(
        string title, string template, IReadOnlyList<(string Label, string Value)> fields,
        string? note)
    {
        var elements = new List<object>();

        var fieldObjs = new List<object>();
        foreach (var (label, value) in fields)
        {
            fieldObjs.Add(new Dictionary<string, object>
            {
                ["is_short"] = true,
                ["text"] = new Dictionary<string, object>
                {
                    ["tag"] = "lark_md",
                    ["content"] = $"**{label}**\n{value}",
                },
            });
        }
        if (fieldObjs.Count > 0)
            elements.Add(new Dictionary<string, object> { ["tag"] = "div", ["fields"] = fieldObjs });

        if (!string.IsNullOrWhiteSpace(note))
        {
            elements.Add(new Dictionary<string, object>
            {
                ["tag"] = "note",
                ["elements"] = new List<object>
                {
                    new Dictionary<string, object> { ["tag"] = "plain_text", ["content"] = note },
                },
            });
        }

        return new Dictionary<string, object>
        {
            ["config"] = new Dictionary<string, object> { ["wide_screen_mode"] = true },
            ["header"] = new Dictionary<string, object>
            {
                ["template"] = template,
                ["title"] = new Dictionary<string, object>
                {
                    ["tag"] = "plain_text",
                    ["content"] = title,
                },
            },
            ["elements"] = elements,
        };
    }

    /// <summary>飞书签名：base64(HMAC-SHA256(key = "{timestamp}\n{secret}", msg = 空))。</summary>
    private static string GenSign(string timestamp, string secret)
    {
        var stringToSign = $"{timestamp}\n{secret}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(stringToSign));
        var hash = hmac.ComputeHash(Array.Empty<byte>());
        return Convert.ToBase64String(hash);
    }

    /// <summary>飞书返回体判定：code / StatusCode 为 0（或缺失）视为成功。</summary>
    private static bool ParseFeishuOk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
                return code.GetInt32() == 0;
            if (root.TryGetProperty("StatusCode", out var status) && status.ValueKind == JsonValueKind.Number)
                return status.GetInt32() == 0;
            return true;
        }
        catch
        {
            return true;
        }
    }

    private string BuildNote()
    {
        var dev = string.IsNullOrWhiteSpace(_settings.DeviceName) ? "" : $"{_settings.DeviceName} · ";
        return $"{dev}大屏时钟插件 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
}
