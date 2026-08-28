using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>
/// Acceptance-evidence self-test for the Actions components (S2-2), run via
/// <c>--selftest-actions</c>. Exercises volume set/read-back, mute toggling,
/// change-event observation and media-key injection, restoring the original
/// volume/mute even on failure. Display off/wake is destructive to the session
/// and therefore opt-in via <c>--selftest-actions-display</c>. The S10-24
/// key-sequence listener is likewise opt-in via
/// <c>--selftest-actions-keysequence</c>; it observes the neutral
/// <c>Ctrl+Shift+F13</c> chord through RegisterHotKey and WH_KEYBOARD_LL.
/// Sleep is never exercised. Results go to stdout (parent console, if any)
/// and to <c>selftest-actions-results.txt</c> next to the exe.
/// </summary>
internal static unsafe partial class ActionsSelfTest
{
    private const int AttachParentProcess = -1;
    private const int WhKeyboardLl = 13;
    private const int WmHotKey = 0x0312;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlkhfUp = 0x00000080;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint PmNoRemove = 0x0000;
    private const uint PmRemove = 0x0001;
    private const uint VkControl = 0x11;
    private const uint VkShift = 0x10;
    private const uint VkF13 = 0x7C;
    private const int KeySequenceHotKeyId = 0x4D48;

    // WH_KEYBOARD_LL invokes its callback on this self-test's message-loop
    // thread, so this single active capture needs no cross-thread locking.
    private static List<KeyboardHookEvent>? _activeHookEvents;

    /// <summary>Runs the self-test. Returns the process exit code: 0 iff every check passed.</summary>
    public static int Run(bool includeDisplayTests, bool includeKeySequenceTest = false)
    {
        _ = AttachConsole(AttachParentProcess); // WinExe has no console; borrow the parent's if present.
        List<string> lines = [];
        bool allPassed = true;

        void Emit(string line)
        {
            lines.Add(line);
            Console.WriteLine(line);
        }

        void Check(bool pass, string what)
        {
            allPassed &= pass;
            Emit($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        Emit($"S2-2 Actions self-test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            using var executor = new ActionExecutor();
            SystemVolume volume = executor.Volume;

            int originalVolume = volume.GetVolumePercent();
            bool originalMuted = volume.GetMuted();
            Emit($"Original state: volume {originalVolume} %, muted {originalMuted}");

            int eventCount = 0;
            VolumeState lastEvent = default;
            volume.VolumeChanged += (_, state) =>
            {
                lastEvent = state;
                Interlocked.Increment(ref eventCount);
            };

            try
            {
                // 1) Volume set + read-back (target guaranteed != current).
                int target = originalVolume == 37 ? 42 : 37;
                bool setOk = executor.Execute("setVolume", target);
                int readBack = volume.GetVolumePercent();
                Check(setOk && readBack == target, $"setVolume {target} → read-back {readBack}");

                // 2) Mute toggle both ways, relative to the original state so each
                //    transition is a real change (CoreAudio only notifies on change).
                bool muteOk = executor.Execute("setMuted", !originalMuted);
                Check(muteOk && volume.GetMuted() == !originalMuted, $"setMuted {!originalMuted} → read-back {volume.GetMuted()}");
                bool unmuteOk = executor.Execute("setMuted", originalMuted);
                Check(unmuteOk && volume.GetMuted() == originalMuted, $"setMuted {originalMuted} → read-back {volume.GetMuted()}");

                // 3) The three changes above must each have raised VolumeChanged
                //    (delivered asynchronously on an audio-service thread).
                int observed = WaitForEvents(() => Volatile.Read(ref eventCount), minimum: 3);
                Check(
                    observed >= 3,
                    $"volume-change events observed: {observed} (expected >= 3; last event: {lastEvent.VolumePercent} %, muted {lastEvent.Muted})");

                // 4) Media key injection — SendInput result only; media state is not
                //    asserted because no player is guaranteed to be running.
                Check(executor.Execute("playPause"), "playPause → SendInput injected key-down/key-up");

                // 5) Key-sequence injection (S7-1) — types into the focused
                //    window, so opt-in only; F15 has no default Windows binding.
                if (includeKeySequenceTest)
                {
                    RunKeySequenceHarness(executor, Emit, Check);
                }
                else
                {
                    Emit("Key sequence: SKIPPED (run with --selftest-actions-keysequence to include)");
                }

                if (includeDisplayTests)
                {
                    Check(executor.Execute("powerOff"), "powerOff → SC_MONITORPOWER displays off");
                    Thread.Sleep(3000);
                    Check(executor.Execute("powerOn"), "powerOn → wake displays via mouse nudge");
                }
                else
                {
                    Emit("Display off/wake: SKIPPED (run with --selftest-actions-display to include)");
                }

                Emit("Sleep(): SKIPPED always (would suspend the machine mid-test)");
            }
            finally
            {
                volume.SetVolumePercent(originalVolume);
                volume.SetMuted(originalMuted);
                Emit($"Restored state: volume {volume.GetVolumePercent()} %, muted {volume.GetMuted()}");
            }
        }
        catch (Exception ex)
        {
            allPassed = false;
            Emit($"FAIL  unhandled: {ex}");
        }

        Emit(allPassed ? "RESULT: PASS" : "RESULT: FAIL");
        WriteResultsFile(lines);
        return allPassed ? 0 : 1;
    }

    /// <summary>Polls <paramref name="count"/> up to 2 s until it reaches <paramref name="minimum"/>.</summary>
    private static int WaitForEvents(Func<int> count, int minimum)
    {
        for (int elapsedMs = 0; elapsedMs < 2000 && count() < minimum; elapsedMs += 50)
        {
            Thread.Sleep(50);
        }

        return count();
    }

    private static void RunKeySequenceHarness(
        ActionExecutor executor,
        Action<string> emit,
        Action<bool, string> check)
    {
        _ = PeekMessage(out _, 0, 0, 0, PmNoRemove); // Ensure this thread owns a message queue.
        _activeHookEvents = [];
        nint hook = SetWindowsHookEx(
            WhKeyboardLl,
            &LowLevelKeyboardHook,
            GetModuleHandle(null),
            0);
        if (hook == 0)
        {
            check(false, $"Ctrl+Shift+F13 listener: SetWindowsHookEx failed ({Marshal.GetLastWin32Error()})");
            _activeHookEvents = null;
            return;
        }

        bool hotKeyRegistered = RegisterHotKey(
            0,
            KeySequenceHotKeyId,
            ModControl | ModShift | ModNoRepeat,
            VkF13);
        if (!hotKeyRegistered)
        {
            check(false, $"Ctrl+Shift+F13 listener: RegisterHotKey failed ({Marshal.GetLastWin32Error()})");
            _ = UnhookWindowsHookEx(hook);
            _activeHookEvents = null;
            return;
        }

        int hotKeyMessages = 0;
        try
        {
            bool parsed = KeyChord.TryParse("Ctrl+Shift+F13", out ParsedKeyChord? chord, out string? error);
            if (parsed && chord is not null)
            {
                foreach (KeyChordEvent entry in KeyChord.BuildEvents(chord))
                {
                    emit(
                        $"INPUT direction={(entry.KeyUp ? "up" : "down")} "
                        + $"vk=0x{entry.VirtualKey:X2} scan=0x{entry.ScanCode:X2} "
                        + $"extended={entry.Extended}");
                }
            }

            bool injected = parsed && chord is not null && executor.Execute("keySequence", chord);
            check(injected, parsed
                ? "keySequence Ctrl+Shift+F13: SendInput accepted the chord"
                : $"keySequence Ctrl+Shift+F13: parse failed: {error}");

            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 1000)
            {
                while (PeekMessage(out NativeMessage message, 0, WmHotKey, WmHotKey, PmRemove))
                {
                    if (message.WParam == KeySequenceHotKeyId)
                    {
                        hotKeyMessages++;
                    }
                }

                if ((_activeHookEvents?.Count ?? 0) >= 6 && hotKeyMessages > 0)
                {
                    break;
                }

                Thread.Sleep(10);
            }

            IReadOnlyList<KeyboardHookEvent> hookEvents = _activeHookEvents ?? [];
            foreach (KeyboardHookEvent observed in hookEvents)
            {
                emit(
                    $"HOOK direction={(observed.KeyUp ? "up" : "down")} "
                    + $"vk=0x{observed.VirtualKey:X2} scan=0x{observed.ScanCode:X2} "
                    + $"flags=0x{observed.Flags:X2} injected={observed.Injected}");
            }

            if (hotKeyMessages > 0)
            {
                emit($"HOTKEY chord=Ctrl+Shift+F13 messages={hotKeyMessages}");
            }

            int injectedEvents = hookEvents.Count(observed => observed.Injected);
            int nonzeroScans = hookEvents.Count(observed => observed.ScanCode != 0);
            emit(
                $"KEYSEQUENCE SUMMARY hookEvents={hookEvents.Count} injectedEvents={injectedEvents} "
                + $"nonzeroScans={nonzeroScans} hotKeyMessages={hotKeyMessages}");

            // The hook reports SIDED modifier VKs (VK_LCONTROL 0xA2 / VK_LSHIFT 0xA0)
            // even though the chord injects the generic VK_CONTROL/VK_SHIFT — Windows
            // normalizes injected generic modifiers to their left-hand variants. Accept
            // generic and both sided forms per position.
            uint[][] expectedVirtualKeys =
            [
                [VkControl, 0xA2, 0xA3],
                [VkShift, 0xA0, 0xA1],
                [VkF13],
                [VkF13],
                [VkShift, 0xA0, 0xA1],
                [VkControl, 0xA2, 0xA3],
            ];
            bool[] expectedKeyUps = [false, false, false, true, true, true];
            bool hookSequenceMatches = hookEvents.Count == expectedVirtualKeys.Length
                && hookEvents.Select((observed, index) => expectedVirtualKeys[index].Contains(observed.VirtualKey)).All(matches => matches)
                && hookEvents.Select(observed => observed.KeyUp).SequenceEqual(expectedKeyUps)
                && hookEvents.All(observed => observed.Injected);
            check(hookSequenceMatches, "WH_KEYBOARD_LL observed six injected events in chord order");
            check(nonzeroScans == expectedVirtualKeys.Length, "WH_KEYBOARD_LL observed a nonzero scan code on every event");
            check(hotKeyMessages > 0, "RegisterHotKey observed Ctrl+Shift+F13");
        }
        finally
        {
            _ = UnregisterHotKey(0, KeySequenceHotKeyId);
            _ = UnhookWindowsHookEx(hook);
            _activeHookEvents = null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint LowLevelKeyboardHook(int code, nuint message, nint data)
    {
        if (code >= 0 && _activeHookEvents is not null)
        {
            LowLevelKeyboardInput input = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
            _activeHookEvents.Add(new KeyboardHookEvent(
                input.VirtualKey,
                input.ScanCode,
                input.Flags,
                (input.Flags & LlkhfInjected) != 0,
                (input.Flags & LlkhfUp) != 0));
        }

        return CallNextHookEx(0, code, message, data);
    }

    private static void WriteResultsFile(IReadOnlyCollection<string> lines)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "selftest-actions-results.txt");
            File.WriteAllLines(path, lines);
            Console.WriteLine($"Results written to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write results file: {ex.Message}");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookEx(
        int hookId,
        delegate* unmanaged<int, nuint, nint, nint> callback,
        nint moduleHandle,
        uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct KeyboardHookEvent(
        uint VirtualKey,
        uint ScanCode,
        uint Flags,
        bool Injected,
        bool KeyUp);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
}
