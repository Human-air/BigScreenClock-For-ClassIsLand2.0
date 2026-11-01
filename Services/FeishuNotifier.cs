using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.NoiseDetection;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 飞书推送服务。
/// 文本/卡片走「自定义机器人 Webhook」；照片需要先用「自建应用」的 tenant_access_token
/// 调用上传图片接口换取 image_key，再把 image_key 放进卡片的 img 元素（同一企业内有效）。
/// 未配置 app_id / app_secret 时自动降级为纯文字卡片。
/// </summary>
public class FeishuNotifier
{
    private readonly PluginSettings _settings;

    /// <summary>tenant_access_token 缓存（有效期 7200 秒，提前 5 分钟过期）。</summary>
    private static string? _cachedToken;
    private static DateTime _cachedTokenExpireAt = DateTime.MinValue;
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    public FeishuNotifier(PluginSettings settings)
    {
        _settings = settings;
    }

    /// <summary>噪音段结束提醒。level 为 Normal / Noisy。</summary>
    public async Task<(bool Ok, string Message)> SendNoiseAsync(
        NoiseLevel level, double smoothedRms, string course, int normalCount, int noisyCount,
        byte[]? snapshot = null, string? snapshotPath = null)
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

        return await SendInteractiveAsync(title, template, fields, BuildNote(snapshotPath), false, snapshot);
    }

    /// <summary>进入大屏时钟通知。</summary>
    public async Task<(bool Ok, string Message)> SendEnterAsync(
        string course, byte[]? snapshot = null, string? snapshotPath = null)
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

        return await SendInteractiveAsync("🖥 已进入大屏时钟", "blue", fields, BuildNote(snapshotPath), false, snapshot);
    }

    /// <summary>退出大屏时钟通知（附本次会话噪音汇总 + 抓拍）。</summary>
    public async Task<(bool Ok, string Message)> SendExitAsync(
        string course, TimeSpan duration, int normalCount, int noisyCount,
        byte[]? snapshot = null, string? snapshotPath = null)
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

        return await SendInteractiveAsync("⏹ 已退出大屏时钟", "grey", fields, BuildNote(snapshotPath), false, snapshot);
    }

    /// <summary>设置页「测试发送」：忽略启用开关，只校验 Webhook（及图片通道）是否可用。</summary>
    public Task<(bool Ok, string Message)> SendTestAsync(byte[]? snapshot = null, string? snapshotPath = null)
    {
        var fields = new List<(string Label, string Value)>
        {
            ("状态", "测试消息发送成功"),
            ("时间", DateTime.Now.ToString("HH:mm:ss")),
        };

        return SendInteractiveAsync("🔔 大屏时钟 · 飞书推送测试", "green", fields, BuildNote(snapshotPath), true, snapshot);
    }

    // ===== 内部实现 =====

    private async Task<(bool Ok, string Message)> SendInteractiveAsync(
        string title, string template, IReadOnlyList<(string Label, string Value)> fields,
        string? note, bool bypassEnabled, byte[]? snapshot)
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

            // 1) 有照片先上传，拿 img_key，并把照片的真实发送结果写进卡片字段（方便群里直接看到失败原因）
            string? imageKey = null;
            string? imageError = null;
            var fieldList = new List<(string Label, string Value)>(fields);
            if (snapshot is { Length: > 0 })
            {
                var (key, err) = await UploadImageAsync(http, snapshot).ConfigureAwait(false);
                imageKey = key;
                imageError = err;
                fieldList.Add(("照片", key is not null
                    ? $"已嵌入消息（{snapshot.Length / 1024} KB）"
                    : $"❌ 发送失败：{err}"));
            }

            // 2) 发送卡片（带图）；若带图失败则去掉图片重发一次，保证文字提醒不丢
            var (ok, message) = await PostCardAsync(http, uri, imageKey, title, template, fieldList, note, timeoutSeconds)
                .ConfigureAwait(false);

            if (!ok && imageKey is not null)
            {
                var retryNote = $"{note} · 照片发送失败（{Truncate(message, 120)}），已改为纯文字";
                var retry = await PostCardAsync(http, uri, null, title, template, fieldList, retryNote, timeoutSeconds)
                    .ConfigureAwait(false);
                if (retry.Ok)
                    return (true, "文字已送达，但照片发送失败：" + Truncate(message, 160));
                return retry;
            }

            if (ok && imageError is not null)
                return (true, "文字已送达，照片未发送：" + imageError);

            return (ok, message);
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
        HttpClient http, Uri uri, string? imageKey, string title, string template,
        IReadOnlyList<(string Label, string Value)> fields, string? note, int timeoutSeconds)
    {
        var payload = new Dictionary<string, object>
        {
            ["msg_type"] = "interactive",
            ["card"] = BuildCard(title, template, fields, note, imageKey),
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

    /// <summary>上传图片到飞书，返回 img_key（需要自建应用 + im:resource 权限）。</summary>
    private async Task<(string? Key, string? Error)> UploadImageAsync(HttpClient http, byte[] jpeg)
    {
        var appId = (_settings.FeishuAppId ?? "").Trim();
        var appSecret = (_settings.FeishuAppSecret ?? "").Trim();
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
            return (null, "未配置飞书应用 App ID / App Secret，无法上传照片");

        var token = await GetTenantTokenAsync(http, appId, appSecret).ConfigureAwait(false);
        if (token.Key is null)
            return (null, token.Error);

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("message"), "image_type");

            var imageContent = new ByteArrayContent(jpeg);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(imageContent, "image", "snapshot.jpg");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images")
            {
                Content = form,
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Key);

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, $"上传接口 HTTP {(int)resp.StatusCode}：{Truncate(text, 150)}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                return (null, $"上传失败 code={code.GetInt32()}：{Truncate(text, 150)}" +
                    "（常见原因：未开通「获取与上传图片 im:resource」权限；权限后未重新发布应用版本）");

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("image_key", out var key))
                return (key.GetString(), null);

            return (null, "上传接口未返回 image_key");
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>获取并缓存 tenant_access_token。</summary>
    private static async Task<(string? Key, string? Error)> GetTenantTokenAsync(
        HttpClient http, string appId, string appSecret)
    {
        if (_cachedToken is not null && DateTime.Now < _cachedTokenExpireAt)
            return (_cachedToken, null);

        await TokenLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && DateTime.Now < _cachedTokenExpireAt)
                return (_cachedToken, null);

            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["app_id"] = appId,
                ["app_secret"] = appSecret,
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http
                .PostAsync("https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal", content)
                .ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code != 0)
                return (null, $"获取应用凭证失败 code={code}：{Truncate(text, 150)}" +
                    "（常见原因：App Secret 填错；应用未创建版本并发布；应用被停用）");

            if (!root.TryGetProperty("tenant_access_token", out var t))
                return (null, "接口未返回 tenant_access_token");

            _cachedToken = t.GetString();
            var expire = root.TryGetProperty("expire", out var e) ? e.GetInt32() : 7200;
            _cachedTokenExpireAt = DateTime.Now.AddSeconds(Math.Max(60, expire - 300));
            return (_cachedToken, null);
        }
        catch (Exception ex)
        {
            return (null, $"获取应用凭证异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TokenLock.Release();
        }
    }

    /// <summary>构建飞书交互式卡片（card v1），可选内嵌照片。</summary>
    private static Dictionary<string, object> BuildCard(
        string title, string template, IReadOnlyList<(string Label, string Value)> fields,
        string? note, string? imageKey)
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

        if (!string.IsNullOrWhiteSpace(imageKey))
        {
            elements.Add(new Dictionary<string, object> { ["tag"] = "hr" });
            elements.Add(new Dictionary<string, object>
            {
                ["tag"] = "img",
                ["img_key"] = imageKey,
                ["alt"] = new Dictionary<string, object>
                {
                    ["tag"] = "plain_text",
                    ["content"] = "教室现场照片",
                },
            });
        }

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

    private string BuildNote(string? snapshotPath)
    {
        var dev = string.IsNullOrWhiteSpace(_settings.DeviceName) ? "" : $"{_settings.DeviceName} · ";
        var note = $"{dev}大屏时钟插件 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        if (!string.IsNullOrWhiteSpace(snapshotPath))
            note += $"\n照片已存：{snapshotPath}";
        return note;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
}
