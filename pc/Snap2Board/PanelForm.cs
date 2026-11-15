using System.Drawing;
using System.Windows.Forms;

namespace Snap2Board;

internal sealed class PanelForm : Form
{
    private readonly TrayApp _app;
    private readonly Label _statusLabel = new();
    private readonly Label _detailLabel = new();
    private readonly ListView _list = new();
    private readonly PictureBox _preview = new();
    private readonly ImageList _thumbs = new() { ImageSize = new Size(64, 64), ColorDepth = ColorDepth.Depth32Bit };
    private readonly Dictionary<string, int> _thumbIndex = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private string _listSignature = "";
    private bool _allowClose;

    public PanelForm(TrayApp app)
    {
        _app = app;
        // WinForms 在这块 125% DPI 的屏上按字体比例自动缩到 0.8 倍，
        // 干脆关掉自动缩放：字号本身会跟 DPI 走，像素尺寸按写的来
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9f);
        Text = "Snap2Board";
        Width = 760;
        Height = 600;
        MinimumSize = new Size(420, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _statusLabel.Text = "未连接";

        _detailLabel.AutoSize = true;
        _detailLabel.ForeColor = Color.DimGray;

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12, 10, 12, 6)
        };
        header.Controls.Add(_statusLabel);
        header.Controls.Add(_detailLabel);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Dock = DockStyle.Fill;
        _list.SmallImageList = _thumbs;
        _list.Columns.Add("缩略图", 68);
        _list.Columns.Add("时间", 130);
        _list.Columns.Add("序号", 46);
        _list.Columns.Add("文件名", 192);
        _list.DoubleClick += (_, _) => RestoreSelected();
        _list.SelectedIndexChanged += (_, _) => UpdatePreview();

        var listGroup = new GroupBox
        {
            Text = "最近 10 张（双击写回剪贴板）",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(12, 0, 6, 8)
        };
        listGroup.Controls.Add(_list);

        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.Dock = DockStyle.Fill;
        _preview.BackColor = Color.FromArgb(246, 246, 246);
        _preview.Margin = new Padding(0);

        var previewGroup = new GroupBox
        {
            Text = "选中预览",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(6, 0, 12, 8)
        };
        previewGroup.Controls.Add(_preview);

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        lists.Controls.Add(listGroup, 0, 0);
        lists.Controls.Add(previewGroup, 1, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 46,
            Padding = new Padding(10, 7, 10, 8)
        };
        buttons.Controls.Add(MakeButton("立即同步", () => _app.ManualSync()));
        buttons.Controls.Add(MakeButton("恢复选中", RestoreSelected));
        buttons.Controls.Add(MakeButton("配对…", () => _app.PromptPair()));
        buttons.Controls.Add(MakeButton("上传剪贴板", () => _app.ManualUpload()));
        buttons.Controls.Add(MakeButton("媒体文件夹", () => _app.OpenMediaFolder()));

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        body.Controls.Add(lists);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        _timer.Tick += (_, _) => RefreshView();
        _timer.Start();
        RefreshView();
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(5, 2, 5, 2), Margin = new Padding(2, 3, 2, 3) };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Info($"面板操作失败: {ex.Message}");
            }
        };
        return button;
    }

    private void RestoreSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is int index)
        {
            _app.Restore(index);
        }
    }

    private void RefreshView()
    {
        _statusLabel.Text = _app.StatusText;
        _detailLabel.Text = _app.StatusDetail;
        _listSignature = FillList();
    }

    private string FillList()
    {
        var all = ClipboardHistory.All();
        var signature = all.Count == 0
            ? "0"
            : $"{all.Count}|{all[0].Path}|{all[^1].Path}";
        if (signature == _listSignature) return signature;

        var selected = _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is int i ? i : -1;
        _list.BeginUpdate();
        _list.Items.Clear();
        for (var index = 0; index < all.Count; index++)
        {
            var entry = all[index];
            var stamp = entry.CapturedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(entry.CapturedAt).LocalDateTime
                : File.GetLastWriteTime(entry.Path);
            var item = new ListViewItem("") { ImageIndex = ThumbFor(entry) };
            item.SubItems.Add(stamp.ToString("MM-dd HH:mm:ss"));
            item.SubItems.Add(entry.Seq.ToString());
            item.SubItems.Add(entry.Name);
            item.Tag = index;
            if (!File.Exists(entry.Path)) item.ForeColor = Color.Gray;
            _list.Items.Add(item);
            if (index == selected) item.Selected = true;
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0 && _list.SelectedItems.Count == 0)
        {
            _list.Items[0].Selected = true;
        }
        return signature;
    }

    private void UpdatePreview()
    {
        Image? image = null;
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is int index
            && ClipboardHistory.Get(index) is { } entry && File.Exists(entry.Path))
        {
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(entry.Path));
                using var source = Image.FromStream(stream);
                // 拷一份出来，别让 GDI+ 继续依赖这个流
                image = new Bitmap(source);
            }
            catch (Exception ex)
            {
                AppLog.Info($"预览加载失败 {entry.Name}: {ex.Message}");
            }
        }
        var old = _preview.Image;
        _preview.Image = image;
        old?.Dispose();
    }

    private int ThumbFor(HistoryEntry entry)
    {
        if (_thumbIndex.TryGetValue(entry.Path, out var cached)) return cached;
        Image? thumb = null;
        try
        {
            // 先读进内存再解码，避免 Image.FromFile 长期占着文件
            var bytes = File.ReadAllBytes(entry.Path);
            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            thumb = FitThumb(source);
        }
        catch (Exception ex)
        {
            AppLog.Info($"缩略图加载失败 {entry.Name}: {ex.Message}");
        }
        thumb ??= PlaceholderThumb();
        _thumbs.Images.Add(thumb);
        var index = _thumbs.Images.Count - 1;
        _thumbIndex[entry.Path] = index;
        return index;
    }

    // 等比缩放居中，别把竖屏截图压成正方形
    private Bitmap FitThumb(Image source)
    {
        var size = _thumbs.ImageSize;
        var scale = Math.Min((double)size.Width / source.Width, (double)size.Height / source.Height);
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var canvas = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.White);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        return canvas;
    }

    private Bitmap PlaceholderThumb()
    {
        var size = _thumbs.ImageSize;
        var bitmap = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.WhiteSmoke);
        using var pen = new Pen(Color.Gainsboro);
        g.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
        using var font = new Font("Microsoft YaHei UI", 8f);
        TextRenderer.DrawText(g, "缺失", font, new Rectangle(Point.Empty, size), Color.Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return bitmap;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            // 关面板只是收起来，托盘继续跑
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    public void StopTimer()
    {
        _timer.Stop();
        _timer.Dispose();
        _preview.Image?.Dispose();
    }
}
