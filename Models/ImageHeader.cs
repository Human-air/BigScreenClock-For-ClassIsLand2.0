using System.IO;
using Avalonia;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 只读文件头，问出图片的**原始**像素尺寸。**纯字节解析，不解码**，也不依赖渲染器。
///
/// 为什么要这个：<c>Bitmap.DecodeToWidth(流, 宽)</c> 给多宽就解多宽，原图比它小也照样放大 ——
/// 一张 1600×1600 的壁纸给它 2560，就解出一张 2560×2560，白占一倍半内存、白插一遍值，
/// 玻璃里那张模糊底图和两份像素快照全都跟着翻倍（2026-10-11 内存顶到看门狗就是这么来的）。
/// 知道原图多大了，才能「要多少解多少，绝不放大」。
///
/// 认不出的格式返回 null，调用方按老办法（按窗口宽解）处理即可，不会更糟。
/// </summary>
internal static class ImageHeader
{
    /// <summary>PNG 的固定开头。</summary>
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>WebP 有损块里紧跟帧标签的固定起始码。</summary>
    private static readonly byte[] Vp8StartCode = [0x9D, 0x01, 0x2A];

    /// <summary>读原图尺寸；读不出来（格式不认识 / 文件坏了 / 没权限）返回 null。</summary>
    public static PixelSize? ReadSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadSize(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc cref="ReadSize(string)"/>
    public static PixelSize? ReadSize(Stream stream)
    {
        Span<byte> head = stackalloc byte[32];
        var got = ReadUpTo(stream, head);

        // JPEG 前面没有定长头，够认出它就行 —— 后面是自己一段段跳着找 SOF
        if (got >= 2 && head[0] == 0xFF && head[1] == 0xD8) return JpegSize(stream);

        // 其余格式的签名和宽高都落在头 32 字节里，读不满就不猜
        if (got < head.Length) return null;

        if (head[..8].SequenceEqual(PngSignature)) return PngSize(head);
        if (head[..3].SequenceEqual("GIF"u8)) return GifSize(head);
        if (head[..2].SequenceEqual("BM"u8)) return BmpSize(head);
        if (head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return WebpSize(head);

        return null;
    }

    /// <summary>PNG：签名之后紧跟着第一个块，规定就是 IHDR，宽高各 4 字节大端。</summary>
    private static PixelSize? PngSize(Span<byte> head)
    {
        if (!head[12..16].SequenceEqual("IHDR"u8)) return null;

        return Valid((int)ReadU32Be(head[16..20]), (int)ReadU32Be(head[20..24]));
    }

    /// <summary>GIF：逻辑屏幕宽高各 2 字节小端。</summary>
    private static PixelSize? GifSize(Span<byte> head)
        => Valid(head[6] | head[7] << 8, head[8] | head[9] << 8);

    /// <summary>BMP：DIB 头里宽高都是 4 字节小端，高度为负表示自上而下存。</summary>
    private static PixelSize? BmpSize(Span<byte> head)
    {
        var dibSize = (int)ReadU32Le(head[14..18]);

        if (dibSize == 12)   // OS/2 那版头：宽高是 2 字节
            return Valid(head[18] | head[19] << 8, head[20] | head[21] << 8);

        if (dibSize < 40) return null;
        return Valid((int)ReadU32Le(head[18..22]), Math.Abs((int)ReadU32Le(head[22..26])));
    }

    /// <summary>
    /// WebP：三种块布局不一样。有 VP8X（带透明/动画）就信它，那是画布尺寸；
    /// 否则按有损（VP8）/无损（VP8L）各自的头部格式取。
    /// </summary>
    private static PixelSize? WebpSize(Span<byte> head)
    {
        if (head[12..16].SequenceEqual("VP8X"u8))
            return Valid(ReadU24Le(head[24..]) + 1, ReadU24Le(head[27..]) + 1);

        if (head[12..16].SequenceEqual("VP8L"u8) && head[20] == 0x2F)
        {
            var bits = ReadU32Le(head[21..25]);
            return Valid((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        // 有损：块数据开头是 3 字节帧标签，接着固定起始码 9D 01 2A，然后是 14 位的宽高
        if (head[12..16].SequenceEqual("VP8 "u8) && head[23..26].SequenceEqual(Vp8StartCode))
            return Valid((head[26] | head[27] << 8) & 0x3FFF, (head[28] | head[29] << 8) & 0x3FFF);

        return null;
    }

    /// <summary>
    /// JPEG：没有固定头部，靠一段段跳过段头找 SOF。注意不能只读开头几十字节就算 ——
    /// 手机拍的照片前面可能挂着几十 KB 的 EXIF（APP1，里面还塞着缩略图），SOF 远在后面。
    /// </summary>
    private static PixelSize? JpegSize(Stream stream)
    {
        stream.Position = 2;

        while (true)
        {
            var marker = NextMarker(stream);
            if (marker is null) return null;
            var m = marker.Value;

            // 无长度字段的标记：填充 / 重启标记，直接看下一个
            if (m == 0x01 || m is >= 0xD0 and <= 0xD8) continue;
            // 图像数据开始 / 文件结束：SOF 本该在这之前出现，没有就认输
            if (m is 0xD9 or 0xDA) return null;

            var length = ReadU16Be(stream);
            if (length < 2) return null;

            // SOF0~SOF15，去掉中间那三个不是 SOF 的（DHT/JPG/DAC）
            if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC))
            {
                Span<byte> sof = stackalloc byte[5];   // 精度 1 字节 + 高 2 + 宽 2（都是大端）
                if (!ReadFull(stream, sof)) return null;
                return Valid(sof[3] << 8 | sof[4], sof[1] << 8 | sof[2]);
            }

            stream.Seek(length - 2, SeekOrigin.Current);
        }
    }

    /// <summary>跳到下一个标记（0xFF 之后那个非 0xFF 的字节）。</summary>
    private static int? NextMarker(Stream stream)
    {
        int b;
        do
        {
            b = stream.ReadByte();
            if (b < 0) return null;
        }
        while (b != 0xFF);

        do
        {
            b = stream.ReadByte();
            if (b < 0) return null;
        }
        while (b == 0xFF);

        return b;
    }

    private static PixelSize? Valid(int w, int h)
        => w > 0 && h > 0 ? new PixelSize(w, h) : null;

    /// <summary>读满一个缓冲区；读不满（文件比头还短）返回 false。</summary>
    private static bool ReadFull(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }

        return true;
    }

    /// <summary>尽量读满，返回实际读到的字节数（文件短了也不当出错）。</summary>
    private static int ReadUpTo(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0) break;
            read += n;
        }

        return read;
    }

    private static ushort ReadU16Be(Stream stream)
    {
        var hi = stream.ReadByte();
        var lo = stream.ReadByte();
        return (ushort)(hi < 0 || lo < 0 ? 0 : hi << 8 | lo);
    }

    private static uint ReadU32Be(ReadOnlySpan<byte> b)
        => (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];

    private static uint ReadU32Le(ReadOnlySpan<byte> b)
        => (uint)b[3] << 24 | (uint)b[2] << 16 | (uint)b[1] << 8 | b[0];

    private static int ReadU24Le(ReadOnlySpan<byte> b)
        => b[0] | b[1] << 8 | b[2] << 16;
}
