using System.Runtime.InteropServices;

namespace OcrKit.Serve;

/// <summary>
/// 路径闸门：只有用户显式授权过的目录/文件才可读，输出只能落在授权目录里。
/// 拒绝 UNC、设备路径、盘符相对路径、穿越、以及指向授权区外的符号链接。
/// </summary>
internal sealed class PathGuard
{
    private readonly List<string> _dirRoots = new();
    private readonly List<string> _fileRoots = new();
    private readonly object _lock = new();

    public static readonly string[] AllowedExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".gif" };

    /// <summary>登记授权根。目录与单文件都收；返回可回显的规范化路径。</summary>
    public string Grant(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new GuardException("E_PATH", "授权路径为空");
        string full = Canonical(raw);

        if (Directory.Exists(full))
        {
            lock (_lock) _dirRoots.Add(TrimSep(full));
            return full;
        }
        if (File.Exists(full))
        {
            lock (_lock) _fileRoots.Add(full);
            return full;
        }
        throw new GuardException("E_PATH", $"授权路径不存在：{raw}");
    }

    public IReadOnlyList<string> DirRoots { get { lock (_lock) return _dirRoots.ToArray(); } }
    public IReadOnlyList<string> FileRoots { get { lock (_lock) return _fileRoots.ToArray(); } }

    /// <summary>校验一个待读文件。返回解析后的真实路径与文件长度。</summary>
    public (string FullPath, long Length) CheckInput(string raw)
    {
        string full = Canonical(raw);
        if (!File.Exists(full)) throw new GuardException("E_PATH", $"文件不存在：{Path.GetFileName(raw)}");
        full = ResolveFinal(full);

        if (!IsAllowed(full)) throw new GuardException("E_ROOT", $"不在授权范围内：{Path.GetFileName(raw)}");

        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (Array.IndexOf(AllowedExtensions, ext) < 0) throw new GuardException("E_FORMAT", $"不支持的扩展名：{ext}");

        long len = new FileInfo(full).Length;
        if (len <= 0) throw new GuardException("E_SIZE", "空文件");
        if (len > ImageGuard.MaxBytes) throw new GuardException("E_SIZE", $"超过单文件上限 {ImageGuard.MaxBytes / 1024 / 1024} MB");

        return (full, len);
    }

    /// <summary>校验输出目录：必须在授权目录内，可不存在（将由调用方创建）。</summary>
    public string CheckOutDir(string raw)
    {
        string full = Canonical(raw);
        if (!IsAllowed(TrimSep(full))) throw new GuardException("E_ROOT", "输出目录不在授权范围内");
        return full;
    }

    /// <summary>输出文件名消毒：去非法字符、保留名、结尾点与空格、备用数据流。</summary>
    public static string SafeFileName(string name)
    {
        string n = name.Replace('\0', '_');
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        int colon = n.IndexOf(':');
        if (colon >= 0) n = n[..colon];
        n = n.Trim().TrimEnd('.');
        if (n.Length == 0) n = "output";
        string stem = Path.GetFileNameWithoutExtension(n).ToUpperInvariant();
        string[] reserved =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        if (Array.IndexOf(reserved, stem) >= 0) n = "_" + n;
        if (n.Length > 180) n = n[..180];
        return n;
    }

    private static string Canonical(string raw)
    {
        if (raw.Length > 32000) throw new GuardException("E_PATH", "路径过长");
        if (raw.IndexOf('\0') >= 0) throw new GuardException("E_PATH", "路径含空字符");
        if (raw.StartsWith(@"\\", StringComparison.Ordinal)) throw new GuardException("E_PATH", "不接受 UNC / 设备路径");
        if (raw.Length >= 2 && raw[1] == ':' && raw.Length > 2 && raw[2] != '\\' && raw[2] != '/')
            throw new GuardException("E_PATH", "不接受盘符相对路径（C:file 形式）");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && raw.Contains('\\'))
            throw new GuardException("E_PATH", "路径分隔符不合法");

        string full = Path.GetFullPath(raw);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new GuardException("E_PATH", "不接受 UNC / 设备路径");
        return TrimSep(full);
    }

    private static string TrimSep(string p) =>
        p.Length > 3 ? p.TrimEnd('\\', '/') : p;

    private static string ResolveFinal(string p)
    {
        try
        {
            var target = File.ResolveLinkTarget(p, true);
            return target is null ? p : TrimSep(Path.GetFullPath(target.FullName));
        }
        catch
        {
            return p;
        }
    }

    private bool IsAllowed(string full)
    {
        lock (_lock)
        {
            foreach (var f in _fileRoots)
                if (string.Equals(f, full, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var d in _dirRoots)
                if (string.Equals(full, d, StringComparison.OrdinalIgnoreCase)) return true;
                else if (full.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}