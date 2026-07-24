using ZDesk.Models;

namespace ZDesk.Services;

public static class HotKeyParser
{
    private static readonly IReadOnlyDictionary<string, uint> NamedKeys = BuildNamedKeys();

    public static bool TryParse(string? text, out HotKeyGesture? gesture, out string error)
    {
        gesture = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请输入快捷键，例如 Ctrl+Alt+T。";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = HotKeyModifiers.None;
        string? keyToken = null;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotKeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotKeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotKeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotKeyModifiers.Windows;
                    break;
                default:
                    if (keyToken is not null)
                    {
                        error = "快捷键只能包含一个普通按键。";
                        return false;
                    }

                    keyToken = part;
                    break;
            }
        }

        if (modifiers == HotKeyModifiers.None)
        {
            error = "快捷键必须至少包含 Ctrl、Alt、Shift 或 Win 中的一个修饰键。";
            return false;
        }

        if (keyToken is null || !TryGetVirtualKey(keyToken, out var virtualKey, out var displayKey))
        {
            error = "普通按键无效。支持字母、数字、F1-F24、方向键、Home、End、PageUp、PageDown、Insert 和 Delete。";
            return false;
        }

        var display = BuildDisplayText(modifiers, displayKey);
        gesture = new HotKeyGesture(modifiers, virtualKey, display);
        return true;
    }

    private static bool TryGetVirtualKey(string token, out uint virtualKey, out string display)
    {
        var normalized = token.Trim().ToUpperInvariant();
        if (normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            virtualKey = normalized[0];
            display = normalized;
            return true;
        }

        if (normalized.StartsWith('F') && int.TryParse(normalized[1..], out var function) && function is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + function - 1);
            display = $"F{function}";
            return true;
        }

        if (NamedKeys.TryGetValue(normalized, out virtualKey))
        {
            display = normalized switch
            {
                "PRIOR" => "PageUp",
                "NEXT" => "PageDown",
                _ => char.ToUpperInvariant(normalized[0]) + normalized[1..].ToLowerInvariant()
            };
            return true;
        }

        display = string.Empty;
        return false;
    }

    private static string BuildDisplayText(HotKeyModifiers modifiers, string key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotKeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotKeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotKeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotKeyModifiers.Windows)) parts.Add("Win");
        parts.Add(key);
        return string.Join('+', parts);
    }

    private static IReadOnlyDictionary<string, uint> BuildNamedKeys() =>
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PRIOR"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["NEXT"] = 0x22,
            ["INSERT"] = 0x2D,
            ["DELETE"] = 0x2E,
            ["SPACE"] = 0x20
        };
}
