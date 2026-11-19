namespace SimpleVideoSnipper;

internal sealed class SaveSettingsDialog : Form
{
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox folder = new() { Width = 340 };
    private readonly TextBox temporaryFolder = new() { Width = 340 };
    private readonly CheckBox copy = new() { Text = "Automatically copy the saved video file", AutoSize = true };
    private readonly (ShortcutModifiers Modifier, CheckBox Box)[] modifiers =
    [
        (ShortcutModifiers.Control, new CheckBox { Text = "Ctrl", AutoSize = true }),
        (ShortcutModifiers.Alt, new CheckBox { Text = "Alt", AutoSize = true }),
        (ShortcutModifiers.Shift, new CheckBox { Text = "Shift", AutoSize = true }),
        (ShortcutModifiers.Windows, new CheckBox { Text = "Win", AutoSize = true }),
    ];
    private readonly ComboBox shortcutKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 104, AccessibleName = "Snip shortcut key" };
    private readonly Label shortcutError = new() { AutoSize = true, Visible = false, ForeColor = SystemColors.HotTrack };

    public SaveSettingsDialog(SavePreferences preferences, string currentTemporaryFolder)
    {
        Text = "Simple Video Snipper Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimizeBox = MaximizeBox = false;

        mode.Items.AddRange(["Ask every time", "Quick Save"]);
        mode.SelectedIndex = preferences.Mode == SaveMode.QuickSave ? 1 : 0;
        mode.AccessibleName = "When saving";
        format.Items.AddRange(["MP4", "WebM"]);
        format.SelectedIndex = preferences.Format == ExportFormat.WebM ? 1 : 0;
        format.AccessibleName = "Preferred video format";
        folder.Text = preferences.Folder;
        folder.AccessibleName = "Preferred save folder";
        temporaryFolder.Text = currentTemporaryFolder;
        temporaryFolder.AccessibleName = "Temporary recordings folder";
        copy.Checked = preferences.AutoCopy;
        shortcutKey.Items.AddRange(CaptureShortcut.AvailableKeys.Select(key => (object)CaptureShortcut.KeyName(key)).ToArray());
        SetShortcut(preferences.SnipShortcut);
        foreach (var (_, box) in modifiers)
            box.CheckedChanged += (_, _) => shortcutError.Visible = false;
        shortcutKey.SelectedIndexChanged += (_, _) => shortcutError.Visible = false;

        var shortcut = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        shortcut.Controls.AddRange(modifiers.Select(item => (Control)item.Box).Append(shortcutKey).ToArray());
        var reset = new Button { Text = "Use default", AutoSize = true, AccessibleName = $"Use default shortcut {CaptureShortcut.Default}" };
        reset.Click += (_, _) => SetShortcut(CaptureShortcut.Default);

        var browse = BrowseButton(folder, "Choose the save folder");
        var browseTemporary = BrowseButton(temporaryFolder, "Choose the temporary recordings folder");
        var save = new Button { Text = "Save settings", AutoSize = true };
        save.Click += (_, _) =>
        {
            if (!SelectedShortcut.IsValid)
                ShowShortcutError("Choose at least one modifier and a key.");
            else if (Path.IsPathFullyQualified(folder.Text.Trim()))
                DialogResult = DialogResult.OK;
            else
                MessageBox.Show(this, "Enter a full save folder path, or use Browse to select one.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var layout = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(14),
        };
        layout.Controls.Add(new Label { Text = "When saving", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(mode, 1, 0);
        layout.Controls.Add(new Label { Text = "Preferred format", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(format, 1, 1);
        layout.Controls.Add(new Label { Text = "Save folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(folder, 1, 2);
        layout.Controls.Add(browse, 2, 2);
        layout.Controls.Add(new Label { Text = "Temporary recordings", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(temporaryFolder, 1, 3);
        layout.Controls.Add(browseTemporary, 2, 3);
        layout.Controls.Add(copy, 1, 4);
        layout.Controls.Add(new Label { Text = "Snip shortcut", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        layout.Controls.Add(shortcut, 1, 5);
        layout.Controls.Add(reset, 2, 5);
        layout.Controls.Add(shortcutError, 1, 6);
        layout.SetColumnSpan(shortcutError, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([save, cancel]);
        layout.Controls.Add(buttons, 1, 7);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public SavePreferences Preferences => new()
    {
        Mode = mode.SelectedIndex == 1 ? SaveMode.QuickSave : SaveMode.Ask,
        Format = format.SelectedIndex == 1 ? ExportFormat.WebM : ExportFormat.Mp4,
        Folder = folder.Text.Trim(),
        AutoCopy = copy.Checked,
        TemporaryFolder = temporaryFolder.Text.Trim(),
        SnipShortcut = SelectedShortcut,
    };

    private CaptureShortcut SelectedShortcut => new(
        modifiers.Aggregate(ShortcutModifiers.None, (selected, item) => item.Box.Checked ? selected | item.Modifier : selected),
        shortcutKey.SelectedIndex < 0 ? Keys.None : CaptureShortcut.AvailableKeys[shortcutKey.SelectedIndex]);

    public void ShowShortcutError(string message)
    {
        shortcutError.Text = message;
        shortcutError.Visible = true;
        ActiveControl = shortcutKey;
    }

    private void SetShortcut(CaptureShortcut shortcut)
    {
        foreach (var (modifier, box) in modifiers)
            box.Checked = shortcut.Modifiers.HasFlag(modifier);
        shortcutKey.SelectedItem = CaptureShortcut.KeyName(shortcut.Key);
    }

    private Button BrowseButton(TextBox field, string title)
    {
        var browse = new Button { Text = "Browse…", AutoSize = true, AccessibleName = title };
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
            if (Directory.Exists(field.Text))
                picker.InitialDirectory = field.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
                field.Text = picker.SelectedPath;
        };
        return browse;
    }
}
