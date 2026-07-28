namespace HtpcMatterBridge.Ui;

/// <summary>
/// Modal add/edit dialog for one custom command (ADR-004 §4). The key is
/// locked when editing an existing command — it is the Matter endpoint id and
/// wire identifier, so renames must never change it (stable identity,
/// ADR-004 §1). The action editor switches between the media-key combo and
/// the launch path/args row with the action-type combo. Validation is inline
/// (via <see cref="SettingsViewModel"/>'s pure rules) and OK stays disabled
/// while anything is invalid.
/// </summary>
public sealed class CustomCommandDialog : Form
{
    private const int MediaKeyActionIndex = 0;
    private const int LaunchActionIndex = 1;

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

        if (existing?.Action is LaunchActionConfig launch)
        {
            _actionTypeCombo.SelectedIndex = LaunchActionIndex;
            _pathBox.Text = launch.Path;
            _argsBox.Text = launch.Args;
        }
        else
        {
            _actionTypeCombo.SelectedIndex = MediaKeyActionIndex;
            if (existing?.Action is MediaKeyActionConfig mediaKey)
            {
                int index = SettingsViewModel.MediaKeyChoices.ToList().FindIndex(c => c.Key == mediaKey.KeyName);
                _mediaKeyCombo.SelectedIndex = Math.Max(0, index);
            }
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

    private void OnActionTypeChanged()
    {
        _mediaKeyRow.Visible = !IsLaunchAction;
        _launchRows.Visible = IsLaunchAction;
        Revalidate();
    }

    private void Revalidate()
    {
        string? error =
            _vm.ValidateCustomCommandKey(_keyBox.Text.Trim(), _originalKey)
            ?? SettingsViewModel.ValidateCustomCommandName(_nameBox.Text.Trim())
            ?? (IsLaunchAction ? _vm.ValidateLaunchPath(_pathBox.Text.Trim()) : null);
        _errorLabel.Text = error ?? "";
        _okButton.Enabled = error is null;
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

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);
}
