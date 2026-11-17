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
        private readonly GlobalCaptureShortcut shortcut;
        private AreaRecordingToolbar? areaToolbar;
        private bool selecting;

        public RecordingTray(RecordingHost host, bool openOnLaunch)
        {
            this.host = host;
            menu = new ContextMenuStrip();
            var selectArea = menu.Items.Add("Record area", null, (_, _) => BeginAreaCapture());
            menu.Items.Add("Open Chrome recorder", null, (_, _) => OpenRecorder());
            var stop = menu.Items.Add("Stop recording", null, (_, _) => host.RequestStop());
            menu.Items.Add("Open recordings folder", null, (_, _) => Open(host.Folder));
            menu.Items.Add(new ToolStripSeparator());
            var exit = menu.Items.Add("Exit", null, (_, _) => ExitThread());
            menu.Opening += (_, _) =>
            {
                selectArea.Enabled = !selecting && !host.IsRecording;
                stop.Enabled = host.IsRecording;
                exit.Enabled = !selecting && !host.IsRecording;
            };

            icon = new NotifyIcon
            {
                Text = "Simple Video Snipper",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };
            var areaButton = new Button { Text = "Record area", AutoSize = true };
            areaButton.Click += (_, _) => BeginAreaCapture();
            var openButton = new Button { Text = "Open Chrome recorder", AutoSize = true };
            openButton.Click += (_, _) => OpenRecorder();
            var stopButton = new Button { Text = "Stop recording", AutoSize = true, Enabled = false };
            stopButton.Click += (_, _) => host.RequestStop();
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
            };
            shortcut = new GlobalCaptureShortcut(BeginAreaCapture);
            var shortcutLabel = new Label
            {
                Text = shortcut.IsRegistered ? "Area shortcut: Ctrl+Alt+R" : "Ctrl+Alt+R is unavailable. Use Record area.",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 3),
            };
            actions.Controls.AddRange([areaButton, openButton, stopButton, shortcutLabel]);
            actions.SetFlowBreak(stopButton, true);
            if (shortcut.IsRegistered)
                selectArea.Text = "Record area (Ctrl+Alt+R)";

            window = new Form
            {
                Text = "Simple Video Snipper",
                ClientSize = new Size(440, 90),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                AcceptButton = openButton,
            };
            icon.DoubleClick += (_, _) =>
            {
                if (areaToolbar is not null)
                {
                    areaToolbar.Activate();
                    return;
                }
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
            refresh.Tick += (_, _) =>
            {
                var recording = host.IsRecording;
                stopButton.Enabled = recording;
                areaButton.Enabled = !recording;
            };
            refresh.Start();
            if (openOnLaunch)
                window.Shown += (_, _) => OpenRecorder();
            window.Show();
        }

        private void OpenRecorder() => Open("chrome.exe", host.RecorderUrl);

        private async void BeginAreaCapture()
        {
            if (areaToolbar is not null)
            {
                areaToolbar.Activate();
                return;
            }
            if (selecting)
                return;
            if (host.IsRecording)
            {
                window.Show();
                window.Activate();
                return;
            }

            var showWindowOnCancel = window.Visible;
            window.Hide();
            try
            {
                selecting = true;
                SelectedArea area;
                try
                {
                    using var overlay = new AreaSelectionOverlay(Screen.FromPoint(Cursor.Position));
                    NativeCaptureMethods.ExcludeWindow(overlay);
                    if (overlay.ShowDialog() != DialogResult.OK || overlay.Selection.Region.IsEmpty)
                    {
                        if (showWindowOnCancel)
                            window.Show();
                        return;
                    }

                    area = overlay.Selection;
                }
                finally
                {
                    selecting = false;
                }

                using var session = host.TryStartRecording();
                if (session is null)
                {
                    ShowError("Finish the current recording before starting another.");
                    return;
                }

                using var toolbar = new AreaRecordingToolbar(area, session);
                areaToolbar = toolbar;
                var excluded = NativeCaptureMethods.ExcludeWindow(toolbar);
                toolbar.Show();
                if (!excluded && toolbar.Bounds.IntersectsWith(area.DesktopRegion))
                    toolbar.Hide(); // The tray Stop remains available for full-display selections.

                var saved = await Task.Run(() => AreaRecorder.RecordAsync(area, host.Folder, session.StopRequested));
                if (saved is not null)
                    icon.ShowBalloonTip(4000, "Area recording saved", Path.GetFileName(saved), ToolTipIcon.Info);
            }
            catch (Exception exception)
            {
                ShowError(exception.GetBaseException().Message);
            }
            finally
            {
                areaToolbar = null;
            }
        }

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
                shortcut.Dispose();
                window.Dispose();
                icon.Dispose();
                menu.Dispose();
                host.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
