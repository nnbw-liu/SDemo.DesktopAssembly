using System.Windows;
using System.Windows.Forms;

namespace SDemo.Hub.App;

/// <summary>显示器标识与工作区。Windows 的 DeviceName 在同一次登录会话内稳定，够做“按显示器记忆”。</summary>
internal static class MonitorLayout
{
    public static bool Exists(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && Screen.AllScreens.Any(screen => screen.DeviceName.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static string? KeyForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Screen.FromHandle(hwnd).DeviceName;
        }
        catch
        {
            return null;
        }
    }

    public static Rect PrimaryWorkArea => SystemParameters.WorkArea;
}
