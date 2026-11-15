using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Snap2Board;

/// <summary>按像素内容算哈希（不含 PNG 编码差异），用于识别「这张图是不是我自己刚写进剪贴板的」。</summary>
internal static class ImageHash
{
    public static string Of(Image image)
    {
        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImage(image, 0, 0, image.Width, image.Height);
        }

        var stride = bitmap.Width * 4;
        var buffer = new byte[stride * bitmap.Height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var digest = SHA256.HashData(buffer);
        return $"{bitmap.Width}x{bitmap.Height}:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
