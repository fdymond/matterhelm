using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class MediaKeysTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void PlayUsesAbsoluteSessionVerbWhenCurrentSessionAcceptsIt()
    {
        var controller = new FakeMediaSessionController();
        var log = new TestSupport.LogCapture();

        bool result = MediaKeys.Play(
            controller,
            TestTimeout,
            log.Sink);

        Assert.True(result);
        Assert.Equal(1, controller.PlayCalls);
        Assert.Equal(0, controller.PauseCalls);
        Assert.True(log.Contains("DEBUG", "accepted the SMTC verb"));
    }

    [Fact]
    public void PauseUsesAbsoluteSessionVerbWhenCurrentSessionAcceptsIt()
    {
        var controller = new FakeMediaSessionController();

        bool result = MediaKeys.Pause(
            controller,
            TestTimeout,
            (_, _) => { });

        Assert.True(result);
        Assert.Equal(0, controller.PlayCalls);
        Assert.Equal(1, controller.PauseCalls);
    }

    [Fact]
    public void NoCurrentSessionFailsWithoutSendingAnIntentInvertingFallback()
    {
        var controller = new FakeMediaSessionController
        {
            PlayResult = Task.FromResult(MediaSessionActionResult.NoCurrentSession),
        };
        var log = new TestSupport.LogCapture();

        bool result = MediaKeys.Play(
            controller,
            TestTimeout,
            log.Sink);

        Assert.False(result);
        Assert.True(log.Contains("WARN", "no current Windows media session"));
        Assert.True(log.Contains("WARN", "no fallback was sent"));
        Assert.True(log.Contains("WARN", "invert the requested intent"));
    }

    [Fact]
    public void RejectedSessionVerbFailsWithoutSendingAnIntentInvertingFallback()
    {
        var controller = new FakeMediaSessionController
        {
            PauseResult = Task.FromResult(MediaSessionActionResult.Rejected),
        };
        var log = new TestSupport.LogCapture();

        bool result = MediaKeys.Pause(
            controller,
            TestTimeout,
            log.Sink);

        Assert.False(result);
        Assert.True(log.Contains("WARN", "rejected the SMTC verb"));
        Assert.True(log.Contains("WARN", "no fallback was sent"));
    }

    [Fact]
    public void TimedOutSessionCallFailsWithoutSendingAnIntentInvertingFallback()
    {
        var controller = new FakeMediaSessionController
        {
            PlayResult = new TaskCompletionSource<MediaSessionActionResult>().Task,
        };
        var log = new TestSupport.LogCapture();

        bool result = MediaKeys.Play(
            controller,
            TimeSpan.Zero,
            log.Sink);

        Assert.False(result);
        Assert.True(log.Contains("WARN", "media-session call timed out"));
        Assert.True(log.Contains("WARN", "no fallback was sent"));
    }

    [Fact]
    public void FailedSessionCallFailsWithoutSendingAnIntentInvertingFallback()
    {
        var controller = new FakeMediaSessionController
        {
            PauseResult = Task.FromException<MediaSessionActionResult>(
                new InvalidOperationException("SMTC unavailable")),
        };
        var log = new TestSupport.LogCapture();

        bool result = MediaKeys.Pause(controller, TestTimeout, log.Sink);

        Assert.False(result);
        Assert.True(log.Contains("WARN", "media-session call failed: SMTC unavailable"));
        Assert.True(log.Contains("WARN", "no fallback was sent"));
    }

    [Fact]
    public async Task ManagerAcquisitionIsCachedWhileCurrentSessionIsResolvedForEveryVerb()
    {
        var manager = new FakeMediaSessionManager();
        int managerRequests = 0;
        var controller = new WindowsMediaSessionController(() =>
        {
            managerRequests++;
            return Task.FromResult<IMediaSessionManager>(manager);
        });

        Assert.Equal(
            MediaSessionActionResult.Succeeded,
            await controller.TryPlayAsync(TestTimeout, CancellationToken.None));
        Assert.Equal(
            MediaSessionActionResult.Succeeded,
            await controller.TryPauseAsync(TestTimeout, CancellationToken.None));

        Assert.Equal(1, managerRequests);
        Assert.Equal(2, manager.CurrentSessionLookups);
    }

    private sealed class FakeMediaSessionController : IMediaSessionController
    {
        internal Task<MediaSessionActionResult> PlayResult { get; init; } =
            Task.FromResult(MediaSessionActionResult.Succeeded);

        internal Task<MediaSessionActionResult> PauseResult { get; init; } =
            Task.FromResult(MediaSessionActionResult.Succeeded);

        internal int PlayCalls { get; private set; }

        internal int PauseCalls { get; private set; }

        public Task<MediaSessionActionResult> TryPlayAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            PlayCalls++;
            return PlayResult;
        }

        public Task<MediaSessionActionResult> TryPauseAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            PauseCalls++;
            return PauseResult;
        }
    }

    private sealed class FakeMediaSessionManager : IMediaSessionManager
    {
        internal int CurrentSessionLookups { get; private set; }

        public Task<MediaSessionActionResult> TryPlayAsync(CancellationToken cancellationToken)
        {
            CurrentSessionLookups++;
            return Task.FromResult(MediaSessionActionResult.Succeeded);
        }

        public Task<MediaSessionActionResult> TryPauseAsync(CancellationToken cancellationToken)
        {
            CurrentSessionLookups++;
            return Task.FromResult(MediaSessionActionResult.Succeeded);
        }
    }
}
