using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Snap2Board;

internal static class Discovery
{
    // 上次可用地址优先，其次本机回环，最后枚举所有网卡的 IPv4 默认网关
    public static IEnumerable<string> Candidates(string? savedHost)
    {
        var seen = new HashSet<string>();
        if (!string.IsNullOrWhiteSpace(savedHost) && seen.Add(savedHost))
        {
            yield return savedHost;
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
}
