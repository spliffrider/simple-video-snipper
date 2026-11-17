namespace SimpleVideoSnipper;

internal sealed class SaveSettingsDialog : Form
{
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox folder = new() { Width = 340 };
    private readonly CheckBox copy = new() { Text = "Automatically copy the saved video file", AutoSize = true };

    public SaveSettingsDialog(SavePreferences preferences)
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
        mode.AccessibleName = "After recording";
        format.Items.AddRange(["MP4", "WebM"]);
        format.SelectedIndex = preferences.Format == ExportFormat.WebM ? 1 : 0;
        format.AccessibleName = "Preferred video format";
        folder.Text = preferences.Folder;
        folder.AccessibleName = "Preferred save folder";
        copy.Checked = preferences.AutoCopy;

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Choose the save folder", UseDescriptionForTitle = true };
            if (Directory.Exists(folder.Text))
                picker.InitialDirectory = folder.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
                folder.Text = picker.SelectedPath;
        };
        var save = new Button { Text = "Save settings", AutoSize = true };
        save.Click += (_, _) =>
        {
            if (Path.IsPathFullyQualified(folder.Text.Trim()))
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
        layout.Controls.Add(new Label { Text = "After recording", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(mode, 1, 0);
        layout.Controls.Add(new Label { Text = "Preferred format", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(format, 1, 1);
        layout.Controls.Add(new Label { Text = "Save folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(folder, 1, 2);
        layout.Controls.Add(browse, 2, 2);
        layout.Controls.Add(copy, 1, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([save, cancel]);
        layout.Controls.Add(buttons, 1, 4);
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
    };
}
