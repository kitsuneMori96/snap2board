using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Snap2Board;

internal sealed class TrayApp : ApplicationContext
{
    private readonly Config _config;
    private readonly HubClient _hub = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pushItem;
    private readonly ToolStripMenuItem _historyItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ClipboardWatcher _watcher;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _autoPairCode;
    private string _host = "";
    private bool _connected;
    private bool _pushEnabled = true;
    private string _lastPushedSha = "";

    public TrayApp(CliOptions options)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _config = Config.Load();
        if (!string.IsNullOrWhiteSpace(options.Host))
        {
            ApplyHostArgument(options.Host);
        }
        _autoPairCode = options.PairCode;

        _statusItem = new ToolStripMenuItem("未连接") { Enabled = false };
        _pushItem = new ToolStripMenuItem("上传剪贴板图片", null, (_, _) => TogglePush()) { Checked = true };
        _historyItem = new ToolStripMenuItem("历史（最近 10 张）");
        _autoStartItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart())
        {
            Checked = AutoStart.IsEnabled()
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("配对…", null, (_, _) => PromptPair()));
        menu.Items.Add(new ToolStripMenuItem("立即同步", null, (_, _) => ManualSync()));
        menu.Items.Add(_pushItem);
        menu.Items.Add(new ToolStripMenuItem("上传当前剪贴板", null, (_, _) => ManualUpload()));
        menu.Items.Add(_historyItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripMenuItem("打开媒体文件夹", null, (_, _) => OpenFolder(AppLog.MediaRoot)));
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenFile(AppLog.LogPath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
        menu.Opening += (_, _) => RebuildHistoryMenu();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Snap2Board",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => OpenFolder(AppLog.MediaRoot);

        AppLog.Info("托盘程序启动");
        _watcher = new ClipboardWatcher();
        _watcher.ImageCaptured += OnClipboardImage;
        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private void RebuildHistoryMenu()
    {
        _historyItem.DropDownItems.Clear();
        var all = ClipboardHistory.All();
        if (all.Count == 0)
        {
            _historyItem.DropDownItems.Add(new ToolStripMenuItem("（还没有记录）") { Enabled = false });
            return;
        }
        for (var i = 0; i < all.Count; i++)
        {
            var index = i;
            var entry = all[i];
            var stamp = entry.CapturedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(entry.CapturedAt).LocalDateTime
                : File.GetLastWriteTime(entry.Path);
            var item = new ToolStripMenuItem($"{stamp:MM-dd HH:mm:ss}  #{entry.Seq}  {entry.Name}", null,
                (_, _) => Restore(index));
            if (!File.Exists(entry.Path)) item.Enabled = false;
            _historyItem.DropDownItems.Add(item);
        }
        _historyItem.DropDownItems.Add(new ToolStripSeparator());
        _historyItem.DropDownItems.Add(new ToolStripMenuItem("恢复上一张", null, (_, _) => Restore(1)));
    }

    private void Restore(int index)
    {
        var entry = ClipboardHistory.Get(index);
        if (entry == null)
        {
            AppLog.Info("历史里没有这一张");
            return;
        }
        if (!File.Exists(entry.Path))
        {
            AppLog.Info($"历史文件已不存在: {entry.Path}");
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(entry.Path);
            ClipboardWriter.Write(entry.Path, bytes, entry.Mime);
            ClipboardHistory.Record(entry);
            AppLog.Info($"恢复历史 #{entry.Seq} {entry.Name}");
            ShowBalloon($"已恢复 #{entry.Seq}");
        }
        catch (Exception ex)
        {
            AppLog.Info($"恢复失败: {ex.Message}");
        }
    }

    private void OnClipboardImage(ClipboardImage image)
    {
        if (!_pushEnabled) return;
        if (ClipboardWriter.IsRecentWrite(image.PixelHash))
        {
            AppLog.Info("跳过回显：这张图刚从手机来");
            return;
        }
        if (image.Sha256 == _lastPushedSha) return;
        if (image.Png.Length > 50 * 1024 * 1024)
        {
            AppLog.Info($"剪贴板图片过大，跳过: {image.Png.Length} bytes");
            return;
        }
        _lastPushedSha = image.Sha256;
        _ = Task.Run(() => PushAsync(image));
    }

    private void ToggleAutoStart()
    {
        var want = !_autoStartItem.Checked;
        if (AutoStart.Set(want))
        {
            _autoStartItem.Checked = want;
            ShowBalloon(want ? "已加入开机启动" : "已取消开机启动");
        }
    }

    private void TogglePush()
    {
        _pushEnabled = !_pushEnabled;
        _pushItem.Checked = _pushEnabled;
        AppLog.Info(_pushEnabled ? "剪贴板上传已开启" : "剪贴板上传已关闭");
    }

    private void ManualUpload()
    {
        if (!Clipboard.ContainsImage())
        {
            AppLog.Info("剪贴板里没有图片");
            return;
        }
        try
        {
            using var img = Clipboard.GetImage();
            if (img == null) return;
            using var stream = new MemoryStream();
            img.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            var png = stream.ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            _lastPushedSha = sha;
            _ = Task.Run(() => PushAsync(new ClipboardImage(png, sha, "", img.Width, img.Height)));
        }
        catch (Exception ex)
        {
            AppLog.Info($"读取剪贴板失败: {ex.Message}");
        }
    }

    private async Task PushAsync(ClipboardImage image)
    {
        if (!_connected || string.IsNullOrEmpty(_config.Token))
        {
            AppLog.Info("未连接手机，剪贴板图片未上传");
            return;
        }
        try
        {
            var name = $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var ok = await _hub.PushImageAsync(
                _host, _config.Port, _config.Token, image.Png, image.Sha256, name,
                DateTimeOffset.Now.ToUnixTimeMilliseconds(), _cts.Token);
            AppLog.Info(ok
                ? $"已上传剪贴板图片 {image.Width}x{image.Height} ({image.Png.Length} bytes)"
                : "上传剪贴板图片失败");
            if (ok) ShowBalloon("已存入手机相册");
        }
        catch (Exception ex)
        {
            AppLog.Info($"上传异常: {ex.Message}");
        }
    }

    private void ApplyHostArgument(string value)
    {
        var parts = value.Split(':');
        _config.Host = parts[0];
        if (parts.Length > 1 && int.TryParse(parts[1], out var port))
        {
            _config.Port = port;
            _config.Save();
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        if (_autoPairCode != null)
        {
            for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                if (await DiscoverAsync(ct) != null)
                {
                    await PerformPairAsync(_autoPairCode, ct);
                    break;
                }
                try
                {
                    await Task.Delay(1000, ct);
                }
                catch
                {
                    return;
                }
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) break;
                // HttpClient 自身超时也走这里；手机 hub 假死时不能顺手把轮询线程也结束掉
                AppLog.Info("请求超时，稍后重试");
                _connected = false;
            }
            catch (Exception ex)
            {
                AppLog.Info($"同步异常: {ex.Message}");
                _connected = false;
            }

            try
            {
                await Task.Delay(2000, ct);
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }

        if (!ct.IsCancellationRequested)
        {
            AppLog.Info("轮询线程意外退出，5 秒后重启");
            try
            {
                await Task.Delay(5000, ct);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => PollLoopAsync(ct));
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        if (!_connected)
        {
            var host = await DiscoverAsync(ct);
            if (host == null)
            {
                SetStatus("未连接");
                return;
            }
        }

        if (string.IsNullOrEmpty(_config.Token))
        {
            SetStatus($"待配对 {_host}");
            return;
        }

        List<HubItem> items;
        try
        {
            items = await _hub.ItemsSinceAsync(_host, _config.Port, _config.Token, _config.Cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _connected = false;
            SetStatus("连接中断");
            throw;
        }

        HubItem? newest = null;
        string? newestPath = null;
        var pulled = 0;
        foreach (var item in items)
        {
            if (item.Origin == "windows")
            {
                _config.Cursor = item.Seq;
                _config.Save();
                AppLog.Info($"跳过回传 #{item.Seq} {item.Name}（PC 自己上传的）");
                continue;
            }
            var path = await EnsureFileAsync(item, ct);
            ClipboardHistory.Record(new HistoryEntry(
                item.Seq, item.Name, path, item.Mime, item.Sha256, item.CapturedAt));
            newest = item;
            newestPath = path;
            pulled++;
            _config.Cursor = item.Seq;
            _config.Save();
            AppLog.Info($"拉取 #{item.Seq} {item.Name} ({item.Size} bytes) -> {path}");
        }

        if (newest != null && newestPath != null)
        {
            var bytes = await File.ReadAllBytesAsync(newestPath, ct);
            ClipboardWriter.Write(newestPath, bytes, newest.Mime);
            if (pulled > 1)
            {
                AppLog.Info($"断线补课 {pulled} 张，剪贴板只写最新 #{newest.Seq}，其余在历史里");
                ShowBalloon($"补课 {pulled} 张，已复制最新 #{newest.Seq}");
            }
            else
            {
                ShowBalloon($"已复制: {newest.Name}");
            }
        }

        SetStatus($"已连接 {_host}:{_config.Port}");
    }

    private async Task<string> EnsureFileAsync(HubItem item, CancellationToken ct)
    {
        var stamp = item.CapturedAt > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(item.CapturedAt).LocalDateTime
            : DateTime.Now;
        var directory = Path.Combine(AppLog.MediaRoot, stamp.ToString("yyyyMM"));
        Directory.CreateDirectory(directory);

        var key = item.Sha256.Length > 0 ? item.Sha256 : item.Id;
        var file = Path.Combine(directory, key + ExtensionFor(item));
        if (File.Exists(file)) return file;

        var data = await _hub.DownloadAsync(_host, _config.Port, _config.Token, item.Id, ct);
        var actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (item.Sha256.Length > 0 && actual != item.Sha256)
        {
            AppLog.Info($"哈希不一致 {item.Id}: {actual} != {item.Sha256}");
        }

        var tmp = file + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct);
        File.Move(tmp, file, true);
        return file;
    }

    private static string ExtensionFor(HubItem item)
    {
        var extension = Path.GetExtension(item.Name);
        if (!string.IsNullOrEmpty(extension)) return extension.ToLowerInvariant();
        return item.Mime switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/heic" => ".heic",
            "image/heif" => ".heif",
            "image/gif" => ".gif",
            _ => ".jpg"
        };
    }

    private async Task<string?> DiscoverAsync(CancellationToken ct)
    {
        foreach (var candidate in Candidates())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            var health = await _hub.ProbeAsync(candidate, _config.Port, timeout.Token);
            if (health != null)
            {
                _host = candidate;
                _connected = true;
                if (_config.Host != candidate)
                {
                    _config.Host = candidate;
                    _config.Save();
                }
                AppLog.Info($"发现 hub: {candidate}:{_config.Port} ({health.Device})");
                return candidate;
            }
            if (ct.IsCancellationRequested) return null;
        }
        return null;
    }

    private IEnumerable<string> Candidates()
    {
        var seen = new HashSet<string>();
        if (!string.IsNullOrWhiteSpace(_config.Host) && seen.Add(_config.Host))
        {
            yield return _config.Host;
        }
        if (seen.Add("127.0.0.1"))
        {
            yield return "127.0.0.1";
        }
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up) continue;
            if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var gateway in network.GetIPProperties().GatewayAddresses)
            {
                var address = gateway.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (address.Equals(IPAddress.Any)) continue;
                var text = address.ToString();
                if (seen.Add(text))
                {
                    yield return text;
                }
            }
        }
    }

    private void PromptPair()
    {
        using var dialog = new PairDialog();
        if (dialog.ShowDialog() == DialogResult.OK && dialog.Code.Length > 0)
        {
            _ = PairFromDialogAsync(dialog.Code);
        }
    }

    private async Task PairFromDialogAsync(string code)
    {
        try
        {
            if (!_connected && await DiscoverAsync(_cts.Token) == null)
            {
                SetStatus("未连接，无法配对");
                return;
            }
            await PerformPairAsync(code, _cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Info($"配对异常: {ex.Message}");
        }
    }

    private async Task PerformPairAsync(string code, CancellationToken ct)
    {
        var token = await _hub.PairAsync(_host, _config.Port, code, ct);
        if (token == null)
        {
            AppLog.Info("配对失败：配对码不正确");
            SetStatus("配对失败");
            return;
        }
        _config.Token = token;
        _config.Save();
        AppLog.Info("配对成功");
        SetStatus("已配对");
    }

    private void ManualSync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncOnceAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                AppLog.Info($"手动同步失败: {ex.Message}");
            }
        });
    }

    private void SetStatus(string text)
    {
        _ui.Post(_ =>
        {
            _statusItem.Text = text;
            var shortText = text.Length > 52 ? text[..52] : text;
            _tray.Text = $"Snap2Board · {shortText}";
        }, null);
    }

    private void ShowBalloon(string text)
    {
        _ui.Post(_ =>
        {
            _tray.BalloonTipTitle = "Snap2Board";
            _tray.BalloonTipText = text;
            _tray.ShowBalloonTip(1500);
        }, null);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Info($"打开文件夹失败: {ex.Message}");
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Info($"打开日志失败: {ex.Message}");
        }
    }

    private void ExitApp()
    {
        _cts.Cancel();
        _watcher.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
