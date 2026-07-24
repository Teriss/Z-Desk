namespace ZDesk.Models;

[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed record HotKeyGesture(HotKeyModifiers Modifiers, uint VirtualKey, string DisplayText);
