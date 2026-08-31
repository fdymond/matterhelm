namespace MatterHelm.Ui;

/// <summary>
/// Shared preset/coordinate picker for standalone retained mouse commands and
/// stateless one-shot mouse steps. It only edits the target request; the
/// existing mouse resolver remains the single owner of virtual-desktop
/// coordinate resolution and clamping.
/// </summary>
internal sealed class MouseTargetPicker : TableLayoutPanel
{
    private readonly ComboBox _targetCombo;
    private readonly NumericUpDown _x;
    private readonly NumericUpDown _y;

    internal MouseTargetPicker()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 2;
        Margin = new Padding(S(16), 0, 0, 0);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _targetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240) };
        foreach ((_, string label) in SettingsViewModel.MouseTargetChoices)
        {
            _targetCombo.Items.Add(label);
        }

        _targetCombo.SelectedIndexChanged += (_, _) => UpdateCoordinateVisibility();
        _x = CoordinateEditor();
        _y = CoordinateEditor();
        AddRow("Target", _targetCombo);
        AddRow("X", _x);
        AddRow("Y", _y);

        SetSelection(new MouseTargetSelection(MouseTarget.BottomRight, 0, 0));
    }

    internal MouseTargetSelection GetSelection() => new(
        SettingsViewModel.MouseTargetChoices[Math.Max(0, _targetCombo.SelectedIndex)].Target,
        decimal.ToInt32(_x.Value),
        decimal.ToInt32(_y.Value));

    internal void SetSelection(MouseTargetSelection value)
    {
        _targetCombo.SelectedIndex = Math.Max(
            0,
            SettingsViewModel.MouseTargetChoices.ToList().FindIndex(choice => choice.Target == value.Target));
        _x.Value = value.X;
        _y.Value = value.Y;
        UpdateCoordinateVisibility();
    }

    private NumericUpDown CoordinateEditor() => new()
    {
        Minimum = int.MinValue,
        Maximum = int.MaxValue,
        Width = S(120),
        ThousandsSeparator = true,
    };

    private void UpdateCoordinateVisibility()
    {
        bool custom = SettingsViewModel.MouseTargetChoices[Math.Max(0, _targetCombo.SelectedIndex)].Target
            == MouseTarget.Custom;
        _x.Enabled = custom;
        _y.Enabled = custom;
    }

    private void AddRow(string label, Control editor)
    {
        Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(S(3), S(6), S(8), S(3)),
        });
        editor.Margin = new Padding(S(3));
        Controls.Add(editor);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
}

/// <summary>Pure target-picker state shared by both mouse editor surfaces.</summary>
internal readonly record struct MouseTargetSelection(MouseTarget Target, int X, int Y)
{
    internal static MouseTargetSelection FromAction(MouseMoveActionConfig action) =>
        new(action.Target, action.X ?? 0, action.Y ?? 0);

    internal MouseMoveActionConfig ToAction() => new()
    {
        Target = Target,
        X = Target == MouseTarget.Custom ? X : null,
        Y = Target == MouseTarget.Custom ? Y : null,
    };
}
