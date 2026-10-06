using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopUtility.Host.Core;

public static class NetworkUtil
{
    private static readonly string[] VirtualAdapterMarkers =
        ["vEthernet", "Hyper-V", "VirtualBox", "VMware", "WSL", "Loopback", "TAP-", "Npcap", "Bluetooth", "Docker", "VPN"];

    /// <summary>
    /// Picks the IPv4 address the iPad is most likely to reach: an adapter with a default gateway
    /// (so not a host-only VM/VPN network), Wi-Fi/Ethernet preferred, link-local addresses ignored.
    /// </summary>
    public static string GetLocalIpAddress()
    {
        try
        {
            var best = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up
                            && i.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && !VirtualAdapterMarkers.Any(m =>
                                i.Name.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                                i.Description.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(i =>
                {
                    var props = i.GetIPProperties();
                    bool hasGateway = props.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

                    return props.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a.Address))
                        .Select(a => (Address: a.Address, Score: (hasGateway ? 100 : 0)
                                                               + (i.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 10 : 0)
                                                               + (IsPrivate(a.Address) ? 1 : 0)));
                })
                .OrderByDescending(c => c.Score)
                .Select(c => c.Address)
                .FirstOrDefault();

            if (best != null) return best.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] Could not enumerate adapters: {ex.Message}");
        }

        return "127.0.0.1";
    }

    private static bool IsLinkLocal(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
