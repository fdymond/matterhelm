namespace HtpcMatterBridge;

/// <summary>Composition root: enforces single instance, then runs the tray context.</summary>
internal static class Program
{
    // Session-local (not "Global\") mutex is sufficient: this app is per-user,
    // per-session tray tooling, not a service shared across RDP sessions.
    private const string MutexName = "HtpcMatterBridge.SingleInstance";

    /// <summary>Entry point.</summary>
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns the mutex; exit silently (no dialog, no log spam).
            return;
        }

        Log.Initialize();
        Log.Info("HtpcMatterBridge starting.");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());

        Log.Info("HtpcMatterBridge exited.");
    }
}
