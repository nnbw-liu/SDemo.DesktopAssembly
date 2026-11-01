using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Discovery;
using SDemo.Shell.Registry;

namespace SDemo.Shell.Install;

public sealed record InstallResult(bool Success, string? AppId, string? Message, string? InstalledPath)
{
    public static InstallResult Fail(string message) => new(false, null, message, null);
}

public sealed record UninstallResult(bool Success, string AppId, string? Message, string? DataDirectory);

/// <summary>
/// 安装 / 卸载 / 升级（DA-28 / DA-98）：zip → apps\&lt;appId&gt;\，替换目录 + 重新校验哈希；
/// 卸载只删程序目录，保留 <c>%APPDATA%\SDemo\Apps\&lt;appId&gt;\</c> 数据。
/// </summary>
public sealed class AppInstaller
{
    private static readonly Regex IdRegex = new("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.Compiled);

    private readonly ShellPaths _paths;
    private readonly AppRegistry _registry;
    private readonly ManifestLoader _loader;
    private readonly IShellLog _log;

    public AppInstaller(ShellPaths paths, AppRegistry registry, ManifestLoader loader, IShellLog? log = null)
    {
        _paths = paths;
        _registry = registry;
        _loader = loader;
        _log = log ?? NullShellLog.Instance;
    }

    public InstallResult InstallFromZip(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            return InstallResult.Fail("安装包不存在。");
        }

        var staging = CreateStagingDirectory();
        try
        {
            if (!ExtractSafely(zipPath, staging, out var error))
            {
                return InstallResult.Fail(error ?? "解压失败。");
            }

            return InstallFromStaging(staging);
        }
        finally
        {
            CleanupDirectory(staging);
        }
    }

    public InstallResult InstallFromDirectory(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return InstallResult.Fail("源目录不存在。");
        }

        var staging = CreateStagingDirectory();
        try
        {
            CopyDirectory(sourceDirectory, staging);
            return InstallFromStaging(staging);
        }
        finally
        {
            CleanupDirectory(staging);
        }
    }

    public UninstallResult Uninstall(string appId)
    {
        var entry = _registry.Find(appId);
        if (entry is null)
        {
            return new UninstallResult(false, appId, "应用未安装。", null);
        }

        var target = entry.AppDirectory;
        try
        {
            Directory.Delete(target, recursive: true);
            _registry.Scan();
            var data = ShellPaths.AppDataDirectoryFor(appId);
            _log.Info($"已卸载 {appId}；用户数据保留在 {data}");
            return new UninstallResult(true, appId, null, data);
        }
        catch (Exception ex)
        {
            _log.Error($"卸载 {appId} 失败。", ex);
            return new UninstallResult(false, appId, ex.Message, null);
        }
    }

    private InstallResult InstallFromStaging(string staging)
    {
        var manifestPath = Path.Combine(staging, "app.json");
        if (!File.Exists(manifestPath))
        {
            return InstallResult.Fail("安装包根目录缺少 app.json。");
        }

        AppManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AppManifest>(File.ReadAllText(manifestPath), ShellJson.File);
        }
        catch (Exception ex)
        {
            return InstallResult.Fail($"app.json 解析失败：{ex.Message}");
        }

        if (manifest is null || !IdRegex.IsMatch(manifest.Id ?? string.Empty))
        {
            return InstallResult.Fail("app.json 的 id 非法。");
        }

        if (!File.Exists(Path.Combine(staging, "widget.json")))
        {
            return InstallResult.Fail("安装包缺少 widget.json。");
        }

        // 先校验再落盘：哈希不符的包不进入 apps 目录（DA-76）。暂存目录名不是 appId，用清单 id 当身份。
        var probe = _loader.Load(staging, manifest.Id);
        if (!probe.IsLaunchable)
        {
            return InstallResult.Fail(probe.FailureSummary ?? "安装包清单未通过校验。");
        }

        var appId = manifest!.Id ?? string.Empty;
        var target = Path.Combine(_paths.AppsDirectory, appId);
        var backup = target + ".old-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        try
        {
            Directory.CreateDirectory(_paths.AppsDirectory);
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }

            Directory.Move(staging, target);
            var validated = _loader.Load(target);
            if (!validated.IsLaunchable)
            {
                if (Directory.Exists(backup))
                {
                    Directory.Delete(target, recursive: true);
                    Directory.Move(backup, target);
                }

                return InstallResult.Fail(validated.FailureSummary ?? "安装后校验失败。");
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            _registry.Scan();
            _log.Info($"应用已安装：{manifest.Id} → {target}");
            return new InstallResult(true, manifest.Id, null, target);
        }
        catch (Exception ex)
        {
            _log.Error($"安装 {manifest.Id} 失败。", ex);
            return InstallResult.Fail($"安装失败：{ex.Message}");
        }
    }

    private static string CreateStagingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sdemo-shell-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool ExtractSafely(string zipPath, string destination, out string? error)
    {
        error = null;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) && string.IsNullOrEmpty(entry.FullName))
                {
                    continue;
                }

                if (entry.FullName.Contains("..", StringComparison.Ordinal)
                    || Path.IsPathRooted(entry.FullName))
                {
                    error = "安装包包含非法路径（zip slip）。";
                    return false;
                }

                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                var root = Path.GetFullPath(destination);
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    error = "安装包包含逃逸目录的条目。";
                    return false;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"解压失败：{ex.Message}";
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不阻塞安装
        }
    }
}

