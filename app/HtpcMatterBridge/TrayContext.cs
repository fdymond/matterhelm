namespace HtpcMatterBridge;

/// <summary>
/// Application lifetime anchored to a tray icon rather than a window.
/// Sprint 0 scaffold only: shows a placeholder icon (<see cref="SystemIcons.Application"/>;
/// a later story swaps in a real <c>.ico</c> asset) with a single "Exit" menu item.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;

    /// <summary>Creates the tray icon and its context menu, and logs startup.</summary>
    public TrayContext()
    {
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExitClicked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "HTPC Matter Bridge",
            ContextMenuStrip = menu,
            Visible = true,
        };

        Log.Info("TrayContext started; icon visible.");
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        Log.Info("Exit requested from tray menu; shutting down.");
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }
}
