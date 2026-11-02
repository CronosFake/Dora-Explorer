using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public static class LocalNetworkAddressService
{
    public static IReadOnlyList<LocalNetworkAddressOption> GetSortedIpv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses
                    .Where(ua =>
                        ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                    .Select(ua => new LocalNetworkAddressOption
                    {
                        IpAddress = ua.Address.ToString(),
                        InterfaceName = string.IsNullOrWhiteSpace(ni.Name) ? ni.Description : ni.Name
                    }))
                .DistinctBy(o => o.IpAddress, StringComparer.OrdinalIgnoreCase)
                .OrderBy(o => GetSortGroup(o.IpAddress))
                .ThenBy(o => ToSortableIpv4(o.IpAddress))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao enumerar endereços de rede locais", ex);
            return [];
        }
    }

    private static int GetSortGroup(string ipAddress)
    {
        if (!TryParseIpv4Bytes(ipAddress, out var bytes))
        {
            return 3;
        }

        // 10.0.0.0/8
        if (bytes[0] == 10)
        {
            return 0;
        }

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return 1;
        }

        return 2;
    }

    private static uint ToSortableIpv4(string ipAddress)
    {
        if (!TryParseIpv4Bytes(ipAddress, out var bytes))
        {
            return uint.MaxValue;
        }

        return ((uint)bytes[0] << 24)
               | ((uint)bytes[1] << 16)
               | ((uint)bytes[2] << 8)
               | bytes[3];
    }

    private static bool TryParseIpv4Bytes(string ipAddress, out byte[] bytes)
    {
        bytes = [];
        if (!IPAddress.TryParse(ipAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        bytes = address.GetAddressBytes();
        return bytes.Length == 4;
    }
}
