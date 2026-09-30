using System.Text.Json;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 读取 ClassIsland 主界面所用字体（CI 设置 → 外观 → 主界面字体），
/// 让大屏时钟的数字用上与 CI 主界面一致的字形（原来是 Consolas，数字 0 中间带斜杠）。
/// </summary>
public static class CiMainFont
{
    /// <summary>CI 字体串只含它自己配置的字体，缺字时回退到系统中文字体。</summary>
    public const string Fallback = "Microsoft YaHei UI, SimHei";

    /// <summary>
    /// 从 CI 的 data 目录读 Settings.json 的 MainWindowFont，
    /// 取第一段（文本字体，丢掉后面图标字体那段）并补上中文回退；读不到返回 null。
    /// </summary>
    public static string? ReadFontString(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, "Settings.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("MainWindowFont", out var el)) return null;
            return Build(el.GetString());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 CI 的字体串里取第一段文本字体并补中文回退。
    /// CI 的值形如 "avares://ClassIsland/Assets/Fonts/#HarmonyOS Sans SC, avares://.../#FluentSystemIcons"，
    /// 后面那段是图标字体（供 CI 的图标组件用），大屏时钟不需要。
    /// </summary>
    public static string? Build(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // FontFamily.Parse 按逗号拆分且不 trim，这里自己先 trim 掉各段空白
        foreach (var part in raw.Split(','))
        {
            var name = part.Trim();
            if (name.Length > 0) return $"{name}, {Fallback}";
        }
        return null;
    }
}
