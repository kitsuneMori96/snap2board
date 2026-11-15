using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Snap2Board;

internal sealed record ClipboardImage(byte[] Png, string Sha256, string PixelHash, int Width, int Height);

/// <summary>监听系统剪贴板里的位图。变化后等 250ms 再读，避开源程序「先写空再写满」的中间态。</summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private sealed class MessageWindow : NativeWindow
    {
        public Action<int>? OnMessage;

        protected override void WndProc(ref Message m)
        {
            OnMessage?.Invoke(m.Msg);
            base.WndProc(ref m);
        }
    }

    private readonly MessageWindow _window = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
    private bool _capturing;

    public event Action<ClipboardImage>? ImageCaptured;

    public ClipboardWatcher()
    {
        _window.CreateHandle(new CreateParams { Caption = "Snap2BoardClipboardWatcher" });
        _window.OnMessage = msg =>
        {
            if (msg != WM_CLIPBOARDUPDATE) return;
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Capture();
        };
        AddClipboardFormatListener(_window.Handle);
    }

    private void Capture()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            if (!Clipboard.ContainsImage()) return;
            var before = GetClipboardSequenceNumber();
            using var image = ReadWithRetry();
            if (image == null) return;
            if (GetClipboardSequenceNumber() != before)
            {
                _debounce.Start();
                return;
            }

            var pixelHash = ImageHash.Of(image);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            var png = stream.ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            ImageCaptured?.Invoke(new ClipboardImage(png, sha, pixelHash, image.Width, image.Height));
        }
        catch (Exception ex)
        {
            AppLog.Info($"读取剪贴板图片失败: {ex.Message}");
        }
        finally
        {
            _capturing = false;
        }
    }

    private static Image? ReadWithRetry()
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                return Clipboard.GetImage();
            }
            catch (ExternalException)
            {
                if (attempt == 4) throw;
                Thread.Sleep(80);
            }
        }
        return null;
    }

    public void Dispose()
    {
        _debounce.Dispose();
        if (_window.Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_window.Handle);
            _window.DestroyHandle();
        }
    }
}
