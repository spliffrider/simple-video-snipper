using System.Runtime.InteropServices;

namespace SimpleVideoSnipper;

internal sealed class GlobalCaptureShortcut : NativeWindow, IDisposable
{
    private static readonly nint MessageOnlyWindow = new(-3);
    private const int ShortcutId = 1;
    private const int WmHotkey = 0x0312;
    private const uint Modifiers = 0x4003; // MOD_NOREPEAT | MOD_CONTROL | MOD_ALT
    private readonly Action invoke;

    public GlobalCaptureShortcut(Action invoke)
    {
        this.invoke = invoke;
        CreateHandle(new CreateParams { Parent = MessageOnlyWindow });
        IsRegistered = RegisterHotKey(Handle, ShortcutId, Modifiers, (uint)Keys.R);
    }

    public bool IsRegistered { get; }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam == ShortcutId)
        {
            invoke();
            return;
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (IsRegistered)
            UnregisterHotKey(Handle, ShortcutId);
        DestroyHandle();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
