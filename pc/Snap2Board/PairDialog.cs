using System.Drawing;
using System.Windows.Forms;

namespace Snap2Board;

internal sealed class PairDialog : Form
{
    private readonly TextBox _codeBox;

    public PairDialog()
    {
        Text = "配对 Snap2Board";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 120);

        var label = new Label
        {
            Text = "输入手机 App 上显示的 6 位配对码",
            Left = 16,
            Top = 14,
            Width = 290,
            AutoSize = false
        };
        _codeBox = new TextBox { Left = 16, Top = 40, Width = 288, MaxLength = 12 };
        var ok = new Button
        {
            Text = "配对",
            DialogResult = DialogResult.OK,
            Left = 148,
            Top = 78,
            Width = 75
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Left = 229,
            Top = 78,
            Width = 75
        };

        Controls.AddRange(new Control[] { label, _codeBox, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Code => _codeBox.Text.Trim();
}
