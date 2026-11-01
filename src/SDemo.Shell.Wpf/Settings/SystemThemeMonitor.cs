using System.Windows.Media;
using Microsoft.Win32;
using SDemo.Shell.Diagnostics;
using Win32Registry = Microsoft.Win32.Registry;

namespace SDemo.Shell.Wpf.Settings;

/// <summary>
/// 主题跟随（DA-64）：读系统「应用主题」与 DWM 强调色，系统设置变化时即时通知。
/// 宿主只做「读 + 下发」，组件自己决定怎么用色。
/// </summary>
public sealed class SystemThemeMonitor : IDisposable
{
    private readonly IShellLog _log;
    private bool _started;

    public SystemThemeMonitor(IShellLog? log = null) => _log = log ?? NullShellLog.Instance;

    public string Theme { get; private set; } = "light";

    public string Accent { get; private set; } = "#2563EB";

    public event EventHandler? Changed;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Read();
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            _log.Warn("订阅系统主题变化失败（仍可按需手动刷新）。", ex);
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch
        {
            // 退出阶段忽略
        }
    }

    /// <summary>重新读取并广播（面板/托盘也可主动调用）。</summary>
    public void Refresh() => Read();

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle)
        {
            Read();
        }
    }

    private void Read()
    {
        var theme = ReadTheme();
        var accent = ReadAccent();
        if (theme == Theme && accent == Accent)
        {
            return;
        }

        var previous = (Theme, Accent);
        Theme = theme;
        Accent = accent;
        _log.Info($"系统主题变化：{previous.Accent}/{previous.Theme} → {Accent}/{Theme}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string ReadTheme()
    {
        try
        {
            using var key = Win32Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int light && light == 0 ? "dark" : "light";
        }
        catch
        {
            return "light";
        }
    }

    private static string ReadAccent()
    {
        try
        {
            using var key = Win32Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                // DWM 存的是 0xAABBGGRR。
                var red = (byte)(abgr & 0xFF);
                var green = (byte)((abgr >> 8) & 0xFF);
                var blue = (byte)((abgr >> 16) & 0xFF);
                return $"#{red:X2}{green:X2}{blue:X2}";
            }
        }
        catch
        {
            // 忽略
        }

        return "#2563EB";
    }
}

/// <summary>面板 / 骨架 / 占位卡的配色（dark / light 两套，强调色来自系统）。</summary>
public sealed class ShellTheme
{
    public static ShellTheme From(string theme, string accent) => new(theme, accent);

    public ShellTheme(string theme, string accent)
    {
        IsDark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        Accent = Parse(accent, Color.FromRgb(0x25, 0x63, 0xEB));
        Background = IsDark ? Color.FromRgb(0x16, 0x18, 0x1E) : Color.FromRgb(0xF4, 0xF5, 0xF8);
        Surface = IsDark ? Color.FromRgb(0x1D, 0x20, 0x28) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        Elevated = IsDark ? Color.FromRgb(0x2A, 0x2E, 0x3A) : Color.FromRgb(0xEC, 0xEF, 0xF4);
        Text = IsDark ? Color.FromRgb(0xE8, 0xE8, 0xF0) : Color.FromRgb(0x1B, 0x1D, 0x22);
        Muted = IsDark ? Color.FromRgb(0x9A, 0x9A, 0xAA) : Color.FromRgb(0x60, 0x66, 0x72);
        Border = IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0xDC, 0xE0, 0xE7);
        Danger = Color.FromRgb(0xF8, 0x71, 0x71);
    }

    public bool IsDark { get; }

    public Color Accent { get; }

    public Color Background { get; }

    public Color Surface { get; }

    public Color Elevated { get; }

    public Color Text { get; }

    public Color Muted { get; }

    public Color Border { get; }

    public Color Danger { get; }

    public Brush BackgroundBrush => new SolidColorBrush(Background);

    public Brush SurfaceBrush => new SolidColorBrush(Surface);

    public Brush ElevatedBrush => new SolidColorBrush(Elevated);

    public Brush TextBrush => new SolidColorBrush(Text);

    public Brush MutedBrush => new SolidColorBrush(Muted);

    public Brush BorderBrush => new SolidColorBrush(Border);

    public Brush AccentBrush => new SolidColorBrush(Accent);

    public Brush DangerBrush => new SolidColorBrush(Danger);

    private static Color Parse(string hex, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return fallback;
        }
    }
}
