using System.Text.Json;
using SDemo.Hub.Contracts;

namespace SDemo.Hub;

/// <summary>
/// 监听公共目录的 *.json。变更做 300ms 去抖后全量重扫，避免半写入文件被当成坏配置。
/// 不判断应用存活；存活与能力由 HubConnectionMonitor 负责。
/// </summary>
public sealed class HubAppDiscovery : IDisposable
{
    private readonly HubAppRegistry _registry;
    private readonly IHubLog _log;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private readonly object _scanGate = new();
    private bool _disposed;

    public HubAppDiscovery(HubPaths paths, HubAppRegistry registry, IHubLog? log = null)
    {
        Paths = paths;
        _registry = registry;
        _log = log ?? NullHubLog.Instance;
        _debounce = new Timer(_ => Scan(), null, Timeout.Infinite, Timeout.Infinite);

        Directory.CreateDirectory(paths.AppsDirectory);

        _watcher = new FileSystemWatcher(paths.AppsDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false,
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
    }

    public HubPaths Paths { get; }

    public IReadOnlyList<string> LastIssues => _issues;

    private readonly List<string> _issues = [];

    public void Start()
    {
        Scan();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>立即全量重扫。可安全并发调用。</summary>
    public void Scan()
    {
        lock (_scanGate)
        {
            if (_disposed)
            {
                return;
            }

            _issues.Clear();
            var discovered = new List<DiscoveredApp>();

            foreach (var file in Directory.EnumerateFiles(Paths.AppsDirectory, "*.json").OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                string json;
                try
                {
                    if (new FileInfo(file).Length > HubConfigValidator.MaxFileBytes)
                    {
                        var tooLarge = $"配置文件超过 {HubConfigValidator.MaxFileBytes / 1024} KB";
                        _issues.Add($"{name}: {tooLarge}");
                        discovered.Add(new DiscoveredApp(InvalidConfig(name), file, Invalid: true, Error: tooLarge));
                        continue;
                    }

                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
                catch (IOException ex)
                {
                    _issues.Add($"{name}: 读取失败（{ex.Message}）");
                    continue;
                }

                var config = HubJson.ParseConfig(json);
                if (config is null)
                {
                    _issues.Add($"{name}: JSON 解析失败");
                    discovered.Add(new DiscoveredApp(
                        InvalidConfig(name),
                        file,
                        Invalid: true,
                        Error: "配置文件不是合法 JSON"));
                    continue;
                }

                // Hub 自己的配置也在公共目录里，但它不是「受管应用」，不进应用列表。
                if (string.Equals(config.Kind, "hub", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var validation = HubConfigValidator.Validate(config, name);
                if (validation is not null)
                {
                    _issues.Add($"{name}: {validation}");
                    discovered.Add(new DiscoveredApp(config with { DisplayName = config.DisplayName ?? name }, file, Invalid: true, Error: validation));
                    continue;
                }

                if (discovered.Count(item => !item.Invalid) >= HubConfigValidator.MaxApps)
                {
                    var limit = $"应用数量超过上限 {HubConfigValidator.MaxApps}";
                    _issues.Add($"{name}: {limit}");
                    discovered.Add(new DiscoveredApp(config, file, Invalid: true, Error: limit));
                    continue;
                }

                discovered.Add(new DiscoveredApp(config, file, Invalid: false, Error: null));
            }

            _registry.Synchronize(discovered);
            foreach (var issue in _issues)
            {
                _log.Warn($"发现配置异常：{issue}");
            }
        }
    }

    private static HubAppConfig InvalidConfig(string fileName) => new()
    {
        AppId = Path.GetFileNameWithoutExtension(fileName),
        DisplayName = Path.GetFileNameWithoutExtension(fileName),
        Kind = "app",
        State = "stopped",
    };

    private void OnChanged(object sender, FileSystemEventArgs e) => Schedule();

    private void OnRenamed(object sender, RenamedEventArgs e) => Schedule();

    private void Schedule()
    {
        if (_disposed)
        {
            return;
        }

        _debounce.Change(300, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
        }
    }
}

