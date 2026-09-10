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
    TimedOut,
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

/// <summary>Production timing and suppression policy for one focused-first media route.</summary>
internal sealed record MediaRoutingPolicy(
    TimeSpan SessionTimeout,
    TimeSpan VerificationDelay,
    TimeSpan AppCommandRetryDelay,
    TimeSpan RouteDeadline,
    TimeSpan UnverifiableRepeatWindow);

/// <summary>Focused-first media routing with ownership-aware current-session verification.</summary>
internal static class FocusedMediaRouter
{
    internal static MediaRoutingPolicy ProductionRoutingPolicy { get; } = new(
        SessionTimeout: TimeSpan.FromSeconds(1),
        // WM_APPCOMMAND returns before SMTC necessarily publishes its new state.
        VerificationDelay: TimeSpan.FromMilliseconds(400),
        // Retry only a timed-out window, after a short drain interval that limits duplicate delivery risk.
        AppCommandRetryDelay: TimeSpan.FromMilliseconds(200),
        // Keep the complete route below IpcServer's 5 s send timeout so its acknowledgement can still leave.
        RouteDeadline: TimeSpan.FromSeconds(4),
        // Sessionless players such as Kodi may implement dedicated verbs as toggles; suppress immediate repeats.
        UnverifiableRepeatWindow: TimeSpan.FromSeconds(2));

    internal static ActionExecutionResult RouteDetailed(
        ForegroundMediaCommand command,
        IMediaSessionController controller,
        IForegroundMediaCommandSender foreground,
        IMediaVerificationDelay delay,
        IUnverifiableMediaRepeatGuard repeatGuard,
        MediaRoutingPolicy policy,
        Action<string, string> log)
    {
        TimeSpan sessionTimeout = policy.SessionTimeout;
        TimeSpan verificationDelay = policy.VerificationDelay;
        TimeSpan appCommandRetryDelay = policy.AppCommandRetryDelay;
        TimeSpan routeDeadline = policy.RouteDeadline;
        TimeSpan repeatWindow = policy.UnverifiableRepeatWindow;
        string verb = Verb(command);
        using var deadline = new CancellationTokenSource(routeDeadline);
        CancellationToken cancellationToken = deadline.Token;
        ForegroundMediaTarget? target = foreground.GetTarget();

        // Phase 1: already-in-state.
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
            return ActionExecutionResult.Success;
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
            return ActionExecutionResult.Success;
        }

        // Phase 2: focused delivery.
        ForegroundCommandDelivery focusedDelivery = target is ForegroundMediaTarget focusedTarget
            ? foreground.Send(focusedTarget, command)
            : ForegroundCommandDelivery.Failed;
        int focusedAttempts = target is null ? 0 : 1;
        if (focusedDelivery == ForegroundCommandDelivery.TimedOut)
        {
            log(
                "WARN",
                $"media routing '{verb}': targeted appcommand timed out; retrying once after {appCommandRetryDelay.TotalMilliseconds:0} ms.");
            if (Wait(delay, appCommandRetryDelay, cancellationToken)
                && target is ForegroundMediaTarget retryTarget)
            {
                focusedDelivery = foreground.Send(retryTarget, command);
                focusedAttempts++;
            }
        }

        // Phase 3: verification.
        bool focusedSent = focusedDelivery is ForegroundCommandDelivery.DeliveredUnhandled
            or ForegroundCommandDelivery.DeliveredHandled;
        if (focusedSent)
        {
            if (!Wait(delay, verificationDelay, cancellationToken))
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("WARN", $"media routing '{verb}': path=unverifiable; focused appcommand delivered but the {routeDeadline.TotalSeconds:0.#} s route deadline expired before verification.");
                return ActionExecutionResult.Success;
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
                return ActionExecutionResult.Success;
            }

            bool afterFocusedOwned = IsOwnedBy(afterFocused, target);
            LogOwnership(log, verb, "after-focused", target, afterFocused, afterFocusedOwned, null);
            if (!afterFocusedOwned)
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("DEBUG", $"media routing '{verb}': path=unverifiable; focused appcommand delivered; current session belongs to a different or unresolved app, so no fallback was allowed.");
                return ActionExecutionResult.Success;
            }

            if (focusedDesired is MediaPlaybackState expected && afterFocused.State == expected)
            {
                repeatGuard.Reset();
                log("DEBUG", $"media routing '{verb}': path=focused-handled; matching app ownership and {StateName(expected)} state verified.");
                return ActionExecutionResult.Success;
            }

            if (focusedDesired is null)
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("DEBUG", $"media routing '{verb}': path=unverifiable; play/pause lacked a matching initial session state, so its focused toggle outcome cannot be proven; no fallback sent.");
                return ActionExecutionResult.Success;
            }

            if (focusedDelivery == ForegroundCommandDelivery.DeliveredHandled)
            {
                RecordUnverifiable(command, target, repeatGuard, log, verb);
                log("DEBUG", $"media routing '{verb}': path=unverifiable; focused window reported handled but its matching session did not update; no fallback sent to avoid a repeated side effect.");
                return ActionExecutionResult.Success;
            }

            // Phase 4: owner-pinned fallback after focused verification.
            return ExecuteAndVerifyFallback(
                verb,
                focusedDesired.Value,
                afterFocused,
                controller,
                delay,
                sessionTimeout,
                verificationDelay,
                routeDeadline,
                log,
                cancellationToken);
        }

        // Phase 5: unverifiable delivery failure, or owner-pinned fallback when safe.
        repeatGuard.Reset();
        if (!initialRead || !initial.HasSession)
        {
            string detail = initialRead ? "no media session exists" : $"session read failed ({readError})";
            string error = $"{DeliveryFailure(focusedDelivery, target, focusedAttempts)}; {detail}, so there is no fallback target";
            log("WARN", $"media routing '{verb}': path=unverifiable failed; {error}.");
            return ActionExecutionResult.Failure(error);
        }

        if (target is ForegroundMediaTarget targeted && !initialOwned)
        {
            string error = $"{DeliveryFailure(focusedDelivery, target, focusedAttempts)}; no same-owner media session is available for {targeted.Identity.LogLabel}";
            log(
                "WARN",
                $"media routing '{verb}': path=session-fallback blocked; {error}; current session owner is '{initial.SourceAppUserModelId}'.");
            return ActionExecutionResult.Failure(error);
        }

        MediaPlaybackState? fallbackDesired = DesiredState(command, initial.State);
        if (fallbackDesired is null)
        {
            log("WARN", $"media routing '{verb}': path=session-fallback failed; no playback state exists from which to derive the toggle intent.");
            return ActionExecutionResult.Failure("media fallback could not derive the requested play/pause state");
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
            routeDeadline,
            log,
            cancellationToken);
    }

    private static ActionExecutionResult ExecuteAndVerifyFallback(
        string verb,
        MediaPlaybackState desired,
        MediaSessionSnapshot target,
        IMediaSessionController controller,
        IMediaVerificationDelay delay,
        TimeSpan sessionTimeout,
        TimeSpan verificationDelay,
        TimeSpan routeDeadline,
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
            return ActionExecutionResult.Failure($"session fallback did not finish before the {routeDeadline.TotalSeconds:0.#} s route deadline");
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
            return ActionExecutionResult.Success;
        }

        string detail = readError is null
            ? $"owner/state became '{afterFallback.SourceAppUserModelId ?? "no-session"}'/{StateName(afterFallback.State)}"
            : $"verification failed ({readError})";
        log("WARN", $"media routing '{verb}': path=session-fallback failed; session verb returned {fallbackResult}; {detail}.");
        return ActionExecutionResult.Failure($"same-owner session fallback returned {fallbackResult}; {detail}");
    }

    private static string DeliveryFailure(
        ForegroundCommandDelivery delivery,
        ForegroundMediaTarget? target,
        int attempts) => delivery switch
    {
        ForegroundCommandDelivery.TimedOut when attempts == 2 => "targeted appcommand timed out after two attempts",
        ForegroundCommandDelivery.TimedOut => "targeted appcommand timed out after one attempt",
        ForegroundCommandDelivery.Failed when target is null => "no focused media target was available",
        ForegroundCommandDelivery.Failed => "targeted appcommand was refused",
        _ => "targeted appcommand delivery failed",
    };

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

    internal static string Verb(ForegroundMediaCommand command) => command switch
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

}

/// <summary>Implements the media verification delay with cancellable task scheduling.</summary>
internal sealed class TaskMediaVerificationDelay : IMediaVerificationDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>Tracks the last unverifiable focused-media delivery so immediate duplicate dedicated verbs can be suppressed.</summary>
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
