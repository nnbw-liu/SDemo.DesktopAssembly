using System.Globalization;
using SDemo.Shell.Contracts;
using SDemo.Shell.Wpf.Settings;

namespace SDemo.Shell.Wpf;

/// <summary>GET /v1/host/info 的数据来源（主题 / 强调色 / 语言 / 显示器集合）。</summary>
public sealed class HostInfoProvider : IHostInfoProvider
{
    private readonly IMonitorProvider _monitors;
    private readonly SystemThemeMonitor? _theme;

    public HostInfoProvider(IMonitorProvider monitors, SystemThemeMonitor? theme = null)
    {
        _monitors = monitors;
        _theme = theme;
    }

    public HostInfoResponse GetHostInfo() => new()
    {
        HostVersion = HostVersion.Product,
        HostApiVersion = ShellConstants.HostApiVersion,
        Theme = _theme?.Theme ?? ReadTheme(),
        Accent = _theme?.Accent ?? ReadAccent(),
        Language = "zh-CN",
        Monitors = _monitors.Monitors.Select(m => new MonitorDescriptor
        {
            Key = m.Key,
            DeviceName = m.DeviceName,
            Primary = m.IsPrimary,
            Dpi = m.Dpi,
            Bounds = m.DipBounds,
            WorkArea = m.DipWorkArea,
        }).ToList(),
    };

    private static string ReadTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
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
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var value = key?.GetValue("AccentColor");
            if (value is int abgr)
            {
                // DWM 存的是 0xAABBGGRR
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

