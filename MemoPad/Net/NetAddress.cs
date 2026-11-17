using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MemoPad.Net;

public static class NetAddress
{
    public static bool IsIPv4(string text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') == 3;

    /// <summary>이 PC 의 IPv4 주소들 (켜진 네트워크 카드, 게이트웨이가 있는 것 먼저)</summary>
    public static List<string> LocalAddresses()
    {
        var found = new List<(string Ip, bool Gateway)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var properties = adapter.GetIPProperties();
                bool gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)) continue;
                    string ip = unicast.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    found.Add((ip, gateway));
                }
            }
        }
        catch (NetworkInformationException) { }
        return found.OrderByDescending(f => f.Gateway).Select(f => f.Ip).Distinct().ToList();
    }

    /// <summary>상대에게 알릴 내 주소: 설정 값, 없으면 첫 주소</summary>
    public static string MyAddress(string configured)
    {
        if (IsIPv4(configured)) return configured;
        return LocalAddresses().FirstOrDefault() ?? "127.0.0.1";
    }
}
