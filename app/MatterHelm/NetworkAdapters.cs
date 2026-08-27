using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MatterHelm;

internal sealed record NetworkAdapterInfo(string Name, string? Ipv4Address);

internal interface INetworkAdapterProvider
{
    IReadOnlyList<NetworkAdapterInfo> GetAdapters();
}

internal sealed class SystemNetworkAdapterProvider : INetworkAdapterProvider
{
    internal static SystemNetworkAdapterProvider Instance { get; } = new();

    private SystemNetworkAdapterProvider()
    {
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => IsEligible(adapter.OperationalStatus, adapter.NetworkInterfaceType))
                .Select(adapter => new NetworkAdapterInfo(adapter.Name, GetIpv4Address(adapter)))
                .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    internal static bool IsEligible(OperationalStatus status, NetworkInterfaceType type) =>
        status == OperationalStatus.Up && type != NetworkInterfaceType.Loopback;

    internal static string? FirstIpv4Address(IEnumerable<IPAddress> addresses) =>
        addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString();

    private static string? GetIpv4Address(NetworkInterface adapter)
    {
        try
        {
            return FirstIpv4Address(adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
