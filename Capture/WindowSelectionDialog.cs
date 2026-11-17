using System.Runtime.InteropServices;
using System.Text;

namespace SimpleVideoSnipper;

internal sealed class WindowSelectionDialog : Form
{
    private readonly ListBox list = new() { Width = 440, Height = 260 };

    public WindowSelectionDialog()
    {
        Text = "Select a window to record";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;

        list.AccessibleName = "Open windows";
        list.Items.AddRange(OpenWindows());
        var next = new Button { Text = "Continue", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        list.SelectedIndexChanged += (_, _) => next.Enabled = list.SelectedIndex >= 0;
        list.DoubleClick += (_, _) =>
        {
            if (next.Enabled)
                DialogResult = DialogResult.OK;
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([next, cancel]);
        var layout = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(12),
        };
        if (list.Items.Count == 0)
            layout.Controls.Add(new Label { Text = "Open a window to record and try again.", AutoSize = true });
        layout.Controls.AddRange([list, buttons]);
        Controls.Add(layout);
        AcceptButton = next;
        CancelButton = cancel;
    }

    public WindowChoice? Selection => list.SelectedItem as WindowChoice;

    internal sealed record WindowChoice(nint Handle, string Title, Rectangle Bounds)
    {
        public override string ToString() => Title;
    }

    private static WindowChoice[] OpenWindows()
    {
        var result = new List<WindowChoice>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var process);
            var length = GetWindowTextLength(handle);
            if (process == Environment.ProcessId || length == 0 || !IsWindowVisible(handle) ||
                (GetWindowLong(handle, -20) & 0x80) != 0 ||
                (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                return true;

            var title = new StringBuilder(length + 1);
            if (GetWindowText(handle, title, title.Capacity) > 0 && GetWindowRect(handle, out var rect))
                result.Add(new WindowChoice(handle, title.ToString(),
                    Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)));
            return true;
        }, 0);
        return result.OrderBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private delegate bool WindowCallback(nint window, nint context);

    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint context);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder title, int size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
