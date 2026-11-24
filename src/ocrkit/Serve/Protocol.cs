using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OcrKit.Serve;

/// <summary>
/// 边车协议 v1：一行一个 JSON（NDJSON），stdin 收请求、stdout 回事件。
/// 没有监听端口、没有 HTTP、没有跨进程共享内存——壳能做的只有「发一行 JSON」。
/// </summary>
internal static class Proto
{
    public const int Version = 1;
    public const int MaxLineBytes = 1 << 20;      // 单条请求上限 1 MiB
    public const int MaxPathsPerJob = 500;

    private static readonly object WriteLock = new();
    private static StreamWriter _out = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>导出 JSON 用：紧凑、不转义中文。</summary>
    public static JsonSerializerOptions JsonWriteOpts => JsonOpts;

    public static void Emit(object payload)
    {
        string line = JsonSerializer.Serialize(payload, JsonOpts);
        lock (WriteLock)
        {
            _out.Write(line);
            _out.Write('\n');
            _out.Flush();
        }
    }

    public static void EmitError(string? id, string code, string message) =>
        Emit(new { type = "error", id, code, message });

    public static void EmitProgress(string? id, int index, int total, string name) =>
        Emit(new { type = "progress", id, index, total, name });

    public static void EmitLog(string level, string message) =>
        Emit(new { type = "log", level, message });

    public static bool TryGetString(JsonElement p, string name, out string? value)
    {
        value = null;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString();
        return true;
    }

    public static string? GetString(JsonElement p, string name, string? fallback = null) =>
        TryGetString(p, name, out var v) ? v : fallback;

    public static long GetLong(JsonElement p, string name, long fallback) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.TryGetInt64(out var v) ? v : fallback;

    public static bool GetBool(JsonElement p, string name, bool fallback) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean() : fallback;

    public static JsonElement GetParams(JsonObject? o)
    {
        if (o is null) return default;
        if (!o.TryGetPropertyValue("params", out var n) || n is null) return default;
        return JsonSerializer.SerializeToElement(n);
    }
}

internal sealed class GuardException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}