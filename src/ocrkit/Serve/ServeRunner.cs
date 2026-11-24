using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OcrKit.Serve;

/// <summary>
/// 边车主循环。启动条件：父进程给了一个 64 位十六进制令牌（环境变量）。
/// 读 stdin 的 NDJSON 请求，单工作线程串行跑引擎，stdout 回事件。
/// </summary>
internal static class ServeRunner
{
    private static readonly PathGuard Guard = new();
    private static readonly AuditLog Audit = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> JobTokens = new();
    private static OcrEngine? _engine;
    private static string _engineKey = "";

    /// <summary>最近一次任务的页面缓存，导出合并排版时用。</summary>
    private static System.Text.Json.Nodes.JsonArray? LastPages;
    private static readonly object LastLock = new();

    public static int Run()
    {
        if (!VerifyParentToken(out string why))
        {
            Console.Error.WriteLine($"OcrKit 边车拒绝启动：{why}");
            return 3;
        }

        StartParentWatchdog();

        Proto.Emit(new
        {
            type = "hello",
            proto = Proto.Version,
            app = "OcrKit",
            pid = Environment.ProcessId,
            limits = new
            {
                maxPaths = Proto.MaxPathsPerJob,
                maxLineBytes = Proto.MaxLineBytes,
                maxBytes = ImageGuard.MaxBytes,
                maxPixels = ImageGuard.MaxPixels
            }
        });

        return Loop();
    }

    // ── 循环 ────────────────────────────────────────────────────────────────

    private static int Loop()
    {
        var queue = new BlockingCollection<JsonObject?>(64);
        var worker = new Thread(() =>
        {
            foreach (var req in queue.GetConsumingEnumerable())
            {
                if (req is null) continue;
                string method = req["method"]?.GetValue<string>() ?? "";
                if (method == "export")
                {
                    HandleExport(req, req["id"]?.GetValue<string>());
                }
                else
                {
                    RunOcr(req);
                }
            }
        })
        { IsBackground = true, Name = "ocrkit-worker" };
        worker.Start();

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 64 * 1024);

        try
        {
            while (true)
            {
                string? line = stdin.ReadLine();
                if (line is null) break;                 // 壳退出，stdin 关闭
                if (line.Length == 0) continue;
                if (Encoding.UTF8.GetByteCount(line) > Proto.MaxLineBytes)
                {
                    Proto.EmitError(null, "E_PARSE", "请求超过 1 MiB");
                    continue;
                }

                JsonObject? root;
                try { root = JsonNode.Parse(line) as JsonObject; }
                catch { Proto.EmitError(null, "E_PARSE", "不是合法 JSON"); continue; }
                if (root is null) { Proto.EmitError(null, "E_PARSE", "请求必须是 JSON 对象"); continue; }

                string? id = root["id"]?.GetValue<string>();
                string method = root["method"]?.GetValue<string>() ?? "";
                string? jobId = root["params"]?["jobId"]?.GetValue<string>() ?? id;

                switch (method)
                {
                    case "ping":
                        Proto.Emit(new { type = "pong", id, t = root["params"]?["t"]?.GetValue<long>() ?? 0 });
                        break;

                    case "grant":
                        HandleGrant(root, id);
                        break;

                    case "ocr":
                        if (!queue.TryAdd(root)) Proto.EmitError(id, "E_BUSY", "队列已满");
                        break;

                    case "cancel":
                        if (jobId is not null && JobTokens.TryGetValue(jobId, out var cts))
                        {
                            cts.Cancel();
                            Proto.Emit(new { type = "cancelled", id, jobId });
                        }
                        else Proto.EmitError(id, "E_CANCEL", "没有这个任务");
                        break;

                    case "export":
                        // 也进同一队列：保证「先识别、后导出」的先后关系
                        if (!queue.TryAdd(root)) Proto.EmitError(id, "E_BUSY", "队列已满");
                        break;

                    case "shutdown":
                        queue.CompleteAdding();
                        worker.Join(TimeSpan.FromSeconds(5));
                        return 0;

                    case "":
                        Proto.EmitError(id, "E_METHOD", "缺少 method");
                        break;

                    default:
                        Proto.EmitError(id, "E_METHOD", $"未知方法：{method}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Proto.EmitLog("error", $"边车主循环异常：{ex.Message}");
        }
        finally
        {
            queue.CompleteAdding();
            DisposeEngine();
        }
        return 0;
    }

    // ── 授权 ────────────────────────────────────────────────────────────────

    private static void HandleGrant(JsonObject root, string? id)
    {
        try
        {
            var roots = root["params"]?["roots"]?.AsArray();
            if (roots is null || roots.Count == 0)
            {
                Proto.EmitError(id, "E_ROOT", "grant 需要 roots 数组");
                return;
            }
            var accepted = new List<string>();
            var rejected = new List<object>();
            foreach (var node in roots)
            {
                string raw = node?.GetValue<string>() ?? "";
                try { accepted.Add(Guard.Grant(raw)); }
                catch (GuardException g) { rejected.Add(new { path = raw, code = g.Code, message = g.Message }); }
            }
            Proto.Emit(new
            {
                type = "granted",
                id,
                roots = Guard.DirRoots,
                files = Guard.FileRoots,
                accepted,
                rejected
            });
        }
        catch (Exception ex)
        {
            Proto.EmitError(id, "E_ROOT", $"授权失败：{ex.Message}");
        }
    }

    // ── 识别 ────────────────────────────────────────────────────────────────

    private static void RunOcr(JsonObject root)
    {
        string? id = root["id"]?.GetValue<string>();
        var p = root["params"];
        string jobId = p?["jobId"]?.GetValue<string>() ?? id ?? Guid.NewGuid().ToString("N");

        var cts = new CancellationTokenSource();
        JobTokens[jobId] = cts;
        var totalSw = Stopwatch.StartNew();
        var pages = new List<object>();
        int ok = 0, fail = 0;

        try
        {
            var paths = p?["paths"]?.AsArray();
            if (paths is null || paths.Count == 0)
            {
                Proto.EmitError(id, "E_PARSE", "paths 为空");
                return;
            }
            if (paths.Count > Proto.MaxPathsPerJob)
            {
                Proto.EmitError(id, "E_LIMIT", $"单次最多 {Proto.MaxPathsPerJob} 个文件");
                return;
            }

            string format = ResolveFormat(p?["format"]?.GetValue<string>());
            var tier = ResolveTier(p?["tier"]?.GetValue<string>());
            var device = ResolveDevice(p?["device"]?.GetValue<string>());
            bool wantChars = p?["chars"]?.GetValue<bool>() ?? false;
            string? outDirRaw = p?["outDir"]?.GetValue<string>();
            string? outDir = null;
            if (!string.IsNullOrWhiteSpace(outDirRaw))
                outDir = Guard.CheckOutDir(outDirRaw);

            var engine = EnsureEngine(tier, device);

            for (int i = 0; i < paths.Count; i++)
            {
                if (cts.IsCancellationRequested)
                {
                    Proto.EmitLog("info", $"任务已取消，剩余 {paths.Count - i} 个未处理");
                    break;
                }

                string raw = paths[i]?.GetValue<string>() ?? "";
                Proto.EmitProgress(id, i + 1, paths.Count, Path.GetFileName(raw));

                long bytes = 0;
                var fileSw = Stopwatch.StartNew();
                try
                {
                    var (full, len) = Guard.CheckInput(raw);
                    bytes = len;
                    var (kind, w, h) = ImageGuard.Inspect(full, Path.GetExtension(full));
                    var page = engine.Run(full);
                    fileSw.Stop();

                    if (outDir is not null)
                    {
                        string name = PathGuard.SafeFileName(Path.GetFileNameWithoutExtension(full)) + OutputFormat.Extension(format);
                        string target = Path.Combine(outDir, name);
                        Directory.CreateDirectory(outDir);
                        File.WriteAllText(target, OutputFormat.Render(page, format), new UTF8Encoding(false));
                    }

                    pages.Add(new
                    {
                        name = Path.GetFileName(full),
                        ok = true,
                        kind = kind.ToString().ToLowerInvariant(),
                        width = page.Width,
                        height = page.Height,
                        pixelWidth = w,
                        pixelHeight = h,
                        elapsedMs = Math.Round(page.ElapsedMs, 1),
                        lineCount = page.Lines.Count,
                        text = page.Text,
                        lines = page.Lines.Select(l => new
                        {
                            text = l.Text,
                            score = Math.Round(l.Score, 4),
                            box = l.Box,
                            chars = wantChars
                                ? l.Characters.Select(c => new { t = c.Text, x1 = c.X1, y1 = c.Y1, x2 = c.X2, y2 = c.Y2 })
                                : null
                        })
                    });
                    Audit.Write(jobId, full, bytes, page.Width, page.Height, page.ElapsedMs, true, null);
                    ok++;
                }
                catch (GuardException g)
                {
                    fileSw.Stop();
                    Audit.Write(jobId, raw, bytes, 0, 0, fileSw.Elapsed.TotalMilliseconds, false, g.Code);
                    pages.Add(new { name = Path.GetFileName(raw), ok = false, code = g.Code, message = g.Message });
                    fail++;
                }
                catch (Exception ex)
                {
                    fileSw.Stop();
                    Audit.Write(jobId, raw, bytes, 0, 0, fileSw.Elapsed.TotalMilliseconds, false, "E_DECODE");
                    pages.Add(new { name = Path.GetFileName(raw), ok = false, code = "E_DECODE", message = ex.Message });
                    fail++;
                }
            }

            totalSw.Stop();
            lock (LastLock)
            {
                LastPages = System.Text.Json.JsonSerializer.SerializeToNode(pages) as System.Text.Json.Nodes.JsonArray;
            }
            Proto.Emit(new
            {
                type = "result",
                id,
                jobId,
                ok = fail == 0,
                counts = new { ok, fail },
                totalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 0),
                cancelled = cts.IsCancellationRequested,
                pages
            });
        }
        catch (GuardException g)
        {
            Proto.EmitError(id, g.Code, g.Message);
        }
        catch (Exception ex)
        {
            Proto.EmitError(id, "E_INTERNAL", ex.Message);
        }
        finally
        {
            JobTokens.TryRemove(jobId, out _);
            cts.Dispose();
        }
    }

    // ── 导出（含合并排版） ────────────────────────────────────────────────

    private static void HandleExport(JsonObject root, string? id)
    {
        try
        {
            var p = Proto.GetParams(root);
            string format = Proto.GetString(p, "format", "txt")!.ToLowerInvariant();
            string layout = Proto.GetString(p, "layout", "sections")!.ToLowerInvariant();
            string paging = Proto.GetString(p, "paging", "pagebreak")!.ToLowerInvariant();
            string scope = Proto.GetString(p, "scope", "all")!.ToLowerInvariant();
            string? current = Proto.GetString(p, "current");
            string? outDirRaw = Proto.GetString(p, "outDir");
            if (string.IsNullOrWhiteSpace(outDirRaw))
            {
                Proto.EmitError(id, "E_PATH", "缺少导出目录");
                return;
            }
            string outDir = Guard.CheckOutDir(outDirRaw!);
            Directory.CreateDirectory(outDir);

            List<System.Text.Json.Nodes.JsonObject> pages;
            lock (LastLock)
            {
                pages = LastPages is null
                    ? new()
                    : LastPages.OfType<System.Text.Json.Nodes.JsonObject>()
                             .Select(n => (System.Text.Json.Nodes.JsonObject)n.DeepClone())
                             .ToList();
            }
            if (pages.Count == 0)
            {
                Proto.EmitError(id, "E_NOCACHE", "没有可导出的识别结果，请先识别");
                return;
            }

            // 自然排序：让 2 排在 10 前面
            pages.Sort((a, b) =>
            {
                var an = a["name"]?.GetValue<string>() ?? "";
                var bn = b["name"]?.GetValue<string>() ?? "";
                return NaturalCompare(an, bn);
            });

            if (scope == "file" && !string.IsNullOrEmpty(current))
                pages = pages.Where(x => string.Equals(x["name"]?.GetValue<string>(), current, StringComparison.OrdinalIgnoreCase)).ToList();

            // scope=group：界面把当前分组的文件名传进来，按名单过滤
            var names = p.ValueKind == System.Text.Json.JsonValueKind.Object
                        && p.TryGetProperty("names", out var na) && na.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? na.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : null;
            if (scope == "group" && names is { Count: > 0 })
                pages = pages.Where(x => names.Contains(x["name"]?.GetValue<string>() ?? "")).ToList();
            if (pages.Count == 0)
            {
                Proto.EmitError(id, "E_SCOPE", "所选范围内没有结果");
                return;
            }

            string ext = format switch { "md" or "markdown" => ".md", "json" => ".json", _ => ".txt" };
            string stem = (scope == "file" && !string.IsNullOrEmpty(current))
                ? "导出-" + Path.GetFileNameWithoutExtension(current)
                : "合并导出-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string target = Path.Combine(outDir, PathGuard.SafeFileName(stem) + ext);

            string content = format switch
            {
                "json" => BuildJson(pages),
                "md" or "markdown" => BuildMerged(pages, "md", layout, paging),
                _ => BuildMerged(pages, "txt", layout, paging)
            };
            File.WriteAllText(target, content, new UTF8Encoding(false));
            Proto.Emit(new { type = "exported", id, ok = true, path = target, files = pages.Count });
        }
        catch (GuardException g) { Proto.EmitError(id, g.Code, g.Message); }
        catch (Exception ex) { Proto.EmitError(id, "E_EXPORT", ex.Message); }
    }

    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int ei = i, ej = j;
                while (ei < a.Length && char.IsDigit(a[ei])) ei++;
                while (ej < b.Length && char.IsDigit(b[ej])) ej++;
                var ia = long.Parse(a[i..ei]);
                var ib = long.Parse(b[j..ej]);
                if (ia != ib) return ia.CompareTo(ib);
                i = ei; j = ej;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static string BuildMerged(List<System.Text.Json.Nodes.JsonObject> pages, string kind, string layout, string paging)
    {
        var sb = new StringBuilder();
        bool withToc = layout == "toc";
        bool plain = layout == "plain";
        bool pagebreak = paging == "pagebreak";

        if (withToc && kind == "md")
        {
            sb.AppendLine($"# 合并导出 · {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine("## 目录");
            sb.AppendLine();
            int n = 1;
            foreach (var pg in pages)
            {
                string name = pg["name"]?.GetValue<string>() ?? "";
                int lines = pg["lineCount"]?.GetValue<int>() ?? 0;
                sb.AppendLine($"{n++}. {name} — {lines} 行");
            }
            sb.AppendLine();
        }

        bool first = true;
        foreach (var pg in pages)
        {
            if (!first)
            {
                if (pagebreak)
                    sb.AppendLine(kind == "md" ? "\n---\n" : "\n\f\n");
                else
                    sb.AppendLine();
            }
            first = false;

            string name = pg["name"]?.GetValue<string>() ?? "";
            string text = pg["text"]?.GetValue<string>() ?? "";
            if (!plain)
            {
                sb.AppendLine(kind == "md" ? $"## {Path.GetFileNameWithoutExtension(name)}" : $"===== {name} =====");
                sb.AppendLine();
            }
            sb.AppendLine(text);
        }
        return sb.ToString();
    }

    private static string BuildJson(List<System.Text.Json.Nodes.JsonObject> pages)
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var pg in pages) arr.Add(pg.DeepClone());
        var doc = new System.Text.Json.Nodes.JsonObject
        {
            ["exportedAt"] = DateTime.Now.ToString("o"),
            ["count"] = pages.Count,
            ["pages"] = arr
        };
        return doc.ToJsonString(Proto.JsonWriteOpts);
    }

    // ── 引擎 ────────────────────────────────────────────────────────────────

    private static OcrEngine EnsureEngine(OcrTier tier, OcrDevice device)
    {
        string key = $"{tier}|{device}";
        if (_engine is not null && _engineKey == key) return _engine;

        DisposeEngine();
        _engine = new OcrEngine(new OcrOptions { Tier = tier, Device = device });
        _engine.Warmup();
        _engineKey = key;
        Proto.EmitLog("info", _engine.LoadDescription);
        return _engine;
    }

    private static void DisposeEngine()
    {
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        _engineKey = "";
    }

    private static string ResolveFormat(string? v) => v?.ToLowerInvariant() switch
    {
        "md" or "markdown" => OutputFormat.Markdown,
        "json" => OutputFormat.Json,
        _ => OutputFormat.Txt
    };

    private static OcrTier ResolveTier(string? v) => v?.ToLowerInvariant() switch
    {
        "tiny" => OcrTier.Tiny,
        "small" => OcrTier.Small,
        _ => OcrTier.Medium
    };

    private static OcrDevice ResolveDevice(string? v) => v?.ToLowerInvariant() switch
    {
        "cpu" => OcrDevice.Cpu,
        "gpu" or "vulkan" => OcrDevice.Vulkan,
        _ => OcrDevice.Auto
    };

    // ── 父进程 ──────────────────────────────────────────────────────────────

    private static bool VerifyParentToken(out string why)
    {
        why = "";
        string? token = Environment.GetEnvironmentVariable("OCRKIT_PARENT_TOKEN");
        if (string.IsNullOrEmpty(token)) { why = "缺少 OCRKIT_PARENT_TOKEN，只接受壳进程启动"; return false; }
        if (token.Length != 64) { why = "令牌长度不对（应为 64 位十六进制）"; return false; }
        foreach (char c in token)
            if (!Uri.IsHexDigit(c)) { why = "令牌不是十六进制"; return false; }
        return true;
    }

    /// <summary>壳死了就自杀，避免留下没人收尸的引擎进程。</summary>
    private static void StartParentWatchdog()
    {
        string? pidText = Environment.GetEnvironmentVariable("OCRKIT_PARENT_PID");
        if (!int.TryParse(pidText, out int pid)) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (proc.HasExited) break;
                }
                catch
                {
                    break;
                }
            }
            DisposeEngine();
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "ocrkit-parent-watchdog" };
        thread.Start();
    }
}