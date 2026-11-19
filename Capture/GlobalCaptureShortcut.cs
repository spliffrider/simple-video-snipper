using System.Runtime.InteropServices;

namespace SimpleVideoSnipper;

internal sealed class GlobalCaptureShortcut : NativeWindow, IDisposable
{
    private static readonly nint MessageOnlyWindow = new(-3);
    private const int WmHotkey = 0x0312;
    private const uint NoRepeat = 0x4000;
    private readonly Action invoke;
    private int activeId;

    public GlobalCaptureShortcut(CaptureShortcut binding, Action invoke)
    {
        this.invoke = invoke;
        CreateHandle(new CreateParams { Parent = MessageOnlyWindow });
        Binding = binding;
        if (Register(binding, 1))
            activeId = 1;
    }

    public bool IsRegistered => activeId != 0;
    public CaptureShortcut Binding { get; private set; }
    public event Action? Changed;

    public bool TryApply(CaptureShortcut next, Action persist)
    {
        if (!next.IsValid)
            return false;
        if (next == Binding && IsRegistered)
        {
            persist();
            return true;
        }

        // Reserve the new chord while the old one is still registered.
        var newId = activeId == 1 ? 2 : 1;
        if (!Register(next, newId))
        {
            if (next != Binding)
                return false;
            // An unavailable startup shortcut can be left as-is while other settings are saved.
            persist();
            return true;
        }

        try
        {
            persist();
        }
        catch
        {
            UnregisterHotKey(Handle, newId);
            throw;
        }

        if (IsRegistered)
            UnregisterHotKey(Handle, activeId);
        activeId = newId;
        Binding = next;
        Changed?.Invoke();
        return true;
    }

    private bool Register(CaptureShortcut shortcut, int id) =>
        shortcut.IsValid && RegisterHotKey(Handle, id, (uint)shortcut.Modifiers | NoRepeat, (uint)shortcut.Key);

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && IsRegistered && message.WParam == activeId &&
            ((uint)message.LParam & 0xF) == (uint)Binding.Modifiers && ((uint)message.LParam >> 16) == (uint)Binding.Key)
        {
            invoke();
            return;
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            if (IsRegistered)
                UnregisterHotKey(Handle, activeId);
            activeId = 0;
            DestroyHandle();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
