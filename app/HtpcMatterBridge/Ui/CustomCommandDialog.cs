using HtpcMatterBridge.Actions;

namespace HtpcMatterBridge.Ui;

/// <summary>
/// Modal add/edit dialog for one custom command (ADR-004 §4, extended by
/// S7-1). The key is locked when editing an existing command — it is the
/// Matter endpoint id and wire identifier, so renames must never change it
/// (stable identity, ADR-004 §1). The action editor switches between the
/// media-key combo, the launch path/args rows, and the key-sequence row with
/// the action-type combo. Validation is inline (via
/// <see cref="SettingsViewModel"/>'s pure rules) and OK stays disabled while
/// anything is invalid.
///
/// <para><b>Key-sequence capture UX</b> (S7-1): the sequence row pairs a
/// TextBox (type the chord in <see cref="KeyChord"/> grammar, validated
/// inline) with a <c>Capture</c> toggle-button. Arming the toggle (mouse
/// click, or Tab to it and press Space — fully keyboard-accessible) routes
/// the dialog's next keystroke through <see cref="ProcessCmdKey"/>: pure
/// modifier presses (Ctrl/Shift/Alt/Win alone) are swallowed until a
/// non-modifier arrives, Esc cancels capture, and the first captured chord is
/// written into the TextBox in canonical form
/// (<see cref="ParsedKeyChord.Canonical"/>) before the toggle disarms.
/// While armed every key — including Enter, Esc, and Space — is capture
/// input, never dialog navigation. Ctrl/Alt/Shift combine from the live
/// modifier state; Win+ chords cannot be captured (the OS intercepts most of
/// them) — type those into the TextBox instead. A non-modifier key outside
/// the <see cref="KeyChord"/> table is ignored and capture stays armed.</para>
/// </summary>
public sealed class CustomCommandDialog : Form
{
    private const int MediaKeyActionIndex = 0;
    private const int LaunchActionIndex = 1;
    private const int KeySequenceActionIndex = 2;

    private readonly SettingsViewModel _vm;
    private readonly string? _originalKey;

    private readonly TextBox _keyBox;
    private readonly TextBox _nameBox;
    private readonly CheckBox _enabledCheck;
    private readonly ComboBox _actionTypeCombo;
    private readonly ComboBox _mediaKeyCombo;
    private readonly TableLayoutPanel _mediaKeyRow;
    private readonly TextBox _pathBox;
    private readonly TextBox _argsBox;
    private readonly TableLayoutPanel _launchRows;
    private readonly TextBox _sequenceBox;
    private readonly CheckBox _captureToggle;
    private readonly TableLayoutPanel _sequenceRow;
    private readonly Label _errorLabel;
    private readonly Button _okButton;

    /// <summary>Builds the dialog; <paramref name="existing"/> null = add, non-null = edit (key locked).</summary>
    public CustomCommandDialog(SettingsViewModel viewModel, CustomCommandConfig? existing)
    {
        _vm = viewModel;
        _originalKey = existing?.Key;

        Text = existing is null ? "Add custom command" : "Edit custom command";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        // Sizes go through S() (LogicalToDeviceUnits) — WinForms auto-scaling
        // is a no-op for runtime-built forms, see SettingsWindow's DPI note.
        ClientSize = new Size(S(440), S(320));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(12)),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _keyBox = new TextBox { Width = S(240), Text = existing?.Key ?? "", ReadOnly = existing is not null };
        _keyBox.TextChanged += (_, _) => Revalidate();
        AddRow(grid, existing is null ? "Key (kebab-case)" : "Key (locked)", _keyBox);

        _nameBox = new TextBox { Width = S(240), Text = existing?.Name ?? "" };
        _nameBox.TextChanged += (_, _) => Revalidate();
        AddRow(grid, "Name", _nameBox);

        _enabledCheck = new CheckBox { AutoSize = true, Checked = existing?.Enabled ?? true };
        AddRow(grid, "Enabled", _enabledCheck);

        _actionTypeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240) };
        _actionTypeCombo.Items.Add("Press a media key");
        _actionTypeCombo.Items.Add("Launch a program");
        _actionTypeCombo.Items.Add("Key sequence");
        _actionTypeCombo.SelectedIndexChanged += (_, _) => OnActionTypeChanged();
        AddRow(grid, "Action", _actionTypeCombo);

        _mediaKeyCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240) };
        foreach ((_, string label) in SettingsViewModel.MediaKeyChoices)
        {
            _mediaKeyCombo.Items.Add(label);
        }

        _mediaKeyCombo.SelectedIndex = 0;
        _mediaKeyRow = SubGrid(grid);
        AddRow(_mediaKeyRow, "Key to press", _mediaKeyCombo);

        _pathBox = new TextBox { Width = S(240) };
        _pathBox.TextChanged += (_, _) => Revalidate();
        var browseButton = new Button { Text = "Browse…", AutoSize = true };
        browseButton.Click += (_, _) => BrowseForProgram();
        var pathRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, // keep Browse… beside the path box; the dialog auto-sizes
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        pathRow.Controls.Add(_pathBox);
        pathRow.Controls.Add(browseButton);
        _argsBox = new TextBox { Width = S(240) };
        _launchRows = SubGrid(grid);
        AddRow(_launchRows, "Program", pathRow);
        AddRow(_launchRows, "Arguments", _argsBox);

        // S7-1 key-sequence editor: typed chord + Capture toggle (see the
        // class doc for the capture UX contract).
        _sequenceBox = new TextBox { Width = S(160) };
        _sequenceBox.TextChanged += (_, _) => Revalidate();
        _captureToggle = new CheckBox { Appearance = Appearance.Button, Text = "Capture", AutoSize = true };
        _captureToggle.CheckedChanged += (_, _) =>
            _captureToggle.Text = _captureToggle.Checked ? "Press keys…" : "Capture";
        var sequenceEditor = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        sequenceEditor.Controls.Add(_sequenceBox);
        sequenceEditor.Controls.Add(_captureToggle);
        _sequenceRow = SubGrid(grid);
        AddRow(_sequenceRow, "Sequence (e.g. Ctrl+Shift+V)", sequenceEditor);

        _errorLabel = new Label
        {
            AutoSize = true,
            ForeColor = Application.IsDarkModeEnabled ? Color.FromArgb(255, 140, 143) : Color.FromArgb(197, 44, 44),
            Margin = new Padding(S(3), S(6), S(3), S(3)),
        };
        grid.Controls.Add(_errorLabel);
        grid.SetColumnSpan(_errorLabel, 2);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(S(3), S(8), S(3), S(3)),
        };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _okButton = new Button { Text = "OK", AutoSize = true };
        _okButton.Click += (_, _) => OnOk();
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_okButton);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);

        Controls.Add(grid);
        AcceptButton = _okButton;
        CancelButton = cancelButton;

        switch (existing?.Action)
        {
            case LaunchActionConfig launch:
                _actionTypeCombo.SelectedIndex = LaunchActionIndex;
                _pathBox.Text = launch.Path;
                _argsBox.Text = launch.Args;
                break;
            case KeySequenceActionConfig keySequence:
                _actionTypeCombo.SelectedIndex = KeySequenceActionIndex;
                _sequenceBox.Text = keySequence.Sequence;
                break;
            default:
                _actionTypeCombo.SelectedIndex = MediaKeyActionIndex;
                if (existing?.Action is MediaKeyActionConfig mediaKey)
                {
                    int index = SettingsViewModel.MediaKeyChoices.ToList().FindIndex(c => c.Key == mediaKey.KeyName);
                    _mediaKeyCombo.SelectedIndex = Math.Max(0, index);
                }

                break;
        }

        OnActionTypeChanged();
    }

    /// <summary>The validated command; non-null iff the dialog closed with OK.</summary>
    public CustomCommandConfig? Result { get; private set; }

    private TableLayoutPanel SubGrid(TableLayoutPanel parent)
    {
        var sub = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(S(16), 0, 0, 0),
        };
        sub.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        parent.Controls.Add(sub);
        parent.SetColumnSpan(sub, 2);
        return sub;
    }

    private void AddRow(TableLayoutPanel grid, string label, Control editor)
    {
        grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(S(3), S(6), S(8), S(3)),
        });
        editor.Margin = new Padding(S(3));
        grid.Controls.Add(editor);
    }

    private bool IsLaunchAction => _actionTypeCombo.SelectedIndex == LaunchActionIndex;

    private bool IsKeySequenceAction => _actionTypeCombo.SelectedIndex == KeySequenceActionIndex;

    private void OnActionTypeChanged()
    {
        _mediaKeyRow.Visible = !IsLaunchAction && !IsKeySequenceAction;
        _launchRows.Visible = IsLaunchAction;
        _sequenceRow.Visible = IsKeySequenceAction;
        _captureToggle.Checked = false; // leaving the row always disarms capture
        Revalidate();
    }

    private void Revalidate()
    {
        string? error =
            _vm.ValidateCustomCommandKey(_keyBox.Text.Trim(), _originalKey)
            ?? SettingsViewModel.ValidateCustomCommandName(_nameBox.Text.Trim())
            ?? (IsLaunchAction ? _vm.ValidateLaunchPath(_pathBox.Text.Trim()) : null)
            ?? (IsKeySequenceAction ? SettingsViewModel.ValidateKeySequence(_sequenceBox.Text.Trim()) : null);
        _errorLabel.Text = error ?? "";
        _okButton.Enabled = error is null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Chord capture (see the class doc): while the Capture toggle is armed
    /// this intercepts every key message before normal dialog processing —
    /// Esc disarms without writing, pure modifier presses wait for a
    /// non-modifier, and the first mappable chord lands in the sequence box
    /// canonically and disarms the toggle.
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_captureToggle.Checked || !IsKeySequenceAction)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode == Keys.Escape)
        {
            _captureToggle.Checked = false;
            return true;
        }

        if (keyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            return true; // pure modifier — hold it and press the main key
        }

        if (TryMapCapturedKey(keyCode, out string keyName))
        {
            var modifiers = KeyChordModifiers.None;
            if (keyData.HasFlag(Keys.Control))
            {
                modifiers |= KeyChordModifiers.Ctrl;
            }

            if (keyData.HasFlag(Keys.Alt))
            {
                modifiers |= KeyChordModifiers.Alt;
            }

            if (keyData.HasFlag(Keys.Shift))
            {
                modifiers |= KeyChordModifiers.Shift;
            }

            _sequenceBox.Text = new ParsedKeyChord(modifiers, KeyChord.Keys[keyName]).Canonical;
            _captureToggle.Checked = false;
        }

        // Unmappable non-modifier keys are swallowed and capture stays armed.
        return true;
    }

    /// <summary>Maps a captured <see cref="Keys"/> code onto its <see cref="KeyChord"/> table name; false = not in the curated set (capture ignores it).</summary>
    private static bool TryMapCapturedKey(Keys keyCode, out string keyName)
    {
        keyName = keyCode switch
        {
            >= Keys.A and <= Keys.Z => keyCode.ToString(),
            >= Keys.D0 and <= Keys.D9 => keyCode.ToString()[1..], // "D7" -> "7"
            >= Keys.NumPad0 and <= Keys.NumPad9 =>
                ((char)('0' + (keyCode - Keys.NumPad0))).ToString(),
            >= Keys.F1 and <= Keys.F24 => keyCode.ToString(),
            Keys.Enter => "Enter",
            Keys.Tab => "Tab",
            Keys.Space => "Space",
            Keys.Up => "Up",
            Keys.Down => "Down",
            Keys.Left => "Left",
            Keys.Right => "Right",
            Keys.Home => "Home",
            Keys.End => "End",
            Keys.PageUp => "PageUp",
            Keys.PageDown => "PageDown",
            Keys.Insert => "Insert",
            Keys.Delete => "Delete",
            Keys.Back => "Backspace",
            Keys.PrintScreen => "PrintScreen",
            Keys.MediaPlayPause => "MediaPlayPause",
            Keys.MediaNextTrack => "MediaNext",
            Keys.MediaPreviousTrack => "MediaPrevious",
            Keys.MediaStop => "MediaStop",
            Keys.VolumeMute => "VolumeMute",
            Keys.VolumeUp => "VolumeUp",
            Keys.VolumeDown => "VolumeDown",
            _ => "",
        };
        return keyName.Length > 0;
    }

    private void BrowseForProgram()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose a program to launch",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = picker.FileName;
        }
    }

    private void OnOk()
    {
        Revalidate();
        if (!_okButton.Enabled)
        {
            return;
        }

        CustomActionConfig action = IsLaunchAction
            ? new LaunchActionConfig { Path = _pathBox.Text.Trim(), Args = _argsBox.Text.Trim() }
            : IsKeySequenceAction
                ? new KeySequenceActionConfig { Sequence = CanonicalSequence(_sequenceBox.Text.Trim()) }
                : new MediaKeyActionConfig { KeyName = SettingsViewModel.MediaKeyChoices[Math.Max(0, _mediaKeyCombo.SelectedIndex)].Key };
        Result = new CustomCommandConfig
        {
            Key = _keyBox.Text.Trim(),
            Name = _nameBox.Text.Trim(),
            Enabled = _enabledCheck.Checked,
            Action = action,
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Canonical form of a sequence the validator already accepted (case-insensitive input, canonical casing out — the config stores canonical only).</summary>
    private static string CanonicalSequence(string sequence) =>
        KeyChord.TryParse(sequence, out ParsedKeyChord? chord, out _) ? chord.Canonical : sequence;

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);
}
