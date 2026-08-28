using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MatterHelm;

internal sealed record NetworkAdapterInfo(
    string Name,
    string? Ipv4Address,
    string Description = "",
    OperationalStatus OperationalStatus = OperationalStatus.Up,
    NetworkInterfaceType InterfaceType = NetworkInterfaceType.Ethernet,
    bool HasIpUnicastAddress = true,
    bool HasIpv4DefaultGateway = false);

internal interface INetworkAdapterProvider
{
    IReadOnlyList<NetworkAdapterInfo> GetAdapters();
}

internal sealed class SystemNetworkAdapterProvider : INetworkAdapterProvider
{
    // These description markers identify common host-only, VPN, capture, and
    // virtual-switch adapters; a real IPv4 default gateway overrides the hint.
    private static readonly string[] VirtualAdapterDescriptionKeywords =
    [
        "Hyper-V",
        "vEthernet",
        "WSL",
        "VMware",
        "VirtualBox",
        "TAP",
        "Npcap",
        "loopback",
        "Bluetooth Device (Personal Area Network)",
    ];

    internal static SystemNetworkAdapterProvider Instance { get; } = new();

    private SystemNetworkAdapterProvider()
    {
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Select(CreateSnapshot)
                .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    internal static bool IsVisibleByDefault(NetworkAdapterInfo adapter) =>
        IsUpIpLanAdapter(adapter)
        && (adapter.HasIpv4DefaultGateway || !DescriptionLooksVirtual(adapter.Description));

    internal static bool IsVisibleWhenShowingAll(NetworkAdapterInfo adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up
        && adapter.InterfaceType != NetworkInterfaceType.Loopback;

    internal static string? FirstIpv4Address(IEnumerable<IPAddress> addresses) =>
        addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString();

    private static bool IsUpIpLanAdapter(NetworkAdapterInfo adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up
        && IsEthernetOrWireless(adapter.InterfaceType)
        && adapter.HasIpUnicastAddress;

    private static bool IsEthernetOrWireless(NetworkInterfaceType type) => type is
        NetworkInterfaceType.Ethernet
        or NetworkInterfaceType.Ethernet3Megabit
        or NetworkInterfaceType.FastEthernetT
        or NetworkInterfaceType.FastEthernetFx
        or NetworkInterfaceType.GigabitEthernet
        or NetworkInterfaceType.Wireless80211;

    private static bool DescriptionLooksVirtual(string description) =>
        VirtualAdapterDescriptionKeywords.Any(keyword =>
            description.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static NetworkAdapterInfo CreateSnapshot(NetworkInterface adapter)
    {
        try
        {
            IPInterfaceProperties properties = adapter.GetIPProperties();
            IPAddress[] unicastAddresses =
            [
                .. properties.UnicastAddresses
                    .Select(address => address.Address)
                    .Where(address => address.AddressFamily is
                        AddressFamily.InterNetwork or AddressFamily.InterNetworkV6),
            ];
            bool hasIpv4DefaultGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork
                && !gateway.Address.Equals(IPAddress.Any));
            return new NetworkAdapterInfo(
                adapter.Name,
                FirstIpv4Address(unicastAddresses),
                adapter.Description,
                adapter.OperationalStatus,
                adapter.NetworkInterfaceType,
                unicastAddresses.Length > 0,
                hasIpv4DefaultGateway);
        }
        catch (NetworkInformationException)
        {
            return new NetworkAdapterInfo(
                adapter.Name,
                null,
                adapter.Description,
                adapter.OperationalStatus,
                adapter.NetworkInterfaceType,
                HasIpUnicastAddress: false,
                HasIpv4DefaultGateway: false);
        }
    }
}
