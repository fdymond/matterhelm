using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>Resolves the foreground app and sends bounded Win32 media appcommands.</summary>
internal sealed partial class WindowsForegroundMediaCommandSender : IForegroundMediaCommandSender
{
    private const uint WmAppCommand = 0x0319;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SendTimeoutMs = 1000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorTimeout = 1460;
    private const int MaxPathChars = 32768;

    public ForegroundMediaTarget? GetTarget()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || GetWindowThreadProcessId(foreground, out uint processId) == 0)
        {
            return null;
        }

        MediaAppIdentity identity = ResolveIdentity(processId);
        return new ForegroundMediaTarget(foreground, identity);
    }

    public ForegroundCommandDelivery Send(ForegroundMediaTarget target, ForegroundMediaCommand command)
    {
        nint lParam = (nint)((int)command << 16);
        nint deliveryResult = SendMessageTimeoutW(
            target.WindowHandle,
            WmAppCommand,
            target.WindowHandle,
            lParam,
            SmtoAbortIfHung,
            SendTimeoutMs,
            out nint handlerResult);
        if (deliveryResult == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            Log.Warn($"media appcommand '{FocusedMediaRouter.Verb(command)}' to {target.Identity.LogLabel} failed or timed out (Win32 {error}).");
            return ClassifyAppCommandFailure(error);
        }

        return handlerResult == 0
            ? ForegroundCommandDelivery.DeliveredUnhandled
            : ForegroundCommandDelivery.DeliveredHandled;
    }

    /// <summary>Classifies a failed <c>SendMessageTimeout</c> call without invoking Win32.</summary>
    internal static ForegroundCommandDelivery ClassifyAppCommandFailure(int win32Error) =>
        win32Error == ErrorTimeout
            ? ForegroundCommandDelivery.TimedOut
            : ForegroundCommandDelivery.Failed;

    private static MediaAppIdentity ResolveIdentity(uint processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process == 0)
        {
            return new MediaAppIdentity(processId, "", null);
        }

        try
        {
            string executableName = QueryExecutableName(process);
            string? appUserModelId = QueryAppUserModelId(process);
            return new MediaAppIdentity(processId, executableName, appUserModelId);
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private static string QueryExecutableName(nint process)
    {
        nint buffer = Marshal.AllocHGlobal(MaxPathChars * sizeof(char));
        try
        {
            uint length = MaxPathChars;
            return QueryFullProcessImageNameW(process, 0, buffer, ref length)
                ? Path.GetFileName(Marshal.PtrToStringUni(buffer, checked((int)length)))
                : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? QueryAppUserModelId(nint process)
    {
        uint length = 0;
        if (GetApplicationUserModelId(process, ref length, 0) != ErrorInsufficientBuffer || length <= 1)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
        try
        {
            return GetApplicationUserModelId(process, ref length, buffer) == 0
                ? Marshal.PtrToStringUni(buffer, checked((int)length - 1))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(
        nint process,
        uint flags,
        nint executableName,
        ref uint size);

    [LibraryImport("kernel32.dll")]
    private static partial int GetApplicationUserModelId(
        nint process,
        ref uint applicationUserModelIdLength,
        nint applicationUserModelId);
}
