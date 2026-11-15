using System.Drawing;
using System.Windows.Forms;

namespace Snap2Board;

internal sealed class PanelForm : Form
{
    private readonly TrayApp _app;
    private readonly Label _statusLabel = new();
    private readonly Label _detailLabel = new();
    private readonly ListView _list = new();
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
        Width = 560;
        Height = 560;
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
        _list.Columns.Add("时间", 130);
        _list.Columns.Add("序号", 60);
        _list.Columns.Add("文件名", 300);
        _list.DoubleClick += (_, _) => RestoreSelected();

        var listGroup = new GroupBox
        {
            Text = "最近 10 张（双击写回剪贴板）",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(12, 0, 12, 8)
        };
        listGroup.Controls.Add(_list);

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
        body.Controls.Add(listGroup);

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
            var item = new ListViewItem(stamp.ToString("MM-dd HH:mm:ss"));
            item.SubItems.Add(entry.Seq.ToString());
            item.SubItems.Add(entry.Name);
            item.Tag = index;
            if (!File.Exists(entry.Path)) item.ForeColor = Color.Gray;
            _list.Items.Add(item);
            if (index == selected) item.Selected = true;
        }
        _list.EndUpdate();
        return signature;
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
    }
}
