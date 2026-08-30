using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>Executor payload for one retained mouse-move custom command.</summary>
internal sealed record MouseMoveRequest(string CommandKey, MouseMoveActionConfig Action, bool On);

/// <summary>Executor payload for one stateless absolute mouse move inside a sequence.</summary>
internal sealed record MouseMoveOnceRequest(MouseMoveActionConfig Action);

/// <summary>Testable pointer and virtual-screen boundary.</summary>
internal interface IMousePointer
{
    Rectangle VirtualScreen { get; }

    bool TryGetPosition(out Point position);

    bool TrySetPosition(Point position);
}

/// <summary>Pure virtual-desktop target computation.</summary>
internal static class MouseTargetResolver
{
    internal static Point Resolve(MouseMoveActionConfig action, Rectangle virtualScreen)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualScreen), "Virtual screen must have positive dimensions.");
        }

        int right = virtualScreen.Right - 1;
        int bottom = virtualScreen.Bottom - 1;
        return action.Target switch
        {
            MouseTarget.BottomRight => new Point(right, bottom),
            MouseTarget.BottomLeft => new Point(virtualScreen.Left, bottom),
            MouseTarget.TopRight => new Point(right, virtualScreen.Top),
            MouseTarget.TopLeft => new Point(virtualScreen.Left, virtualScreen.Top),
            MouseTarget.Center => new Point(
                virtualScreen.Left + (virtualScreen.Width / 2),
                virtualScreen.Top + (virtualScreen.Height / 2)),
            MouseTarget.Custom when action.X is int x && action.Y is int y => new Point(
                Math.Clamp(x, virtualScreen.Left, right),
                Math.Clamp(y, virtualScreen.Top, bottom)),
            MouseTarget.Custom => throw new ArgumentException("Custom mouse target requires X and Y coordinates.", nameof(action)),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Target, null),
        };
    }
}

/// <summary>
/// Retained-switch pointer movement. Each command key owns one in-memory
/// captured position: ON captures/moves, OFF restores/removes it.
/// </summary>
internal sealed partial class MouseMover
{
    private readonly IMousePointer _pointer;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Point> _capturedByCommand = [];

    internal MouseMover()
        : this(new WindowsMousePointer())
    {
    }

    internal MouseMover(IMousePointer pointer) => _pointer = pointer;

    internal bool Move(string commandKey, MouseMoveActionConfig action)
    {
        lock (_gate)
        {
            return MoveCore(commandKey, action);
        }
    }

    internal bool Restore(string commandKey)
    {
        lock (_gate)
        {
            return RestoreCore(commandKey);
        }
    }

    /// <summary>
    /// Moves once without reading or changing retained capture state. Sequence
    /// steps use this path so a macro cannot change where another command's
    /// next OFF edge restores the pointer.
    /// </summary>
    internal bool MoveOnce(MouseMoveActionConfig action)
    {
        Point target = MouseTargetResolver.Resolve(action, _pointer.VirtualScreen);
        return _pointer.TrySetPosition(target);
    }

    internal void Reconcile(IReadOnlySet<string> activeCommandKeys)
    {
        lock (_gate)
        {
            foreach (string key in _capturedByCommand.Keys
                         .Where(key => !activeCommandKeys.Contains(key))
                         .ToArray())
            {
                _capturedByCommand.Remove(key);
            }
        }
    }

    private bool MoveCore(string commandKey, MouseMoveActionConfig action)
    {
        if (!_pointer.TryGetPosition(out Point captured))
        {
            return false;
        }

        Point target = MouseTargetResolver.Resolve(action, _pointer.VirtualScreen);
        if (!_pointer.TrySetPosition(target))
        {
            return false;
        }

        _capturedByCommand[commandKey] = captured;
        return true;
    }

    private bool RestoreCore(string commandKey)
    {
        if (!_capturedByCommand.TryGetValue(commandKey, out Point captured))
        {
            return true;
        }

        if (!_pointer.TrySetPosition(captured))
        {
            return false;
        }

        _capturedByCommand.Remove(commandKey);
        return true;
    }

    private sealed partial class WindowsMousePointer : IMousePointer
    {
        public Rectangle VirtualScreen => SystemInformation.VirtualScreen;

        public bool TryGetPosition(out Point position)
        {
            bool ok = GetCursorPos(out NativePoint point);
            position = new Point(point.X, point.Y);
            if (!ok)
            {
                Log.Warn($"mouse move: GetCursorPos failed (Win32 {Marshal.GetLastPInvokeError()}).");
            }

            return ok;
        }

        public bool TrySetPosition(Point position)
        {
            bool ok = SetCursorPos(position.X, position.Y);
            if (!ok)
            {
                Log.Warn($"mouse move: SetCursorPos failed (Win32 {Marshal.GetLastPInvokeError()}).");
            }

            return ok;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out NativePoint point);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetCursorPos(int x, int y);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int X;
            internal int Y;
        }
    }
}
