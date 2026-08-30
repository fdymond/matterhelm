using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class MediaKeysTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RouteDeadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(2);
    private static readonly MediaAppIdentity Kodi = new(10, "kodi.exe", null);
    private static readonly MediaAppIdentity Chrome = new(20, "chrome.exe", "Chrome.App");
    private static readonly MediaAppIdentity Spotify = new(30, "Spotify.exe", "SpotifyAB.Spotify!App");

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
    public void FailedFocusedDeliveryMayUseAnUnrelatedCurrentSessionAsTheFallbackTarget()
    {
        var controller = new FakeMediaSessionController(
            Snapshot(MediaPlaybackState.Paused, "Chrome.App"),
            Snapshot(MediaPlaybackState.Playing, "Chrome.App"));
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
        Assert.Equal(1, controller.PlayCalls);
        Assert.Equal(["Chrome.App"], controller.ActionTargets);
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
        var foreground = new FakeForegroundSender(Kodi)
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
        var repeatGuard = new MediaKeys.UnverifiableMediaRepeatGuard(() => ticks);
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
        var repeatGuard = new MediaKeys.UnverifiableMediaRepeatGuard(() => ticks);
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
            (await controller.GetCurrentSessionAsync(TestTimeout, CancellationToken.None)).State);
        Assert.Equal(
            MediaSessionActionResult.Succeeded,
            await controller.TryPlayAsync("Chrome.App", TestTimeout, CancellationToken.None));
        Assert.Equal(
            MediaSessionActionResult.Succeeded,
            await controller.TryPauseAsync("Chrome.App", TestTimeout, CancellationToken.None));

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
        MediaKeys.Route(
            command,
            controller,
            foreground,
            delay,
            repeatGuard,
            TestTimeout,
            VerifyDelay,
            RouteDeadline,
            RepeatWindow,
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
        internal List<ForegroundMediaCommand> Commands { get; } = [];

        public ForegroundMediaTarget? GetTarget() => identity is null
            ? null
            : new ForegroundMediaTarget((nint)123, identity);

        public ForegroundCommandDelivery Send(ForegroundMediaTarget target, ForegroundMediaCommand command)
        {
            Commands.Add(command);
            return Result;
        }
    }

    private sealed class FakeDelay : IMediaVerificationDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
