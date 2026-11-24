using System.Text;

namespace OcrKit;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 边车模式：由壳进程通过 stdio 拉起，只认父进程令牌。不初始化 WinForms，也不开任何端口。
        if (args.Length > 0 && args[0].Equals("--serve", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = OcrKit.Serve.ServeRunner.Run();
            return;
        }

        ApplicationConfiguration.Initialize();

        // 命令行模式：OcrKit.exe --cli <图片或文件夹> [--format txt|md|json] [--tier medium|small|tiny] [--out 目录]
        if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(CliRunner.Run(args.Skip(1).ToArray()));
            return;
        }

        if (args.Length == 1 && (args[0] == "--help" || args[0] == "/?" || args[0] == "-h"))
        {
            MessageBox.Show(                "OcrKit · 本地中文 OCR\n\n" +
                "图形界面：直接双击运行，拖入图片或文件夹即可。\n\n" +
                "命令行：\n" +
                "  OcrKit.exe --cli <图片或文件夹> [选项]\n\n" +
                "选项：\n" +
                "  --format txt|md|json   输出格式（默认 txt）\n" +
                "  --tier medium|small|tiny  模型档位（默认 medium）\n" +
                "  --device auto|gpu|cpu  运行设备（默认 auto）\n" +
                "  --out <目录>           输出目录（默认图片旁边）\n\n" +
                "模型：PP-OCRv6（PaddleOCR 官方权重，内嵌在程序里）\n" +
                "推理：Sdcb.SimdPaddleOCR 2.0（纯托管，无需 ONNX Runtime）",
                "OcrKit 帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm(args));
    }
}

internal static class CliRunner
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: OcrKit.exe --cli <图片或文件夹> [--format txt|md|json] [--tier ...] [--device ...] [--out 目录]");
            return 2;
        }

        string input = args[0];
        string format = OutputFormat.Txt;
        OcrTier tier = OcrTier.Medium;
        OcrDevice device = OcrDevice.Auto;
        string? outDir = null;

        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--format":
                    format = args[i + 1].ToLowerInvariant() switch
                    {
                        "md" or "markdown" => OutputFormat.Markdown,
                        "json" => OutputFormat.Json,
                        _ => OutputFormat.Txt
                    };
                    break;
                case "--tier":
                    tier = args[i + 1].ToLowerInvariant() switch
                    {
                        "tiny" => OcrTier.Tiny,
                        "small" => OcrTier.Small,
                        _ => OcrTier.Medium
                    };
                    break;
                case "--device":
                    device = args[i + 1].ToLowerInvariant() switch
                    {
                        "gpu" or "vulkan" => OcrDevice.Vulkan,
                        "cpu" => OcrDevice.Cpu,
                        _ => OcrDevice.Auto
                    };
                    break;
                case "--out":
                    outDir = args[i + 1];
                    break;
            }
        }

        string[] exts = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".gif" };
        List<string> files;
        if (Directory.Exists(input))
        {
            files = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (File.Exists(input)) files = new List<string> { input };
        else
        {
            Console.Error.WriteLine($"找不到: {input}");
            return 2;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("没有找到图片文件。");
            return 2;
        }

        Console.WriteLine($"加载引擎 {tier}/{device} …");
        var engine = new OcrEngine(new OcrOptions { Tier = tier, Device = device });
        Console.WriteLine(engine.LoadDescription);
        engine.Warmup();
        Console.WriteLine($"待处理 {files.Count} 个文件\n");

        string ext = OutputFormat.Extension(format);
        int ok = 0, fail = 0;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var f in files)
        {
            try
            {
                var page = engine.Run(f);
                string target;
                if (outDir is null)
                {
                    target = Path.ChangeExtension(f, ext);
                }
                else
                {
                    Directory.CreateDirectory(outDir);
                    target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ext);
                }
                File.WriteAllText(target, OutputFormat.Render(page, format), new UTF8Encoding(false));
                Console.WriteLine($"[{ok + fail + 1}/{files.Count}] {Path.GetFileName(f)} → {Path.GetFileName(target)}  {page.ElapsedMs:F0} ms  {page.Lines.Count} 行");
                ok++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{ok + fail + 1}/{files.Count}] {Path.GetFileName(f)} 失败: {ex.Message}");
                fail++;
            }
        }

        totalSw.Stop();
        engine.Dispose();
        Console.WriteLine($"\n完成 {ok} 个" + (fail > 0 ? $"，失败 {fail} 个" : "") +
                          $" · 总耗时 {totalSw.Elapsed.TotalSeconds:F1}s");
        return fail == 0 ? 0 : 1;
    }
}
