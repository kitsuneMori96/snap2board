using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Snap2Board;

internal static class ClipboardWriter
{
    private sealed record Job(string Path, byte[] Data, string Mime);

    private static readonly BlockingCollection<Job> Queue = new();
    private static readonly List<object> KeepAlive = new();
    private static (string? Hash, DateTime At) _lastWrite;

    /// <summary>剪贴板里的这张图是不是我们刚从手机写进去的（10 秒内按像素哈希比对）。</summary>
    public static bool IsRecentWrite(string pixelHash)
    {
        var (hash, at) = _lastWrite;
        return hash != null && hash == pixelHash && DateTime.UtcNow - at < TimeSpan.FromSeconds(10);
    }

    static ClipboardWriter()
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = "clipboard-writer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Write(string path, byte[] data, string mime)
    {
        Queue.Add(new Job(path, data, mime));
    }

    private static void Loop()
    {
        foreach (var job in Queue.GetConsumingEnumerable())
        {
            try
            {
                Apply(job);
            }
            catch (Exception ex)
            {
                AppLog.Info($"剪贴板写入失败: {ex.Message}");
            }
        }
    }

    private static void Apply(Job job)
    {
        var data = new DataObject();
        Image? image = null;
        MemoryStream? pngStream = null;

        try
        {
            using var raw = new MemoryStream(job.Data);
            using var decoded = Image.FromStream(raw);
            image = new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            AppLog.Info($"图片解码失败（仅提供文件）: {ex.Message}");
        }

        if (image != null)
        {
            data.SetData(DataFormats.Bitmap, true, image);
            if (job.Mime.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            {
                pngStream = new MemoryStream(job.Data);
                data.SetData("PNG", false, pngStream);
            }
        }

        data.SetData(DataFormats.FileDrop, true, new[] { job.Path });

        Exception? last = null;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true, 10, 30);
                lock (KeepAlive)
                {
                    KeepAlive.Add(data);
                    if (image != null) KeepAlive.Add(image);
                    if (pngStream != null) KeepAlive.Add(pngStream);
                    while (KeepAlive.Count > 32) KeepAlive.RemoveAt(0);
                }
                AppLog.Info($"剪贴板已更新: {Path.GetFileName(job.Path)} 格式={string.Join(",", data.GetFormats())}");
                if (image != null)
                {
                    _lastWrite = (ImageHash.Of(image), DateTime.UtcNow);
                }
                return;
            }
            catch (ExternalException ex)
            {
                last = ex;
                Thread.Sleep(60);
            }
        }

        AppLog.Info($"剪贴板被占用，放弃写入: {last?.Message}");
    }
}
