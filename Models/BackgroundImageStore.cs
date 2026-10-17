using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 背景图片的存放：用户选的那张**复制一份**进插件配置目录，设置里只记这份副本的文件名。
/// 原图之后被挪走/删掉/改名都不影响大屏。
/// </summary>
public static class BackgroundImageStore
{
    /// <summary>副本的文件名前缀。后面接一串时间戳，配置目录里同一时间只留最新那张。</summary>
    private const string BaseName = "background";

    /// <summary>设置里记的文件名 → 完整路径；没设或文件已经不在了都返回 null。</summary>
    public static string? ResolvePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var path = Path.Combine(folder, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 复制进配置目录并写进设置。会顺手删掉上一张
    /// （不然换几次图，配置目录里就攒一堆几 MB 的图）。
    ///
    /// 文件名带时间戳，每次选图都必然是一串新名字 —— 固定名字的话，
    /// 连着换两张同格式的图（jpg 换 jpg），设置里那个字符串没变，
    /// PropertyChanged 就不会发，大屏和预览都还以为没换过。
    /// </summary>
    public static void Replace(string sourcePath, PluginSettings settings)
    {
        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("插件配置目录不可用");

        Directory.CreateDirectory(folder);

        var name = BaseName + "-" + DateTime.Now.ToString("yyyyMMddHHmmssfff")
                   + Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(folder, name);
        var temp = target + ".tmp";

        // 先写临时文件、成功了再换成正式名字；**绝不先删旧图**。
        // 以前是「先删旧图 → 再拷新图」，源文件读不了 / 磁盘满的时候，用户就既没新图也没旧图，
        // 背景直接退回纯色。大文件拷到一半也不会被当成背景图读（名字还不叫正式名）。
        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);   // 同一目录内的改名，原子
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 删不掉就留给下次清理 */ }
            throw;
        }

        settings.BackgroundImageFile = name;

        // 新图已经落位、设置也指过去了，才收拾上一张
        RemoveExisting(exceptName: name);
    }

    /// <summary>清掉副本并清空设置（回到纯色底）。</summary>
    public static void Clear(PluginSettings settings)
    {
        RemoveExisting();
        settings.BackgroundImageFile = "";
    }

    // ===== 整体明暗（「自动」主题档用）=====

    /// <summary>判明暗只用到缩略图，这个宽足够了；越小编得越快。</summary>
    private const int ToneSampleWidth = 48;

    private static readonly object ToneGate = new();
    private static string? _tonePath;
    private static DateTime _toneStamp;
    private static double? _toneValue;

    /// <summary>
    /// 背景图的平均亮度（0 黑 ~ 1 白）；没图 / 读不出来返回 null（调用方按「跟随 ClassIsland」处理）。
    /// 同一个文件只算一次（按路径 + 修改时间认），拖滑块时不会反复解图。
    /// </summary>
    public static double? ReadLuminance(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var stamp = File.GetLastWriteTimeUtc(path);
        lock (ToneGate)
            if (path == _tonePath && stamp == _toneStamp) return _toneValue;

        double? value = null;
        try
        {
            using var stream = File.OpenRead(path);
            using var thumb = WriteableBitmap.DecodeToWidth(stream, ToneSampleWidth);
            value = BackgroundTone.AverageLuminance(ReadPixels(thumb));
        }
        catch
        {
            // 格式不支持 / 文件坏了：当作没有背景图，别把大屏拖垮
            value = null;
        }

        lock (ToneGate)
        {
            _tonePath = path;
            _toneStamp = stamp;
            _toneValue = value;
        }
        return value;
    }

    /// <summary>把缩略图的像素倒成连续的 0xAARRGGBB（解码出来的是预乘 BGRA，先还原回去再算）。</summary>
    private static unsafe uint[] ReadPixels(WriteableBitmap bitmap)
    {
        using var framebuffer = bitmap.Lock();
        var stride = framebuffer.RowBytes;
        var origin = (byte*)framebuffer.Address;
        var w = framebuffer.Size.Width;
        var h = framebuffer.Size.Height;

        var pixels = new uint[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = origin + (long)y * stride;
            for (var x = 0; x < w; x++)
            {
                var b = row[x * 4];
                var g = row[x * 4 + 1];
                var r = row[x * 4 + 2];
                var a = row[x * 4 + 3];

                if (a is > 0 and < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }

                pixels[y * w + x] = (uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b;
            }
        }
        return pixels;
    }

    /// <param name="exceptName">这张别删（刚放进去的那张）。</param>
    private static void RemoveExisting(string? exceptName = null)
    {
        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        foreach (var file in Directory.GetFiles(folder, BaseName + "*"))
        {
            // 只删我们自己放的副本，别误伤配置目录里同前缀的其它文件
            var name = Path.GetFileName(file);
            if (!name.StartsWith(BaseName + ".", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(BaseName + "-", StringComparison.OrdinalIgnoreCase)) continue;
            if (exceptName is not null && string.Equals(name, exceptName, StringComparison.OrdinalIgnoreCase))
                continue;

            try { File.Delete(file); }
            catch { /* 正被占用就留着，下次换图再删 */ }
        }
    }
}
