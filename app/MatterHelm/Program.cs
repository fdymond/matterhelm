using System.Runtime.InteropServices;
using MatterHelm.Diagnostics;
using MatterHelm.Ui;

namespace MatterHelm;

/// <summary>Composition root: enforces single instance, then wires <see cref="TrayContext"/> to <see cref="BridgeHost"/> and runs the tray.</summary>
internal static partial class Program
{
    // Session-local (not "Global\") mutex is sufficient: this app is per-user,
    // per-session tray tooling, not a service shared across RDP sessions.
    private const string MutexName = "MatterHelm.SingleInstance";

    private const int AttachParentProcess = -1;

    /// <summary>Entry point.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        // S2-2 acceptance demo: run the Actions/ self-test and exit — no tray,
        // no single-instance guard (it must work beside a running instance).
        if (args.Contains("--selftest-actions"))
        {
            Environment.ExitCode = Actions.ActionsSelfTest.Run(
                includeDisplayTests: args.Contains("--selftest-actions-display"),
                includeKeySequenceTest: args.Contains("--selftest-actions-keysequence"));
            return;
        }

        // S2-3 acceptance demo: objectively prove the overlay HUD never
        // steals focus or blocks clicks. Not part of the production tray flow.
        if (args.Contains("--demo-overlay"))
        {
            // Same DPI context as production (PerMonitorV2): without this the
            // demo ran DPI-unaware, so the HUD's S4-5 DPI scaling (and the
            // volume-bar pixel sampling) would silently verify at 96 dpi only.
            ApplicationConfiguration.Initialize();
            Environment.ExitCode = Ui.OverlayHudDemo.Run();
            return;
        }

        // Design aid: export the runtime-drawn tray icons (all states, both
        // taskbar themes, several sizes + a contact sheet) for visual review.
        if (args.Contains("--export-tray-icons"))
        {
            Ui.TrayIcons.ExportPreviews();
            return;
        }

        // Design aid (S10-2): render the trademark-safe logo candidates.
        if (args.Contains("--export-logo-candidates"))
        {
            _ = Ui.TrayIcons.ExportLogoCandidates();
            return;
        }

        // S6-1 resource-hygiene probe: overlay/settings/tray churn with
        // before/after handle + GDI + private-bytes bounds, objective
        // PASS/FAIL output. Hidden; not part of the production tray flow.
        if (args.Contains("--probe-resources"))
        {
            _ = AttachConsole(AttachParentProcess);
            ApplicationConfiguration.Initialize();
            Environment.ExitCode = Diagnostics.ResourceProbe.Run();
            return;
        }

        // S2-1 acceptance demo: sidecar crash/auto-restart backoff plus the
        // wrong-token socket close, with objective PASS/FAIL output.
        if (args.Contains("--demo-sidecar-chaos"))
        {
            Environment.ExitCode = Sidecar.SidecarChaosDemo.Run();
            return;
        }

        // S2-4 acceptance demo: open the pairing window with a sample payload,
        // screenshot it, and objectively verify the QR actually rendered
        // (non-trivial pixel variance) before exiting. Not part of the
        // production tray flow.
        if (args.Contains("--demo-pairing-window"))
        {
            Environment.ExitCode = Demos.PairingWindowDemo.Run();
            return;
        }

        // S2-5 acceptance demo: full mock-sidecar E2E — stub node sidecar over
        // the real WS protocol → executor side effects (read back) → overlay →
        // acks → state frames, with objective PASS/FAIL output. Uses its own
        // temp config/storage and an ephemeral port, never %APPDATA%.
        if (args.Contains("--demo-wired"))
        {
            Environment.ExitCode = Demos.WiredDemo.Run();
            return;
        }

        // S4-3 acceptance demo: scripted settings-window walk (staged edits →
        // save → config.json round-trip asserted) plus light/dark/search
        // screenshots, all against a temp config dir. Not part of the
        // production tray flow.
        if (args.Contains("--demo-settings-window"))
        {
            Environment.ExitCode = Demos.SettingsWindowDemo.Run(dark: args.Contains("--dark"));
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns the mutex; exit silently (no dialog, no log spam).
            return;
        }

        // S7-2 rename migration: move %APPDATA%\HtpcMatterBridge to
        // %APPDATA%\MatterHelm BEFORE Config/Log touch disk — creating the new
        // root first would make the atomic Directory.Move impossible. Log
        // lines are buffered because the log directory itself depends on the
        // outcome (a locked legacy root keeps the app on the old paths).
        var migrationLog = new List<(string Level, string Message)>();
        MigrationResult migration = Migration.Run(
            AppPaths.LegacyRoot, AppPaths.DefaultRoot, (level, message) => migrationLog.Add((level, message)));
        AppPaths.Root = migration.EffectiveRoot;

        Log.Initialize();
        foreach ((string level, string message) in migrationLog)
        {
            if (level == "WARN")
            {
                Log.Warn(message);
            }
            else
            {
                Log.Info(message);
            }
        }

        Log.Info("MatterHelm starting.");

        // ADR-006 §2: JSON-lines metrics snapshots beside the app log (60 s
        // timer + final flush on exit); dotnet-counters can attach live too.
        using var metrics = new MetricsFileListener();

        ApplicationConfiguration.Initialize();

        // ADR-005 production dark mode: enabled after PairingWindow moved to
        // SystemColors (the S4-3 blocker — hard-coded white went
        // white-on-white under dark). The QR image's white quiet zone is
        // deliberately unthemed (scannability). OverlayHud draws its own
        // bitmaps and is unaffected.
        Application.SetColorMode(SystemColorMode.System);

        var trayContext = new TrayContext();

        // ADR-006 §2: the app's own log level follows config `appLogLevel`
        // live — settings saves reload the config, which re-applies it here.
        Log.MinimumLevel = Log.ParseLevel(trayContext.Config.Current.AppLogLevel);
        trayContext.Config.Changed += (_, e) => Log.MinimumLevel = Log.ParseLevel(e.NewConfig.AppLogLevel);

        using var executor = new ActionExecutorAdapter();
        using var overlay = new OverlayHud
        {
            Visible = trayContext.Config.Current.OverlayEnabled,
            Position = trayContext.Config.Current.OverlayPosition,
            Theme = trayContext.Config.Current.OverlayTheme,
            OpacityPercent = trayContext.Config.Current.OverlayOpacityPercent,
        };

        // Settings-window saves reload the config; re-apply the HUD look so
        // position/theme/opacity changes apply live, no restart needed.
        trayContext.Config.Changed += (_, e) =>
        {
            overlay.Position = e.NewConfig.OverlayPosition;
            overlay.Theme = e.NewConfig.OverlayTheme;
            overlay.OpacityPercent = e.NewConfig.OverlayOpacityPercent;
        };
        using var host = new BridgeHost(
            trayContext.Config,
            executor,
            SidecarLaunchSpec.Default(),
            overlaySink: overlay.Show);

        // TrayContext.SetState/SetPairingInfo marshal to the UI thread
        // internally, so BridgeHost's pool-thread events forward directly.
        host.StateChanged += (_, state) => trayContext.SetState(state);
        host.PairingReceived += (_, pairing) => trayContext.SetPairingInfo(pairing.QrPayload, pairing.ManualCode);

        // Start/stop happen on a worker: SetEnabled blocks for the child's
        // stop grace, and the UI thread must never wait on that.
        trayContext.EnableBridgeChanged += (_, enabled) => Task.Run(() => host.SetEnabled(enabled));

        // FactoryReset blocks on the same child-stop grace plus delete
        // retries (S3-2) — a worker thread, same reasoning as SetEnabled
        // above. The confirmation dialog already ran (TrayContext); this only
        // fires after the user said yes. Success/failure is already logged
        // and (when enabled) flashed on the overlay inside FactoryReset —
        // nothing else to marshal back to the UI here.
        trayContext.FactoryResetRequested += (_, _) => Task.Run(() => host.FactoryReset());
        trayContext.OverlayEnabledChanged += (_, enabled) => overlay.Visible = enabled;
        // S9-1/S9-4: preview with the STAGED position/theme/opacity (the
        // settings window passes its unsaved working values), then restore the
        // live-config look once the flash has faded (hold ~2.5 s + fade
        // ~0.3 s) so real commands keep the saved config's rendering until
        // the user saves.
        var previewRestore = new System.Windows.Forms.Timer { Interval = 3_200 };
        previewRestore.Tick += (_, _) =>
        {
            previewRestore.Stop();
            overlay.Position = trayContext.Config.Current.OverlayPosition;
            overlay.Theme = trayContext.Config.Current.OverlayTheme;
            overlay.OpacityPercent = trayContext.Config.Current.OverlayOpacityPercent;
        };
        trayContext.OverlayPreviewRequested += (_, staged) =>
        {
            // A preview must show even while the feature toggle is off — the
            // Visible flag only gates Show, so flip it around the one call.
            bool wasVisible = overlay.Visible;
            overlay.Position = staged.Position;
            overlay.Theme = staged.Theme;
            overlay.OpacityPercent = staged.OpacityPercent;
            overlay.Visible = true;
            overlay.Show("Overlay preview", "Settings", isError: false);
            overlay.Visible = wasVisible;
            previewRestore.Stop();
            previewRestore.Start();
        };

        // Exit is the one sanctioned synchronous stop: the sidecar must be
        // down (stdin tether, then kill) before the process goes away.
        trayContext.ExitRequested += (_, _) => host.SetEnabled(false);

        if (trayContext.Config.Current.BridgeEnabled)
        {
            Task.Run(() => host.SetEnabled(true));
        }

        Application.Run(trayContext);

        Log.Info("MatterHelm exited.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
