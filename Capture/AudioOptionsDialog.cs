using NAudio.CoreAudioApi;

namespace SimpleVideoSnipper;

internal sealed class AudioOptionsDialog : Form
{
    private readonly CheckBox mic = new() { Text = "Microphone", AutoSize = true };
    private readonly CheckBox system = new() { Text = "System audio", AutoSize = true };
    private readonly ComboBox microphones = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Enabled = false, AccessibleName = "Microphone input",
    };
    private readonly ComboBox outputs = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Enabled = false, AccessibleName = "System audio output",
    };
    private readonly ProgressBar level = new() { Width = 340, AccessibleName = "Microphone level" };
    private readonly Label status = new() { AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60 };
    private AudioEndpoint? preview;

    public AudioOptionsDialog(AudioSelection initial, bool editingDefaults = false)
    {
        Text = editingDefaults ? "Recording audio defaults" : "Recording audio";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimizeBox = MaximizeBox = false;

        var accept = new Button { Text = editingDefaults ? "OK" : "Record", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([accept, cancel]);
        var layout = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(12),
        };
        layout.Controls.AddRange([mic, microphones, level, status, system, outputs, buttons]);
        Controls.Add(layout);
        AcceptButton = accept;
        CancelButton = cancel;
        mic.Enabled = PopulateDevices(microphones, DataFlow.Capture, Role.Console, initial.MicrophoneId);
        system.Enabled = PopulateDevices(outputs, DataFlow.Render, Role.Multimedia, initial.OutputId);
        microphones.Enabled = mic.Checked = initial.MicrophoneId is not null;
        outputs.Enabled = system.Checked = initial.OutputId is not null;
        if (!mic.Enabled)
            status.Text = "Windows has no active microphone.";
        if (!system.Enabled)
            system.Text = "System audio (no active output)";
        mic.CheckedChanged += (_, _) => RefreshMicrophone();
        microphones.SelectedIndexChanged += (_, _) => RefreshMicrophone();
        system.CheckedChanged += (_, _) =>
        {
            outputs.Enabled = system.Checked;
            UpdateAccept();
        };
        outputs.SelectedIndexChanged += (_, _) => UpdateAccept();
        Shown += (_, _) =>
        {
            RefreshMicrophone();
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            level.Value = (int)Math.Clamp(Math.Sqrt(preview?.Peak ?? 0) * 100, 0, 100);
            if (preview?.Error is { } error)
                status.Text = error;
            UpdateAccept();
        };

        void RefreshMicrophone()
        {
            RefreshPreview();
            UpdateAccept();
        }

        void UpdateAccept() => accept.Enabled = editingDefaults ||
            ((!mic.Checked || (preview?.Ready.IsCompletedSuccessfully == true && preview.Error is null)) &&
            (!system.Checked || outputs.SelectedItem is AudioDeviceChoice { Available: true }));
    }

    public AudioSelection Selection => new(
        mic.Checked ? (microphones.SelectedItem as AudioDeviceChoice)?.Id : null,
        system.Checked ? (outputs.SelectedItem as AudioDeviceChoice)?.Id : null);

    private static bool PopulateDevices(ComboBox list, DataFlow flow, Role role, string? selectedId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.HasDefaultAudioEndpoint(flow, role)
            ? enumerator.GetDefaultAudioEndpoint(flow, role) : null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                var isDefault = device.ID == defaultDevice?.ID;
                var name = isDefault ? $"{device.FriendlyName} (Windows default)" : device.FriendlyName;
                var index = list.Items.Add(new AudioDeviceChoice(device.ID, name));
                if (isDefault)
                    list.SelectedIndex = index;
            }
        }
        if (selectedId is not null)
        {
            var selected = list.Items.OfType<AudioDeviceChoice>().FirstOrDefault(choice => choice.Id == selectedId);
            list.SelectedIndex = selected is null
                ? list.Items.Add(new AudioDeviceChoice(selectedId, "Saved device (unavailable)", Available: false))
                : list.Items.IndexOf(selected);
        }
        if (list.Items.Count > 0 && list.SelectedIndex < 0)
            list.SelectedIndex = 0;
        return list.Items.Count > 0;
    }

    private void RefreshPreview()
    {
        StopPreview();
        microphones.Enabled = mic.Checked;
        status.Text = mic.Enabled ? "" : "Windows has no active microphone.";
        if (mic.Checked && microphones.SelectedItem is AudioDeviceChoice device)
        {
            if (device.Available)
                preview = new AudioEndpoint(device.Id);
            else
                status.Text = "The saved microphone is unavailable.";
        }
    }

    private void StopPreview()
    {
        preview?.Dispose();
        if (preview?.Ready.IsFaulted ?? false)
            _ = preview.Ready.Exception;
        preview = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            StopPreview();
        }
        base.Dispose(disposing);
    }

    private sealed record AudioDeviceChoice(string Id, string Name, bool Available = true)
    {
        public override string ToString() => Name;
    }
}
