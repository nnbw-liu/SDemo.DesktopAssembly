using SDemo.Shell.Contracts;

namespace SDemo.Shell;

/// <summary>
/// 壳的目录约定（desktop-shell §5 / shell-storage §2）。
/// 安装目录与数据目录分离：<c>%APPDATA%</c> 放清单与布局，<c>%LOCALAPPDATA%</c> 放端口/token/日志。
/// </summary>
public sealed class ShellPaths
{
    public ShellPaths(string appDataRoot, string localAppDataRoot, string? logsDirectory = null)
    {
        AppDataRoot = appDataRoot;
        LocalAppDataRoot = localAppDataRoot;
        LogsDirectory = logsDirectory ?? Path.Combine(localAppDataRoot, "logs", "desktop-assembly");
    }

    /// <summary><c>%APPDATA%\SDemo\DesktopAssembly</c>。</summary>
    public string AppDataRoot { get; }

    /// <summary><c>%LOCALAPPDATA%\SDemo\DesktopAssembly</c>。</summary>
    public string LocalAppDataRoot { get; }

    /// <summary><c>%LOCALAPPDATA%\SDemo\logs\desktop-assembly</c>（host-protocol §12）。</summary>
    public string LogsDirectory { get; }

    public string AppsDirectory => Path.Combine(AppDataRoot, "apps");

    public string SettingsFilePath => Path.Combine(AppDataRoot, "settings.json");

    public string WidgetsFilePath => Path.Combine(AppDataRoot, "widgets.json");

    public string RuntimeDirectory => Path.Combine(LocalAppDataRoot, "runtime");

    public string HostFilePath => Path.Combine(RuntimeDirectory, ShellConstants.HostFileName);

    public string ClientTokenFilePath => Path.Combine(RuntimeDirectory, ShellConstants.ClientTokenFileName);

    public string RuntimeFilePath(string appId) => Path.Combine(RuntimeDirectory, appId + ".json");

    /// <summary>应用自有数据目录（宿主不读、不写，只在卸载确认文案里提示）。</summary>
    public static string AppDataDirectoryFor(string appId)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, "SDemo", "Apps", appId);
    }

    public static ShellPaths CreateDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ShellPaths(
            Path.Combine(appData, "SDemo", "DesktopAssembly"),
            Path.Combine(localAppData, "SDemo", "DesktopAssembly"),
            Path.Combine(localAppData, "SDemo", "logs", "desktop-assembly"));
    }

    /// <summary>测试用：把两份目录都挂到同一个根下，互不污染真实用户目录。</summary>
    public static ShellPaths ForTestRoot(string root) => new(
        Path.Combine(root, "appdata"),
        Path.Combine(root, "localappdata"),
        Path.Combine(root, "logs"));

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(AppsDirectory);
        Directory.CreateDirectory(LocalAppDataRoot);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public override string ToString() => $"AppData={AppDataRoot}, LocalAppData={LocalAppDataRoot}";
}
