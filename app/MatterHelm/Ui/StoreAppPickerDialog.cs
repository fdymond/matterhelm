using MatterHelm.Actions;

namespace MatterHelm.Ui;

/// <summary>
/// Picks a Microsoft Store app for a <c>launch</c> custom command (S10-5).
///
/// <para>Why this exists: a Store app (Spotify, Media Player, …) cannot be
/// started from its <c>Program Files\WindowsApps\…</c> package path — Windows
/// denies it — and that folder is not browsable either, so "Browse…" is a
/// dead end for exactly the apps people most want to launch. Windows keeps a
/// launchable alias per Store app under <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>;
/// this dialog lists those and returns the one the user picks.</para>
/// </summary>
public sealed class StoreAppPickerDialog : Form
{
    private readonly ListBox _list;
    private readonly Button _okButton;
    private readonly IReadOnlyList<(string Name, string Path)> _apps;

    /// <summary>Builds the picker over the installed Store apps.</summary>
    /// <param name="apps">Override for demos/tests; defaults to <see cref="AppLaunch.ListStoreApps"/>.</param>
    public StoreAppPickerDialog(IReadOnlyList<(string Name, string Path)>? apps = null)
    {
        _apps = apps ?? AppLaunch.ListStoreApps();

        Text = "Choose a Store app";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(S(380), S(340));

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(S(12)),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label
        {
            Text = _apps.Count > 0
                ? "Apps installed from the Microsoft Store:"
                : "No Store apps found on this PC — use Browse… for a normal program instead.",
            AutoSize = true,
            Margin = new Padding(S(3), S(3), S(3), S(6)),
        });

        _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach ((string name, _) in _apps)
        {
            _list.Items.Add(name);
        }

        _list.SelectedIndexChanged += (_, _) => _okButton!.Enabled = _list.SelectedIndex >= 0;
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedIndex >= 0)
            {
                Accept();
            }
        };
        grid.Controls.Add(_list);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(S(3), S(8), S(3), S(3)),
        };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _okButton = new Button { Text = "OK", AutoSize = true, Enabled = false };
        _okButton.Click += (_, _) => Accept();
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_okButton);
        grid.Controls.Add(buttons);

        Controls.Add(grid);
        AcceptButton = _okButton;
        CancelButton = cancelButton;
    }

    /// <summary>Full path of the chosen app's execution alias; non-null iff the dialog closed with OK.</summary>
    public string? SelectedPath { get; private set; }

    private void Accept()
    {
        if (_list.SelectedIndex < 0)
        {
            return;
        }

        SelectedPath = _apps[_list.SelectedIndex].Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);
}
