using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class MouseMoverTests
{
    private static readonly Rectangle MultiMonitorBounds = new(-1920, -200, 3520, 1200);

    [Theory]
    [InlineData(MouseTarget.BottomRight, 1599, 999)]
    [InlineData(MouseTarget.BottomLeft, -1920, 999)]
    [InlineData(MouseTarget.TopRight, 1599, -200)]
    [InlineData(MouseTarget.TopLeft, -1920, -200)]
    [InlineData(MouseTarget.Center, -160, 400)]
    public void PresetsResolveAgainstTheWholeMultiMonitorVirtualDesktop(
        MouseTarget target,
        int expectedX,
        int expectedY)
    {
        Point result = MouseTargetResolver.Resolve(
            new MouseMoveActionConfig { Target = target },
            MultiMonitorBounds);

        Assert.Equal(new Point(expectedX, expectedY), result);
    }

    [Theory]
    [InlineData(-99999, -99999, -1920, -200)]
    [InlineData(99999, 99999, 1599, 999)]
    [InlineData(42, 73, 42, 73)]
    public void CustomCoordinatesAreClampedIntoVirtualDesktop(
        int x,
        int y,
        int expectedX,
        int expectedY)
    {
        Point result = MouseTargetResolver.Resolve(
            new MouseMoveActionConfig { Target = MouseTarget.Custom, X = x, Y = y },
            MultiMonitorBounds);

        Assert.Equal(new Point(expectedX, expectedY), result);
    }

    [Fact]
    public void InvalidBoundsAndIncompleteCustomCoordinatesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MouseTargetResolver.Resolve(
            new MouseMoveActionConfig { Target = MouseTarget.BottomRight },
            Rectangle.Empty));
        Assert.Throws<ArgumentException>(() => MouseTargetResolver.Resolve(
            new MouseMoveActionConfig { Target = MouseTarget.Custom, X = 10 },
            MultiMonitorBounds));
    }

    [Fact]
    public void ExplicitOnCapturesAndMovesThenExplicitOffRestores()
    {
        var pointer = new FakePointer(MultiMonitorBounds, new Point(100, 200));
        var mover = new MouseMover(pointer);
        var action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight };

        Assert.True(mover.Move("park-mouse", action));
        Assert.Equal(new Point(1599, 999), pointer.Position);

        Assert.True(mover.Restore("park-mouse"));
        Assert.Equal(new Point(100, 200), pointer.Position);
        Assert.Equal(2, pointer.SetCalls);
    }

    [Fact]
    public void ExplicitOnAlwaysReplacesThePositionRestoredByTheNextOff()
    {
        var pointer = new FakePointer(MultiMonitorBounds, new Point(100, 200));
        var mover = new MouseMover(pointer);
        var action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight };

        Assert.True(mover.Move("park-mouse", action));
        pointer.Position = new Point(-500, 50);
        Assert.True(mover.Move("park-mouse", action));
        Assert.True(mover.Restore("park-mouse"));

        Assert.Equal(new Point(-500, 50), pointer.Position);
    }

    [Fact]
    public void OffBeforeOnIsANoOpSuccess()
    {
        var pointer = new FakePointer(MultiMonitorBounds, new Point(7, 9));
        var mover = new MouseMover(pointer);

        Assert.True(mover.Restore("park-mouse"));

        Assert.Equal(new Point(7, 9), pointer.Position);
        Assert.Equal(0, pointer.SetCalls);
    }

    [Fact]
    public void ReconcileDropsCapturesForDeletedDisabledOrRetypedCommands()
    {
        var pointer = new FakePointer(MultiMonitorBounds, new Point(7, 9));
        var mover = new MouseMover(pointer);
        var action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight };

        Assert.True(mover.Move("keep", action));
        pointer.Position = new Point(50, 60);
        Assert.True(mover.Move("remove", action));
        mover.Reconcile(new HashSet<string>(["keep"], StringComparer.Ordinal));
        pointer.Position = new Point(70, 80);

        Assert.True(mover.Restore("remove"));
        Assert.Equal(new Point(70, 80), pointer.Position);
        Assert.True(mover.Restore("keep"));
        Assert.Equal(new Point(7, 9), pointer.Position);
    }

    [Fact]
    public void FailedCaptureOrMoveDoesNotCreateRestoreState()
    {
        var pointer = new FakePointer(MultiMonitorBounds, new Point(7, 9)) { GetResult = false };
        var mover = new MouseMover(pointer);
        var action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight };

        Assert.False(mover.Move("park-mouse", action));
        pointer.GetResult = true;
        pointer.SetResult = false;
        Assert.False(mover.Move("park-mouse", action));
        pointer.SetResult = true;
        Assert.True(mover.Restore("park-mouse"));

        Assert.Equal(new Point(7, 9), pointer.Position);
    }

    [Fact]
    public void FailedRestoreKeepsCapturedPositionForRetry()
    {
        var pointer = new FakePointer(MultiMonitorBounds, new Point(7, 9));
        var mover = new MouseMover(pointer);
        var action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight };
        Assert.True(mover.Move("park-mouse", action));
        pointer.SetResult = false;

        Assert.False(mover.Restore("park-mouse"));
        pointer.SetResult = true;
        Assert.True(mover.Restore("park-mouse"));

        Assert.Equal(new Point(7, 9), pointer.Position);
    }

    private sealed class FakePointer(Rectangle virtualScreen, Point position) : IMousePointer
    {
        public Rectangle VirtualScreen { get; } = virtualScreen;

        internal Point Position { get; set; } = position;

        internal int SetCalls { get; private set; }

        internal bool GetResult { get; set; } = true;

        internal bool SetResult { get; set; } = true;

        public bool TryGetPosition(out Point current)
        {
            current = Position;
            return GetResult;
        }

        public bool TrySetPosition(Point target)
        {
            SetCalls++;
            if (SetResult)
            {
                Position = target;
            }

            return SetResult;
        }
    }
}
