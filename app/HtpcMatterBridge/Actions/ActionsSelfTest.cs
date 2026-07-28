using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Actions;

/// <summary>
/// Acceptance-evidence self-test for the Actions components (S2-2), run via
/// <c>--selftest-actions</c>. Exercises volume set/read-back, mute toggling,
/// change-event observation and media-key injection, restoring the original
/// volume/mute even on failure. Display off/wake is destructive to the session
/// and therefore opt-in via <c>--selftest-actions-display</c>; sleep is never
/// exercised. Results go to stdout (parent console, if any) and to
/// <c>selftest-actions-results.txt</c> next to the exe.
/// </summary>
internal static partial class ActionsSelfTest
{
    private const int AttachParentProcess = -1;

    /// <summary>Runs the self-test. Returns the process exit code: 0 iff every check passed.</summary>
    public static int Run(bool includeDisplayTests)
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
}
