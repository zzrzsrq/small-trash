using System.Buffers.Binary;

namespace OcrKit.Serve;

/// <summary>
/// 按 magic bytes 认格式，不信扩展名；并从文件头读宽高，挡解压炸弹。
/// 已知超限就读头拒绝，不解码。
/// </summary>
internal static class ImageGuard
{
    public const long MaxBytes = 96L * 1024 * 1024;
    public const long MaxPixels = 120_000_000L;
    public const int MinPixels = 64;

    public enum Kind { Unknown, Png, Jpeg, Bmp, Gif, Webp, Tiff }

    public static Kind Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return Kind.Png;
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return Kind.Jpeg;
        if (head.Length >= 2 && head[0] == 0x42 && head[1] == 0x4D) return Kind.Bmp;
        if (head.Length >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38) return Kind.Gif;
        if (head.Length >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return Kind.Webp;
        if (head.Length >= 4 && ((head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x2A && head[3] == 0x00)
                              || (head[0] == 0x4D && head[1] == 0x4D && head[2] == 0x00 && head[3] == 0x2A))) return Kind.Tiff;
        return Kind.Unknown;
    }

    public static Kind ExpectFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => Kind.Png,
        ".jpg" or ".jpeg" => Kind.Jpeg,
        ".bmp" => Kind.Bmp,
        ".gif" => Kind.Gif,
        ".webp" => Kind.Webp,
        ".tif" or ".tiff" => Kind.Tiff,
        _ => Kind.Unknown
    };

    /// <summary>读文件头：识别真实格式、与扩展名对照、解析宽高并检查像素上限。</summary>
    public static (Kind Kind, int Width, int Height) Inspect(string path, string ext)
    {
        byte[] head = new byte[64 * 1024];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            n = fs.Read(head, 0, head.Length);

        var span = head.AsSpan(0, n);
        Kind kind = Sniff(span);
        if (kind == Kind.Unknown) throw new GuardException("E_FORMAT", "无法识别的图像格式（扩展名对了也不算数）");

        Kind expected = ExpectFromExtension(ext);
        if (expected != Kind.Unknown && expected != kind)
            throw new GuardException("E_FORMAT", $"扩展名与实际格式不符：{ext} 实际是 {kind}");

        var (w, h) = ReadSize(kind, span);
        long pixels = (long)w * h;
        if (w > 0 && h > 0)
        {
            if (pixels > MaxPixels) throw new GuardException("E_PIXELS", $"像素数过大：{w}×{h}，超过 {MaxPixels:N0}");
            if (pixels < MinPixels) throw new GuardException("E_PIXELS", $"图像太小：{w}×{h}");
        }
        return (kind, w, h);
    }

    private static (int W, int H) ReadSize(Kind kind, ReadOnlySpan<byte> b)
    {
        try
        {
            switch (kind)
            {
                case Kind.Png when b.Length >= 24:
                    return (BinaryPrimitives.ReadInt32BigEndian(b.Slice(16, 4)),
                            BinaryPrimitives.ReadInt32BigEndian(b.Slice(20, 4)));
                case Kind.Gif when b.Length >= 10:
                    return (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6, 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(8, 2)));
                case Kind.Bmp when b.Length >= 26:
                {
                    int w = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(18, 4));
                    int h = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(22, 4));
                    return (Math.Abs(w), Math.Abs(h));
                }
                case Kind.Webp when b.Length >= 30 && b[12] == 0x56 && b[13] == 0x50 && b[14] == 0x38 && b[15] == 0x58:
                    return ((b[24] | (b[25] << 8) | (b[26] << 16)) + 1,
                            (b[27] | (b[28] << 8) | (b[29] << 16)) + 1);
                case Kind.Jpeg:
                {
                    int i = 2;
                    while (i + 9 < b.Length)
                    {
                        if (b[i] != 0xFF) { i++; continue; }
                        byte m = b[i + 1];
                        if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                        int len = (b[i + 2] << 8) | b[i + 3];
                        if (len < 2) return (0, 0);
                        if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                            return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                        i += 2 + len;
                    }
                    return (0, 0);
                }
            }
        }
        catch
        {
            // 头部损坏就交给上层按「读不出来」处理
        }
        return (0, 0);
    }
}