using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>VESA MCCS power modes accepted by VCP code <c>0xD6</c>.</summary>
internal enum DdcPowerMode : uint
{
    On = 0x01,
    Off = 0x04,
}

/// <summary>Stable-enough identity for one physical monitor during an off/on cycle.</summary>
internal readonly record struct DdcMonitor(string Id, string Name);

/// <summary>Outcome of setting VCP power mode on one physical monitor.</summary>
internal readonly record struct DdcMonitorPowerResult(DdcMonitor Monitor, bool Success, string? Error = null);

/// <summary>Injected boundary around physical-monitor enumeration and VCP writes.</summary>
internal interface IDdcDisplayPower
{
    /// <summary>
    /// Sets <paramref name="mode"/> on all physical monitors, or only on
    /// <paramref name="targets"/> when restoring a previous off transition.
    /// </summary>
    IReadOnlyList<DdcMonitorPowerResult> SetPower(
        DdcPowerMode mode,
        IReadOnlyCollection<DdcMonitor>? targets = null);
}

/// <summary>
/// Hardware-level display power through Windows' DDC/CI monitor APIs. Physical
/// handles are enumerated fresh for each transition and always closed before
/// returning. VCP <c>0xD6=0x04</c> is DPMS off; unlike <c>0x05</c> (power-button
/// equivalent), MCCS requires it to remain recoverable through power management.
/// </summary>
internal sealed partial class DdcDisplayPower : IDdcDisplayPower
{
    private const byte PowerModeVcpCode = 0xD6;
    private const int PhysicalMonitorDescriptionSize = 128;

    public IReadOnlyList<DdcMonitorPowerResult> SetPower(
        DdcPowerMode mode,
        IReadOnlyCollection<DdcMonitor>? targets = null)
    {
        if (targets is { Count: 0 })
        {
            return [];
        }

        var results = new List<DdcMonitorPowerResult>();
        Dictionary<string, DdcMonitor>? remaining = targets?.ToDictionary(target => target.Id);
        int logicalIndex = 0;
        MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            ProcessLogicalMonitor(hMonitor, logicalIndex++, mode, remaining, results);
            return true;
        };

        if (!EnumDisplayMonitors(0, 0, callback, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            results.Add(Failure(
                new DdcMonitor("display-topology", "Windows display topology"),
                $"EnumDisplayMonitors failed ({error})."));
        }

        if (remaining is not null)
        {
            foreach (DdcMonitor monitor in remaining.Values)
            {
                results.Add(Failure(monitor, "Monitor was not found during restore."));
            }
        }

        return results;
    }

    private static void ProcessLogicalMonitor(
        nint hMonitor,
        int logicalIndex,
        DdcPowerMode mode,
        Dictionary<string, DdcMonitor>? remaining,
        List<DdcMonitorPowerResult> results)
    {
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count))
        {
            int error = Marshal.GetLastPInvokeError();
            results.Add(Failure(
                new DdcMonitor($"logical:{hMonitor:X}", $"Display {logicalIndex + 1}"),
                $"Physical-monitor count failed ({error})."));
            return;
        }

        if (count == 0)
        {
            return;
        }

        var physicalMonitors = new PhysicalMonitor[checked((int)count)];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
        {
            int error = Marshal.GetLastPInvokeError();
            results.Add(Failure(
                new DdcMonitor($"logical:{hMonitor:X}", $"Display {logicalIndex + 1}"),
                $"Physical-monitor enumeration failed ({error})."));
            return;
        }

        try
        {
            for (int physicalIndex = 0; physicalIndex < physicalMonitors.Length; physicalIndex++)
            {
                ref PhysicalMonitor physical = ref physicalMonitors[physicalIndex];
                string id = $"logical:{hMonitor:X}:physical:{physicalIndex}";
                var monitor = new DdcMonitor(id, physical.GetDescription(logicalIndex, physicalIndex));
                if (remaining is not null && !remaining.Remove(id))
                {
                    continue;
                }

                if (SetVCPFeature(physical.Handle, PowerModeVcpCode, (uint)mode))
                {
                    results.Add(new DdcMonitorPowerResult(monitor, Success: true));
                }
                else
                {
                    int error = Marshal.GetLastPInvokeError();
                    results.Add(Failure(monitor, $"SetVCPFeature failed ({error})."));
                }
            }
        }
        finally
        {
            if (!DestroyPhysicalMonitors(count, physicalMonitors))
            {
                Log.Warn($"Display power: failed to close {count} physical-monitor handle(s) ({Marshal.GetLastPInvokeError()}).");
            }
        }
    }

    private static DdcMonitorPowerResult Failure(DdcMonitor monitor, string error) =>
        new(monitor, Success: false, error);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint monitorRect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PhysicalMonitor
    {
        internal nint Handle;
        private fixed char _description[PhysicalMonitorDescriptionSize];

        internal string GetDescription(int logicalIndex, int physicalIndex)
        {
            fixed (char* description = _description)
            {
                string value = new(description);
                return string.IsNullOrWhiteSpace(value)
                    ? $"Display {logicalIndex + 1}, monitor {physicalIndex + 1}"
                    : value;
            }
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(
        nint hdc,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint count);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicalMonitorsFromHMONITOR(
        nint hMonitor,
        uint physicalMonitorArraySize,
        [Out] PhysicalMonitor[] physicalMonitorArray);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyPhysicalMonitors(
        uint physicalMonitorArraySize,
        [In] PhysicalMonitor[] physicalMonitorArray);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetVCPFeature(nint hMonitor, byte vcpCode, uint newValue);
}
