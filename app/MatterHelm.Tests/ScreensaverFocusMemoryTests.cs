using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class ScreensaverFocusMemoryTests
{
    [Fact]
    public void ScreensaverStartCapturesFocusBeforeLaunching()
    {
        var native = new FakeForegroundWindowNative();
        var events = native.Events;
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });

        bool started = ActionExecutor.StartScreenSaver(
            memory,
            () =>
            {
                events.Add("screensaver-start");
                return true;
            });

        Assert.True(started);
        Assert.True(memory.HasCapture);
        Assert.True(events.IndexOf("get-foreground") < events.IndexOf("screensaver-start"));
    }

    [Fact]
    public void PowerScreensaverOffRouteUsesTheSameCaptureBeforeStartPath()
    {
        var native = new FakeForegroundWindowNative();
        var events = native.Events;
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });

        (bool started, _) = BridgeHost.RoutePowerAction(
            PowerOffAction.Screensaver,
            on: false,
            (name, _) => name == "startScreenSaver"
                && ActionExecutor.StartScreenSaver(
                    memory,
                    () =>
                    {
                        events.Add("screensaver-start");
                        return true;
                    }));

        Assert.True(started);
        Assert.True(memory.HasCapture);
        Assert.True(events.IndexOf("get-foreground") < events.IndexOf("screensaver-start"));
    }

    [Fact]
    public void DisplaysOffDoesNotCaptureScreensaverFocus()
    {
        var native = new FakeForegroundWindowNative();
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });

        (bool ok, _) = BridgeHost.RoutePowerAction(
            PowerOffAction.DisplaysOff,
            on: false,
            (name, _) => name == "startScreenSaver"
                ? ActionExecutor.StartScreenSaver(memory, () => true)
                : true);

        Assert.True(ok);
        Assert.False(memory.HasCapture);
        Assert.DoesNotContain("get-foreground", native.Events);
    }

    [Fact]
    public void RestoreTargetsCapturedHandleAndRestoresItWhenMinimized()
    {
        var native = new FakeForegroundWindowNative { IsMinimizedResult = true };
        var logs = new List<(string Level, string Message)>();
        var memory = new ScreensaverFocusMemory(native, (level, message) => logs.Add((level, message)));
        Assert.True(memory.Capture());

        bool restored = ActionExecutor.StopScreenSaver(memory, () => true);

        Assert.True(restored);
        Assert.Equal([native.Window], native.RestoredWindows);
        Assert.Equal([native.Window], native.ActivationTargets);
        Assert.Contains(logs, entry => entry.Level == "INFO"
            && entry.Message.Contains("Kodi fullscreen", StringComparison.Ordinal)
            && entry.Message.Contains("restored", StringComparison.Ordinal));
        Assert.False(memory.HasCapture);
    }

    [Theory]
    [InlineData(false, 123u, "kodi", "no longer a window")]
    [InlineData(true, 999u, "kodi", "now belongs to process 999")]
    [InlineData(true, 123u, "not-kodi", "process identity changed")]
    public void StaleCaptureIsWarnedAndClearedWithoutFailingScreensaverDismissal(
        bool isWindow,
        uint currentProcessId,
        string currentProcessName,
        string expectedReason)
    {
        var native = new FakeForegroundWindowNative();
        var logs = new List<(string Level, string Message)>();
        var memory = new ScreensaverFocusMemory(native, (level, message) => logs.Add((level, message)));
        Assert.True(memory.Capture());
        native.IsWindowResult = isWindow;
        native.ProcessId = currentProcessId;
        native.ProcessName = currentProcessName;

        bool restored = ActionExecutor.StopScreenSaver(memory, () => true);

        Assert.True(restored);
        Assert.Empty(native.ActivationTargets);
        Assert.Contains(logs, entry => entry.Level == "WARN"
            && entry.Message.Contains(expectedReason, StringComparison.Ordinal));
        Assert.False(memory.HasCapture);
    }

    [Fact]
    public void ActivationRefusalIsRetriedAndWarnedWithoutFailingScreensaverDismissal()
    {
        var native = new FakeForegroundWindowNative { ActivationDefaultResult = false };
        var logs = new List<(string Level, string Message)>();
        var delay = new FakeFocusRestoreDelay();
        var memory = new ScreensaverFocusMemory(
            native,
            (level, message) => logs.Add((level, message)),
            delay);
        Assert.True(memory.Capture());
        native.ProcessIdLookups.Clear();
        native.ProcessNameLookups.Clear();
        native.ForegroundWindow = 0x222;

        bool dismissed = ActionExecutor.StopScreenSaver(memory, () => true);

        Assert.True(dismissed);
        Assert.Equal(15, delay.Waits.Count);
        Assert.All(delay.Waits, wait => Assert.Equal(TimeSpan.FromMilliseconds(100), wait));
        Assert.Equal(TimeSpan.FromSeconds(1.5), delay.Waits.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait));
        Assert.Equal(16, native.IsWindowCalls);
        Assert.Equal(Enumerable.Repeat(native.Window, 16), native.ProcessIdLookups);
        Assert.Equal(Enumerable.Repeat(123u, 16), native.ProcessNameLookups);
        Assert.Equal(32, native.AttachCalls.Count);
        Assert.Contains(logs, entry => entry.Level == "WARN"
            && entry.Message.Contains("Windows refused SetForegroundWindow after the AttachThreadInput fallback", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, entry => entry.Level == "INFO"
            && entry.Message.Contains("restored", StringComparison.Ordinal));
        Assert.False(memory.HasCapture);
    }

    [Fact]
    public void CapturedProcessIdentityIsRecheckedAndAnIdentityChangeStopsRetries()
    {
        var native = new FakeForegroundWindowNative { ActivationDefaultResult = false };
        var delay = new FakeFocusRestoreDelay(() => native.ProcessName = "replacement");
        var memory = new ScreensaverFocusMemory(native, (_, _) => { }, delay);
        Assert.True(memory.Capture());
        native.ProcessIdLookups.Clear();
        native.ProcessNameLookups.Clear();
        native.ForegroundWindow = 0x222;

        Assert.False(memory.Restore());

        Assert.Equal([TimeSpan.FromMilliseconds(100)], delay.Waits);
        Assert.Equal([native.Window, native.Window], native.ProcessIdLookups);
        Assert.Equal([123u, 123u], native.ProcessNameLookups);
        Assert.Equal(2, native.ActivationTargets.Count);
    }

    [Fact]
    public void AttachFallbackSuccessIsLoggedAndInputQueuesAreDetached()
    {
        var native = new FakeForegroundWindowNative();
        native.ActivationResults.Enqueue(false);
        native.ActivationResults.Enqueue(true);
        var logs = new List<(string Level, string Message)>();
        var memory = new ScreensaverFocusMemory(native, (level, message) => logs.Add((level, message)));
        Assert.True(memory.Capture());
        native.ForegroundWindow = 0x222;

        bool restored = memory.Restore();

        Assert.True(restored);
        Assert.Equal([(44u, 55u, true), (44u, 55u, false)], native.AttachCalls);
        Assert.Contains(logs, entry => entry.Level == "INFO"
            && entry.Message.Contains("AttachThreadInput fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusalWithoutACurrentForegroundWindowIsWarned()
    {
        var native = new FakeForegroundWindowNative { ActivationDefaultResult = false };
        var logs = new List<(string Level, string Message)>();
        var memory = new ScreensaverFocusMemory(
            native,
            (level, message) => logs.Add((level, message)),
            new FakeFocusRestoreDelay());
        Assert.True(memory.Capture());
        native.ForegroundWindow = 0;

        Assert.False(memory.Restore());

        Assert.Contains(logs, entry => entry.Level == "WARN"
            && entry.Message.Contains("no current foreground input queue", StringComparison.Ordinal));
    }

    [Fact]
    public void AttachFailureIsWarnedWithoutAnotherActivationAttempt()
    {
        var native = new FakeForegroundWindowNative
        {
            ActivationDefaultResult = false,
            AttachDefaultResult = false,
        };
        var logs = new List<(string Level, string Message)>();
        var memory = new ScreensaverFocusMemory(
            native,
            (level, message) => logs.Add((level, message)),
            new FakeFocusRestoreDelay());
        Assert.True(memory.Capture());
        native.ForegroundWindow = 0x222;

        Assert.False(memory.Restore());

        Assert.Equal(16, native.ActivationTargets.Count);
        Assert.Contains(logs, entry => entry.Level == "WARN"
            && entry.Message.Contains("could not attach", StringComparison.Ordinal));
    }

    [Fact]
    public void ForegroundInputThreadRetriesWithoutSelfAttach()
    {
        var native = new FakeForegroundWindowNative { CurrentThreadId = 55 };
        native.ActivationResults.Enqueue(false);
        native.ActivationResults.Enqueue(true);
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });
        Assert.True(memory.Capture());
        native.ForegroundWindow = 0x222;

        Assert.True(memory.Restore());

        Assert.Equal(2, native.ActivationTargets.Count);
        Assert.Empty(native.AttachCalls);
    }

    [Fact]
    public void MissingForegroundQueueCanAppearDuringRetryAndRestoreThenSucceeds()
    {
        var native = new FakeForegroundWindowNative { ActivationDefaultResult = false };
        var delay = new FakeFocusRestoreDelay(() =>
        {
            native.ForegroundWindow = 0x222;
            native.ActivationDefaultResult = true;
        });
        var memory = new ScreensaverFocusMemory(native, (_, _) => { }, delay);
        Assert.True(memory.Capture());
        native.ForegroundWindow = 0;

        Assert.True(memory.Restore());

        Assert.Single(delay.Waits);
        Assert.Equal(2, native.IsWindowCalls);
        Assert.Equal(2, native.ActivationTargets.Count);
    }

    [Fact]
    public void RefusedFocusRestoreDoesNotAbortASequenceAfterSuccessfulDismissal()
    {
        var native = new FakeForegroundWindowNative { ActivationDefaultResult = false };
        var memory = new ScreensaverFocusMemory(
            native,
            (_, _) => { },
            new FakeFocusRestoreDelay());
        Assert.True(memory.Capture());
        native.ForegroundWindow = 0;
        var sequence = new SequenceActionConfig
        {
            Steps =
            [
                new SystemActionConfig { Command = SystemCommandName.StopScreenSaver },
                new MediaKeyActionConfig { KeyName = MediaKeyName.Play },
            ],
        };
        int playCalls = 0;

        (bool ok, _, string? error) = BridgeHost.RunSequenceSteps(
            "resume",
            sequence,
            step => step switch
            {
                SystemActionConfig => (
                    ActionExecutor.StopScreenSaver(memory, () => true),
                    "screensaver dismissed",
                    null),
                MediaKeyActionConfig => (++playCalls == 1, "play pressed", null),
                _ => (false, "failed", "unexpected step"),
            });

        Assert.True(ok, error);
        Assert.Equal(1, playCalls);
    }

    [Fact]
    public void SecondStartReplacesTheFirstCapturedWindow()
    {
        var native = new FakeForegroundWindowNative();
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });
        Assert.True(memory.Capture());
        native.Window = 0x333;
        native.ForegroundWindow = native.Window;
        native.ProcessId = 456;
        native.ProcessName = "mpc-hc";
        native.Title = "Movie Player";
        Assert.True(memory.Capture());

        Assert.True(memory.Restore());

        Assert.Equal([(nint)0x333], native.ActivationTargets);
    }

    [Fact]
    public void RestoreConsumesCaptureSoASecondStopIsANoOpSuccess()
    {
        var native = new FakeForegroundWindowNative();
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });
        Assert.True(memory.Capture());
        Assert.True(memory.Restore());

        bool secondRestore = memory.Restore();

        Assert.True(secondRestore);
        Assert.Single(native.ActivationTargets);
    }

    [Fact]
    public void StopWithoutPriorCaptureRunsStopAndSucceedsWithoutNativeActivation()
    {
        var native = new FakeForegroundWindowNative();
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });
        int stopCalls = 0;

        bool stopped = ActionExecutor.StopScreenSaver(
            memory,
            () =>
            {
                stopCalls++;
                return true;
            });

        Assert.True(stopped);
        Assert.Equal(1, stopCalls);
        Assert.Empty(native.ActivationTargets);
    }

    [Fact]
    public void ClearDropsCaptureWithoutTryingToRestoreIt()
    {
        var native = new FakeForegroundWindowNative();
        var memory = new ScreensaverFocusMemory(native, (_, _) => { });
        Assert.True(memory.Capture());

        memory.Clear("test lifecycle boundary");

        Assert.False(memory.HasCapture);
        Assert.True(memory.Restore());
        Assert.Empty(native.ActivationTargets);
    }

    [Fact]
    public void FailedScreensaverStartClearsTheCapture()
    {
        var memory = new ScreensaverFocusMemory(new FakeForegroundWindowNative(), (_, _) => { });

        bool started = ActionExecutor.StartScreenSaver(memory, () => false);

        Assert.False(started);
        Assert.False(memory.HasCapture);
    }

    [Fact]
    public void ThrowingScreensaverStartClearsTheCaptureBeforeTheExecutorHandlesIt()
    {
        var memory = new ScreensaverFocusMemory(new FakeForegroundWindowNative(), (_, _) => { });

        _ = Assert.Throws<InvalidOperationException>(() =>
            ActionExecutor.StartScreenSaver(memory, () => throw new InvalidOperationException("failed")));

        Assert.False(memory.HasCapture);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("process-id")]
    [InlineData("process-name")]
    public void FailedReplacementCaptureClearsTheOlderCaptureAndLogsWhy(string failure)
    {
        var native = new FakeForegroundWindowNative();
        var logs = new List<(string Level, string Message)>();
        var memory = new ScreensaverFocusMemory(native, (level, message) => logs.Add((level, message)));
        Assert.True(memory.Capture());
        switch (failure)
        {
            case "window":
                native.ForegroundWindow = 0;
                break;
            case "process-id":
                native.ProcessId = 0;
                break;
            case "process-name":
                native.ProcessName = null;
                break;
        }

        bool captured = memory.Capture();

        Assert.False(captured);
        Assert.False(memory.HasCapture);
        Assert.Contains(logs, entry => entry.Level == "WARN"
            && entry.Message.Contains("capture skipped", StringComparison.Ordinal));
    }

    private sealed class FakeForegroundWindowNative : IForegroundWindowNative
    {
        public nint Window { get; set; } = 0x111;

        public nint ForegroundWindow { get; set; } = 0x111;

        public bool IsWindowResult { get; set; } = true;

        public uint ProcessId { get; set; } = 123;

        public string? ProcessName { get; set; } = "kodi";

        public string Title { get; set; } = "Kodi fullscreen";

        public bool IsMinimizedResult { get; set; }

        public bool ActivationDefaultResult { get; set; } = true;

        public bool AttachDefaultResult { get; set; } = true;

        public int IsWindowCalls { get; private set; }

        public List<nint> ProcessIdLookups { get; } = [];

        public List<uint> ProcessNameLookups { get; } = [];

        public Queue<bool> ActivationResults { get; } = new();

        public Queue<bool> AttachResults { get; } = new();

        public uint CurrentThreadId { get; set; } = 44;

        public List<string> Events { get; } = [];

        public List<nint> RestoredWindows { get; } = [];

        public List<nint> ActivationTargets { get; } = [];

        public List<(uint AttachThread, uint AttachToThread, bool Attach)> AttachCalls { get; } = [];

        public nint GetForegroundWindow()
        {
            Events.Add("get-foreground");
            return ForegroundWindow;
        }

        public bool IsWindow(nint window)
        {
            IsWindowCalls++;
            return IsWindowResult;
        }

        public uint GetWindowProcessId(nint window)
        {
            ProcessIdLookups.Add(window);
            return ProcessId;
        }

        public string? GetProcessName(uint processId)
        {
            ProcessNameLookups.Add(processId);
            return ProcessName;
        }

        public string GetWindowTitle(nint window) => Title;

        public bool IsMinimized(nint window) => IsMinimizedResult;

        public void RestoreWindow(nint window) => RestoredWindows.Add(window);

        public bool SetForegroundWindow(nint window)
        {
            ActivationTargets.Add(window);
            return ActivationResults.Count == 0
                ? ActivationDefaultResult
                : ActivationResults.Dequeue();
        }

        public uint GetWindowThreadId(nint window) => 55;

        public uint GetCurrentThreadId() => CurrentThreadId;

        public bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach)
        {
            AttachCalls.Add((attachThreadId, attachToThreadId, attach));
            return AttachResults.Count == 0
                ? AttachDefaultResult
                : AttachResults.Dequeue();
        }
    }

    private sealed class FakeFocusRestoreDelay(Action? afterWait = null) : IFocusRestoreDelay
    {
        public List<TimeSpan> Waits { get; } = [];

        public void Wait(TimeSpan delay)
        {
            Waits.Add(delay);
            afterWait?.Invoke();
        }
    }
}
