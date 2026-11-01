using SDemo.Shell.Diagnostics;
using Win32Registry = Microsoft.Win32.Registry;

namespace SDemo.Shell.Wpf.Startup;

/// <summary>
/// 开机自启（DA-11 / DA-71 / Q6）：只注册一个 Run 项（<c>SDemo.Shell</c>），应用不各自注册；
/// 默认开启，设置里可关。
/// </summary>
public sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SDemo.Shell";

    private readonly IShellLog _log;

    public AutostartService(IShellLog? log = null) => _log = log ?? NullShellLog.Instance;

    public void Apply(bool enabled, string executablePath)
    {
        try
        {
            using var key = Win32Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                _log.Warn("无法打开注册表 Run 项，开机自启设置未生效。");
                return;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{executablePath}\"");
                _log.Info($"已注册开机自启：{executablePath}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Info("已关闭开机自启。");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("写入开机自启注册表失败。", ex);
        }
    }
}
