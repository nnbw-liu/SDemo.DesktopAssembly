namespace SDemo.Hub;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>纯解析的全局热键描述。注册由 WPF 层完成，避免把 Win32 依赖带进核心库。</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey, string Normalized)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPACE"] = 0x20,
        ["TAB"] = 0x09,
        ["ENTER"] = 0x0D,
        ["RETURN"] = 0x0D,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["BACKSPACE"] = 0x08,
        ["DELETE"] = 0x2E,
        ["DEL"] = 0x2E,
        ["INSERT"] = 0x2D,
        ["INS"] = 0x2D,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["PAGEUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
    };

    public static bool TryParse(string? text, out HotkeyGesture? gesture, out string? error)
    {
        gesture = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键为空";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? keyName = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (keyName is not null)
                    {
                        error = "只能包含一个主键";
                        return false;
                    }

                    keyName = raw;
                    break;
            }
        }

        if (keyName is null)
        {
            error = "缺少主键";
            return false;
        }

        if (!TryParseKey(keyName, out var vk))
        {
            error = $"无法识别的按键：{keyName}";
            return false;
        }

        // 功能键可以单独使用；其他键必须带修饰键，避免抢占普通输入。
        if (modifiers == HotkeyModifiers.None && (vk < 0x70 || vk > 0x87))
        {
            error = "请至少使用 Ctrl / Alt / Shift / Win 中的一个修饰键";
            return false;
        }

        var name = keyName.ToUpperInvariant() switch
        {
            "RETURN" => "Enter",
            "ESCAPE" => "Esc",
            "DEL" => "Delete",
            "INS" => "Insert",
            "PAGEUP" => "PageUp",
            "PAGEDOWN" => "PageDown",
            _ => char.ToUpperInvariant(keyName[0]) + keyName[1..].ToUpperInvariant(),
        };

        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(name);

        gesture = new HotkeyGesture(modifiers, vk, string.Join("+", parts));
        return true;
    }

    private static bool TryParseKey(string name, out uint vk)
    {
        vk = 0;
        if (NamedKeys.TryGetValue(name, out vk))
        {
            return true;
        }

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z')
            {
                vk = c;
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        if (name.Length is >= 2 and <= 3 && (name[0] is 'F' or 'f')
            && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        return false;
    }
}
