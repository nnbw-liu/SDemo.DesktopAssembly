using Microsoft.Win32;

namespace SDemo.Hub.App;

/// <summary>HKCU Run 的开机自启；不写计划任务、不需要管理员。</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SDemo.Hub";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    return;
                }

                key.SetValue(ValueName, $"\"{exe}\" --hidden");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表不可写时不影响 Hub 运行，只是失去自启。
        }
    }
}
