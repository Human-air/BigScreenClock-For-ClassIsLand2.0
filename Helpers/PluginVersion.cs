namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 运行中的插件版本号：读程序集同目录的 manifest.yml（csproj 会把那份复制到输出目录）。
/// 不写死，升级后不会忘记同步。
/// </summary>
public static class PluginVersion
{
    /// <summary>读不到时是空串（调用方自己决定怎么显示）。</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(PluginVersion).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                var manifestPath = Path.Combine(dir, "manifest.yml");
                if (File.Exists(manifestPath))
                {
                    foreach (var line in File.ReadAllLines(manifestPath))
                    {
                        var trimmed = line.TrimStart();
                        if (trimmed.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                        {
                            var version = trimmed["version:".Length..].Trim().Trim('"', '\'');
                            if (version.Length > 0) return version;
                        }
                    }
                }
            }
        }
        catch { }

        return "";
    }
}
