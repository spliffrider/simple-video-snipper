namespace SimpleVideoSnipper;

[Flags]
internal enum ShortcutModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

internal readonly record struct CaptureShortcut(ShortcutModifiers Modifiers, Keys Key)
{
    private const ShortcutModifiers SupportedModifiers = ShortcutModifiers.Control | ShortcutModifiers.Alt |
        ShortcutModifiers.Shift | ShortcutModifiers.Windows;

    public static CaptureShortcut Default { get; } = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, Keys.R);

    internal static IReadOnlyList<Keys> AvailableKeys { get; } = Enumerable.Range((int)Keys.A, 26).Select(key => (Keys)key)
        .Concat(Enumerable.Range((int)Keys.D0, 10).Select(key => (Keys)key))
        .Concat(Enumerable.Range((int)Keys.F1, 24).Select(key => (Keys)key).Where(key => key != Keys.F12))
        .Concat(Enumerable.Range((int)Keys.NumPad0, 10).Select(key => (Keys)key))
        .Concat([Keys.Space, Keys.Insert, Keys.Delete, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
            Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Pause, Keys.PrintScreen]).ToArray();

    internal bool IsValid => Modifiers != ShortcutModifiers.None && (Modifiers & ~SupportedModifiers) == 0 &&
        AvailableKeys.Contains(Key);

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    internal static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"Numpad {key - Keys.NumPad0}",
        Keys.PageUp => "Page Up",
        Keys.PageDown => "Page Down",
        Keys.PrintScreen => "Print Screen",
        _ => key.ToString(),
    };
}
