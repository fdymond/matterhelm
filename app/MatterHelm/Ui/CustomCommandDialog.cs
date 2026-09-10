using MatterHelm.Actions;

namespace MatterHelm.Ui;

/// <summary>
/// Modal add/edit dialog for one custom command (ADR-004 §4, extended by
/// S7-1). The key is locked when editing an existing command — it is the
/// Matter endpoint id and wire identifier, so renames must never change it
/// (stable identity, ADR-004 §1). The action editor switches between the
/// media-key combo, the launch path/args rows, the key-sequence row, and the
/// macro step list (S8-3: an ordered list of steps edited via
/// <see cref="SequenceStepDialog"/>) with the
/// action-type combo. Validation
/// is inline (via
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
    private const int SystemActionIndex = 3;
    private const int MouseMoveActionIndex = 4;
    private const int SequenceMacroActionIndex = 5;

    private readonly SettingsViewModel _vm;
    private readonly string? _originalKey;

    private readonly TextBox _keyBox;
    private readonly TextBox _nameBox;
    private readonly CheckBox _enabledCheck;
    private readonly CheckBox _resetAfterActivationCheck;
    private readonly ComboBox _actionTypeCombo;
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
    private readonly ListBox _stepsList;
    private readonly Button _editStepButton;
    private readonly Button _removeStepButton;
    private readonly Button _stepUpButton;
    private readonly Button _stepDownButton;
    private readonly TableLayoutPanel _macroRow;
    private readonly List<CustomActionConfig> _steps = [];
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

        _resetAfterActivationCheck = new CheckBox
        {
            AutoSize = true,
            Checked = existing?.ResetAfterActivation ?? false,
            Text = "Reset the switch after it runs (momentary button)",
        };
        var resetEditor = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        resetEditor.Controls.Add(_resetAfterActivationCheck);
        resetEditor.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "The automatic reset does not run the command again.",
        });
        AddRow(grid, "Switch behavior", resetEditor);

        _actionTypeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240) };
        _actionTypeCombo.Items.Add("Press a media key");
        _actionTypeCombo.Items.Add("Launch a program");
        _actionTypeCombo.Items.Add("Key sequence");
        _actionTypeCombo.Items.Add("System command");
        _actionTypeCombo.Items.Add("Move the mouse");
        _actionTypeCombo.Items.Add("Command sequence (macro)");
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
        // S10-5: Store (MSIX) apps live in a non-browsable package folder and
        // must be launched through their execution alias - Browse… cannot
        // reach them, so they get their own picker.
        var storeButton = new Button { Text = "Store app…", AutoSize = true };
        storeButton.Click += (_, _) => PickStoreApp();
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
        pathRow.Controls.Add(storeButton);
        _argsBox = new TextBox { Width = S(240) };
        _argsBox.TextChanged += (_, _) => Revalidate();
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

        // S8-3 macro editor: ordered step list + add/edit/remove/reorder.
        // Every step type is added and edited through SequenceStepDialog.
        _stepsList = new ListBox { Width = S(300), Height = S(110), IntegralHeight = false };
        _stepsList.SelectedIndexChanged += (_, _) => UpdateStepButtons();
        _stepsList.DoubleClick += (_, _) => EditStep();
        var addStepButton = new Button { Text = "Add…", AutoSize = true };
        addStepButton.Click += (_, _) => AddStep();
        _editStepButton = new Button { Text = "Edit…", AutoSize = true, Enabled = false };
        _editStepButton.Click += (_, _) => EditStep();
        _removeStepButton = new Button { Text = "Remove", AutoSize = true, Enabled = false };
        _removeStepButton.Click += (_, _) => RemoveStep();
        _stepUpButton = new Button { Text = "Up", AutoSize = true, Enabled = false };
        _stepUpButton.Click += (_, _) => MoveStep(-1);
        _stepDownButton = new Button { Text = "Down", AutoSize = true, Enabled = false };
        _stepDownButton.Click += (_, _) => MoveStep(+1);
        var stepButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        stepButtons.Controls.Add(addStepButton);
        stepButtons.Controls.Add(_editStepButton);
        stepButtons.Controls.Add(_removeStepButton);
        stepButtons.Controls.Add(_stepUpButton);
        stepButtons.Controls.Add(_stepDownButton);
        var stepsEditor = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        stepsEditor.Controls.Add(_stepsList);
        stepsEditor.Controls.Add(stepButtons);
        _macroRow = SubGrid(grid);
        AddRow(_macroRow, "Steps (run in order)", stepsEditor);

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
            case SystemActionConfig system:
                _actionTypeCombo.SelectedIndex = SystemActionIndex;
                _systemCombo.SelectedIndex = Math.Max(
                    0,
                    SettingsViewModel.SystemCommandChoices.ToList().FindIndex(c => c.Command == system.Command));
                break;
            case MouseMoveActionConfig mouseMove:
            {
                _actionTypeCombo.SelectedIndex = MouseMoveActionIndex;
                _mouseTargetPicker.SetSelection(MouseTargetSelection.FromAction(mouseMove));
                break;
            }
            case SequenceActionConfig sequence:
                _actionTypeCombo.SelectedIndex = SequenceMacroActionIndex;
                _steps.AddRange(sequence.Steps);
                RefreshStepsList();
                break;
            default:
                _actionTypeCombo.SelectedIndex = MediaKeyActionIndex;
                if (existing?.Action is MediaKeyActionConfig mediaKey)
                {
                    _mediaKeyCombo.SelectedIndex = MediaKeyIndex(mediaKey.KeyName);
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

    private bool IsSystemAction => _actionTypeCombo.SelectedIndex == SystemActionIndex;

    private bool IsMouseMoveAction => _actionTypeCombo.SelectedIndex == MouseMoveActionIndex;

    private bool IsMacroAction => _actionTypeCombo.SelectedIndex == SequenceMacroActionIndex;

    private void OnActionTypeChanged()
    {
        _mediaKeyRow.Visible = !IsLaunchAction && !IsKeySequenceAction && !IsSystemAction && !IsMouseMoveAction && !IsMacroAction;
        _launchRows.Visible = IsLaunchAction;
        _sequenceRow.Visible = IsKeySequenceAction;
        _systemRow.Visible = IsSystemAction;
        _mouseTargetPicker.Visible = IsMouseMoveAction;
        _macroRow.Visible = IsMacroAction;
        if (IsMouseMoveAction)
        {
            _resetAfterActivationCheck.Checked = false;
        }

        _resetAfterActivationCheck.Enabled = !IsMouseMoveAction;
        _captureToggle.Checked = false; // leaving the row always disarms capture
        Revalidate();
    }

    private void Revalidate()
    {
        string? error =
            _vm.ValidateCustomCommandKey(_keyBox.Text.Trim(), _originalKey)
            ?? SettingsViewModel.ValidateCustomCommandName(_nameBox.Text.Trim())
            ?? (IsLaunchAction ? _vm.ValidateLaunchPath(_pathBox.Text.Trim()) : null)
            ?? (IsLaunchAction ? SettingsViewModel.ValidateLaunchArguments(_argsBox.Text.Trim()) : null)
            ?? (IsKeySequenceAction ? SettingsViewModel.ValidateKeySequence(_sequenceBox.Text.Trim()) : null)
            ?? (IsMacroAction ? _vm.ValidateAction(new SequenceActionConfig { Steps = _steps }, allowSequence: true) : null);
        _errorLabel.Text = error ?? "";
        _okButton.Enabled = error is null;
    }

    private void RefreshStepsList()
    {
        int selected = _stepsList.SelectedIndex;
        _stepsList.BeginUpdate();
        _stepsList.Items.Clear();
        for (int i = 0; i < _steps.Count; i++)
        {
            _stepsList.Items.Add($"{i + 1}. {SettingsViewModel.DescribeAction(_steps[i])}");
        }

        _stepsList.EndUpdate();
        if (_steps.Count > 0)
        {
            _stepsList.SelectedIndex = Math.Clamp(selected, 0, _steps.Count - 1);
        }

        UpdateStepButtons();
        Revalidate();
    }

    private void UpdateStepButtons()
    {
        int index = _stepsList.SelectedIndex;
        _editStepButton.Enabled = index >= 0;
        _removeStepButton.Enabled = index >= 0;
        _stepUpButton.Enabled = index > 0;
        _stepDownButton.Enabled = index >= 0 && index < _steps.Count - 1;
    }

    private void AddStep()
    {
        using var dialog = new SequenceStepDialog(_vm, existing: null);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } step)
        {
            _steps.Add(step);
            RefreshStepsList();
            _stepsList.SelectedIndex = _steps.Count - 1;
        }
    }

    private void EditStep()
    {
        int index = _stepsList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        using var dialog = new SequenceStepDialog(_vm, _steps[index]);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } step)
        {
            _steps[index] = step;
            RefreshStepsList();
            _stepsList.SelectedIndex = index;
        }
    }

    private void RemoveStep()
    {
        int index = _stepsList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _steps.RemoveAt(index);
        RefreshStepsList();
    }

    private void MoveStep(int direction)
    {
        int index = _stepsList.SelectedIndex;
        int target = index + direction;
        if (index < 0 || target < 0 || target >= _steps.Count)
        {
            return;
        }

        (_steps[index], _steps[target]) = (_steps[target], _steps[index]);
        RefreshStepsList();
        _stepsList.SelectedIndex = target;
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

        CustomActionConfig action = _actionTypeCombo.SelectedIndex switch
        {
            LaunchActionIndex => new LaunchActionConfig { Path = _pathBox.Text.Trim(), Args = _argsBox.Text.Trim() },
            KeySequenceActionIndex => new KeySequenceActionConfig
            {
                Sequence = KeySequenceCanonicalizer.Canonicalize(_sequenceBox.Text.Trim()),
            },
            SystemActionIndex => new SystemActionConfig
            {
                Command = SettingsViewModel.SystemCommandChoices[Math.Max(0, _systemCombo.SelectedIndex)].Command,
            },
            MouseMoveActionIndex => _mouseTargetPicker.GetSelection().ToAction(),
            SequenceMacroActionIndex => new SequenceActionConfig { Steps = [.. _steps] },
            _ => new MediaKeyActionConfig { KeyName = MediaKeyFromIndex(_mediaKeyCombo.SelectedIndex) },
        };
        Result = new CustomCommandConfig
        {
            Key = _keyBox.Text.Trim(),
            Name = _nameBox.Text.Trim(),
            Enabled = _enabledCheck.Checked,
            ResetAfterActivation = _resetAfterActivationCheck.Checked,
            Action = action,
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Pure media-key editor mapping used by initialization and UI round-trip tests.</summary>
    internal static int MediaKeyIndex(MediaKeyName key) =>
        Math.Max(0, SettingsViewModel.MediaKeyChoices.ToList().FindIndex(choice => choice.Key == key));

    /// <summary>Pure inverse of <see cref="MediaKeyIndex"/>; an unselected combo uses its first choice.</summary>
    internal static MediaKeyName MediaKeyFromIndex(int index) =>
        SettingsViewModel.MediaKeyChoices[Math.Max(0, index)].Key;

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);
}
