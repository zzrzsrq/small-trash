using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace OcrKit;

public sealed class MainForm : Form
{
    // 配色：纸底 + 墨蓝（与资料卡同一套语言）
    static readonly Color Paper = ColorTranslator.FromHtml("#F7F2EB");
    static readonly Color Ink = ColorTranslator.FromHtml("#081F5C");
    static readonly Color Accent = ColorTranslator.FromHtml("#334EAC");
    static readonly Color Muted = ColorTranslator.FromHtml("#7096D1");
    static readonly Color Line = ColorTranslator.FromHtml("#D8DFEA");
    static readonly Color Panel = ColorTranslator.FromHtml("#FFFFFF");

    private readonly ComboBox _tierBox = new();
    private readonly ComboBox _deviceBox = new();
    private readonly ComboBox _formatBox = new();
    private readonly TextBox _outputDirBox = new();
    private readonly ListView _fileList = new();
    private readonly PictureBox _preview = new();
    private readonly TextBox _result = new();
    private readonly Button _runButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _copyButton = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Label _engineInfo = new();

    private OcrEngine? _engine;
    private string? _engineKey;
    private readonly Dictionary<string, OcrPage> _pages = new();
    private CancellationTokenSource? _cts;
    private readonly string[] _initialPaths;
    private SplitContainer _split = null!;

    public MainForm(string[]? initialPaths = null)
    {
        _initialPaths = initialPaths ?? Array.Empty<string>();
        Text = "OcrKit · 本地中文 OCR（PP-OCRv6）";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1180, 760);
        BackColor = Paper;
        Font = new Font("Microsoft YaHei UI", 9F);
        AllowDrop = true;

        BuildUi();
        WireEvents();
        UpdateEngineInfo("引擎未加载 · 可直接把图片拖进窗口");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // SplitContainer 在构造期尺寸未定，SplitterDistance 会被夹回默认值，
        // 所以等窗口真正显示后再设一次。
        try
        {
            _split.SplitterDistance = 360;
            _split.Panel1MinSize = 240;
            _split.Panel2MinSize = 320;
        }
        catch { /* 尺寸夹取失败不影响使用 */ }

        if (_initialPaths.Length > 0)
        {
            foreach (var p in _initialPaths) AddPath(p, recursive: true);
            if (_fileList.Items.Count > 0)
            {
                _fileList.Items[0].Selected = true;
                SetStatus($"已载入 {_fileList.Items.Count} 个文件，点「开始识别」");
            }
        }
    }

    // ───────────── UI ─────────────
    private void BuildUi()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 66,
            BackColor = Paper,
            Padding = new Padding(22, 12, 22, 0)
        };
        var title = new Label
        {
            Text = "OcrKit",
            Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(22, 12)
        };
        var subtitle = new Label
        {
            Text = "本地中文 OCR · PP-OCRv6 · 纯托管推理，不联网、不上传",
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(24, 40),
            Font = new Font("Microsoft YaHei UI", 8.5F)
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 54,
            BackColor = Paper,
            Padding = new Padding(22, 8, 22, 8),
            WrapContents = false,
            AutoScroll = false
        };
        _tierBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _tierBox.Items.AddRange(new object[] { "medium（最准）", "small（均衡）", "tiny（最快）" });
        _tierBox.SelectedIndex = 0;
        _tierBox.Width = 132;

        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceBox.Items.AddRange(new object[] { "自动", "GPU (Vulkan)", "CPU" });
        _deviceBox.SelectedIndex = 0;
        _deviceBox.Width = 118;

        _formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _formatBox.Items.AddRange(new object[] { "纯文本 .txt", "Markdown .md", "JSON（含坐标）.json" });
        _formatBox.SelectedIndex = 0;
        _formatBox.Width = 150;

        toolbar.Controls.Add(MakeLabel("模型"));
        toolbar.Controls.Add(_tierBox);
        toolbar.Controls.Add(MakeSpacer(10));
        toolbar.Controls.Add(MakeLabel("设备"));
        toolbar.Controls.Add(_deviceBox);
        toolbar.Controls.Add(MakeSpacer(10));
        toolbar.Controls.Add(MakeLabel("输出"));
        toolbar.Controls.Add(_formatBox);

        var actions = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Paper,
            Padding = new Padding(22, 6, 22, 6)
        };
        var addFiles = MakeButton("添加图片…", false);
        addFiles.Location = new Point(22, 10);
        addFiles.Click += (_, _) => PickFiles();

        var addFolder = MakeButton("添加文件夹…", false);
        addFolder.Location = new Point(140, 10);
        addFolder.Click += (_, _) => PickFolder();

        var clear = MakeButton("清空列表", false);
        clear.Location = new Point(272, 10);
        clear.Click += (_, _) => ClearAll();

        _runButton.Text = "开始识别";
        StylePrimary(_runButton);
        _runButton.Size = new Size(120, 34);
        _runButton.Location = new Point(390, 9);
        _runButton.Click += async (_, _) => await RunAllAsync();

        _exportButton.Text = "导出结果";
        StyleSecondary(_exportButton);
        _exportButton.Size = new Size(100, 34);
        _exportButton.Location = new Point(520, 9);
        _exportButton.Enabled = false;
        _exportButton.Click += (_, _) => ExportAll();

        _copyButton.Text = "复制当前";
        StyleSecondary(_copyButton);
        _copyButton.Size = new Size(100, 34);
        _copyButton.Location = new Point(628, 9);
        _copyButton.Enabled = false;
        _copyButton.Click += (_, _) => CopyCurrent();

        actions.Controls.AddRange(new Control[] { addFiles, addFolder, clear, _runButton, _exportButton, _copyButton });

        // 主体：左侧文件列表，右侧预览 + 结果
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Line,
            Padding = new Padding(0)
        };
        _split = split;

        _fileList.View = View.Details;
        _fileList.FullRowSelect = true;
        _fileList.GridLines = false;
        _fileList.BorderStyle = BorderStyle.None;
        _fileList.BackColor = Panel;
        _fileList.ForeColor = Ink;
        _fileList.Font = new Font("Microsoft YaHei UI", 9F);
        _fileList.Columns.Add("文件", 210);
        _fileList.Columns.Add("状态", 96);
        _fileList.Columns.Add("耗时", 60);
        _fileList.Dock = DockStyle.Fill;
        _fileList.SelectedIndexChanged += (_, _) => ShowSelected();

        var leftPanel = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(1) };
        leftPanel.Controls.Add(_fileList);
        split.Panel1.Controls.Add(leftPanel);

        _preview.Dock = DockStyle.Top;
        _preview.Height = 250;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = ColorTranslator.FromHtml("#EFEDE7");
        _preview.BorderStyle = BorderStyle.None;

        _result.Multiline = true;
        _result.ScrollBars = ScrollBars.Vertical;
        _result.Dock = DockStyle.Fill;
        _result.BorderStyle = BorderStyle.None;
        _result.BackColor = Panel;
        _result.ForeColor = ColorTranslator.FromHtml("#1C1C1A");
        _result.Font = new Font("Microsoft YaHei UI", 10.5F);
        _result.ReadOnly = true;

        var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(1) };
        rightPanel.Controls.Add(_result);
        rightPanel.Controls.Add(_preview);
        split.Panel2.Controls.Add(rightPanel);

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 66,
            BackColor = Paper,
            Padding = new Padding(22, 8, 22, 8)
        };
        _outputDirBox.Width = 520;
        _outputDirBox.Location = new Point(100, 6);
        _outputDirBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OcrKit 输出");
        _outputDirBox.BorderStyle = BorderStyle.FixedSingle;

        var outLabel = MakeLabel("输出目录");
        outLabel.Location = new Point(22, 9);
        outLabel.AutoSize = true;

        var browse = MakeButton("浏览…", false);
        browse.Location = new Point(628, 4);
        browse.Size = new Size(70, 26);
        browse.Click += (_, _) => PickOutputDir();

        _progress.Location = new Point(22, 38);
        _progress.Size = new Size(300, 8);
        _progress.Style = ProgressBarStyle.Continuous;

        _status.Location = new Point(334, 34);
        _status.AutoSize = true;
        _status.ForeColor = Accent;

        _engineInfo.Location = new Point(710, 9);
        _engineInfo.AutoSize = true;
        _engineInfo.ForeColor = Muted;
        _engineInfo.Font = new Font("Microsoft YaHei UI", 8F);

        bottom.Controls.AddRange(new Control[] { outLabel, _outputDirBox, browse, _progress, _status, _engineInfo });

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(actions);
        Controls.Add(toolbar);
        Controls.Add(header);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = ColorTranslator.FromHtml("#334EAC"),
        Font = new Font("Microsoft YaHei UI", 9F),
        Margin = new Padding(0, 10, 6, 0)
    };

    private static Control MakeSpacer(int w) => new Panel { Width = w, Height = 1, Margin = new Padding(0) };

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(118, 34),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9F),
            Cursor = Cursors.Hand
        };
        if (primary) StylePrimary(b); else StyleSecondary(b);
        return b;
    }

    private static void StylePrimary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = ColorTranslator.FromHtml("#081F5C");
        b.ForeColor = ColorTranslator.FromHtml("#F7F2EB");
        b.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
    }

    private static void StyleSecondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#BAD6EB");
        b.FlatAppearance.BorderSize = 1;
        b.BackColor = ColorTranslator.FromHtml("#FFFFFF");
        b.ForeColor = ColorTranslator.FromHtml("#081F5C");
        b.Cursor = Cursors.Hand;
    }

    // ───────────── 事件 ─────────────
    private void WireEvents()
    {
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _runButton.Enabled = true;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var p in paths) AddPath(p, recursive: true);
    }

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff;*.gif|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            foreach (var f in dlg.FileNames) AddPath(f, recursive: false);
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择包含图片的文件夹" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPath(dlg.SelectedPath, recursive: true);
    }

    private void PickOutputDir()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择输出目录" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _outputDirBox.Text = dlg.SelectedPath;
    }

    private static readonly string[] ImageExts =
        { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".gif" };

    private void AddPath(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(path, "*", opt)
                .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var f in files) AddFile(f);
            SetStatus($"已从文件夹添加 {files.Count} 个文件");
        }
        else if (File.Exists(path) && ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            AddFile(path);
        }
    }

    private void AddFile(string path)
    {
        if (_fileList.Items.Cast<ListViewItem>().Any(i => string.Equals(i.Tag as string, path, StringComparison.OrdinalIgnoreCase)))
            return;
        var item = new ListViewItem(Path.GetFileName(path)) { Tag = path };
        item.SubItems.Add("待处理");
        item.SubItems.Add("—");
        _fileList.Items.Add(item);
    }

    private void ClearAll()
    {
        _fileList.Items.Clear();
        _pages.Clear();
        _result.Clear();
        _preview.Image?.Dispose();
        _preview.Image = null;
        _exportButton.Enabled = false;
        _copyButton.Enabled = false;
        _progress.Value = 0;
        SetStatus("");
    }

    private void ShowSelected()
    {
        if (_fileList.SelectedItems.Count == 0) return;
        var path = _fileList.SelectedItems[0].Tag as string;
        if (path is null) return;

        try
        {
            using var fs = File.OpenRead(path);
            using var tmp = Image.FromStream(fs);
            _preview.Image?.Dispose();
            _preview.Image = new Bitmap(tmp);
        }
        catch
        {
            _preview.Image?.Dispose();
            _preview.Image = null;
        }

        _result.Text = _pages.TryGetValue(path, out var page) ? page.Text : "";
    }

    private OcrOptions CurrentOptions() => new()
    {
        Tier = _tierBox.SelectedIndex switch { 1 => OcrTier.Small, 2 => OcrTier.Tiny, _ => OcrTier.Medium },
        Device = _deviceBox.SelectedIndex switch { 1 => OcrDevice.Vulkan, 2 => OcrDevice.Cpu, _ => OcrDevice.Auto },
        UseDirectionClassifier = true
    };

    private async Task EnsureEngineAsync(OcrOptions options)
    {
        string key = $"{options.Tier}|{options.Device}|{options.UseDirectionClassifier}";
        if (_engine is not null && _engineKey == key) return;

        _runButton.Enabled = false;
        UpdateEngineInfo("引擎加载中…");
        SetStatus("正在加载模型…");

        var previous = _engine;
        try
        {
            var created = await Task.Run(() =>
            {
                var e = new OcrEngine(options);
                e.Warmup();
                return e;
            });
            _engine = created;
            _engineKey = key;
            previous?.Dispose();
            UpdateEngineInfo(created.LoadDescription);
        }
        catch (Exception ex)
        {
            _engine = previous;
            UpdateEngineInfo("加载失败，回退 CPU");
            MessageBox.Show(this, $"GPU 后端加载失败，已退回 CPU：\n\n{ex.Message}",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            options.Device = OcrDevice.Cpu;
            var created = await Task.Run(() =>
            {
                var e = new OcrEngine(options);
                e.Warmup();
                return e;
            });
            _engine = created;
            _engineKey = $"{options.Tier}|{OcrDevice.Cpu}|{options.UseDirectionClassifier}";
            UpdateEngineInfo(created.LoadDescription);
        }
        finally
        {
            _runButton.Enabled = true;
        }
    }

    private async Task RunAllAsync()
    {
        if (_fileList.Items.Count == 0)
        {
            MessageBox.Show(this, "先添加图片或文件夹。\n\n也可以直接把图片拖进窗口。",
                "还没有文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = CurrentOptions();
        await EnsureEngineAsync(options);
        if (_engine is null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _runButton.Enabled = false;
        _exportButton.Enabled = false;
        _progress.Maximum = _fileList.Items.Count;
        _progress.Value = 0;

        var total = new Stopwatch();
        total.Start();
        int done = 0, failed = 0;

        try
        {
            foreach (ListViewItem item in _fileList.Items)
            {
                if (token.IsCancellationRequested) break;
                var path = item.Tag as string;
                if (path is null) continue;

                item.SubItems[1].Text = "识别中…";
                item.SubItems[1].ForeColor = Accent;
                _fileList.Refresh();
                SetStatus($"正在识别 {Path.GetFileName(path)}");
                Application.DoEvents();

                try
                {
                    var page = await Task.Run(() => _engine!.Run(path), token);
                    _pages[path] = page;
                    item.SubItems[1].Text = "完成";
                    item.SubItems[1].ForeColor = ColorTranslator.FromHtml("#2F7D4F");
                    item.SubItems[2].Text = $"{page.ElapsedMs:F0} ms";
                    done++;

                    if (_fileList.SelectedItems.Count == 0 || _fileList.SelectedItems[0] == item)
                        _result.Text = page.Text;
                }
                catch (Exception ex)
                {
                    item.SubItems[1].Text = "失败";
                    item.SubItems[1].ForeColor = ColorTranslator.FromHtml("#B3402A");
                    item.SubItems[2].Text = "—";
                    _result.Text = $"识别失败：{ex.Message}";
                    failed++;
                }
                _progress.Value = Math.Min(_progress.Maximum, _progress.Value + 1);
            }
        }
        finally
        {
            total.Stop();
            _runButton.Enabled = true;
            _cts?.Dispose();
            _cts = null;
        }

        _exportButton.Enabled = _pages.Count > 0;
        _copyButton.Enabled = _pages.Count > 0;
        double per = done > 0 ? total.Elapsed.TotalMilliseconds / done : 0;
        SetStatus(failed == 0
            ? $"完成 {done} 张 · 总耗时 {total.Elapsed.TotalSeconds:F1}s · 平均 {per:F0} ms/张"
            : $"完成 {done} 张，失败 {failed} 张 · 总耗时 {total.Elapsed.TotalSeconds:F1}s");
    }

    private void ExportAll()
    {
        if (_pages.Count == 0) { SetStatus("没有可导出的结果"); return; }

        string dir = _outputDirBox.Text.Trim();
        if (dir.Length == 0) dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OcrKit 输出");

        try
        {
            Directory.CreateDirectory(dir);
            string format = _formatBox.SelectedItem as string ?? OutputFormat.Txt;
            string ext = OutputFormat.Extension(format);
            int n = 0;

            foreach (var (path, page) in _pages)
            {
                string name = Path.GetFileNameWithoutExtension(path) + ext;
                string target = Path.Combine(dir, name);
                int dup = 1;
                while (File.Exists(target))
                    target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} ({dup++}){ext}");

                File.WriteAllText(target, OutputFormat.Render(page, format), new UTF8Encoding(false));
                n++;
            }

            SetStatus($"已导出 {n} 个文件到 {dir}");
            if (MessageBox.Show(this, $"已导出 {n} 个文件到：\n{dir}\n\n要打开这个文件夹吗？",
                    "导出完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopyCurrent()
    {
        if (_fileList.SelectedItems.Count == 0)
        {
            SetStatus("先选一个文件");
            return;
        }
        var path = _fileList.SelectedItems[0].Tag as string;
        if (path is not null && _pages.TryGetValue(path, out var page))
        {
            Clipboard.SetText(page.Text);
            SetStatus("已复制到剪贴板");
        }
    }

    private void SetStatus(string text) => _status.Text = text;
    private void UpdateEngineInfo(string text) => _engineInfo.Text = text;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine?.Dispose();
            _preview.Image?.Dispose();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
