using System.Text.Json;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Runtime;

/// <summary><c>runtime.json</c> 文件读写（host-protocol §4）。</summary>
public sealed class RuntimeFileStore : IRuntimeFileStore
{
    private readonly ShellPaths _paths;
    private readonly IShellLog _log;

    public RuntimeFileStore(ShellPaths paths, IShellLog? log = null)
    {
        _paths = paths;
        _log = log ?? NullShellLog.Instance;
    }

    public string GetPath(string appId) => _paths.RuntimeFilePath(appId);

    public RuntimeFile? Read(string appId)
    {
        var path = GetPath(appId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var runtime = JsonSerializer.Deserialize<RuntimeFile>(File.ReadAllText(path), ShellJson.File);
            if (runtime is null || runtime.Pid <= 0 || runtime.Port <= 0 || string.IsNullOrWhiteSpace(runtime.Token))
            {
                return null;
            }

            return runtime;
        }
        catch (Exception ex)
        {
            _log.Warn($"runtime.json 读取失败：{path}", ex);
            return null;
        }
    }

    public void Delete(string appId)
    {
        try
        {
            var path = GetPath(appId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"删除 runtime.json 失败：{appId}", ex);
        }
    }

    public IReadOnlyList<string> EnumerateAppIds()
    {
        try
        {
            if (!Directory.Exists(_paths.RuntimeDirectory))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(_paths.RuntimeDirectory, "*.json")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(name => !string.Equals(name, "host", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
