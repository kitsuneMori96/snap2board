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
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _autoPairCode;
    private string _host = "";
    private bool _connected;

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
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("配对…", null, (_, _) => PromptPair()));
        menu.Items.Add(new ToolStripMenuItem("立即同步", null, (_, _) => ManualSync()));
        menu.Items.Add(new ToolStripMenuItem("打开媒体文件夹", null, (_, _) => OpenFolder(AppLog.MediaRoot)));
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenFile(AppLog.LogPath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Snap2Board",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => OpenFolder(AppLog.MediaRoot);

        AppLog.Info("托盘程序启动");
        _ = Task.Run(() => PollLoopAsync(_cts.Token));
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
                break;
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
                break;
            }
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
        foreach (var item in items)
        {
            var path = await EnsureFileAsync(item, ct);
            _config.Cursor = item.Seq;
            _config.Save();
            newest = item;
            newestPath = path;
            AppLog.Info($"拉取 #{item.Seq} {item.Name} ({item.Size} bytes) -> {path}");
        }

        if (newest != null && newestPath != null)
        {
            var bytes = await File.ReadAllBytesAsync(newestPath, ct);
            ClipboardWriter.Write(newestPath, bytes, newest.Mime);
            ShowBalloon($"已复制: {newest.Name}");
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
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
