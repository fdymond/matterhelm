using System.Globalization;
using System.Text.Json;

namespace MatterHelm;

/// <summary>Serializes one named built-in endpoint into the sidecar endpoint environment.</summary>
internal sealed record SidecarEndpointEntry(string Name, bool Enabled);

/// <summary>Serializes the power endpoint and its derived momentary behavior into the sidecar environment.</summary>
internal sealed record SidecarPowerEndpointEntry(string Name, bool Enabled, bool Momentary);

/// <summary>Serializes one enabled custom endpoint into the sidecar endpoint environment.</summary>
internal sealed record SidecarCustomEndpointEntry(string Key, string Name, bool ResetAfterActivation);

/// <summary>Defines the complete endpoint document passed to one sidecar session.</summary>
internal sealed record SidecarEndpointsEnv(
    SidecarEndpointEntry Speaker,
    SidecarEndpointEntry PlayPause,
    SidecarEndpointEntry Next,
    SidecarEndpointEntry Previous,
    SidecarPowerEndpointEntry Power,
    SidecarCustomEndpointEntry[] Custom);

/// <summary>Builds the non-core environment passed to one sidecar session.</summary>
internal static class SidecarEnvironment
{
    internal static Dictionary<string, string> BuildExtraEnv(BridgeConfig config)
    {
        CommandsConfig commands = config.Commands;
        var extra = new Dictionary<string, string>
        {
            ["HTPC_BRIDGE_ENDPOINTS"] = JsonSerializer.Serialize(
                new SidecarEndpointsEnv(
                    Speaker: new SidecarEndpointEntry(commands.Speaker.Name, commands.Speaker.Enabled),
                    PlayPause: new SidecarEndpointEntry(commands.PlayPause.Name, commands.PlayPause.Enabled),
                    Next: new SidecarEndpointEntry(commands.Next.Name, commands.Next.Enabled),
                    Previous: new SidecarEndpointEntry(commands.Previous.Name, commands.Previous.Enabled),
                    Power: new SidecarPowerEndpointEntry(
                        commands.Power.Name,
                        commands.Power.Enabled,
                        IsMomentaryPowerAction(config.PowerOffAction)),
                    Custom: [.. commands.Custom
                        .Where(c => c.Enabled)
                        .Select(c => new SidecarCustomEndpointEntry(c.Key, c.Name, c.ResetAfterActivation))]),
                SidecarEnvJsonContext.Default.SidecarEndpointsEnv),
            ["HTPC_BRIDGE_MOMENTARY_RESET_MS"] = config.MomentaryResetMs.ToString(CultureInfo.InvariantCulture),
            ["HTPC_BRIDGE_VENDOR_ID"] = config.VendorId.ToString(CultureInfo.InvariantCulture),
            ["HTPC_BRIDGE_PRODUCT_ID"] = config.ProductId.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(config.UniqueIdSeed))
        {
            extra["HTPC_BRIDGE_UNIQUE_ID_SEED"] = config.UniqueIdSeed;
        }

        if (!string.IsNullOrWhiteSpace(config.BridgeName))
        {
            extra["HTPC_BRIDGE_NAME"] = config.BridgeName;
        }

        if (!string.IsNullOrWhiteSpace(config.MdnsInterface))
        {
            extra["HTPC_BRIDGE_MDNS_INTERFACE"] = config.MdnsInterface;
        }

        return extra;
    }

    internal static bool IsMomentaryPowerAction(PowerOffAction action) => action switch
    {
        PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff or PowerOffAction.Screensaver => false,
        PowerOffAction.Sleep => true,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
