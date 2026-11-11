using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 设置页「更新说明」的数据：打开设置页时从 GitHub Releases API 拉，拉成功一次就落盘，
/// 这一版以后都不再请求；没拉到则每次启动试一次。
///
/// 不内嵌离线快照——插件市场本身就架在 GitHub 上，学校电脑连不上 GitHub 的话也根本装不了
/// 这个插件。拉不到就整块不显示，宁可不显示也不显示过期内容。
/// </summary>
public static class ReleaseNotesCatalog
{
    private const string ApiUrl =
        "https://api.github.com/repos/Human-air/BigScreenClock-For-ClassIsLand2.0/releases?per_page=100";

    private static readonly HttpClient Http = CreateClient();

    // 拉成功、并且这一版的说明确实在列表里 → 落盘，之后这个版本号都不再请求（重开设置页、重启都不请求）。
    // 没拉到 / 列表里还没有这一版 → 不落盘，但同一次启动里也只试这一次，下次启动再试一次。
    private static bool _attemptedThisSession;
    private static IReadOnlyList<ReleaseNote>? _runtime;

    /// <summary>
    /// 从 GitHub 拉最新的 Releases；失败返回 null。不抛异常：
    /// 设置页不该因为一次网络请求失败而报错。
    /// </summary>
    /// <param name="cachePath">缓存文件路径；为 null 时不落盘（只在本进程里记着）。</param>
    /// <param name="version">当前运行的插件版本号，用来判断「这一版的说明到位了」。</param>
    public static async Task<IReadOnlyList<ReleaseNote>?> FetchFromGitHubAsync(
        string? cachePath, string version, CancellationToken cancellationToken = default)
    {
        if (_runtime is not null) return _runtime;

        // 这一版已经成功拉过并落盘：直接用，不再联网
        if (TryLoadCache(cachePath, version, out var cached))
        {
            _runtime = cached;
            return cached;
        }

        // 本次启动已经试过一次（成没成都算）：没成就等下次启动，别在同一个进程里反复卡超时
        if (_attemptedThisSession) return null;
        _attemptedThisSession = true;

        try
        {
            var json = await Http.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            var notes = ReleaseNoteParser.ParseApi(json);
            if (notes.Count == 0) return null;

            _runtime = notes;
            // 只在这一版的条目真在列表里时落盘：还没发布的开发版对不上任何条目，
            // 这时候缓存下来，之后正式版发布了也永远不会再刷新。
            if (HasVersion(notes, version)) SaveCache(cachePath, version, notes);
            return notes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>列表里有没有「这一版」的条目（开发版五位号按前缀归到四位版本）。</summary>
    private static bool HasVersion(IReadOnlyList<ReleaseNote> notes, string version)
    {
        if (version.Length == 0) return false;
        foreach (var note in notes)
            if (note.Version == version
                || version.StartsWith(note.Version + ".", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool TryLoadCache(string? cachePath, string version, out IReadOnlyList<ReleaseNote> notes)
    {
        notes = Array.Empty<ReleaseNote>();
        if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath)) return false;

        try
        {
            var cache = JsonSerializer.Deserialize<ReleaseNotesCache>(File.ReadAllText(cachePath));
            // 只认当前这一版拉到的：插件升级以后要重新拉一次，那之后同样不再拉
            if (cache is not { Notes.Count: > 0 }
                || !string.Equals(cache.Version, version, StringComparison.Ordinal))
                return false;

            // 旧缓存里可能存过五位数的开发版条目，读到的时候一并滤掉
            var filtered = cache.Notes
                .Where(n => n.Version.TrimStart('v', 'V').Split('.').Length <= 4)
                .ToList();
            if (filtered.Count == 0) return false;

            notes = filtered;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveCache(string? cachePath, string version, IReadOnlyList<ReleaseNote> notes)
    {
        if (string.IsNullOrEmpty(cachePath)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var cache = new ReleaseNotesCache { Version = version, Notes = notes.ToList() };
            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache));
        }
        catch { }
    }

    private sealed class ReleaseNotesCache
    {
        public string Version { get; set; } = "";

        public List<ReleaseNote> Notes { get; set; } = new();
    }

    /// <summary>
    /// 找出「当前运行的这一版」在 Releases 里的条目。
    /// 开发版的五位号（1.2.0.0.4）按前缀归到对应的四位版本（1.2.0.0）；
    /// 还没发版的开发版对不上任何条目时退回最新的一版，至少让人看到最近的更新。
    /// </summary>
    public static ReleaseNote? FindCurrent(IReadOnlyList<ReleaseNote> notes, string version)
    {
        if (notes.Count == 0) return null;
        if (version.Length > 0)
        {
            foreach (var note in notes)
                if (note.Version == version
                    || version.StartsWith(note.Version + ".", StringComparison.Ordinal))
                    return note;
        }

        return notes[0];
    }

    private static HttpClient CreateClient()
    {
        // GitHub 要求带 User-Agent；超时压短一点，设置页打开时它在后台跑，但别一直挂着
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EveningSelfStudyClock");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

/// <summary>
/// 把 Releases API 的原始 JSON 切成设置页要的三种块（heading / bullet / text）。
///
/// GitHub 上各版更新说明的排版不统一：有的用 <c>###</c> 标题，有的直接写「✨ 新功能」，
/// 有的用 <c>*</c> / <c>-</c> 列表，还有的用四格缩进代替列表符号。
/// </summary>
public static class ReleaseNoteParser
{
    private static readonly Regex HtmlComment = new("<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex Separator = new(@"^\s*-{3,}\s*$");
    private static readonly Regex VersionLine = new(@"^v?\d+\.\d+\.\d+\.\d+");
    private static readonly Regex DateRegex = new(@"\d{4}-\d{2}-\d{2}");
    private static readonly Regex EmojiHeading = new(@"^(✨|🧩|🐛|🔧|🎯|🛠|⚙|🎨|📦|⚠|💡|🚀|🔍|🆕|🐞|📝|📌)");
    private static readonly Regex Bullet = new(@"^\s*[*-]\s+");
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)");

    /// <summary>解析 Releases API 的数组返回；草稿与本版说明为空的跳过。</summary>
    public static List<ReleaseNote> ParseApi(string json)
    {
        var notes = new List<ReleaseNote>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return notes;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;

            var version = GetString(element, "tag_name");

            // 五位版本号是作者发给自己人先试用的开发版：不进更新说明列表。
            // 正式版 / 对外测试版开着设置页时，不该看到这些开发版条目。
            if (version.TrimStart('v', 'V').Split('.').Length > 4) continue;

            var body = GetString(element, "body");
            var publishedAt = GetString(element, "published_at");
            var prerelease = element.TryGetProperty("prerelease", out var pre)
                             && pre.ValueKind == JsonValueKind.True;

            var note = ParseRelease(version, body, prerelease, publishedAt);
            if (note.Version.Length > 0 && note.Blocks.Count > 0) notes.Add(note);
        }

        return notes;
    }

    private static ReleaseNote ParseRelease(string version, string body, bool prerelease, string publishedAt)
    {
        var lines = HtmlComment.Replace(body ?? "", "").Replace("\r\n", "\n").Split('\n');
        var blocks = new List<ReleaseNoteBlock>();
        var date = "";
        var first = true;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var stripped = line.Trim();
            if (stripped.Length == 0) continue;
            if (Separator.IsMatch(line)) continue;

            // 第一条「1.2.0.0（正式版 · 2026-10-31）」这类抬头：版本号与日期已经在标题里显示，
            // 这一行只用来取日期，不再进正文。
            if (first)
            {
                first = false;
                if (VersionLine.IsMatch(stripped) && (stripped.Contains('版') || stripped.Length <= 24))
                {
                    var match = DateRegex.Match(stripped);
                    if (match.Success) date = match.Value;
                    continue;
                }
            }

            if (stripped.StartsWith('#'))
            {
                Add(blocks, "heading", stripped.TrimStart('#'));
                continue;
            }

            if (EmojiHeading.IsMatch(stripped) && stripped.Length <= 40)
            {
                Add(blocks, "heading", stripped);
                continue;
            }

            if (Bullet.IsMatch(line))
            {
                Add(blocks, "bullet", Bullet.Replace(line, "", 1));
                continue;
            }

            // 1.2.0.0 那种「**1.1.1.1：……」：其实是列表项，只是没写列表符号（Clean 会去掉 **）
            if (stripped.StartsWith("**", StringComparison.Ordinal))
            {
                Add(blocks, "bullet", stripped);
                continue;
            }

            // 有的版本用四格缩进代替列表符号（1.1.1.1）
            if (line.Length - line.TrimStart(' ').Length >= 4)
            {
                Add(blocks, "bullet", stripped);
                continue;
            }

            Add(blocks, "text", stripped);
        }

        if (date.Length == 0 && publishedAt.Length >= 10) date = publishedAt[..10];

        return new ReleaseNote
        {
            Version = version,
            Date = date,
            Prerelease = prerelease,
            Blocks = blocks,
        };
    }

    private static void Add(List<ReleaseNoteBlock> blocks, string kind, string text)
    {
        text = Clean(text);
        if (text.Length > 0) blocks.Add(new ReleaseNoteBlock { Kind = kind, Text = text });
    }

    private static string Clean(string text)
    {
        // **加粗** / `代码` / [文字](链接) 都退成纯文字；列表项开头的 ** 也在这里去掉
        text = text.Replace("**", "").Replace("`", "");
        return Link.Replace(text, "$1").Trim();
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

public sealed class ReleaseNote
{
    public string Version { get; set; } = "";

    /// <summary>yyyy-MM-dd（优先用更新说明里写的日期，没有才用发布日）。</summary>
    public string Date { get; set; } = "";

    public bool Prerelease { get; set; }

    public List<ReleaseNoteBlock> Blocks { get; set; } = new();

    [JsonIgnore]
    public string Badge => Prerelease ? "测试版" : "正式版";

    /// <summary>这一版在 GitHub 上的发布页。</summary>
    [JsonIgnore]
    public string Url =>
        $"https://github.com/Human-air/BigScreenClock-For-ClassIsLand2.0/releases/tag/{Version}";

    [JsonIgnore]
    public IBrush BadgeBrush => Prerelease ? TestBadgeBrush : ReleaseBadgeBrush;

    private static readonly IBrush TestBadgeBrush = new SolidColorBrush(Color.Parse("#c2740a"));
    private static readonly IBrush ReleaseBadgeBrush = new SolidColorBrush(Color.Parse("#2e7d32"));
}

public sealed class ReleaseNoteBlock
{
    /// <summary>heading / bullet / text。</summary>
    public string Kind { get; set; } = "text";

    public string Text { get; set; } = "";

    [JsonIgnore]
    public bool IsHeading => Kind == "heading";

    [JsonIgnore]
    public bool IsBullet => Kind == "bullet";

    [JsonIgnore]
    public string DisplayText => IsBullet ? "•  " + Text : Text;
}
