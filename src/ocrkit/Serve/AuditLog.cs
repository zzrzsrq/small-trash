using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OcrKit.Serve;

/// <summary>
/// 审计日志：只记「谁在什么时候处理了多大一张图、花了多久、成没成」，
/// 路径做单向哈希，正文一个字都不落盘。
/// </summary>
internal sealed class AuditLog
{
    private readonly string _path;
    private readonly object _lock = new();
    private bool _enabled;

    public AuditLog()
    {
        _path = "";
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OcrKit");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "audit.jsonl");

            // 超过 8 MiB 滚动一次，避免无限增长
            if (File.Exists(_path) && new FileInfo(_path).Length > 8L * 1024 * 1024)
                File.Move(_path, _path + ".1", true);

            _enabled = true;
        }
        catch
        {
            _enabled = false;   // 只读环境或权限不足就直接关掉，不阻断识别
        }
    }

    public void Write(string jobId, string path, long bytes, int width, int height,
                      double elapsedMs, bool ok, string? code)
    {
        if (!_enabled) return;
        try
        {
            var record = new
            {
                ts = DateTimeOffset.Now.ToString("o"),
                job = jobId,
                file = Hash(path),
                bytes,
                w = width,
                h = height,
                ms = Math.Round(elapsedMs, 1),
                ok,
                code
            };
            string line = JsonSerializer.Serialize(record) + "\n";
            lock (_lock) File.AppendAllText(_path, line, new UTF8Encoding(false));
        }
        catch
        {
            _enabled = false;
        }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}