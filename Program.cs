using System.ComponentModel;
using System.Diagnostics;

namespace SimpleVideoSnipper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleVideoSnipper", "Recordings");

        try
        {
            using var tray = new RecordingTray(RecordingHost.Start(folder), args.Contains("--open"));
            Application.Run(tray);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "Simple Video Snipper", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private sealed class RecordingTray : ApplicationContext
    {
        private readonly RecordingHost host;
        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly Form window;
        private readonly System.Windows.Forms.Timer refresh;

        public RecordingTray(RecordingHost host, bool openOnLaunch)
        {
            this.host = host;
            menu = new ContextMenuStrip();
            menu.Items.Add("Open Chrome recorder", null, (_, _) => OpenRecorder());
            var stop = menu.Items.Add("Stop recording", null, (_, _) => host.RequestStop());
            menu.Items.Add("Open recordings folder", null, (_, _) => Open(host.Folder));
            menu.Items.Add(new ToolStripSeparator());
            var exit = menu.Items.Add("Exit", null, (_, _) => ExitThread());
            menu.Opening += (_, _) =>
            {
                stop.Enabled = host.IsRecording;
                exit.Enabled = !host.IsRecording;
            };

            icon = new NotifyIcon
            {
                Text = "Simple Video Snipper",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };
            var openButton = new Button { Text = "Open Chrome recorder", AutoSize = true };
            openButton.Click += (_, _) => OpenRecorder();
            var stopButton = new Button { Text = "Stop recording", AutoSize = true, Enabled = false };
            stopButton.Click += (_, _) => host.RequestStop();
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                WrapContents = false,
            };
            actions.Controls.AddRange([openButton, stopButton]);

            window = new Form
            {
                Text = "Simple Video Snipper",
                ClientSize = new Size(320, 60),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                AcceptButton = openButton,
            };
            icon.DoubleClick += (_, _) =>
            {
                window.Show();
                window.Activate();
            };
            window.Controls.Add(actions);
            window.FormClosing += (_, args) =>
            {
                if (args.CloseReason == CloseReason.UserClosing)
                {
                    args.Cancel = true;
                    window.Hide();
                }
            };
            refresh = new System.Windows.Forms.Timer { Interval = 250 };
            refresh.Tick += (_, _) => stopButton.Enabled = host.IsRecording;
            refresh.Start();
            if (openOnLaunch)
                window.Shown += (_, _) => OpenRecorder();
            window.Show();
        }

        private void OpenRecorder() => Open("chrome.exe", host.RecorderUrl);

        private static void Open(string target, string arguments = "")
        {
            try
            {
                Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true });
            }
            catch (Win32Exception exception)
            {
                ShowError(exception.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
                window.Dispose();
                icon.Dispose();
                menu.Dispose();
                host.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
