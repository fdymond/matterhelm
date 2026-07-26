using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Actions;

/// <summary>Master-volume state in the IPC protocol shape (percent 0–100 + muted).</summary>
public readonly record struct VolumeState(int VolumePercent, bool Muted);

/// <summary>
/// System master volume via hand-rolled CoreAudio COM interop (ADR-003: no NAudio):
/// get/set volume and mute on the default render endpoint, observe changes through
/// <see cref="IAudioEndpointVolumeCallback"/>, and re-acquire the endpoint when the
/// default device changes (<see cref="IMMNotificationClient"/>).
/// </summary>
/// <remarks>
/// <see cref="VolumeChanged"/> is raised on an audio-service thread, never the UI
/// thread — marshalling (e.g. via <see cref="SynchronizationContext"/>) is the
/// subscriber's job (wired in S2-5). Getters/setters throw
/// <see cref="InvalidOperationException"/> when no audio endpoint exists;
/// <see cref="ActionExecutor"/> converts that into a logged failure.
/// </remarks>
public sealed class SystemVolume : IDisposable
{
    private const uint ClsctxAll = 0x17;

    /// <summary>Guards endpoint acquire/release/use; the endpoint can be swapped by a device-change notification.</summary>
    private readonly object _gate = new();

    private readonly IMMDeviceEnumerator _deviceEnumerator;

    // ADR-003: strong references to both registered callbacks. Only the CCW is
    // handed to COM — if the GC collected these, notifications would go silent.
    private readonly EndpointVolumeCallback _volumeCallback;
    private readonly DefaultDeviceListener _deviceListener;

    private IAudioEndpointVolume? _endpointVolume;
    private bool _disposed;

    /// <summary>Binds to the current default render endpoint and starts observing changes.</summary>
    public SystemVolume()
    {
        _volumeCallback = new EndpointVolumeCallback(this);
        _deviceListener = new DefaultDeviceListener(this);
        _deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _deviceEnumerator.RegisterEndpointNotificationCallback(_deviceListener);
        AcquireEndpoint();
    }

    /// <summary>
    /// Raised for every master volume/mute change (including changes made through this
    /// class). Arrives on an audio-service thread; subscribers must marshal to the UI.
    /// </summary>
    public event EventHandler<VolumeState>? VolumeChanged;

    /// <summary>Reads the current volume percent and mute flag.</summary>
    public VolumeState GetState()
    {
        lock (_gate)
        {
            IAudioEndpointVolume endpoint = RequireEndpointLocked();
            endpoint.GetMasterVolumeLevelScalar(out float scalar);
            endpoint.GetMute(out bool muted);
            return new VolumeState(ToPercent(scalar), muted);
        }
    }

    /// <summary>Reads the current master volume as a 0–100 percent.</summary>
    public int GetVolumePercent() => GetState().VolumePercent;

    /// <summary>Reads the current mute flag.</summary>
    public bool GetMuted() => GetState().Muted;

    /// <summary>Sets the master volume; <paramref name="percent"/> is clamped to 0–100.</summary>
    public void SetVolumePercent(int percent)
    {
        float scalar = Math.Clamp(percent, 0, 100) / 100f;
        lock (_gate)
        {
            Guid eventContext = Guid.Empty;
            RequireEndpointLocked().SetMasterVolumeLevelScalar(scalar, ref eventContext);
        }
    }

    /// <summary>Sets the mute flag.</summary>
    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            Guid eventContext = Guid.Empty;
            RequireEndpointLocked().SetMute(muted, ref eventContext);
        }
    }

    /// <summary>Unregisters both callbacks and releases all COM objects.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceListener);
            }
            catch (COMException ex)
            {
                Log.Warn($"UnregisterEndpointNotificationCallback failed: 0x{ex.HResult:X8}");
            }

            ReleaseEndpointLocked();
            Marshal.ReleaseComObject(_deviceEnumerator);
        }
    }

    private static int ToPercent(float scalar) => Math.Clamp((int)MathF.Round(scalar * 100f), 0, 100);

    /// <summary>Releases any current endpoint and binds to the present default render device, if one exists.</summary>
    private void AcquireEndpoint()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReleaseEndpointLocked();
            try
            {
                _deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device);
                try
                {
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    device.Activate(ref iid, ClsctxAll, 0, out object activated);
                    _endpointVolume = (IAudioEndpointVolume)activated;
                    _endpointVolume.RegisterControlChangeNotify(_volumeCallback);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            catch (COMException ex)
            {
                // E.g. E_NOTFOUND: no render device at all. Degrade; the
                // IMMNotificationClient will trigger a re-acquire when one appears.
                Log.Warn($"No default audio endpoint (0x{ex.HResult:X8}); volume actions unavailable until a device change.");
            }
        }
    }

    /// <summary>
    /// Default render device changed: rebind on the thread pool. Releasing WASAPI
    /// objects inside the notification callback itself risks deadlocking the
    /// audio service (MMDevice API documentation).
    /// </summary>
    private void ScheduleEndpointReacquire()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                AcquireEndpoint();
                // The new device carries its own volume/mute; let observers resync.
                VolumeChanged?.Invoke(this, GetState());
            }
            catch (Exception ex)
            {
                Log.Error($"Re-acquiring audio endpoint failed: {ex.Message}");
            }
        });
    }

    private void RaiseVolumeChanged(VolumeState state) => VolumeChanged?.Invoke(this, state);

    private IAudioEndpointVolume RequireEndpointLocked() =>
        _endpointVolume ?? throw new InvalidOperationException("No audio endpoint available.");

    private void ReleaseEndpointLocked()
    {
        if (_endpointVolume is null)
        {
            return;
        }

        try
        {
            _endpointVolume.UnregisterControlChangeNotify(_volumeCallback);
        }
        catch (COMException ex)
        {
            // Endpoint may already be invalidated (device removed); still release the RCW.
            Log.Warn($"UnregisterControlChangeNotify failed: 0x{ex.HResult:X8}");
        }
        finally
        {
            Marshal.ReleaseComObject(_endpointVolume);
            _endpointVolume = null;
        }
    }

    // ---- CoreAudio COM interop -------------------------------------------------
    // Declared here and only here (ADR-003 boundary discipline). Methods must stay
    // in native vtable order; unused slots are declared but never called.

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    /// <summary>Native PROPERTYKEY (needed only to complete the IMMNotificationClient vtable).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>Fixed head of AUDIO_VOLUME_NOTIFICATION_DATA (trailing per-channel floats not read).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted; // Win32 BOOL
        public float MasterVolume; // scalar 0..1
        public uint ChannelCount;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);

        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        void RegisterEndpointNotificationCallback(IMMNotificationClient client);

        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);

        void OpenPropertyStore(uint access, out nint properties);

        void GetId(out nint id);

        void GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        void GetChannelCount(out uint channelCount);

        void SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        void GetMasterVolumeLevel(out float levelDb);

        void GetMasterVolumeLevelScalar(out float level);

        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        void GetChannelVolumeLevel(uint channel, out float levelDb);

        void GetChannelVolumeLevelScalar(uint channel, out float level);

        // BOOL (4-byte) — without MarshalAs, COM interop would default to VARIANT_BOOL.
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);

        void GetVolumeStepInfo(out uint step, out uint stepCount);

        void VolumeStepUp(ref Guid eventContext);

        void VolumeStepDown(ref Guid eventContext);

        void QueryHardwareSupport(out uint hardwareSupportMask);

        void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        void OnNotify(nint notificationData);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        // deviceId stays a raw pointer: it is legitimately null when the last device disappears.
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, nint defaultDeviceId);

        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    /// <summary>Receives master volume/mute notifications from the audio service.</summary>
    private sealed class EndpointVolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly SystemVolume _owner;

        public EndpointVolumeCallback(SystemVolume owner) => _owner = owner;

        public void OnNotify(nint notificationData)
        {
            var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notificationData);
            _owner.RaiseVolumeChanged(new VolumeState(ToPercent(data.MasterVolume), data.Muted != 0));
        }
    }

    /// <summary>Watches for default-render-device changes and triggers endpoint re-acquisition.</summary>
    private sealed class DefaultDeviceListener : IMMNotificationClient
    {
        private readonly SystemVolume _owner;

        public DefaultDeviceListener(SystemVolume owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, uint newState)
        {
        }

        public void OnDeviceAdded(string deviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, nint defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia)
            {
                _owner.ScheduleEndpointReacquire();
            }
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
        }
    }
}
