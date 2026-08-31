using MatterHelm.Actions;

namespace MatterHelm.Ui;

/// <summary>
/// Modal add/edit dialog for one step of a command sequence (S8-3). Offers
/// the non-sequence action types — media key, launch, key sequence, system
/// command (S8-5), mouse move, and a wait — with the same editors and key-capture UX as
/// <see cref="CustomCommandDialog"/> (capture mapping shared via
/// <see cref="KeyChordCapture"/>). Nested sequences are excluded by
/// construction: this dialog simply has no "sequence" choice.
/// </summary>
public sealed class SequenceStepDialog : Form
{
    internal const int MediaKeyIndex = 0;
    internal const int LaunchIndex = 1;
    internal const int KeySequenceIndex = 2;
    internal const int SystemIndex = 3;
    internal const int MouseMoveIndex = 4;
    internal const int DelayIndex = 5;

    internal static IReadOnlyList<string> StepTypeLabels { get; } =
    [
        "Press a media key",
        "Launch a program",
        "Key sequence",
        "System command",
        "Mouse move",
        "Wait",
    ];

    private readonly SettingsViewModel _vm;

    private readonly ComboBox _typeCombo;
    private readonly ComboBox _mediaKeyCombo;
    private readonly TableLayoutPanel _mediaKeyRow;
    private readonly TextBox _pathBox;
    private readonly TextBox _argsBox;
    private readonly TableLayoutPanel _launchRows;
    private readonly TextBox _sequenceBox;
    private readonly CheckBox _captureToggle;
    private readonly TableLayoutPanel _sequenceRow;
    private readonly ComboBox _systemCombo;
    private readonly TableLayoutPanel _systemRow;
    private readonly MouseTargetPicker _mouseTargetPicker;
    private readonly Label _mouseSemanticsNote;
    private readonly NumericUpDown _delayInput;
    private readonly TableLayoutPanel _delayRow;
    private readonly Label _errorLabel;
    private readonly Button _okButton;

    /// <summary>Builds the dialog; <paramref name="existing"/> null = add, non-null = edit.</summary>
    public SequenceStepDialog(SettingsViewModel viewModel, CustomActionConfig? existing)
    {
        _vm = viewModel;

        Text = existing is null ? "Add step" : "Edit step";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(S(420), S(220));
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

        _typeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240) };
        foreach (string label in StepTypeLabels)
        {
            _typeCombo.Items.Add(label);
        }

        _typeCombo.SelectedIndexChanged += (_, _) => OnTypeChanged();
        AddRow(grid, "Step", _typeCombo);

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
        // S10-5: Store (MSIX) apps live in a non-browsable package folder and
        // must be launched through their execution alias - Browse… cannot
        // reach them, so they get their own picker.
        var storeButton = new Button { Text = "Store app…", AutoSize = true };
        storeButton.Click += (_, _) => PickStoreApp();
        var pathRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        pathRow.Controls.Add(_pathBox);
        pathRow.Controls.Add(browseButton);
        pathRow.Controls.Add(storeButton);
        _argsBox = new TextBox { Width = S(240) };
        _launchRows = SubGrid(grid);
        AddRow(_launchRows, "Program", pathRow);
        AddRow(_launchRows, "Arguments", _argsBox);

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

        // S8-5 system commands: one combo, always valid.
        _systemCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240) };
        foreach ((_, string label) in SettingsViewModel.SystemCommandChoices)
        {
            _systemCombo.Items.Add(label);
        }

        _systemCombo.SelectedIndex = 0;
        _systemRow = SubGrid(grid);
        AddRow(_systemRow, "Command", _systemCombo);

        _mouseTargetPicker = new MouseTargetPicker();
        grid.Controls.Add(_mouseTargetPicker);
        grid.SetColumnSpan(_mouseTargetPicker, 2);
        _mouseSemanticsNote = new Label
        {
            Text = "Moves once. Add another mouse step if the pointer should move back.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(S(19), S(6), S(3), S(3)),
        };
        grid.Controls.Add(_mouseSemanticsNote);
        grid.SetColumnSpan(_mouseSemanticsNote, 2);

        _delayInput = new NumericUpDown
        {
            Minimum = DelayActionConfig.MinMs,
            Maximum = DelayActionConfig.MaxMs,
            Value = 300,
            Increment = 50,
            Width = S(100),
        };
        _delayRow = SubGrid(grid);
        AddRow(_delayRow, "Wait (ms)", _delayInput);

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

        EditorState state = EditorState.FromAction(existing);
        _typeCombo.SelectedIndex = state.TypeIndex;
        _mediaKeyCombo.SelectedIndex = Math.Max(
            0,
            SettingsViewModel.MediaKeyChoices.ToList().FindIndex(c => c.Key == state.MediaKey));
        _pathBox.Text = state.Path;
        _argsBox.Text = state.Args;
        _sequenceBox.Text = state.KeySequence;
        _systemCombo.SelectedIndex = Math.Max(
            0,
            SettingsViewModel.SystemCommandChoices.ToList().FindIndex(c => c.Command == state.SystemCommand));
        _mouseTargetPicker.SetSelection(state.MouseTarget);
        _delayInput.Value = state.DelayMs;

        OnTypeChanged();
    }

    /// <summary>The validated step; non-null iff the dialog closed with OK.</summary>
    public CustomActionConfig? Result { get; private set; }

    private bool IsLaunch => _typeCombo.SelectedIndex == LaunchIndex;

    private bool IsKeySequence => _typeCombo.SelectedIndex == KeySequenceIndex;

    private bool IsSystem => _typeCombo.SelectedIndex == SystemIndex;

    private bool IsMouseMove => _typeCombo.SelectedIndex == MouseMoveIndex;

    private bool IsDelay => _typeCombo.SelectedIndex == DelayIndex;

    private void OnTypeChanged()
    {
        _mediaKeyRow.Visible = !IsLaunch && !IsKeySequence && !IsSystem && !IsMouseMove && !IsDelay;
        _launchRows.Visible = IsLaunch;
        _sequenceRow.Visible = IsKeySequence;
        _systemRow.Visible = IsSystem;
        _mouseTargetPicker.Visible = IsMouseMove;
        _mouseSemanticsNote.Visible = IsMouseMove;
        _delayRow.Visible = IsDelay;
        _captureToggle.Checked = false; // leaving the row always disarms capture
        Revalidate();
    }

    private void Revalidate()
    {
        string? error =
            (IsLaunch ? _vm.ValidateLaunchPath(_pathBox.Text.Trim()) : null)
            ?? (IsKeySequence ? SettingsViewModel.ValidateKeySequence(_sequenceBox.Text.Trim()) : null);
        _errorLabel.Text = error ?? "";
        _okButton.Enabled = error is null;
    }

    /// <inheritdoc />
    /// <remarks>Chord capture — same contract as <see cref="CustomCommandDialog"/> (mapping shared via <see cref="KeyChordCapture"/>).</remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_captureToggle.Checked || !IsKeySequence)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode == Keys.Escape)
        {
            _captureToggle.Checked = false;
            return true;
        }

        if (KeyChordCapture.IsPureModifier(keyCode))
        {
            return true; // pure modifier — hold it and press the main key
        }

        if (KeyChordCapture.TryCapture(keyData, out string? canonical))
        {
            _sequenceBox.Text = canonical;
            _captureToggle.Checked = false;
        }

        // Unmappable non-modifier keys are swallowed and capture stays armed.
        return true;
    }

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

    /// <summary>S10-5: fills the path with a Store app's execution alias (the only way a packaged app can be started).</summary>
    private void PickStoreApp()
    {
        using var picker = new StoreAppPickerDialog();
        if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedPath is { } path)
        {
            _pathBox.Text = path;
        }
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

        Result = new EditorState(
            _typeCombo.SelectedIndex,
            SettingsViewModel.MediaKeyChoices[Math.Max(0, _mediaKeyCombo.SelectedIndex)].Key,
            _pathBox.Text.Trim(),
            _argsBox.Text.Trim(),
            _sequenceBox.Text.Trim(),
            SettingsViewModel.SystemCommandChoices[Math.Max(0, _systemCombo.SelectedIndex)].Command,
            _mouseTargetPicker.GetSelection(),
            (int)_delayInput.Value).ToAction();
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Pure add/edit state mapping. The dialog initializes from this state and
    /// creates its result through the inverse mapping, keeping every dropdown
    /// index on one testable round-trip path.
    /// </summary>
    internal readonly record struct EditorState(
        int TypeIndex,
        MediaKeyName MediaKey,
        string Path,
        string Args,
        string KeySequence,
        SystemCommandName SystemCommand,
        MouseTargetSelection MouseTarget,
        int DelayMs)
    {
        internal static EditorState FromAction(CustomActionConfig? action) => action switch
        {
            LaunchActionConfig launch => Default with
            {
                TypeIndex = LaunchIndex,
                Path = launch.Path,
                Args = launch.Args,
            },
            KeySequenceActionConfig keySequence => Default with
            {
                TypeIndex = KeySequenceIndex,
                KeySequence = keySequence.Sequence,
            },
            SystemActionConfig system => Default with
            {
                TypeIndex = SystemIndex,
                SystemCommand = system.Command,
            },
            MouseMoveActionConfig mouseMove => Default with
            {
                TypeIndex = MouseMoveIndex,
                MouseTarget = MouseTargetSelection.FromAction(mouseMove),
            },
            DelayActionConfig delay => Default with
            {
                TypeIndex = DelayIndex,
                DelayMs = Math.Clamp(delay.Ms, DelayActionConfig.MinMs, DelayActionConfig.MaxMs),
            },
            MediaKeyActionConfig mediaKey => Default with { MediaKey = mediaKey.KeyName },
            _ => Default,
        };

        internal CustomActionConfig ToAction() => TypeIndex switch
        {
            LaunchIndex => new LaunchActionConfig { Path = Path, Args = Args },
            KeySequenceIndex => new KeySequenceActionConfig { Sequence = CanonicalSequence(KeySequence) },
            SystemIndex => new SystemActionConfig { Command = SystemCommand },
            MouseMoveIndex => MouseTarget.ToAction(),
            DelayIndex => new DelayActionConfig { Ms = DelayMs },
            _ => new MediaKeyActionConfig { KeyName = MediaKey },
        };

        private static EditorState Default => new(
            MediaKeyIndex,
            SettingsViewModel.MediaKeyChoices[0].Key,
            "",
            "",
            "",
            SettingsViewModel.SystemCommandChoices[0].Command,
            new MouseTargetSelection(global::MatterHelm.MouseTarget.BottomRight, 0, 0),
            300);
    }

    /// <summary>Canonical form of a sequence the validator already accepted.</summary>
    private static string CanonicalSequence(string sequence) =>
        KeyChord.TryParse(sequence, out ParsedKeyChord? chord, out _) ? chord.Canonical : sequence;

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);
}
