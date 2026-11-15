using System.Drawing;
using System.Windows.Forms;

namespace Snap2Board;

internal sealed class HostDialog : Form
{
    private readonly TextBox _addressBox;

    public HostDialog(string current)
    {
        Text = "指定手机地址";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 130);

        var label = new Label
        {
            Text = "输入手机 hub 地址，留空端口则用当前端口\n一般填手机热点网关，例如 192.168.147.177:28642",
            Left = 16,
            Top = 12,
            Width = 330,
            Height = 34
        };
        _addressBox = new TextBox { Left = 16, Top = 52, Width = 328, Text = current };
        var ok = new Button
        {
            Text = "连接",
            DialogResult = DialogResult.OK,
            Left = 188,
            Top = 92,
            Width = 75
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Left = 269,
            Top = 92,
            Width = 75
        };

        Controls.AddRange(new Control[] { label, _addressBox, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Address => _addressBox.Text.Trim();
}
