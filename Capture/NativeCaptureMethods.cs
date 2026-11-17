using System.Runtime.InteropServices;

namespace SimpleVideoSnipper;

internal static class NativeCaptureMethods
{
    private const uint ExcludeFromCapture = 0x11;

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    public static bool ExcludeWindow(Form form) => SetWindowDisplayAffinity(form.Handle, ExcludeFromCapture);
}
