using System.Text.Json;

namespace MatterHelm;

/// <summary>
/// Determines whether a config change requires the Matter sidecar to restart.
/// The same policy drives lifecycle restarts and Settings restart notes; in
/// particular, power-off behavior changes because the sidecar receives its
/// momentary-power flag through the session environment.
/// </summary>
internal static class BridgeRestartPolicy
{
    internal static bool RequiresRestart(BridgeConfig before, BridgeConfig after)
    {
        if (before.IpcPort != after.IpcPort
            || before.MomentaryResetMs != after.MomentaryResetMs
            || before.PowerOffAction != after.PowerOffAction
            || before.LogLevel != after.LogLevel
            || before.BridgeName != after.BridgeName
            || before.MdnsInterface != after.MdnsInterface
            || before.VendorId != after.VendorId
            || before.ProductId != after.ProductId)
        {
            return true;
        }

        return BuiltinChanged(before.Commands.Speaker, after.Commands.Speaker)
            || BuiltinChanged(before.Commands.PlayPause, after.Commands.PlayPause)
            || BuiltinChanged(before.Commands.Next, after.Commands.Next)
            || BuiltinChanged(before.Commands.Previous, after.Commands.Previous)
            || BuiltinChanged(before.Commands.Power, after.Commands.Power)
            || CustomCommandsSnapshot(before.Commands.Custom) != CustomCommandsSnapshot(after.Commands.Custom);
    }

    private static bool BuiltinChanged(BuiltinCommandConfig before, BuiltinCommandConfig after) =>
        before.Name != after.Name || before.Enabled != after.Enabled;

    internal static string CustomCommandsSnapshot(List<CustomCommandConfig> commands)
    {
        var wrapper = new BridgeConfig();
        wrapper.Commands.Custom = commands;
        return JsonSerializer.Serialize(wrapper, ConfigJsonContext.Default.BridgeConfig);
    }
}
