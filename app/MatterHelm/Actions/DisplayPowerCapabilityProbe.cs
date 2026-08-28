namespace MatterHelm.Actions;

/// <summary>How completely the current display topology supports hardware-level DDC/CI power.</summary>
internal enum DisplayPowerCapability
{
    /// <summary>Every detected physical display exposes the MCCS power-mode control.</summary>
    AllDdc,

    /// <summary>No detected physical display exposes the MCCS power-mode control.</summary>
    NoDdc,

    /// <summary>Some, but not all, detected physical displays expose the MCCS power-mode control.</summary>
    Mixed,
}

/// <summary>Read-only display-power capability boundary used when opening Settings.</summary>
internal interface IDisplayPowerCapabilityProbe
{
    /// <summary>Classifies the current physical-display topology without changing display state.</summary>
    DisplayPowerCapability Probe();
}

/// <summary>Production DDC/CI capability probe over the display-power enumeration seam.</summary>
internal sealed class DisplayPowerCapabilityProbe : IDisplayPowerCapabilityProbe
{
    internal static DisplayPowerCapabilityProbe Instance { get; } = new(new DdcDisplayPower());

    private readonly IDdcDisplayPower _ddcDisplayPower;

    internal DisplayPowerCapabilityProbe(IDdcDisplayPower ddcDisplayPower) =>
        _ddcDisplayPower = ddcDisplayPower;

    public DisplayPowerCapability Probe()
    {
        IReadOnlyList<DdcMonitorPowerResult> results = _ddcDisplayPower.ProbePowerSupport();
        int supported = results.Count(result => result.Success);
        return supported switch
        {
            0 => DisplayPowerCapability.NoDdc,
            _ when supported == results.Count => DisplayPowerCapability.AllDdc,
            _ => DisplayPowerCapability.Mixed,
        };
    }
}
