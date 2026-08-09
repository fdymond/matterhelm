using System.Diagnostics;
using System.Runtime.InteropServices;
using MatterHelm.Diagnostics;

namespace MatterHelm.Ui;

/// <summary>
/// The settings window (ADR-004 §4): left-nav categories, top search filter,
/// staged edits with inline validation, and the custom-command CRUD editor.
/// All state and rules live in <see cref="SettingsViewModel"/> — this class
/// only builds controls from the descriptor schema and forwards edits. One
/// resizable, non-modal window; <see cref="TrayContext"/> shows/focuses a
/// single instance. Layout is DPI-safe (nested <see cref="TableLayoutPanel"/>s,
/// logical-unit sizes converted via <see cref="Control.LogicalToDeviceUnits(int)"/>,
/// no absolute pixel positions) and theme-safe (system colors only, so
/// <c>Application.SetColorMode</c> dark rendering works).
/// </summary>
public sealed partial class SettingsWindow : Form
{
    private const int NavItemHeightLogical = 34;

    // EM_SETCUEBANNER (documented choice per S4-3): the native edit-control cue
    // banner gives the "Search settings" placeholder with correct gray/theme
    // rendering for free — no focus-swap hacks. Declared here, not in
    // NativeMethods.cs, because that file is scoped to the overlay technique.
    private const int EmSetCueBanner = 0x1501;

    private readonly SettingsViewModel _vm;
    private readonly Action? _overlayPreview;
    private readonly Action? _factoryReset;

    private readonly TextBox _searchBox;
    private readonly ListBox _navList;
    private readonly Panel _contentHost;
    private readonly Button _saveButton;
    private readonly Button _closeButton;
    private readonly Label _savedFlash;
    private readonly System.Windows.Forms.Timer _savedFlashTimer;
    private readonly ToolTip _toolTip = new();

    private readonly Dictionary<string, Control> _categoryPanels = [];
    private readonly Dictionary<string, Control> _settingRows = [];
    private readonly Dictionary<string, Label> _errorLabels = [];
    private readonly List<Action> _editorRefreshers = [];

    private readonly Font _secondaryFont;
    private readonly Font _noteFont;
    private readonly Font _navSelectedFont;
    private readonly Color _errorColor;

    private ListView _customList = null!;
    private Button _editCustomButton = null!;
    private Button _removeCustomButton = null!;

    private SettingsSearchResult _search;
    private bool _refreshing;
    private bool _applyingSave;

    /// <summary>Builds the window over <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The staged settings state and rules.</param>
    /// <param name="overlayPreview">Invoked by the Overlay page's Preview button; null hides nothing but makes the button a no-op (demo).</param>
    /// <param name="factoryReset">
    /// Invoked by the Advanced page's "Factory reset" button (confirmation +
    /// the actual <see cref="MatterHelm.BridgeHost.FactoryReset"/> call live
    /// in <c>TrayContext</c>/<c>Program</c>, not here — this window only asks
    /// to be told when the button is pressed). Null (the default, e.g. demos)
    /// leaves the button disabled rather than falling back to a no-op —
    /// unlike Preview, a factory reset must never silently do nothing when
    /// unwired.
    /// </param>
    public SettingsWindow(SettingsViewModel viewModel, Action? overlayPreview = null, Action? factoryReset = null)
    {
        _vm = viewModel;
        _overlayPreview = overlayPreview;
        _factoryReset = factoryReset;
        _search = SettingsSearch.Filter(SettingsViewModel.Categories, "");

        Text = "Settings";
        ShowIcon = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // DPI: sizes are declared in 96-dpi logical units and converted with
        // LogicalToDeviceUnits (the S/SP helpers). WinForms auto-scaling is
        // useless for runtime-built forms here: on net10 the
        // AutoScaleDimensions setter converts the assigned value to the
        // form's current DPI (96 reads back as 192 on a 200 % monitor), so
        // the computed factor is always 1 (verified empirically). The ambient
        // font scales with system DPI on its own.
        ClientSize = new Size(S(840), S(560));
        MinimumSize = new Size(S(720), S(480));

        _secondaryFont = new Font(Font.FontFamily, Font.Size - 1f);
        _noteFont = new Font(Font.FontFamily, Font.Size - 1f, FontStyle.Italic);
        _navSelectedFont = new Font(Font, FontStyle.Bold);
        _errorColor = Application.IsDarkModeEnabled
            ? Color.FromArgb(255, 140, 143)
            : Color.FromArgb(197, 44, 44);

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(200)));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _searchBox = new TextBox { Width = S(240), Margin = SP(12, 12, 12, 8) };
        _searchBox.HandleCreated += (_, _) =>
            _ = SendMessage(_searchBox.Handle, EmSetCueBanner, 1, "Search settings");
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        outer.Controls.Add(_searchBox, 0, 0);
        outer.SetColumnSpan(_searchBox, 2);

        _navList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            Margin = SP(8, 0, 0, 8),
        };
        foreach (SettingsCategory category in SettingsViewModel.Categories)
        {
            _navList.Items.Add(category.Title);
        }

        _navList.DrawItem += OnNavDrawItem;
        _navList.SelectedIndexChanged += (_, _) => ShowSelectedCategory();
        outer.Controls.Add(_navList, 0, 1);

        _contentHost = new Panel { Dock = DockStyle.Fill, Margin = SP(0, 0, 8, 8) };
        foreach (SettingsCategory category in SettingsViewModel.Categories)
        {
            Control panel = BuildCategoryPanel(category);
            _categoryPanels[category.Id] = panel;
            _contentHost.Controls.Add(panel);
        }

        outer.Controls.Add(_contentHost, 1, 1);

        var bottomBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(8)),
        };
        _closeButton = new Button { Text = "Close", AutoSize = true, Padding = SP(8, 2, 8, 2) };
        _closeButton.Click += (_, _) => Close();
        _saveButton = new Button { Text = "Save", AutoSize = true, Padding = SP(8, 2, 8, 2), Enabled = false };
        _saveButton.Click += (_, _) => TrySave();
        _savedFlash = new Label
        {
            Text = "Saved",
            AutoSize = true,
            Visible = false,
            ForeColor = SystemColors.Highlight,
            Anchor = AnchorStyles.None,
            Margin = SP(0, 8, 12, 0),
        };
        bottomBar.Controls.Add(_closeButton);
        bottomBar.Controls.Add(_saveButton);
        bottomBar.Controls.Add(_savedFlash);
        outer.Controls.Add(bottomBar, 0, 2);
        outer.SetColumnSpan(bottomBar, 2);

        Controls.Add(outer);

        _savedFlashTimer = new System.Windows.Forms.Timer { Interval = 2_500 };
        _savedFlashTimer.Tick += (_, _) =>
        {
            _savedFlash.Visible = false;
            _savedFlashTimer.Stop();
        };

        FormClosing += OnFormClosingPrompt;

        // S4-R RISK-1: this window is non-modal, so tray toggles and "Reload
        // config" mutate the live config while it is open — reconcile instead
        // of letting a later Save write the stale staged copy back.
        _vm.Config.Changed += OnLiveConfigChanged;
        FormClosed += (_, _) => _vm.Config.Changed -= OnLiveConfigChanged;

        _navList.SelectedIndex = 0;
        RefreshFromViewModel();
    }

    /// <summary>Re-reads every editor control from <see cref="SettingsViewModel.Working"/>. Public for demo/E2E walks that edit the view-model directly.</summary>
    public void RefreshFromViewModel()
    {
        _refreshing = true;
        try
        {
            foreach (Action refresh in _editorRefreshers)
            {
                refresh();
            }

            RefreshCustomCommands();
        }
        finally
        {
            _refreshing = false;
        }

        UpdateValidationAndSaveState();
    }

    /// <summary>Runs the Save path (same as clicking Save). Public for demo/E2E walks.</summary>
    public void SaveNow() => TrySave();

    /// <summary>Sets the search filter text (same as typing into the box). Public for demo/E2E walks.</summary>
    public void SetSearchQuery(string query) => _searchBox.Text = query;

    /// <summary>Selects the left-nav category by index (same as clicking it). Public for demo/E2E walks.</summary>
    public void SelectCategory(int index) => _navList.SelectedIndex = index;

    /// <summary>Scrolls the selected category page so its last row is in view; true iff it actually scrolled (page taller than the viewport). Public for demo/E2E screenshot coverage of long pages.</summary>
    public bool ScrollCurrentCategoryToEnd()
    {
        if (_categoryPanels[CurrentCategoryId] is not ScrollableControl panel || panel.Controls.Count == 0)
        {
            return false;
        }

        panel.ScrollControlIntoView(panel.Controls[^1]);
        return panel.VerticalScroll.Value > 0;
    }

    /// <summary>True iff the given setting's row is currently visible in the window (its category selected and not filtered out by search). Public for demo/E2E objective checks.</summary>
    public bool IsSettingRowVisible(string settingId) => _settingRows[settingId].Visible;

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Owner-draw item height is not a layout property, so it gets its
        // logical→device conversion here rather than in the constructor.
        _navList.ItemHeight = S(NavItemHeightLogical);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // ADR-004 §4 search semantics: Esc clears the filter first; a second
        // Esc (or Esc with no filter) closes the window.
        if (e.KeyCode == Keys.Escape)
        {
            if (_searchBox.Text.Length > 0)
            {
                _searchBox.Clear();
            }
            else
            {
                Close();
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _savedFlashTimer.Dispose();
            _toolTip.Dispose();
            _secondaryFont.Dispose();
            _noteFont.Dispose();
            _navSelectedFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string CurrentConfigDir() =>
        Path.GetDirectoryName(Config.DefaultPath) ?? Config.DefaultPath;

    private void OnFormClosingPrompt(object? sender, FormClosingEventArgs e)
    {
        if (!_vm.IsDirty)
        {
            return;
        }

        DialogResult choice = MessageBox.Show(
            this,
            "Save your changes?",
            "Settings",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (choice == DialogResult.Yes && !TrySave())
        {
            e.Cancel = true;
        }

        // No → close; the staged copy dies with the window (recreated fresh).
    }

    private bool TrySave()
    {
        IReadOnlyList<SettingsValidationError> errors = _vm.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                "Fix these before saving:" + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, errors.Select(er => "• " + er.Message)),
                "Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        // Apply() reloads the config, which fires Config.Changed synchronously
        // on this thread — the guard keeps OnLiveConfigChanged from treating
        // our own save as an external change.
        _applyingSave = true;
        try
        {
            _vm.Apply();
        }
        finally
        {
            _applyingSave = false;
        }

        RefreshFromViewModel();
        _savedFlash.Visible = true;
        _savedFlashTimer.Stop();
        _savedFlashTimer.Start();
        return true;
    }

    private void OnLiveConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnLiveConfigChanged(sender, e)));
            return;
        }

        if (_applyingSave)
        {
            return;
        }

        _vm.AbsorbExternalConfigChange();
        RefreshFromViewModel();
    }

    // ---- search / nav ------------------------------------------------------

    private string CurrentCategoryId => SettingsViewModel.Categories[Math.Max(0, _navList.SelectedIndex)].Id;

    private void ApplyFilter()
    {
        _search = SettingsSearch.Filter(SettingsViewModel.Categories, _searchBox.Text);
        _navList.Invalidate();

        foreach ((string id, Control row) in _settingRows)
        {
            row.Visible = _search.Matches(id);
        }

        // Auto-select the first matching category when the current one has no
        // matches (ADR-004 §4); clearing the filter restores visibility only.
        if (_search.IsFiltering && !_search.CategoryMatches(CurrentCategoryId))
        {
            int firstMatch = SettingsViewModel.Categories
                .Select((category, index) => (category, index))
                .Where(pair => _search.CategoryMatches(pair.category.Id))
                .Select(pair => (int?)pair.index)
                .FirstOrDefault() ?? -1;
            if (firstMatch >= 0)
            {
                _navList.SelectedIndex = firstMatch;
            }
        }
    }

    private void ShowSelectedCategory()
    {
        string selectedId = CurrentCategoryId;
        foreach ((string id, Control panel) in _categoryPanels)
        {
            panel.Visible = id == selectedId;
        }
    }

    private void OnNavDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        SettingsCategory category = SettingsViewModel.Categories[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool matches = _search.CategoryMatches(category.Id);

        using (var back = new SolidBrush(selected ? SystemColors.Control : _navList.BackColor))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        if (selected)
        {
            // Fluent-ish selection: accent bar on the left edge + bold title.
            int inset = S(8);
            using var accent = new SolidBrush(SystemColors.Highlight);
            e.Graphics.FillRectangle(
                accent,
                new Rectangle(e.Bounds.Left, e.Bounds.Top + inset, S(3), e.Bounds.Height - (2 * inset)));
        }

        Rectangle textBounds = e.Bounds with { X = e.Bounds.X + S(14) };
        TextRenderer.DrawText(
            e.Graphics,
            category.Title,
            selected ? _navSelectedFont : Font,
            textBounds,
            matches ? SystemColors.ControlText : SystemColors.GrayText,
            // NoPrefix: category titles are data, not menu captions — without
            // it "&" ("Devices & Commands") renders as a mnemonic underline.
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ---- content construction ---------------------------------------------

    private TableLayoutPanel BuildCategoryPanel(SettingsCategory category)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = SP(16, 4, 16, 8),
            Visible = false,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int rowIndex = 0;
        foreach (SettingDescriptor setting in category.Settings)
        {
            Control row = setting.Kind == SettingKind.CustomCommands
                ? BuildCustomCommandsBlock(setting)
                : BuildSettingRow(setting);
            _settingRows[setting.Id] = row;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(row, 0, rowIndex++);
        }

        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // filler pins rows to the top
        return panel;
    }

    private TableLayoutPanel BuildSettingRow(SettingDescriptor setting)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = SP(0, 6, 0, 6),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        row.Controls.Add(BuildTextStack(setting), 0, 0);

        Control editor = BuildEditor(setting);
        editor.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        editor.Margin = SP(8, 2, 0, 0);
        row.Controls.Add(editor, 1, 0);
        return row;
    }

    private TableLayoutPanel BuildTextStack(SettingDescriptor setting)
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        stack.Controls.Add(new Label { Text = setting.Label, AutoSize = true, Margin = SP(0, 0, 0, 1) });
        stack.Controls.Add(new Label
        {
            Text = setting.Description,
            AutoSize = true,
            Font = _secondaryFont,
            ForeColor = SystemColors.GrayText,
            Margin = SP(0, 0, 0, 1),
        });
        if (setting.NeedsBridgeRestart)
        {
            stack.Controls.Add(new Label
            {
                Text = "Takes effect next time the bridge is enabled.",
                AutoSize = true,
                Font = _noteFont,
                ForeColor = SystemColors.GrayText,
                Margin = SP(0, 0, 0, 1),
            });
        }

        var error = new Label
        {
            AutoSize = true,
            Font = _secondaryFont,
            ForeColor = _errorColor,
            Visible = false,
            Margin = SP(0, 1, 0, 0),
        };
        _errorLabels[setting.Id] = error;
        stack.Controls.Add(error);
        return stack;
    }

    private Control BuildEditor(SettingDescriptor setting) => setting.Kind switch
    {
        SettingKind.Toggle => BuildToggle(setting),
        SettingKind.Port => BuildPort(setting),
        SettingKind.Number => BuildNumber(setting),
        SettingKind.Text or SettingKind.OptionalText => BuildTextBox(setting),
        SettingKind.Choice => BuildChoice(setting),
        SettingKind.ReadOnlyText => BuildReadOnly(setting),
        SettingKind.Command => BuildCommandButton(setting),
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting.Kind, "no editor for kind"),
    };

    private CheckBox BuildToggle(SettingDescriptor setting)
    {
        var check = new CheckBox { AutoSize = true };
        check.CheckedChanged += (_, _) => OnEdited(setting, check.Checked);
        _editorRefreshers.Add(() => check.Checked = (bool)setting.Get!(_vm.Working)!);
        return check;
    }

    private NumericUpDown BuildPort(SettingDescriptor setting)
    {
        var number = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = S(110) };
        number.ValueChanged += (_, _) => OnEdited(setting, (int)number.Value);
        _editorRefreshers.Add(() => number.Value = Math.Clamp((int)setting.Get!(_vm.Working)!, 1, 65535));
        return number;
    }

    private NumericUpDown BuildNumber(SettingDescriptor setting)
    {
        var number = new NumericUpDown { Minimum = setting.Minimum, Maximum = setting.Maximum, Width = S(110) };
        number.ValueChanged += (_, _) => OnEdited(setting, (int)number.Value);
        _editorRefreshers.Add(() =>
            number.Value = Math.Clamp((int)setting.Get!(_vm.Working)!, setting.Minimum, setting.Maximum));
        return number;
    }

    private TextBox BuildTextBox(SettingDescriptor setting)
    {
        var box = new TextBox { Width = S(200) };
        box.TextChanged += (_, _) => OnEdited(setting, box.Text);
        _editorRefreshers.Add(() => box.Text = (string?)setting.Get!(_vm.Working) ?? "");
        return box;
    }

    private ComboBox BuildChoice(SettingDescriptor setting)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(180) };
        IReadOnlyList<string> labels = setting.ChoiceLabels.Count > 0 ? setting.ChoiceLabels : setting.Choices;
        foreach (string label in labels)
        {
            combo.Items.Add(label);
        }

        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                OnEdited(setting, setting.Choices[combo.SelectedIndex]);
            }
        };
        _editorRefreshers.Add(() =>
        {
            // Unknown file value (Config accepts any string for logLevel):
            // shown as no selection; picking an item overwrites it.
            string value = (string)setting.Get!(_vm.Working)!;
            combo.SelectedIndex = setting.Choices.ToList().IndexOf(value);
        });
        return combo;
    }

    private TextBox BuildReadOnly(SettingDescriptor setting)
    {
        var box = new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Width = S(320),
            TabStop = false,
        };
        _editorRefreshers.Add(() => box.Text = (string?)setting.Get!(_vm.Working) ?? "");
        return box;
    }

    private Control BuildCommandButton(SettingDescriptor setting)
    {
        (string text, Action? onClick) = setting.Id switch
        {
            "overlay-preview" => ("Preview", _overlayPreview ?? (() => { })),
            "open-config-file" => ("Open file", () => OpenWithShell(Config.DefaultPath, "config.json")),
            "open-config-folder" => ("Open folder", () => OpenWithShell(CurrentConfigDir(), "config folder")),
            "export-diagnostics" => ("Export…", OnExportDiagnosticsClicked),
            "reload-config" => ("Reload", OnReloadConfigClicked),
            "factory-reset" => ("Reset…", _factoryReset),
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting.Id, "unknown command setting"),
        };

        var button = new Button { Text = text, AutoSize = true, Padding = SP(8, 2, 8, 2) };
        if (onClick is null)
        {
            button.Enabled = false;

            // A disabled control gets no mouse events, so the tooltip sits on
            // an enabled wrapper panel around the button instead. Currently
            // only reachable for "factory-reset" when the window is built
            // without the TrayContext-owned callback (demos, tests) — never
            // in the production tray flow.
            var wrapper = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
            wrapper.Controls.Add(button);
            _toolTip.SetToolTip(wrapper, "not available without a running tray context");
            return wrapper;
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Advanced → "Export diagnostics" (ADR-006 §2): pick a destination, build
    /// the bundle there, then reveal it in Explorer. The export itself is
    /// local file I/O over the day-sized log files — quick enough for the UI
    /// thread (same class of work as the Save path).
    /// </summary>
    private void OnExportDiagnosticsClicked()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export diagnostics",
            Filter = "Zip archive (*.zip)|*.zip",
            FileName = DiagnosticsBundle.SuggestedFileName(),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string zipPath = DiagnosticsBundle.ExportTo(dialog.FileName);
            Log.Info($"Settings: diagnostics bundle exported to '{zipPath}'.");
            using var process = Process.Start(
                new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Settings: diagnostics export failed: {ex.Message}");
            MessageBox.Show(
                this,
                $"Exporting diagnostics failed: {ex.Message}",
                "Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnReloadConfigClicked()
    {
        if (_vm.IsDirty)
        {
            DialogResult choice = MessageBox.Show(
                this,
                "Reloading discards your unsaved changes here. Continue?",
                "Settings",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (choice != DialogResult.OK)
            {
                return;
            }
        }

        _vm.ReloadFromDisk();
        RefreshFromViewModel();
    }

    // ---- custom commands ---------------------------------------------------

    private TableLayoutPanel BuildCustomCommandsBlock(SettingDescriptor setting)
    {
        var block = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = SP(0, 6, 0, 6),
        };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        block.Controls.Add(BuildTextStack(setting));

        _customList = new ListView
        {
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Height = S(150),
            Dock = DockStyle.Fill,
            Margin = SP(0, 4, 0, 4),
        };
        _customList.Columns.Add("Name", S(150));
        _customList.Columns.Add("Key", S(130));
        _customList.Columns.Add("Action", S(200));
        _customList.ItemChecked += OnCustomItemChecked;
        _customList.SelectedIndexChanged += (_, _) => UpdateCustomButtons();
        _customList.DoubleClick += (_, _) => EditSelectedCustomCommand();
        block.Controls.Add(_customList);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        var addButton = new Button { Text = "Add…", AutoSize = true };
        addButton.Click += (_, _) => AddCustomCommand();
        _editCustomButton = new Button { Text = "Edit…", AutoSize = true, Enabled = false };
        _editCustomButton.Click += (_, _) => EditSelectedCustomCommand();
        _removeCustomButton = new Button { Text = "Remove", AutoSize = true, Enabled = false };
        _removeCustomButton.Click += (_, _) => RemoveSelectedCustomCommand();
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(_editCustomButton);
        buttons.Controls.Add(_removeCustomButton);
        block.Controls.Add(buttons);

        return block;
    }

    private void RefreshCustomCommands()
    {
        _customList.BeginUpdate();
        _customList.Items.Clear();
        foreach (CustomCommandConfig command in _vm.Working.Commands.Custom)
        {
            var item = new ListViewItem(command.Name) { Tag = command.Key, Checked = command.Enabled };
            item.SubItems.Add(command.Key);
            item.SubItems.Add(SettingsViewModel.DescribeAction(command.Action));
            _customList.Items.Add(item);
        }

        _customList.EndUpdate();
        UpdateCustomButtons();
    }

    private void UpdateCustomButtons()
    {
        bool hasSelection = _customList.SelectedItems.Count > 0;
        _editCustomButton.Enabled = hasSelection;
        _removeCustomButton.Enabled = hasSelection;
    }

    private void OnCustomItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        string key = (string)e.Item.Tag!;
        CustomCommandConfig? command = _vm.Working.Commands.Custom.FirstOrDefault(c => c.Key == key);
        if (command is not null)
        {
            command.Enabled = e.Item.Checked;
            UpdateValidationAndSaveState();
        }
    }

    private void AddCustomCommand()
    {
        using var dialog = new CustomCommandDialog(_vm, existing: null);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _vm.AddCustomCommand(dialog.Result!);
            RefreshFromViewModel();
        }
    }

    private void EditSelectedCustomCommand()
    {
        if (_customList.SelectedItems.Count == 0)
        {
            return;
        }

        string key = (string)_customList.SelectedItems[0].Tag!;
        CustomCommandConfig? existing = _vm.Working.Commands.Custom.FirstOrDefault(c => c.Key == key);
        if (existing is null)
        {
            return;
        }

        using var dialog = new CustomCommandDialog(_vm, existing);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _vm.UpdateCustomCommand(key, dialog.Result!);
            RefreshFromViewModel();
        }
    }

    private void RemoveSelectedCustomCommand()
    {
        if (_customList.SelectedItems.Count == 0)
        {
            return;
        }

        _vm.RemoveCustomCommand((string)_customList.SelectedItems[0].Tag!);
        RefreshFromViewModel();
    }

    // ---- edit plumbing -----------------------------------------------------

    private void OnEdited(SettingDescriptor setting, object? value)
    {
        if (_refreshing)
        {
            return;
        }

        setting.Set!(_vm.Working, value);
        UpdateValidationAndSaveState();
    }

    private void UpdateValidationAndSaveState()
    {
        IReadOnlyList<SettingsValidationError> errors = _vm.Validate();
        foreach ((string id, Label label) in _errorLabels)
        {
            string[] messages = [.. errors.Where(e => e.SettingId == id).Select(e => e.Message)];
            label.Text = string.Join(Environment.NewLine, messages);
            label.Visible = messages.Length > 0; // sits inside the row, so a filtered-out row hides it too
        }

        _saveButton.Enabled = errors.Count == 0 && _vm.IsDirty;
    }

    private static void OpenWithShell(string path, string what)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Settings: failed to open {what} ('{path}'): {ex.Message}");
        }
    }

    /// <summary>Logical (96-dpi) pixels → device pixels; see the DPI note in the constructor.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>Logical (96-dpi) padding → device padding.</summary>
    private Padding SP(int left, int top, int right, int bottom) => new(S(left), S(top), S(right), S(bottom));

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, string lParam);
}
