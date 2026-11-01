using System.Text;

namespace SDemo.Shell.Contracts;

/// <summary>全局热键的修饰键位（与 Win32 <c>MOD_*</c> 按位一一对应，便于平台层直接映射）。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,

    Alt = 1,

    Control = 2,

    Shift = 4,

    Windows = 8,
}

/// <summary>
/// 全局热键（DA-10 / DA-70）：字符串形式 <c>Ctrl+Alt+T</c> 的解析、规范化与展示。
/// 纯数据，不依赖 Win32；平台层把 <see cref="VirtualKey"/> 交给 <c>RegisterHotKey</c>。
/// 规则：至少一个修饰键（避免把普通按键变成全局钩子）、键名大小写不敏感、修饰键顺序归一。
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey, string KeyName)
{
    private const int VkSpace = 0x20;
    private const int VkTab = 0x09;
    private const int VkEnter = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkBack = 0x08;
    private const int VkDelete = 0x2E;
    private const int VkInsert = 0x2D;
    private const int VkHome = 0x24;
    private const int VkEnd = 0x23;
    private const int VkPageUp = 0x21;
    private const int VkPageDown = 0x22;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkPrintScreen = 0x2C;
    private const int VkPause = 0x13;

    /// <summary>规范文本：修饰键按 <c>Ctrl / Alt / Shift / Win</c> 排列，键名大写。</summary>
    public string ToCanonicalString() => FormatModifiers(Modifiers) + KeyName;

    public override string ToString() => ToCanonicalString();

    /// <summary>解析 <c>Ctrl+Alt+T</c>；非法 / 缺少修饰键 / 未知键名一律返回 false，不抛异常。</summary>
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (!TryParseModifier(parts[index], out var modifier))
            {
                return false;
            }

            if ((modifiers & modifier) != 0)
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            return false;
        }

        if (!TryParseKeyName(parts[^1], out var virtualKey, out var keyName))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, keyName);
        return true;
    }

    /// <summary>把 <c>Ctrl+Alt+T</c> 解析成 <see cref="HotkeyGesture"/>；失败返回 null。</summary>
    public static HotkeyGesture? Parse(string? text) => TryParse(text, out var gesture) ? gesture : null;

    /// <summary>把修饰键描述成人类可读的短标签（例如 <c>Ctrl+Alt</c>）。</summary>
    public static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if ((modifiers & HotkeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & HotkeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & HotkeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & HotkeyModifiers.Windows) != 0)
        {
            parts.Add("Win");
        }

        return parts.Count == 0 ? string.Empty : string.Join('+', parts) + '+';
    }

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                modifier = HotkeyModifiers.Control;
                return true;
            case "alt":
                modifier = HotkeyModifiers.Alt;
                return true;
            case "shift":
                modifier = HotkeyModifiers.Shift;
                return true;
            case "win":
            case "windows":
            case "cmd":
                modifier = HotkeyModifiers.Windows;
                return true;
            default:
                modifier = HotkeyModifiers.None;
                return false;
        }
    }

    private static bool TryParseKeyName(string token, out int virtualKey, out string keyName)
    {
        virtualKey = 0;
        keyName = string.Empty;
        var upper = token.ToUpperInvariant();
        if (upper.Length == 1)
        {
            var ch = upper[0];
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                keyName = upper;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                keyName = upper;
                return true;
            }

            return false;
        }

        if (upper.Length is >= 2 and <= 3 && upper[0] == 'F' && int.TryParse(upper[1..], out var f))
        {
            if (f is >= 1 and <= 24)
            {
                virtualKey = 0x70 + f - 1;
                keyName = upper;
                return true;
            }

            return false;
        }

        var named = upper switch
        {
            "SPACE" => (VkSpace, "Space"),
            "TAB" => (VkTab, "Tab"),
            "ENTER" => (VkEnter, "Enter"),
            "ESC" or "ESCAPE" => (VkEscape, "Esc"),
            "BACKSPACE" or "BACK" => (VkBack, "Backspace"),
            "DELETE" or "DEL" => (VkDelete, "Delete"),
            "INSERT" or "INS" => (VkInsert, "Insert"),
            "HOME" => (VkHome, "Home"),
            "END" => (VkEnd, "End"),
            "PAGEUP" or "PGUP" => (VkPageUp, "PageUp"),
            "PAGEDOWN" or "PGDN" => (VkPageDown, "PageDown"),
            "UP" or "UPARROW" => (VkUp, "Up"),
            "DOWN" or "DOWNARROW" => (VkDown, "Down"),
            "LEFT" or "LEFTARROW" => (VkLeft, "Left"),
            "RIGHT" or "RIGHTARROW" => (VkRight, "Right"),
            "PRINTSCREEN" or "PRTSC" => (VkPrintScreen, "PrintScreen"),
            "PAUSE" => (VkPause, "Pause"),
            _ => (-1, string.Empty),
        };

        if (named.Item1 < 0)
        {
            return false;
        }

        virtualKey = named.Item1;
        keyName = named.Item2;
        return true;
    }
}
