using System.Text.Json;
using System.Text.Json.Nodes;
using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Helpers;

internal static class SettingsMigration
{
    private const string PendingName = "Settings.import.json";
    private static readonly string[] LocalProperties =
    [
        nameof(PluginSettings.BackgroundImageFile),
        nameof(PluginSettings.MonitoredCameras),
        nameof(PluginSettings.CameraListFilled),
        nameof(PluginSettings.DeviceName),
        nameof(PluginSettings.FeishuWebhookUrl),
        nameof(PluginSettings.FeishuSecret)
    ];

    /// <summary>
    /// 设置迁移从 1.2.2.0.6 开发版起可用；1.2.3.0 及以上的正式版也支持。
    /// 五位开发版只要基础版本和序号达到 1.2.2.0.6 即可，不能只认死那一个号。
    /// </summary>
    internal static bool SupportsVersion(string? version)
    {
        var parts = version?.Split('.');
        if (parts is not { Length: 4 or 5 }) return false;
        if (!Version.TryParse(string.Join('.', parts.Take(4)), out var baseVersion)) return false;

        if (parts.Length == 4) return baseVersion >= new Version(1, 2, 3, 0);
        if (!int.TryParse(parts[4], out var revision) || revision <= 0) return false;
        return baseVersion > new Version(1, 2, 2, 0) ||
               (baseVersion == new Version(1, 2, 2, 0) && revision >= 6);
    }

    internal static Task ExportAsync(Stream destination, PluginSettings settings)
    {
        var data = JsonSerializer.SerializeToNode(settings)!.AsObject();
        foreach (var property in LocalProperties)
            data.Remove(property);

        return JsonSerializer.SerializeAsync(destination, new JsonObject
        {
            ["Format"] = "com.bigscreen.clock.settings.v1",
            ["PluginVersion"] = PluginVersion.Current,
            ["Settings"] = data
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static async Task QueueAsync(Stream source, string folder, PluginSettings current)
    {
        var document = await JsonNode.ParseAsync(source);
        if (document is not JsonObject root)
            throw new InvalidDataException("文件不是有效的 JSON 设置。");

        JsonObject data;
        if (root["Format"]?.GetValue<string>() == "com.bigscreen.clock.settings.v1" &&
            SupportsVersion(root["PluginVersion"]?.GetValue<string>()) &&
            root["Settings"] is JsonObject wrapped)
        {
            data = wrapped;
        }
        else if (root.ContainsKey(nameof(PluginSettings.TargetCourseNames)) ||
                 root.ContainsKey(nameof(PluginSettings.DecibelQuietThreshold)))
        {
            // 兼容直接把插件配置目录里的 Settings.json 拿来导入（没有导出文件的外层包装）
            data = root;
        }
        else
        {
            throw new InvalidDataException("请选择导出的大屏时钟设置文件，或插件配置目录里的 Settings.json。");
        }

        Merge(current, data);
        Directory.CreateDirectory(folder);
        var pending = Path.Combine(folder, PendingName);
        var temporary = pending + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, data.ToJsonString());
            File.Move(temporary, pending, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static PluginSettings ApplyPending(string settingsPath, PluginSettings current)
    {
        if (!SupportsVersion(PluginVersion.Current)) return current;
        var pending = Path.Combine(Path.GetDirectoryName(settingsPath)!, PendingName);
        if (!File.Exists(pending)) return current;

        var imported = Merge(current, JsonNode.Parse(File.ReadAllText(pending))!.AsObject());
        var temporary = settingsPath + ".import.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(imported));
            if (File.Exists(settingsPath))
            {
                var backup = settingsPath + $".{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak";
                File.Replace(temporary, settingsPath, backup);
            }
            else
            {
                File.Move(temporary, settingsPath);
            }

            File.Delete(pending);
            return imported;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static PluginSettings Merge(PluginSettings current, JsonObject source)
    {
        var merged = JsonSerializer.SerializeToNode(current)!.AsObject();
        foreach (var property in merged.Select(item => item.Key).ToArray())
        {
            if (!LocalProperties.Contains(property) && source.TryGetPropertyValue(property, out var value))
            {
                if (value is null && merged[property] is not null)
                    throw new InvalidDataException($"设置项 {property} 缺少值。");
                merged[property] = value?.DeepClone();
            }
        }
        return merged.Deserialize<PluginSettings>()!;
    }
}
