using MatterHelm.Actions;

namespace MatterHelm.Ui;

/// <summary>
/// Modal add/edit dialog for one step of a command sequence (S8-3). Offers
/// the non-sequence action types — media key, launch, key sequence, system
/// command (S8-5), and a wait — with the same editors and key-capture UX as
/// <see cref="CustomCommandDialog"/> (capture mapping shared via
/// <see cref="KeyChordCapture"/>). Nested sequences are excluded by
/// construction: this dialog simply has no "sequence" choice.
/// </summary>
public sealed class SequenceStepDialog : Form
{
    private const int MediaKeyIndex = 0;
    private const int LaunchIndex = 1;
    private const int KeySequenceIndex = 2;
    private const int SystemIndex = 3;
    private const int DelayIndex = 4;

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
        _typeCombo.Items.Add("Press a media key");
        _typeCombo.Items.Add("Launch a program");
        _typeCombo.Items.Add("Key sequence");
        _typeCombo.Items.Add("System command");
        _typeCombo.Items.Add("Wait");
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

        switch (existing)
        {
            case LaunchActionConfig launch:
                _typeCombo.SelectedIndex = LaunchIndex;
                _pathBox.Text = launch.Path;
                _argsBox.Text = launch.Args;
                break;
            case KeySequenceActionConfig keySequence:
                _typeCombo.SelectedIndex = KeySequenceIndex;
                _sequenceBox.Text = keySequence.Sequence;
                break;
            case SystemActionConfig system:
                _typeCombo.SelectedIndex = SystemIndex;
                _systemCombo.SelectedIndex = Math.Max(
                    0,
                    SettingsViewModel.SystemCommandChoices.ToList().FindIndex(c => c.Command == system.Command));
                break;
            case DelayActionConfig delay:
                _typeCombo.SelectedIndex = DelayIndex;
                _delayInput.Value = Math.Clamp(delay.Ms, DelayActionConfig.MinMs, DelayActionConfig.MaxMs);
                break;
            default:
                _typeCombo.SelectedIndex = MediaKeyIndex;
                if (existing is MediaKeyActionConfig mediaKey)
                {
                    int index = SettingsViewModel.MediaKeyChoices.ToList().FindIndex(c => c.Key == mediaKey.KeyName);
                    _mediaKeyCombo.SelectedIndex = Math.Max(0, index);
                }

                break;
        }

        OnTypeChanged();
    }

    /// <summary>The validated step; non-null iff the dialog closed with OK.</summary>
    public CustomActionConfig? Result { get; private set; }

    private bool IsLaunch => _typeCombo.SelectedIndex == LaunchIndex;

    private bool IsKeySequence => _typeCombo.SelectedIndex == KeySequenceIndex;

    private bool IsSystem => _typeCombo.SelectedIndex == SystemIndex;

    private bool IsDelay => _typeCombo.SelectedIndex == DelayIndex;

    private void OnTypeChanged()
    {
        _mediaKeyRow.Visible = !IsLaunch && !IsKeySequence && !IsSystem && !IsDelay;
        _launchRows.Visible = IsLaunch;
        _sequenceRow.Visible = IsKeySequence;
        _systemRow.Visible = IsSystem;
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

        Result = _typeCombo.SelectedIndex switch
        {
            LaunchIndex => new LaunchActionConfig { Path = _pathBox.Text.Trim(), Args = _argsBox.Text.Trim() },
            KeySequenceIndex => new KeySequenceActionConfig { Sequence = CanonicalSequence(_sequenceBox.Text.Trim()) },
            SystemIndex => new SystemActionConfig
            {
                Command = SettingsViewModel.SystemCommandChoices[Math.Max(0, _systemCombo.SelectedIndex)].Command,
            },
            DelayIndex => new DelayActionConfig { Ms = (int)_delayInput.Value },
            _ => new MediaKeyActionConfig { KeyName = SettingsViewModel.MediaKeyChoices[Math.Max(0, _mediaKeyCombo.SelectedIndex)].Key },
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Canonical form of a sequence the validator already accepted.</summary>
    private static string CanonicalSequence(string sequence) =>
        KeyChord.TryParse(sequence, out ParsedKeyChord? chord, out _) ? chord.Canonical : sequence;

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);
}
