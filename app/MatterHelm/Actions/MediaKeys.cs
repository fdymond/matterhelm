using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>The focused-window media verb carried by <c>WM_APPCOMMAND</c>.</summary>
internal enum ForegroundMediaCommand
{
    PlayPause = 14,
    Play = 46,
    Pause = 47,
}

/// <summary>Whether the focused window received and claimed an appcommand.</summary>
internal enum ForegroundCommandDelivery
{
    Failed,
    DeliveredUnhandled,
    DeliveredHandled,
}

/// <summary>The process/app identity that owns a captured foreground window.</summary>
internal sealed record MediaAppIdentity(uint ProcessId, string ExecutableName, string? AppUserModelId)
{
    internal string LogLabel => string.IsNullOrEmpty(ExecutableName)
        ? $"pid:{ProcessId}"
        : $"{ExecutableName}(pid:{ProcessId})";

    internal bool OwnsSession(string sourceAppUserModelId)
    {
        if (AppUserModelId is not null
            && string.Equals(AppUserModelId, sourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ExecutableName.Length == 0)
        {
            return false;
        }

        string sessionName = Path.GetFileName(sourceAppUserModelId);
        if (string.Equals(ExecutableName, sessionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(ExecutableName),
            Path.GetFileNameWithoutExtension(sessionName),
            StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One foreground HWND and the process/app identity resolved from it.</summary>
internal readonly record struct ForegroundMediaTarget(nint WindowHandle, MediaAppIdentity Identity);

/// <summary>Testable focused-window identity and appcommand boundary.</summary>
internal interface IForegroundMediaCommandSender
{
    ForegroundMediaTarget? GetTarget();

    ForegroundCommandDelivery Send(ForegroundMediaTarget target, ForegroundMediaCommand command);
}

/// <summary>Testable bounded verification delay.</summary>
internal interface IMediaVerificationDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Suppresses only immediate identical dedicated verbs already delivered to one unverifiable target.</summary>
internal interface IUnverifiableMediaRepeatGuard
{
    bool ShouldSuppress(ForegroundMediaCommand command, MediaAppIdentity target, TimeSpan window);

    void Record(ForegroundMediaCommand command, MediaAppIdentity target);

    void Reset();
}

/// <summary>Focused-first media routing with ownership-aware current-session verification.</summary>
public static partial class MediaKeys
{
    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;

    private static readonly TimeSpan MediaSessionTimeout = TimeSpan.FromSeconds(2);

    // One route must finish before IpcServer's five-second worker-drain budget.
    // Three seconds leaves shutdown headroom while still allowing two short
    // observations and one absolute session operation.
    private static readonly TimeSpan RouteDeadline = TimeSpan.FromSeconds(3);

    // Two seconds covers immediate Matter/routine duplicates and retained edge
    // pairs. A later deliberate command remains deliverable. Suppression is
    // limited to same-process dedicated verbs that were already unverifiable.
    private static readonly TimeSpan UnverifiableRepeatWindow = TimeSpan.FromSeconds(2);

    // 400 ms gives focus-driven players time to publish their asynchronous
    // playback-state change while keeping the observation interval below half
    // a second. Both waits share RouteDeadline.
    private static readonly TimeSpan VerificationDelay = TimeSpan.FromMilliseconds(400);
    private static readonly IMediaSessionController SessionController = new WindowsMediaSessionController();
    private static readonly IForegroundMediaCommandSender ForegroundSender = new WindowsForegroundMediaCommandSender();
    private static readonly IMediaVerificationDelay Delay = new TaskMediaVerificationDelay();
    private static readonly UnverifiableMediaRepeatGuard RepeatGuard = new();

    /// <summary>Routes play/pause to the focused app, then an ownership-pinned absolute session fallback.</summary>
    public static bool PlayPause() => RouteDefault(ForegroundMediaCommand.PlayPause);

    /// <summary>Sends the next-track media key. Returns false if injection failed.</summary>
    public static bool NextTrack()
    {
        RepeatGuard.Reset();
        return SendKey(VkMediaNextTrack, "next track");
    }

    /// <summary>Sends the previous-track media key. Returns false if injection failed.</summary>
    public static bool PreviousTrack()
    {
        RepeatGuard.Reset();
        return SendKey(VkMediaPrevTrack, "previous track");
    }

    /// <summary>Sends the media-stop key. Returns false if injection failed.</summary>
    public static bool Stop()
    {
        RepeatGuard.Reset();
        return SendKey(VkMediaStop, "stop");
    }

    /// <summary>Routes absolute play to the focused app, then an ownership-pinned session fallback.</summary>
    public static bool Play() => RouteDefault(ForegroundMediaCommand.Play);

    /// <summary>Routes absolute pause to the focused app, then an ownership-pinned session fallback.</summary>
    public static bool Pause() => RouteDefault(ForegroundMediaCommand.Pause);

    private static bool RouteDefault(ForegroundMediaCommand command) => Route(
        command,
        SessionController,
        ForegroundSender,
        Delay,
        RepeatGuard,
        MediaSessionTimeout,
        VerificationDelay,
        RouteDeadline,
        UnverifiableRepeatWindow,
        WriteLog);

    internal static bool Route(
        ForegroundMediaCommand command,
        IMediaSessionController controller,
        IForegroundMediaCommandSender foreground,
        IMediaVerificationDelay delay,
        IUnverifiableMediaRepeatGuard repeatGuard,
        TimeSpan sessionTimeout,
        TimeSpan verificationDelay,
        TimeSpan routeDeadline,
        TimeSpan repeatWindow,
        Action<string, string> log)
    {
        string verb = Verb(command);
        using var deadline = new CancellationTokenSource(routeDeadline);
        CancellationToken cancellationToken = deadline.Token;
        ForegroundMediaTarget? target = foreground.GetTarget();

        bool initialRead = TryReadSession(
            controller,
            sessionTimeout,
            cancellationToken,
            out MediaSessionSnapshot initial,
            out string? readError);
        bool initialOwned = initialRead && IsOwnedBy(initial, target);
        LogOwnership(log, verb, "initial", target, initialRead ? initial : null, initialOwned, readError);

        MediaPlaybackState? focusedDesired = initialOwned
            ? DesiredState(command, initial.State)
            : AbsoluteDesiredState(command);
        if (initialOwned
            && command != ForegroundMediaCommand.PlayPause
            && initial.State == focusedDesired)
        {
            repeatGuard.Reset();
            log("DEBUG", $"media routing '{verb}': path=already-in-state; matching focused-app session required no delivery.");
            return true;
        }

        bool initiallyUnverifiable = !initialOwned;
        if (target is ForegroundMediaTarget repeatTarget
            && initiallyUnverifiable
            && IsDedicated(command)
            && repeatGuard.ShouldSuppress(command, repeatTarget.Identity, repeatWindow))
        {
            log(
                "WARN",
                $"media routing '{verb}': path=unverifiable-repeat-suppressed; same target {repeatTarget.Identity.LogLabel} received this dedicated verb within {repeatWindow.TotalSeconds:0.#} s; no second appcommand sent.");
            return true;
        }

        ForegroundCommandDelivery focusedDelivery = target is ForegroundMediaTarget focusedTarget
            ? foreground.Send(focusedTarget, command)
            : ForegroundCommandDelivery.Failed;
        bool focusedSent = focusedDelivery != ForegroundCommandDelivery.Failed;
        if (focusedSent)
        {
            if (!Wait(delay, verificationDelay, cancellationToken))
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("WARN", $"media routing '{verb}': path=unverifiable; focused appcommand delivered but the {routeDeadline.TotalSeconds:0.#} s route deadline expired before verification.");
                return true;
            }

            if (!TryReadSession(
                    controller,
                    sessionTimeout,
                    cancellationToken,
                    out MediaSessionSnapshot afterFocused,
                    out readError))
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                LogOwnership(log, verb, "after-focused", target, null, owned: false, readError);
                log("DEBUG", $"media routing '{verb}': path=unverifiable; focused appcommand delivered; session verification unavailable ({readError}); no fallback sent.");
                return true;
            }

            bool afterFocusedOwned = IsOwnedBy(afterFocused, target);
            LogOwnership(log, verb, "after-focused", target, afterFocused, afterFocusedOwned, null);
            if (!afterFocusedOwned)
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("DEBUG", $"media routing '{verb}': path=unverifiable; focused appcommand delivered; current session belongs to a different or unresolved app, so no fallback was allowed.");
                return true;
            }

            if (focusedDesired is MediaPlaybackState expected && afterFocused.State == expected)
            {
                repeatGuard.Reset();
                log("DEBUG", $"media routing '{verb}': path=focused-handled; matching app ownership and {StateName(expected)} state verified.");
                return true;
            }

            if (focusedDesired is null)
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("DEBUG", $"media routing '{verb}': path=unverifiable; play/pause lacked a matching initial session state, so its focused toggle outcome cannot be proven; no fallback sent.");
                return true;
            }

            if (focusedDelivery == ForegroundCommandDelivery.DeliveredHandled)
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("DEBUG", $"media routing '{verb}': path=unverifiable; focused window reported handled but its matching session did not update; no fallback sent to avoid a repeated side effect.");
                return true;
            }

            return ExecuteAndVerifyFallback(
                verb,
                focusedDesired.Value,
                afterFocused,
                controller,
                delay,
                sessionTimeout,
                verificationDelay,
                log,
                cancellationToken);
        }

        repeatGuard.Reset();
        if (!initialRead || !initial.HasSession)
        {
            string detail = initialRead ? "no media session exists" : $"session read failed ({readError})";
            log("WARN", $"media routing '{verb}': path=unverifiable failed; focused delivery failed and {detail}, so there is no fallback target.");
            return false;
        }

        MediaPlaybackState? fallbackDesired = DesiredState(command, initial.State);
        if (fallbackDesired is null)
        {
            log("WARN", $"media routing '{verb}': path=session-fallback failed; no playback state exists from which to derive the toggle intent.");
            return false;
        }

        log("DEBUG", $"media routing '{verb}': focused delivery failed; using captured session owner '{initial.SourceAppUserModelId}' as the fallback target.");
        return ExecuteAndVerifyFallback(
            verb,
            fallbackDesired.Value,
            initial,
            controller,
            delay,
            sessionTimeout,
            verificationDelay,
            log,
            cancellationToken);
    }

    private static bool ExecuteAndVerifyFallback(
        string verb,
        MediaPlaybackState desired,
        MediaSessionSnapshot target,
        IMediaSessionController controller,
        IMediaVerificationDelay delay,
        TimeSpan sessionTimeout,
        TimeSpan verificationDelay,
        Action<string, string> log,
        CancellationToken cancellationToken)
    {
        string source = target.SourceAppUserModelId!;
        MediaSessionActionResult fallbackResult = ExecuteFallback(
            desired,
            source,
            controller,
            sessionTimeout,
            cancellationToken);
        if (!Wait(delay, verificationDelay, cancellationToken))
        {
            log("WARN", $"media routing '{verb}': path=session-fallback failed; route deadline expired after session verb returned {fallbackResult}.");
            return false;
        }

        if (TryReadSession(
                controller,
                sessionTimeout,
                cancellationToken,
                out MediaSessionSnapshot afterFallback,
                out string? readError)
            && string.Equals(
                source,
                afterFallback.SourceAppUserModelId,
                StringComparison.OrdinalIgnoreCase)
            && afterFallback.State == desired)
        {
            log("DEBUG", $"media routing '{verb}': path=session-fallback; owner '{source}' remained stable and absolute {StateName(desired)} was verified.");
            return true;
        }

        string detail = readError is null
            ? $"owner/state became '{afterFallback.SourceAppUserModelId ?? "no-session"}'/{StateName(afterFallback.State)}"
            : $"verification failed ({readError})";
        log("WARN", $"media routing '{verb}': path=session-fallback failed; session verb returned {fallbackResult}; {detail}.");
        return false;
    }

    private static MediaPlaybackState? AbsoluteDesiredState(ForegroundMediaCommand command) => command switch
    {
        ForegroundMediaCommand.Play => MediaPlaybackState.Playing,
        ForegroundMediaCommand.Pause => MediaPlaybackState.Paused,
        ForegroundMediaCommand.PlayPause => null,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private static MediaPlaybackState? DesiredState(ForegroundMediaCommand command, MediaPlaybackState initial) => command switch
    {
        ForegroundMediaCommand.Play => MediaPlaybackState.Playing,
        ForegroundMediaCommand.Pause => MediaPlaybackState.Paused,
        ForegroundMediaCommand.PlayPause when initial == MediaPlaybackState.NoCurrentSession => null,
        ForegroundMediaCommand.PlayPause when initial == MediaPlaybackState.Playing => MediaPlaybackState.Paused,
        ForegroundMediaCommand.PlayPause => MediaPlaybackState.Playing,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private static MediaSessionActionResult ExecuteFallback(
        MediaPlaybackState desired,
        string expectedSourceAppUserModelId,
        IMediaSessionController controller,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            Task<MediaSessionActionResult> task = desired switch
            {
                MediaPlaybackState.Playing => controller.TryPlayAsync(expectedSourceAppUserModelId, timeout, cancellationToken),
                MediaPlaybackState.Paused => controller.TryPauseAsync(expectedSourceAppUserModelId, timeout, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(desired), desired, null),
            };
            return task.WaitAsync(timeout, cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return MediaSessionActionResult.Rejected;
        }
        catch
        {
            return MediaSessionActionResult.Rejected;
        }
    }

    private static bool TryReadSession(
        IMediaSessionController controller,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        out MediaSessionSnapshot snapshot,
        out string? error)
    {
        try
        {
            snapshot = controller.GetCurrentSessionAsync(timeout, cancellationToken)
                .WaitAsync(timeout, cancellationToken)
                .GetAwaiter()
                .GetResult();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            snapshot = MediaSessionSnapshot.NoSession;
            error = cancellationToken.IsCancellationRequested
                ? "overall route deadline expired"
                : $"timed out after {timeout.TotalSeconds:0.#} s";
            return false;
        }
        catch (Exception ex)
        {
            snapshot = MediaSessionSnapshot.NoSession;
            error = ex.Message;
            return false;
        }
    }

    private static bool Wait(
        IMediaVerificationDelay delay,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            delay.WaitAsync(duration, cancellationToken)
                .WaitAsync(duration + TimeSpan.FromSeconds(1), cancellationToken)
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsOwnedBy(MediaSessionSnapshot snapshot, ForegroundMediaTarget? target) =>
        snapshot.SourceAppUserModelId is string source
        && target is ForegroundMediaTarget focused
        && focused.Identity.OwnsSession(source);

    private static void LogOwnership(
        Action<string, string> log,
        string verb,
        string stage,
        ForegroundMediaTarget? target,
        MediaSessionSnapshot? snapshot,
        bool owned,
        string? readError)
    {
        string foregroundLabel = target?.Identity.LogLabel ?? "none";
        string sessionLabel = snapshot?.SourceAppUserModelId ?? "none";
        string decision = readError is not null
            ? "unresolved-read"
            : !snapshot!.HasSession
                ? "no-session"
                : target is null
                    ? "no-foreground"
                    : owned ? "same-app" : "different-or-unresolved-app";
        log(
            "DEBUG",
            $"media ownership '{verb}': stage={stage}; foreground={foregroundLabel}; session={sessionLabel}; decision={decision}.");
    }

    private static void RecordUnverifiable(
        ForegroundMediaCommand command,
        ForegroundMediaTarget? target,
        IUnverifiableMediaRepeatGuard repeatGuard,
        Action<string, string> log,
        string verb)
    {
        if (IsDedicated(command) && target is ForegroundMediaTarget focused)
        {
            repeatGuard.Record(command, focused.Identity);
            if (command == ForegroundMediaCommand.Pause)
            {
                log(
                    "WARN",
                    $"media routing '{verb}': delivered to an unverifiable target; players such as Kodi may treat Pause as a toggle. An immediate identical repeat is suppressed for two seconds, but a later repeat can resume playback.");
            }
        }
        else
        {
            repeatGuard.Reset();
        }
    }

    private static bool IsDedicated(ForegroundMediaCommand command) =>
        command is ForegroundMediaCommand.Play or ForegroundMediaCommand.Pause;

    private static string Verb(ForegroundMediaCommand command) => command switch
    {
        ForegroundMediaCommand.PlayPause => "playPause",
        ForegroundMediaCommand.Play => "play",
        ForegroundMediaCommand.Pause => "pause",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private static string StateName(MediaPlaybackState state) => state switch
    {
        MediaPlaybackState.NoCurrentSession => "no-session",
        MediaPlaybackState.Playing => "playing",
        MediaPlaybackState.Paused => "paused",
        MediaPlaybackState.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static void WriteLog(string level, string message)
    {
        if (level == "DEBUG")
        {
            Log.Debug(message);
        }
        else
        {
            Log.Warn(message);
        }
    }

    private static bool SendKey(ushort virtualKey, string name)
    {
        var inputs = new NativeInput.Input[2];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i].Type = NativeInput.InputKeyboard;
            inputs[i].Union.Keyboard.VirtualKey = virtualKey;
            inputs[i].Union.Keyboard.Flags = NativeInput.KeyEventFExtendedKey;
        }

        inputs[1].Union.Keyboard.Flags |= NativeInput.KeyEventFKeyUp;
        return NativeInput.Send(inputs, $"media key '{name}'");
    }

    private sealed class TaskMediaVerificationDelay : IMediaVerificationDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    internal sealed class UnverifiableMediaRepeatGuard(Func<long>? getTicks = null) : IUnverifiableMediaRepeatGuard
    {
        private readonly Lock _gate = new();
        private readonly Func<long> _getTicks = getTicks ?? (() => Environment.TickCount64);
        private ForegroundMediaCommand? _lastCommand;
        private MediaAppIdentity? _lastTarget;
        private long _lastTicks;

        public bool ShouldSuppress(ForegroundMediaCommand command, MediaAppIdentity target, TimeSpan window)
        {
            lock (_gate)
            {
                long now = _getTicks();
                bool suppress = _lastCommand == command
                    && _lastTarget == target
                    && now - _lastTicks >= 0
                    && now - _lastTicks <= window.TotalMilliseconds;
                if (!suppress)
                {
                    ClearCore();
                }

                return suppress;
            }
        }

        public void Record(ForegroundMediaCommand command, MediaAppIdentity target)
        {
            lock (_gate)
            {
                _lastCommand = command;
                _lastTarget = target;
                _lastTicks = _getTicks();
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                ClearCore();
            }
        }

        private void ClearCore()
        {
            _lastCommand = null;
            _lastTarget = null;
            _lastTicks = 0;
        }
    }

    private sealed partial class WindowsForegroundMediaCommandSender : IForegroundMediaCommandSender
    {
        private const uint WmAppCommand = 0x0319;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint SendTimeoutMs = 100;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;
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
                Log.Warn($"media appcommand '{Verb(command)}' to {target.Identity.LogLabel} failed or timed out (Win32 {Marshal.GetLastPInvokeError()}).");
                return ForegroundCommandDelivery.Failed;
            }

            return handlerResult == 0
                ? ForegroundCommandDelivery.DeliveredUnhandled
                : ForegroundCommandDelivery.DeliveredHandled;
        }

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
}
