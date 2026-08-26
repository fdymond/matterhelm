namespace MatterHelm.Ui;

/// <summary>
/// First-run onboarding (S10-7). Before this, a fresh install put an icon in
/// the tray and said nothing: the user had to guess that the flow is
/// right-click → Enable bridge → Pair, and that Google needs a one-time
/// (free) Developer Console registration first — the single most common
/// reason pairing fails outright.
///
/// <para>Shown once, only on an install that has never been paired (see
/// <c>Program</c>), and dismissible. It states the prerequisite, links
/// straight to the console, and ends with the one button that actually starts
/// the flow — so "what do I do now?" is answered on screen instead of in a
/// document the user has not opened.</para>
/// </summary>
public sealed class WelcomeWindow : Form
{
    /// <summary>Builds the window. <see cref="StartPairingRequested"/> fires when the user clicks the primary button.</summary>
    public WelcomeWindow(int vendorId = 0xFFF1, int productId = 0x8000)
    {
        Text = "Welcome to MatterHelm";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Window;

        int contentWidth = S(430);

        var layout = new TableLayoutPanel
        {
            Location = new Point(0, 0),
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = SP(28, 24, 28, 20),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "Let's add this PC to Google Home",
            AutoSize = true,
            Font = new Font("Segoe UI", 14f),
            Margin = SP(0, 0, 0, 6),
        });

        layout.Controls.Add(new Label
        {
            Text = "Once it's set up you can say \"Hey Google, set HTPC volume to 40 %\", "
                + "tap the devices in the Home app, or use them in routines.",
            AutoSize = true,
            MaximumSize = new Size(contentWidth, 0),
            ForeColor = SystemColors.GrayText,
            Margin = SP(0, 0, 0, 18),
        });

        layout.Controls.Add(StepBlock(
            "1.  Register once with Google  (free, about 5 minutes)",
            "MatterHelm isn't a commercially certified Matter product, so Google only "
                + "pairs it if your own account has registered its IDs. In the Developer "
                + $"Console: create a project → Add integration → Matter → Vendor ID 0x{vendorId:X4}, "
                + $"Product ID 0x{productId:X4}. Use the same Google account as your Home app.",
            contentWidth));

        var consoleLink = new LinkLabel
        {
            Text = "Open the Google Home Developer Console",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f),
            Margin = SP(18, 2, 0, 16),
        };
        consoleLink.LinkClicked += (_, _) => OpenUrl("https://console.home.google.com/");
        layout.Controls.Add(consoleLink);

        layout.Controls.Add(StepBlock(
            "2.  Name your devices  (optional, but do it now)",
            "Tray icon → Settings → Devices. These names are what you'll say out loud. "
                + "If more than one PC will run MatterHelm, give each one distinct names.",
            contentWidth));

        layout.Controls.Add(StepBlock(
            "3.  Turn the bridge on and pair",
            "The button below does both: it starts the bridge and opens the pairing "
                + "code. Windows will ask to allow it on Private networks — say yes, or "
                + "your hub can't find this PC.",
            contentWidth));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = SP(0, 14, 0, 0),
            // Right-anchored so the primary action lands where the eye ends up
            // after reading, at the bottom-right of the content column.
            Anchor = AnchorStyles.Right,
        };

        var laterButton = new Button
        {
            Text = "I'll do it later",
            AutoSize = true,
            Padding = SP(10, 3, 10, 3),
            DialogResult = DialogResult.Cancel,
        };
        var startButton = new Button
        {
            Text = "Enable bridge && pair",
            AutoSize = true,
            Padding = SP(10, 3, 10, 3),
        };
        startButton.Click += (_, _) => StartPairing();
        buttons.Controls.Add(startButton);
        buttons.Controls.Add(laterButton);
        layout.Controls.Add(buttons);

        layout.Controls.Add(new Label
        {
            Text = "You can reopen this any time: right-click the tray icon → "
                + "Setup guide. The user guide has the same steps in more detail.",
            AutoSize = true,
            MaximumSize = new Size(contentWidth, 0),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SystemColors.GrayText,
            Margin = SP(0, 12, 0, 0),
        });

        Controls.Add(layout);
        AcceptButton = startButton;
        CancelButton = laterButton;
    }

    /// <summary>The user chose to start now: the caller enables the bridge and opens the pairing window.</summary>
    public event EventHandler? StartPairingRequested;

    /// <summary>Takes the primary action (raise <see cref="StartPairingRequested"/>, then close), as if the user clicked the button. Exposed for the S10-7 demo.</summary>
    public void StartPairing()
    {
        StartPairingRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private TableLayoutPanel StepBlock(string heading, string body, int width)
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = SP(0, 0, 0, 12),
        };
        stack.Controls.Add(new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Margin = SP(0, 0, 0, 2),
        });
        stack.Controls.Add(new Label
        {
            Text = body,
            AutoSize = true,
            MaximumSize = new Size(width - S(18), 0),
            Margin = SP(18, 0, 0, 0),
        });

        return stack;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Welcome window: could not open '{url}': {ex.Message}");
        }
    }

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>Logical (96-dpi) padding → device padding.</summary>
    private Padding SP(int left, int top, int right, int bottom) => new(S(left), S(top), S(right), S(bottom));
}
