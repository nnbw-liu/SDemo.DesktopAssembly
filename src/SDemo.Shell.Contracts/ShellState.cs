using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Shell.Contracts;

/// <summary><c>settings.json</c>：只放壳级开关（shell-storage §3）。</summary>
public sealed class SettingsFile
{
    public int SchemaVersion { get; set; } = ShellConstants.ShellStateSchemaVersion;

    public StartupSettings Startup { get; set; } = new();

    public ComponentSettings Components { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    public PrivacySettings Privacy { get; set; } = new();

    public string Language { get; set; } = "zh-CN";

    public LoggingSettings Logging { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static SettingsFile CreateDefault() => new();

    /// <summary>把越界值夹回允许范围；缺省值补默认。</summary>
    public void Normalize()
    {
        Startup ??= new StartupSettings();
        Components ??= new ComponentSettings();
        Hotkeys ??= new HotkeySettings();
        Privacy ??= new PrivacySettings();
        Logging ??= new LoggingSettings();
        NormalizeHotkey(Hotkeys);
        Components.Limit = Math.Clamp(Components.Limit, 1, ShellConstants.MaxComponentsLimit);
        Logging.KeepDays = Math.Clamp(Logging.KeepDays, 1, 90);
        if (string.IsNullOrWhiteSpace(Language))
        {
            Language = "zh-CN";
        }

        if (!Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            Language = "zh-CN";
        }

        Logging.Level = Logging.Level is "error" or "warn" or "info" or "debug" ? Logging.Level : "info";
    }

    /// <summary>热键统一成规范文本；非法值清空（避免运行时静默失效）。</summary>
    private static void NormalizeHotkey(HotkeySettings settings)
    {
        settings.ToggleClickThrough = Normalize(settings.ToggleClickThrough);
        settings.ToggleWidgets = Normalize(settings.ToggleWidgets);
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return HotkeyGesture.TryParse(raw, out var gesture) ? gesture!.ToCanonicalString() : null;
    }
}

public sealed class StartupSettings
{
    public bool LaunchAtLogin { get; set; } = true;

    public bool StartHidden { get; set; } = true;
}

public sealed class ComponentSettings
{
    public int Limit { get; set; } = ShellConstants.DefaultComponentsLimit;
}

public sealed class HotkeySettings
{
    public string? ToggleClickThrough { get; set; }

    public string? ToggleWidgets { get; set; }
}

/// <summary>隐私开关（DA-45）：截屏 / 录屏隐藏组件窗口。</summary>
public sealed class PrivacySettings
{
    public bool HideFromCapture { get; set; }
}

public sealed class LoggingSettings
{
    public string Level { get; set; } = "info";

    public int KeepDays { get; set; } = 14;
}

/// <summary><c>widgets.json</c>：按 widgetId 的组件 UI 状态（shell-storage §4）。</summary>
public sealed class WidgetsFile
{
    public int SchemaVersion { get; set; } = ShellConstants.ShellStateSchemaVersion;

    public Dictionary<string, WidgetState> Widgets { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static WidgetsFile CreateDefault() => new();
}

/// <summary>单个组件的 UI 状态。只存用户可改的部分，不复制清单字段（shell-storage §9）。</summary>
public sealed class WidgetState
{
    public bool Enabled { get; set; } = true;

    public bool Visible { get; set; } = true;

    /// <summary><see cref="StartupPolicies"/> 之一或 null（null = 用 app.json 的默认）。</summary>
    public string? StartupOverride { get; set; }

    public string Layer { get; set; } = WidgetRendering.Floating;

    public bool AlwaysOnTop { get; set; }

    public bool ClickThrough { get; set; }

    public double Opacity { get; set; } = 0.92;

    public WidgetAnchor Anchor { get; set; } = new();

    public string? LastMonitorKey { get; set; }

    /// <summary>monitorKey → 虚拟桌面绝对 DIP 矩形。</summary>
    public Dictionary<string, ScreenRect> Positions { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonElement>? Config { get; set; }

    public Dictionary<string, string>? Permissions { get; set; }

    /// <summary>热键覆盖（DA-70）：null = 用清单里的 <c>activation.hotkey</c>；空串 = 用户关闭。</summary>
    public string? HotkeyOverride { get; set; }

    /// <summary>托盘入口覆盖（DA-72）：null = 用清单里的 <c>activation.showInTray</c>。</summary>
    public bool? ShowInTray { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>锚点：角 + DIP 偏移（shell-storage §4）。</summary>
public sealed class WidgetAnchor
{
    public string Corner { get; set; } = "top-right";

    public int OffsetX { get; set; } = 24;

    public int OffsetY { get; set; } = 24;
}
