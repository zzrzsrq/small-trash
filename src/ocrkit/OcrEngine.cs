using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Small;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Medium;

namespace OcrKit;

public enum OcrTier { Tiny, Small, Medium }

public enum OcrDevice { Auto, Cpu, Vulkan }

public sealed class OcrOptions
{
    public OcrTier Tier { get; set; } = OcrTier.Medium;
    public OcrDevice Device { get; set; } = OcrDevice.Auto;
    public bool UseDirectionClassifier { get; set; } = true;
}

public sealed class OcrLine
{
    public string Text { get; set; } = "";
    public float Score { get; set; }
    public float[] Box { get; set; } = Array.Empty<float>();
    public List<CharacterBox> Characters { get; set; } = new();
}

public sealed class CharacterBox
{
    public string Text { get; set; } = "";
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }
}

public sealed class OcrPage
{
    public string SourceFile { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double ElapsedMs { get; set; }
    public List<OcrLine> Lines { get; set; } = new();
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    public string ToJson()
    {
        var payload = new
        {
            source = SourceFile,
            width = Width,
            height = Height,
            elapsedMs = Math.Round(ElapsedMs, 1),
            lineCount = Lines.Count,
            text = Text,
            lines = Lines.Select(l => new
            {
                text = l.Text,
                score = Math.Round(l.Score, 4),
                box = l.Box,
                chars = l.Characters.Select(c => new { text = c.Text, x1 = c.X1, y1 = c.Y1, x2 = c.X2, y2 = c.Y2 })
            })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}

/// <summary>
/// 引擎封装。同一个 (tier, device) 只加载一次，之后复用。
/// PP-OCRv6 模型以程序集内嵌资源形式存在，加载不写临时文件。
/// </summary>
public sealed class OcrEngine : IDisposable
{
    private readonly OcrOptions _options;
    private readonly PaddleOcrAll _ocr;

    public OcrOptions Options => _options;
    public double LoadMs { get; private set; }
    public string LoadDescription { get; private set; } = "";

    public OcrEngine(OcrOptions options)
    {
        _options = options;
        var bundle = options.Tier switch
        {
            OcrTier.Tiny => ChineseV6TinyModels.Default,
            OcrTier.Small => ChineseV6SmallModels.Default,
            _ => ChineseV6MediumModels.Default
        };

        var backend = options.Device switch
        {
            OcrDevice.Cpu => OcrBackend.Cpu,
            OcrDevice.Vulkan => OcrBackend.Vulkan,
            _ => OcrBackend.Auto
        };

        var sw = Stopwatch.StartNew();
        _ocr = PaddleOcrAll.Load(bundle, new PaddleOcrOptions
        {
            UseDirectionClassification = options.UseDirectionClassifier,
            Detector = new PaddleOcrDetectorOptions { Backend = backend },
            Classifier = new PaddleOcrClassifierOptions { Backend = backend },
            Recognizer = new PaddleOcrRecognizerOptions { Backend = backend }
        });
        sw.Stop();
        LoadMs = sw.Elapsed.TotalMilliseconds;
        LoadDescription = $"{options.Tier} / {options.Device} · 加载 {LoadMs:F0} ms · 行并行 {_ocr.EffectiveLineWorkerCount}";
    }

    public async Task<OcrEngine> EnsureWarmAsync()
    {
        return await Task.Run(() => this);
    }

    /// <summary>
    /// 预热：跑一张极小的合成图，把 JIT 与 GPU 着色器编译的一次性开销提前吃掉。
    /// 不做这一步的话第一张图会慢 5-10 倍（实测 1.7s vs 0.17s）。
    /// </summary>
    public void Warmup(int w = 96, int h = 96)
    {
        try
        {
            byte[] blank = new byte[w * h * 3];
            // 浅色底 + 几条深色横线，让检测与识别路径都真正跑一遍
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 3;
                    bool ink = (y % 18) < 3 && x > 8 && x < w - 8;
                    byte v = ink ? (byte)20 : (byte)245;
                    blank[i] = v; blank[i + 1] = v; blank[i + 2] = v;
                }
            _ocr.Run(blank, w, h, w * 3, ImagePixelFormat.Bgr24);
        }
        catch
        {
            // 预热失败不影响正常使用
        }
    }

    public OcrPage Run(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        using PixelBuffer image = ImageCodecs.Decode(data, PixelFormat.Bgr24, new DecodeOptions { ApplyOrientation = true });

        var sw = Stopwatch.StartNew();
        PaddleOcrResult result = _ocr.Run(image.Pixels, image.Width, image.Height, returnCtcAlignment: true,
            image.Stride, ImagePixelFormat.Bgr24);
        sw.Stop();

        var page = new OcrPage
        {
            SourceFile = path,
            Width = image.Width,
            Height = image.Height,
            ElapsedMs = sw.Elapsed.TotalMilliseconds
        };

        foreach (PaddleOcrLine line in result.Lines)
        {
            var box = line.Box;
            var ocrLine = new OcrLine
            {
                Text = line.Text,
                Score = line.RecognitionScore,
                Box = new[] { box.X1, box.Y1, box.X2, box.Y2, box.X3, box.Y3, box.X4, box.Y4 }
            };
            try
            {
                foreach (PaddleOcrCharacterBox ch in line.EstimateCharacterBoxes())
                {
                    ocrLine.Characters.Add(new CharacterBox
                    {
                        Text = ch.Text,
                        X1 = ch.X1,
                        Y1 = ch.Y1,
                        X2 = ch.X2,
                        Y2 = ch.Y2
                    });
                }
            }
            catch
            {
                // 逐字框是增强信息，失败不影响主结果
            }
            page.Lines.Add(ocrLine);
        }
        return page;
    }

    public void Dispose() => _ocr.Dispose();
}

public static class OutputFormat
{
    public const string Txt = "纯文本 .txt";
    public const string Markdown = "Markdown .md";
    public const string Json = "JSON（含坐标）.json";

    public static string Extension(string format) => format switch
    {
        Markdown => ".md",
        Json => ".json",
        _ => ".txt"
    };

    public static string Render(OcrPage page, string format)
    {
        if (format == Json) return page.ToJson();
        if (format == Markdown)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {Path.GetFileNameWithoutExtension(page.SourceFile)}");
            sb.AppendLine();
            sb.AppendLine($"> {page.Width}×{page.Height} · {page.Lines.Count} 行 · {page.ElapsedMs:F0} ms");
            sb.AppendLine();
            foreach (var l in page.Lines) sb.AppendLine(l.Text);
            sb.AppendLine();
            return sb.ToString();
        }
        return page.Text;
    }
}
