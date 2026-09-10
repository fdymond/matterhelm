using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class MediaKeysTests
{
    private static readonly MediaAppIdentity Kodi = new(10, "kodi.exe", null);
    private static readonly MediaAppIdentity Chrome = new(20, "chrome.exe", "Chrome.App");
    private static readonly MediaAppIdentity Spotify = new(30, "Spotify.exe", "SpotifyAB.Spotify!App");

    [Fact]
    public void ProductionRoutingPolicyPinsReviewedRetryAndDrainBudget()
    {
        MediaRoutingPolicy policy = FocusedMediaRouter.ProductionRoutingPolicy;

        Assert.Equal(TimeSpan.FromSeconds(1), policy.SessionTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(400), policy.VerificationDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.AppCommandRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(4), policy.RouteDeadline);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.UnverifiableRepeatWindow);
    }

    [Fact]
    public void RouteLoggerTreatsUnknownLevelsAsWarnings()
    {
        Assert.Equal(LogLevel.Warn, MediaKeys.RouteLogLevel("INFO"));
        Assert.Equal(LogLevel.Debug, MediaKeys.RouteLogLevel("DEBUG"));
        Assert.Equal(LogLevel.Error, MediaKeys.RouteLogLevel("ERROR"));
    }

    [Theory]
    [InlineData(1460, (int)ForegroundCommandDelivery.TimedOut)]
    [InlineData(0, (int)ForegroundCommandDelivery.Failed)]
    [InlineData(5, (int)ForegroundCommandDelivery.Failed)]
    public void Win32AppCommandFailureClassificationPreservesTimeoutMeaning(
        int win32Error,
        int expectedValue) =>
        Assert.Equal(
            (ForegroundCommandDelivery)expectedValue,
            WindowsForegroundMediaCommandSender.ClassifyAppCommandFailure(win32Error));

    [Theory]
    [InlineData((int)ForegroundMediaCommand.Play, (int)MediaPlaybackState.Playing)]
    [InlineData((int)ForegroundMediaCommand.Pause, (int)MediaPlaybackState.Paused)]
    public void AbsoluteVerbAlreadyInRequestedStateShortCircuitsOnlyForTheFocusedOwner(
        int commandValue,
        int stateValue)
    {
        var controller = new FakeMediaSessionController(
            Snapshot((MediaPlaybackState)stateValue, "Chrome.App"));
        var foreground = new FakeForegroundSender(Chrome);
        var log = new TestSupport.LogCapture();

        bool result = Route(
            (ForegroundMediaCommand)commandValue,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            log);

        Assert.True(result);
        Assert.Empty(foreground.Commands);
        Assert.Equal(0, controller.ActionCalls);
        Assert.True(log.Contains("DEBUG", "decision=same-app"));
        Assert.True(log.Contains("DEBUG", "path=already-in-state"));
    }

    [Fact]
    public void KodiFocusedWithPausedChromeSessionStillReceivesDedicatedPause()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "chrome.exe"),
            Snapshot(MediaPlaybackState.Paused, "chrome.exe"));
        var foreground = new FakeForegroundSender(Kodi);
        var log = new TestSupport.LogCapture();

        bool result = Route(
            ForegroundMediaCommand.Pause,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            log);

        Assert.True(result);
        Assert.Equal([ForegroundMediaCommand.Pause], foreground.Commands);
        Assert.Equal(0, controller.ActionCalls);
        Assert.True(log.Contains("DEBUG", "decision=different-or-unresolved-app"));
        Assert.True(log.Contains("DEBUG", "no fallback was allowed"));
    }

    [Fact]
    public void FocusedSideEffectCannotBeVerifiedByAnUnrelatedSessionAlreadyAtDesiredState()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Playing, "SpotifyAB.Spotify!App"),
            Snapshot(MediaPlaybackState.Paused, "SpotifyAB.Spotify!App"));
        var foreground = new FakeForegroundSender(Kodi);

        bool result = Route(
            ForegroundMediaCommand.Pause,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture());

        Assert.True(result);
        Assert.Equal([ForegroundMediaCommand.Pause], foreground.Commands);
        Assert.Equal(0, controller.ActionCalls);
    }

    [Fact]
    public void SessionOwnerChangingAfterFocusedDeliveryIsNotVerificationOrFallback()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "SpotifyAB.Spotify!App"));
        var foreground = new FakeForegroundSender(Chrome);
        var log = new TestSupport.LogCapture();

        bool result = Route(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            log);

        Assert.True(result);
        Assert.Equal(0, controller.ActionCalls);
        Assert.True(log.Contains("DEBUG", "stage=after-focused"));
        Assert.True(log.Contains("DEBUG", "decision=different-or-unresolved-app"));
    }

    [Fact]
    public void UnhandledFocusedVerbFallsBackOnlyToTheMatchingOwnerAndVerifies()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "Chrome.App"));
        var foreground = new FakeForegroundSender(Chrome);
        var log = new TestSupport.LogCapture();

        bool result = Route(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            log);

        Assert.True(result);
        Assert.Equal(1, controller.PlayCalls);
        Assert.Equal(["Chrome.App"], controller.ActionTargets);
        Assert.True(log.Contains("DEBUG", "path=session-fallback"));
    }

    [Fact]
    public void PlayPauseFallbackUsesComputedAbsolutePauseRatherThanToggle()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Playing, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "Chrome.App"),
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"));

        bool result = Route(
            ForegroundMediaCommand.PlayPause,
            controller,
            new FakeForegroundSender(Chrome),
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture());

        Assert.True(result);
        Assert.Equal(0, controller.PlayCalls);
        Assert.Equal(1, controller.PauseCalls);
    }

    [Fact]
    public void TargetedDeliveryFailureDoesNotFallBackToAnUnrelatedSession()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"));
        var foreground = new FakeForegroundSender(Kodi)
        {
            Result = ForegroundCommandDelivery.Failed,
        };
        var log = new TestSupport.LogCapture();

        ActionExecutionResult result = RouteDetailed(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            log);

        Assert.False(result.Ok);
        Assert.Contains("no same-owner media session", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, controller.PlayCalls);
        Assert.True(log.Contains("WARN", "session-fallback blocked"));
    }

    [Fact]
    public void TargetedDeliveryFailureMayFallBackToASameOwnerSession()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "kodi.exe"),
            Snapshot(MediaPlaybackState.Playing, "kodi.exe"));
        var foreground = new FakeForegroundSender(Kodi)
        {
            Result = ForegroundCommandDelivery.Failed,
        };

        bool result = Route(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture());

        Assert.True(result);
        Assert.Equal(["kodi.exe"], controller.ActionTargets);
    }

    [Fact]
    public void UntargetedRouteUsesTheCapturedCurrentSessionWhenOneExists()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "Chrome.App"));

        bool result = Route(
            ForegroundMediaCommand.Play,
            controller,
            new FakeForegroundSender(null),
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture());

        Assert.True(result);
        Assert.Equal(["Chrome.App"], controller.ActionTargets);
    }

    [Fact]
    public void UntargetedRouteFailsWhenNoCurrentSessionExists()
    {
        var controller = new FakeMediaSessionController(MediaSessionSnapshot.NoSession);

        ActionExecutionResult result = RouteDetailed(
            ForegroundMediaCommand.Play,
            controller,
            new FakeForegroundSender(null),
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture());

        Assert.False(result.Ok);
        Assert.Contains("no focused media target", result.Error, StringComparison.Ordinal);
        Assert.Contains("no media session exists", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, controller.ActionCalls);
    }

    [Fact]
    public void UntargetedRouteFailsWhenCapturedSessionOwnerChangesBeforeVerification()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "SpotifyAB.Spotify!App"))
        {
            PlayResult = MediaSessionActionResult.TargetChanged,
        };

        ActionExecutionResult result = RouteDetailed(
            ForegroundMediaCommand.Play,
            controller,
            new FakeForegroundSender(null),
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture());

        Assert.False(result.Ok);
        Assert.Equal(["Chrome.App"], controller.ActionTargets);
        Assert.Contains("TargetChanged", result.Error, StringComparison.Ordinal);
        Assert.Contains("SpotifyAB.Spotify!App", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TimedOutTargetRetriesOnceThenRejectsAnUnrelatedSessionWithAnHonestReason()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "SpotifyAB.Spotify!App"));
        var foreground = new FakeForegroundSender(Kodi);
        foreground.Results.Enqueue(ForegroundCommandDelivery.TimedOut);
        foreground.Results.Enqueue(ForegroundCommandDelivery.TimedOut);
        var delay = new FakeDelay();
        var log = new TestSupport.LogCapture();

        ActionExecutionResult result = RouteDetailed(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            delay,
            new FakeRepeatGuard(),
            log);

        Assert.False(result.Ok);
        Assert.Contains("timed out after two attempts", result.Error, StringComparison.Ordinal);
        Assert.Contains("no same-owner media session", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, controller.ActionCalls);
        Assert.Equal(2, foreground.Commands.Count);
        Assert.Equal([FocusedMediaRouter.ProductionRoutingPolicy.AppCommandRetryDelay], delay.Waits);
        Assert.True(log.Contains("WARN", "retrying once"));
    }

    [Fact]
    public void TimedOutTargetRetriesOnceThenAllowsASameOwnerSessionFallback()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "kodi.exe"),
            Snapshot(MediaPlaybackState.Playing, "kodi.exe"));
        var foreground = new FakeForegroundSender(Kodi);
        foreground.Results.Enqueue(ForegroundCommandDelivery.TimedOut);
        foreground.Results.Enqueue(ForegroundCommandDelivery.TimedOut);
        var delay = new FakeDelay();

        bool result = Route(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            delay,
            new FakeRepeatGuard(),
            new TestSupport.LogCapture());

        Assert.True(result);
        Assert.Equal(2, foreground.Commands.Count);
        Assert.Equal(1, controller.PlayCalls);
        Assert.Equal("kodi.exe", Assert.Single(controller.ActionTargets));
        Assert.Equal(
            [
                FocusedMediaRouter.ProductionRoutingPolicy.AppCommandRetryDelay,
                FocusedMediaRouter.ProductionRoutingPolicy.VerificationDelay,
            ],
            delay.Waits);
    }

    [Fact]
    public void TimedOutTargetCanHandleTheSingleRetryWithoutUsingSessionFallback()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "kodi.exe"),
            Snapshot(MediaPlaybackState.Playing, "kodi.exe"));
        var foreground = new FakeForegroundSender(Kodi);
        foreground.Results.Enqueue(ForegroundCommandDelivery.TimedOut);
        foreground.Results.Enqueue(ForegroundCommandDelivery.DeliveredHandled);
        var delay = new FakeDelay();
        var log = new TestSupport.LogCapture();

        bool result = Route(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            delay,
            new FakeRepeatGuard(),
            log);

        Assert.True(result);
        Assert.Equal(2, foreground.Commands.Count);
        Assert.Equal(0, controller.ActionCalls);
        Assert.Equal(
            [
                FocusedMediaRouter.ProductionRoutingPolicy.AppCommandRetryDelay,
                FocusedMediaRouter.ProductionRoutingPolicy.VerificationDelay,
            ],
            delay.Waits);
        Assert.True(log.Contains("WARN", "retrying once"));
        Assert.True(log.Contains("DEBUG", "path=focused-handled"));
    }

    [Fact]
    public void NonTimeoutTargetRefusalIsNotRetried()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "kodi.exe"),
            Snapshot(MediaPlaybackState.Playing, "kodi.exe"));
        var foreground = new FakeForegroundSender(Kodi)
        {
            Result = ForegroundCommandDelivery.Failed,
        };

        Assert.True(Route(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture()));

        Assert.Single(foreground.Commands);
    }

    [Fact]
    public void SessionOwnerChangeBeforeFallbackActionFailsWithoutControllingTheReplacement()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "SpotifyAB.Spotify!App"))
        {
            PlayResult = MediaSessionActionResult.TargetChanged,
        };
        var foreground = new FakeForegroundSender(Chrome)
        {
            Result = ForegroundCommandDelivery.Failed,
        };
        var log = new TestSupport.LogCapture();

        bool result = Route(
            ForegroundMediaCommand.Play,
            controller,
            foreground,
            new FakeDelay(),
            new FakeRepeatGuard(),
            log);

        Assert.False(result);
        Assert.Equal(["Chrome.App"], controller.ActionTargets);
        Assert.True(log.Contains("WARN", "TargetChanged"));
        Assert.True(log.Contains("WARN", "SpotifyAB.Spotify!App"));
        Assert.True(log.Contains("WARN", "playing"));
    }

    [Fact]
    public void ImmediateRepeatedDedicatedPauseToSameUnverifiableTargetIsSuppressed()
    {
        long ticks = 1000;
        var repeatGuard = new UnverifiableMediaRepeatGuard(() => ticks);
        var controller = new FakeMediaSessionController(
            MediaSessionSnapshot.NoSession,
            MediaSessionSnapshot.NoSession,
            MediaSessionSnapshot.NoSession);
        var foreground = new FakeForegroundSender(Kodi);
        var log = new TestSupport.LogCapture();

        Assert.True(Route(
            ForegroundMediaCommand.Pause,
            controller,
            foreground,
            new FakeDelay(),
            repeatGuard,
            log));
        ticks += 1500;
        Assert.True(Route(
            ForegroundMediaCommand.Pause,
            controller,
            foreground,
            new FakeDelay(),
            repeatGuard,
            log));

        Assert.Equal([ForegroundMediaCommand.Pause], foreground.Commands);
        Assert.True(log.Contains("WARN", "path=unverifiable-repeat-suppressed"));
    }

    [Fact]
    public void SameDedicatedVerbAfterRepeatWindowIsDeliveredAgain()
    {
        long ticks = 1000;
        var repeatGuard = new UnverifiableMediaRepeatGuard(() => ticks);
        var controller = new FakeMediaSessionController(
            MediaSessionSnapshot.NoSession,
            MediaSessionSnapshot.NoSession,
            MediaSessionSnapshot.NoSession,
            MediaSessionSnapshot.NoSession);
        var foreground = new FakeForegroundSender(Kodi);

        Assert.True(Route(
            ForegroundMediaCommand.Pause,
            controller,
            foreground,
            new FakeDelay(),
            repeatGuard,
            new TestSupport.LogCapture()));
        ticks += 2001;
        Assert.True(Route(
            ForegroundMediaCommand.Pause,
            controller,
            foreground,
            new FakeDelay(),
            repeatGuard,
            new TestSupport.LogCapture()));

        Assert.Equal(
            [ForegroundMediaCommand.Pause, ForegroundMediaCommand.Pause],
            foreground.Commands);
    }

    [Fact]
    public void SessionOperationsShareOneCancellableRouteDeadlineToken()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "Chrome.App"));

        Assert.True(Route(
            ForegroundMediaCommand.Play,
            controller,
            new FakeForegroundSender(Chrome),
            new FakeDelay(),
            new FakeRepeatGuard(),
            new TestSupport.LogCapture()));

        Assert.NotEmpty(controller.Tokens);
        Assert.All(controller.Tokens, token => Assert.True(token.CanBeCanceled));
        Assert.All(controller.Tokens, token => Assert.Equal(controller.Tokens[0], token));
    }

    [Fact]
    public async Task ManagerAcquisitionIsCachedWhileCurrentSessionIsResolvedForEveryOperation()
    {
        var manager = new FakeMediaSessionManager();
        int managerRequests = 0;
        var controller = new WindowsMediaSessionController(() =>
        {
            managerRequests++;
            return Task.FromResult<IMediaSessionManager>(manager);
        });

        Assert.Equal(
            MediaPlaybackState.Paused,
            (await controller.GetCurrentSessionAsync(
                FocusedMediaRouter.ProductionRoutingPolicy.SessionTimeout,
                CancellationToken.None)).State);
        Assert.Equal(
            MediaSessionActionResult.Succeeded,
            await controller.TryPlayAsync(
                "Chrome.App",
                FocusedMediaRouter.ProductionRoutingPolicy.SessionTimeout,
                CancellationToken.None));
        Assert.Equal(
            MediaSessionActionResult.Succeeded,
            await controller.TryPauseAsync(
                "Chrome.App",
                FocusedMediaRouter.ProductionRoutingPolicy.SessionTimeout,
                CancellationToken.None));

        Assert.Equal(1, managerRequests);
        Assert.Equal(3, manager.CurrentSessionLookups);
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("C:\\Program Files\\Google\\Chrome\\chrome.exe")]
    [InlineData("Chrome.App")]
    public void ForegroundIdentityMatchesSessionAumidOrExecutableName(string source)
    {
        Assert.True(Chrome.OwnsSession(source));
        Assert.False(Spotify.OwnsSession(source));
    }

    private static MediaSessionSnapshot Snapshot(MediaPlaybackState state, string source) =>
        new(state, source);

    private static bool Route(
        ForegroundMediaCommand command,
        FakeMediaSessionController controller,
        FakeForegroundSender foreground,
        FakeDelay delay,
        IUnverifiableMediaRepeatGuard repeatGuard,
        TestSupport.LogCapture log) =>
        FocusedMediaRouter.RouteDetailed(
            command,
            controller,
            foreground,
            delay,
            repeatGuard,
            FocusedMediaRouter.ProductionRoutingPolicy,
            log.Sink).Ok;

    private static ActionExecutionResult RouteDetailed(
        ForegroundMediaCommand command,
        FakeMediaSessionController controller,
        FakeForegroundSender foreground,
        FakeDelay delay,
        IUnverifiableMediaRepeatGuard repeatGuard,
        TestSupport.LogCapture log) =>
        FocusedMediaRouter.RouteDetailed(
            command,
            controller,
            foreground,
            delay,
            repeatGuard,
            FocusedMediaRouter.ProductionRoutingPolicy,
            log.Sink);

    private sealed class FakeMediaSessionController(params MediaSessionSnapshot[] snapshots) : IMediaSessionController
    {
        private readonly Queue<MediaSessionSnapshot> _snapshots = new(snapshots);
        private MediaSessionSnapshot _last = snapshots.Length == 0
            ? MediaSessionSnapshot.NoSession
            : snapshots[^1];

        internal MediaSessionActionResult PlayResult { get; init; } = MediaSessionActionResult.Succeeded;
        internal MediaSessionActionResult PauseResult { get; init; } = MediaSessionActionResult.Succeeded;
        internal int PlayCalls { get; private set; }
        internal int PauseCalls { get; private set; }
        internal int ActionCalls => PlayCalls + PauseCalls;
        internal List<string> ActionTargets { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];

        public Task<MediaSessionSnapshot> GetCurrentSessionAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_snapshots.TryDequeue(out MediaSessionSnapshot? snapshot))
            {
                _last = snapshot;
            }

            return Task.FromResult(_last);
        }

        public Task<MediaSessionActionResult> TryPlayAsync(
            string expectedSourceAppUserModelId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            PlayCalls++;
            ActionTargets.Add(expectedSourceAppUserModelId);
            return Task.FromResult(PlayResult);
        }

        public Task<MediaSessionActionResult> TryPauseAsync(
            string expectedSourceAppUserModelId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            PauseCalls++;
            ActionTargets.Add(expectedSourceAppUserModelId);
            return Task.FromResult(PauseResult);
        }
    }

    private sealed class FakeForegroundSender(MediaAppIdentity? identity) : IForegroundMediaCommandSender
    {
        internal ForegroundCommandDelivery Result { get; init; } = ForegroundCommandDelivery.DeliveredUnhandled;
        internal Queue<ForegroundCommandDelivery> Results { get; } = [];
        internal List<ForegroundMediaCommand> Commands { get; } = [];

        public ForegroundMediaTarget? GetTarget() => identity is null
            ? null
            : new ForegroundMediaTarget((nint)123, identity);

        public ForegroundCommandDelivery Send(ForegroundMediaTarget target, ForegroundMediaCommand command)
        {
            Commands.Add(command);
            return Results.TryDequeue(out ForegroundCommandDelivery result)
                ? result
                : Result;
        }
    }

    private sealed class FakeDelay : IMediaVerificationDelay
    {
        internal List<TimeSpan> Waits { get; } = [];

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Waits.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepeatGuard : IUnverifiableMediaRepeatGuard
    {
        public bool ShouldSuppress(ForegroundMediaCommand command, MediaAppIdentity target, TimeSpan window) => false;

        public void Record(ForegroundMediaCommand command, MediaAppIdentity target)
        {
        }

        public void Reset()
        {
        }
    }

    private sealed class FakeMediaSessionManager : IMediaSessionManager
    {
        internal int CurrentSessionLookups { get; private set; }

        public Task<MediaSessionSnapshot> GetCurrentSessionAsync(CancellationToken cancellationToken)
        {
            CurrentSessionLookups++;
            return Task.FromResult(Snapshot(MediaPlaybackState.Paused, "Chrome.App"));
        }

        public Task<MediaSessionActionResult> TryPlayAsync(
            string expectedSourceAppUserModelId,
            CancellationToken cancellationToken)
        {
            CurrentSessionLookups++;
            return Task.FromResult(MediaSessionActionResult.Succeeded);
        }

        public Task<MediaSessionActionResult> TryPauseAsync(
            string expectedSourceAppUserModelId,
            CancellationToken cancellationToken)
        {
            CurrentSessionLookups++;
            return Task.FromResult(MediaSessionActionResult.Succeeded);
        }
    }
}
