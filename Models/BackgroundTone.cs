namespace EveningSelfStudyClock.Models;

/// <summary>
/// 「自动」主题档用的判定：这张背景图整体算亮还是算暗 —— 亮图配深色玻璃 + 白字（亮壁上压暗、
/// 白字才立得住），暗图反过来配浅色玻璃 + 黑字。
///
/// 纯函数，不碰文件也不碰 Avalonia；读图那步在 <see cref="BackgroundImageStore.ReadLuminance"/>。
/// </summary>
public static class BackgroundTone
{
    /// <summary>平均亮度低于这个值就算「暗图」。取中值：0.5 上下对多数壁纸的观感都对得上。</summary>
    public const double DarkThreshold = 0.5;

    /// <summary>
    /// BGRA（小端下的 0xAARRGGBB，与 Avalonia 的 Bgra8888 一致）像素的平均亮度，0 黑 ~ 1 白。
    /// 按人眼的三通道敏感度加权（绿最敏感、蓝最不敏感），不比对的像素（全透明）跳过。
    /// </summary>
    public static double AverageLuminance(ReadOnlySpan<uint> bgra)
    {
        double sum = 0;
        var n = 0;

        foreach (var p in bgra)
        {
            if ((p >> 24) == 0) continue;   // 全透明：没有颜色可算
            sum += 0.2126 * ((p >> 16) & 0xFF) + 0.7152 * ((p >> 8) & 0xFF) + 0.0722 * (p & 0xFF);
            n++;
        }

        return n == 0 ? DarkThreshold : sum / n / 255.0;
    }

    /// <summary>
    /// 算上明暗遮罩后，这张背景还算不算「暗」。遮罩会把整层压暗或提亮，判定得看**看到的那张图**，
    /// 否则把亮图压暗到看不清了，时钟还是白底黑字。
    /// </summary>
    /// <param name="luminance">原图平均亮度（0~1）。</param>
    /// <param name="dim">明暗遮罩：负压暗、正提亮（-100 ~ 100）。</param>
    public static bool IsDark(double luminance, double dim)
    {
        var alpha = Math.Clamp(Math.Abs(dim), 0, 100) / 100.0;
        // 盖一层半透明黑/白：盖黑是往 0 拉、盖白是往 1 拉
        var effective = dim < 0 ? luminance * (1 - alpha) : luminance * (1 - alpha) + alpha;
        return effective < DarkThreshold;
    }
}
