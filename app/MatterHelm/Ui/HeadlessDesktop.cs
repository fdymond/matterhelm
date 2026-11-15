using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MatterHelm.Ui;

/// <summary>Owns the private Win32 desktop used by tests and acceptance demos.</summary>
internal static partial class HeadlessDesktop
{
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopCreateWindow = 0x0002;
    private const uint DesktopCreateMenu = 0x0004;
    private const uint DesktopEnumerate = 0x0040;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint RequiredAccess = DesktopReadObjects
        | DesktopCreateWindow
        | DesktopCreateMenu
        | DesktopEnumerate
        | DesktopWriteObjects;
    private const int UoiName = 2;
    private const string VisibleUiEnvironmentVariable = "MATTERHELM_VISIBLE_UI";
    private static readonly NativeThreadStart NativeThreadEntryPoint = RunNativeStaThread;

    private static readonly string DesktopName = $"MatterHelm-Headless-{Environment.ProcessId}";
    private static readonly Lazy<SafeDesktopHandle> Desktop = new(CreateDesktop);

    static HeadlessDesktop()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            if (Desktop.IsValueCreated)
            {
                // CloseDesktop can refuse a handle still assigned to the
                // exiting thread. SafeHandle still makes the attempt; Windows
                // releases the desktop object when the process terminates.
                Desktop.Value.Dispose();
            }
        };
    }

    /// <summary>
    /// Assigns the process-private desktop to the calling thread before it
    /// creates any USER objects. Returns true when the thread is headless.
    /// </summary>
    internal static bool AttachCurrentThread(bool visibleRequested = false)
    {
        if (IsVisibleUiRequested(visibleRequested))
        {
            return false;
        }

        SafeDesktopHandle desktop = Desktop.Value;
        if (!SetThreadDesktop(desktop.DangerousGetHandle()))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"SetThreadDesktop failed for '{DesktopName}' (Win32 error {error}).");
        }

        GC.KeepAlive(desktop);
        return true;
    }

    /// <summary>Runs one UI entry point on an STA thread, privately desktop-bound unless visibility was explicitly requested.</summary>
    internal static void RunOnStaThread(Action body, TimeSpan timeout, bool visibleRequested = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        var completion = new TaskCompletionSource<ExceptionDispatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = new ThreadInvocation(body, completion);

        if (IsVisibleUiRequested(visibleRequested))
        {
            var thread = new Thread(() => RunVisibleStaThread(invocation))
            {
                IsBackground = true,
                Name = "MatterHelm visible UI STA",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        else
        {
            StartNativeStaThread(invocation);
        }

        if (!completion.Task.Wait(timeout))
        {
            throw new TimeoutException($"STA work did not complete within {timeout.TotalSeconds:0.###} seconds.");
        }

        completion.Task.Result?.Throw();
    }

    /// <summary>Captures objective desktop/window membership evidence for the STA harness tests.</summary>
    internal static (
        string ThreadDesktopName,
        string InputDesktopName,
        bool WindowOnThreadDesktop,
        bool WindowOnInputDesktop) CaptureIsolationForTest(nint window)
    {
        nint threadDesktop = GetThreadDesktop(GetCurrentThreadId());
        if (threadDesktop == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"GetThreadDesktop failed (Win32 error {error}).");
        }

        nint inputDesktopHandle = OpenInputDesktop(
            dwFlags: 0,
            inherit: false,
            desiredAccess: DesktopReadObjects | DesktopEnumerate);
        if (inputDesktopHandle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"OpenInputDesktop failed (Win32 error {error}).");
        }

        using var inputDesktop = new SafeDesktopHandle(inputDesktopHandle);
        HashSet<nint> threadWindows = EnumerateDesktopWindows(threadDesktop, "headless");
        HashSet<nint> inputWindows = EnumerateDesktopWindows(inputDesktop.DangerousGetHandle(), "input");
        return (
            GetDesktopName(threadDesktop),
            GetDesktopName(inputDesktop.DangerousGetHandle()),
            threadWindows.Contains(window),
            inputWindows.Contains(window));
    }

    private static SafeDesktopHandle CreateDesktop()
    {
        SafeDesktopHandle? desktop = null;
        ExceptionDispatchInfo? failure = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                desktop = CreateDesktopOnOwnerThread();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "MatterHelm headless desktop owner",
        };
        ownerThread.Start();
        ownerThread.Join();
        failure?.Throw();
        return desktop
            ?? throw new InvalidOperationException("The headless desktop owner thread returned no handle.");
    }

    internal static bool IsVisibleUiRequested(bool visibleRequested) =>
        visibleRequested
        || string.Equals(
            Environment.GetEnvironmentVariable(VisibleUiEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    private static void StartNativeStaThread(ThreadInvocation invocation)
    {
        GCHandle state = GCHandle.Alloc(invocation);
        nint thread = CreateThread(
            threadAttributes: 0,
            stackSize: 0,
            NativeThreadEntryPoint,
            GCHandle.ToIntPtr(state),
            creationFlags: 0,
            out _);
        if (thread == 0)
        {
            state.Free();
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"CreateThread failed for the headless STA (Win32 error {error}).");
        }

        if (!CloseHandle(thread))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"CloseHandle failed for the headless STA (Win32 error {error}).");
        }
    }

    private static uint RunNativeStaThread(nint parameter)
    {
        try
        {
            // This must stay the first operation on the native UI thread:
            // even managed STA bootstrapping can install OLE hooks before the
            // test/demo body gets control.
            AttachCurrentThread();
        }
        catch (Exception ex)
        {
            CompleteNativeThreadFailure(parameter, ex);
            return 0;
        }

        GCHandle state = GCHandle.FromIntPtr(parameter);
        var invocation = (ThreadInvocation?)state.Target
            ?? throw new InvalidOperationException("The headless STA invocation state was unavailable.");
        bool oleInitialized = false;
        try
        {
            int result = OleInitialize(0);
            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"OleInitialize failed for the headless STA (HRESULT 0x{result:X8}).");
            }

            oleInitialized = true;
            invocation.Body();
            invocation.Completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            invocation.Completion.TrySetResult(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (oleInitialized)
            {
                OleUninitialize();
            }

            state.Free();
        }

        return 0;
    }

    private static void CompleteNativeThreadFailure(nint parameter, Exception failure)
    {
        GCHandle state = GCHandle.FromIntPtr(parameter);
        try
        {
            if (state.Target is ThreadInvocation invocation)
            {
                invocation.Completion.TrySetResult(ExceptionDispatchInfo.Capture(failure));
            }
        }
        finally
        {
            state.Free();
        }
    }

    private static void RunVisibleStaThread(ThreadInvocation invocation)
    {
        try
        {
            invocation.Body();
            invocation.Completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            invocation.Completion.TrySetResult(ExceptionDispatchInfo.Capture(ex));
        }
    }

    private static SafeDesktopHandle CreateDesktopOnOwnerThread()
    {
        nint handle = CreateDesktopW(
            DesktopName,
            device: 0,
            deviceMode: 0,
            flags: 0,
            desiredAccess: RequiredAccess,
            securityAttributes: 0);
        if (handle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"CreateDesktopW failed for '{DesktopName}' (Win32 error {error}).");
        }

        return new SafeDesktopHandle(handle);
    }

    private static HashSet<nint> EnumerateDesktopWindows(nint desktop, string description)
    {
        var windows = new HashSet<nint>();
        WindowEnumerationCallback callback = (window, _) =>
        {
            windows.Add(window);
            return true;
        };
        if (!EnumDesktopWindows(desktop, callback, 0))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"EnumDesktopWindows failed for the {description} desktop (Win32 error {error}).");
        }

        GC.KeepAlive(callback);
        return windows;
    }

    private static string GetDesktopName(nint desktop)
    {
        _ = GetUserObjectInformationW(desktop, UoiName, 0, 0, out uint byteCount);
        if (byteCount == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"GetUserObjectInformationW failed to size the desktop name (Win32 error {error}).");
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)byteCount));
        try
        {
            if (!GetUserObjectInformationW(desktop, UoiName, buffer, byteCount, out _))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"GetUserObjectInformationW failed to read the desktop name (Win32 error {error}).");
            }

            return Marshal.PtrToStringUni(buffer)
                ?? throw new InvalidOperationException("The desktop name was unexpectedly null.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WindowEnumerationCallback(nint window, nint parameter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint NativeThreadStart(nint parameter);

    private sealed record ThreadInvocation(
        Action Body,
        TaskCompletionSource<ExceptionDispatchInfo?> Completion);

    private sealed class SafeDesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeDesktopHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateDesktopW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateDesktopW(
        string desktop,
        nint device,
        nint deviceMode,
        uint flags,
        uint desiredAccess,
        nint securityAttributes);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadDesktop(nint desktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(
        uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetThreadDesktop(uint threadId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDesktopWindows(
        nint desktop,
        WindowEnumerationCallback callback,
        nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(
        nint desktop,
        int index,
        nint information,
        uint informationLength,
        out uint lengthNeeded);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateThread(
        nint threadAttributes,
        nuint stackSize,
        NativeThreadStart startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    private static partial void OleUninitialize();
}
