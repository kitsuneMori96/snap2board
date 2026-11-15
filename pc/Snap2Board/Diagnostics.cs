using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Snap2Board;

internal static class Diagnostics
{
    public static void Run()
    {
        var config = Config.Load();
        var sb = new StringBuilder();
        sb.AppendLine("Snap2Board 诊断报告");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"机器: {Environment.MachineName}  用户: {Environment.UserName}");
        sb.AppendLine($"系统: {Environment.OSVersion}");
        sb.AppendLine($"配置: host={config.Host} port={config.Port} token={(string.IsNullOrEmpty(config.Token) ? "无（还没配对）" : "有")} cursor={config.Cursor}");
        sb.AppendLine();
        sb.AppendLine("网卡与网关:");
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = network.GetIPProperties();
            var ips = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            if (ips.Count == 0) continue;
            var gateways = string.Join(",", props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(g => g.Address.ToString()));
            sb.AppendLine($"  {network.Name} [{network.NetworkInterfaceType}] {network.OperationalStatus} ip={string.Join(",", ips)} gw={gateways}");
        }
        sb.AppendLine();
        sb.AppendLine("探测候选地址:");
        var client = new HubClient();
        foreach (var candidate in Discovery.Candidates(config.Host))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var (health, error) = client.ProbeAsync(candidate, config.Port, cts.Token).GetAwaiter().GetResult();
            sb.AppendLine(health != null
                ? $"  {candidate}:{config.Port} -> 成功，device={health.Device}"
                : $"  {candidate}:{config.Port} -> 失败：{error}");
        }
        sb.AppendLine();
        sb.AppendLine("说明：如果所有候选都失败，先确认这台电脑连的是手机开的热点；");
        sb.AppendLine("仍失败可手动指定地址：Snap2Board.exe --host 192.168.x.x --pair 123456");

        var path = Path.Combine(AppLog.Root, "diag.txt");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        AppLog.Info($"诊断已写入 {path}");
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
