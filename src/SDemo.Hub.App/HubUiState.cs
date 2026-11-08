using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using SDemo.Hub;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.App;

public sealed record HubAppView(
    string AppId,
    string DisplayName,
    string Version,
    string Kind,
    HubConnectionStatus Status,
    string Endpoint,
    int WidgetCount,
    string? Error,
    string? McpUrl,
    string? AgentUrl,
    bool IsRunning,
    bool HasMainWindow,
    bool CanLaunch);

/// <summary>Blazor UI 的唯一数据源。后台事件只做转发，投影成不可变快照。</summary>
public sealed class HubUiState : IDisposable
{
    private readonly HubAppRegistry _registry;
    private readonly HubAppDiscovery _discovery;
    private readonly HubConnectionMonitor _monitor;
    private readonly WidgetHostService _widgets;
    private readonly HubServer _hubServer;
    private readonly HubSettingsStore _settings;
    private readonly Dictionary<string, string?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public HubUiState(
        HubPaths paths,
        HubAppRegistry registry,
        HubAppDiscovery discovery,
        HubConnectionMonitor monitor,
        WidgetHostService widgets,
        HubServer hubServer,
        HubSettingsStore settings)
    {
        Paths = paths;
        _registry = registry;
        _discovery = discovery;
        _monitor = monitor;
        _widgets = widgets;
        _hubServer = hubServer;
        _settings = settings;
        _widgets.LaunchAppAsync = LaunchAppAsync;
        _registry.Changed += OnRegistryChanged;
        _widgets.Changed += OnWidgetsChanged;
    }

    public HubPaths Paths { get; }

    public string AppsDirectory => Paths.AppsDirectory;

    public string HubMcpUrl => _hubServer.McpUrl;

    public string HubDirectoryUrl => _hubServer.DirectoryUrl;

    public bool IsDirectoryRunning => _hubServer.IsRunning;

    public string HubConfigFile => Paths.HubConfigFile;

    public string LogsDirectory => Paths.LogsDirectory;

    public string Version => "0.2.0";

    public IReadOnlyList<string> Issues => _discovery.LastIssues;

    public event Action? Changed;

    public event Action? NavigationRequested;

    public string? RequestedPage { get; private set; }

    public void Navigate(string page)
    {
        RequestedPage = page;
        NavigationRequested?.Invoke();
    }

    public bool LaunchAtLogin => _settings.Settings.LaunchAtLogin;

    public bool StartHidden => _settings.Settings.StartHidden;

    public bool HideFromCapture => _settings.Settings.HideFromCapture;

    public void SetHideFromCapture(bool enabled) => _widgets.SetHideFromCapture(enabled);

    public string Theme => NormalizeTheme(_settings.Settings.Theme);

    public void SetTheme(string? theme)
    {
        _settings.Update(settings => settings.Theme = NormalizeTheme(theme));
        Changed?.Invoke();
    }

    private static string NormalizeTheme(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "auto",
    };

    public void SetLaunchAtLogin(bool enabled)
    {
        _settings.Update(settings => settings.LaunchAtLogin = enabled);
        AutostartService.Apply(enabled);
        Changed?.Invoke();
    }

    public void SetStartHidden(bool enabled)
    {
        _settings.Update(settings => settings.StartHidden = enabled);
        Changed?.Invoke();
    }

    public string? WidgetHotkey(string appId, string widgetId) => _widgets.Hotkey(appId, widgetId);

    public (bool Ok, string Message) SetWidgetHotkey(string appId, string widgetId, string? text) =>
        _widgets.SetHotkey(appId, widgetId, text);

    public IReadOnlyList<HubAppView> Apps => _registry.Snapshot()
        .Select(entry => new HubAppView(
            entry.AppId,
            entry.DisplayName,
            entry.Version,
            entry.Kind,
            entry.Status,
            entry.Endpoint,
            entry.Widgets.Length,
            entry.LastError,
            HubDirectory.NormalizeLoopback(entry.McpUrl, entry.Endpoint),
            HubDirectory.NormalizeLoopback(entry.AgentUrl, entry.Endpoint),
            entry.IsRunning,
            entry.HasMainWindow,
            !string.IsNullOrWhiteSpace(entry.Config.Executable) && File.Exists(entry.Config.Executable)))
        .ToArray();

    public int OnlineCount => _registry.Snapshot().Count(entry => entry.Status == HubConnectionStatus.Online);

    public int IssueCount => _discovery.LastIssues.Count;

    /// <summary>读取应用声明的本地图标，转成 data URI；失败或过大时回退首字母头像。</summary>
    public string? AppIconData(string appId)
    {
        var entry = _registry.Snapshot()
            .FirstOrDefault(item => item.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || string.IsNullOrWhiteSpace(entry.Config.Icon))
        {
            return null;
        }

        try
        {
            var declared = entry.Config.Icon;
            var relative = declared.TrimStart('/', '\\');
            var path = Path.Combine(Path.GetDirectoryName(entry.SourcePath) ?? string.Empty, relative);
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length > 512 * 1024)
            {
                return null;
            }

            var key = $"{path}|{info.LastWriteTimeUtc.Ticks}";
            if (_iconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream",
            };
            var data = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
            _iconCache[key] = data;
            return data;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<WidgetDescriptor> Widgets(string appId) =>
        _registry.Snapshot()
            .FirstOrDefault(entry => entry.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase))
            ?.Widgets ?? [];

    public bool IsWidgetOnDesktop(string appId, string widgetId) => _widgets.IsOnDesktop(appId, widgetId);

    public string WidgetTheme(string appId, string widgetId) => _widgets.Theme(appId, widgetId);

    public void SetWidgetTheme(string appId, string widgetId, string? theme) =>
        _widgets.SetTheme(appId, widgetId, theme);

    public bool WidgetClickThrough(string appId, string widgetId) => _widgets.ClickThrough(appId, widgetId);

    public void SetWidgetClickThrough(string appId, string widgetId, bool enabled) =>
        _widgets.SetClickThrough(appId, widgetId, enabled);

    public bool AddWidget(string appId, string widgetId)
    {
        var entry = _registry.Snapshot()
            .FirstOrDefault(e => e.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));
        var widget = entry?.Widgets.FirstOrDefault(w => w.Id.Equals(widgetId, StringComparison.OrdinalIgnoreCase));
        return entry is not null && widget is not null && _widgets.Add(entry, widget);
    }

    public void RemoveWidget(string appId, string widgetId) => _widgets.Remove(appId, widgetId);

    public async Task<(bool Ok, string Message)> OpenMainWindowAsync(string appId)
    {
        var entry = Find(appId);
        if (entry is null)
        {
            return (false, "应用不存在");
        }

        if (entry.Status != HubConnectionStatus.Online)
        {
            return (false, "应用当前不在线");
        }

        if (!entry.HasMainWindow)
        {
            return (false, "应用未声明主窗口，无法打开");
        }

        if (string.IsNullOrWhiteSpace(entry.Config.Token))
        {
            return (false, "连接配置缺少 token");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{entry.Config.ResolvedBaseUrl}/v1/main-window/open");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Config.Token);
            using var response = await Http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return (true, "已请求应用打开主窗口");
            }

            if ((int)response.StatusCode == 409)
            {
                return (false, "应用当前没有可打开的主窗口（可能以 --headless 启动）");
            }

            return (false, $"应用返回 HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"无法连接应用：{ex.Message}");
        }
    }

    /// <summary>用户显式点击后才启动；启动前后都不做自动拉起。</summary>
    public async Task<(bool Ok, string Message)> LaunchAppAsync(string appId)
    {
        var entry = Find(appId);
        if (entry is null)
        {
            return (false, "应用不存在");
        }

        var executable = entry.Config.Executable;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return (false, "配置里的 executable 不存在，无法启动");
        }

        var confirm = _settings.IsApproved(executable)
            ? MessageBoxResult.Yes
            : ConfirmLaunch($"启动 {entry.DisplayName}？\n\n{executable}");
        if (confirm != MessageBoxResult.Yes)
        {
            return (false, "已取消启动");
        }

        _settings.Approve(executable);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = string.IsNullOrWhiteSpace(entry.Config.WorkingDirectory)
                    ? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
                    : entry.Config.WorkingDirectory,
            };
            foreach (var argument in entry.Config.Arguments ?? [])
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "进程未能启动");
            }

            _registry.Update(appId, current => current.BeginLaunch());

            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(entry.Config.StartupTimeoutMs, 1000, 60000));
            var deadline = DateTimeOffset.Now + timeout;
            while (DateTimeOffset.Now < deadline)
            {
                await Task.Delay(250);
                var current = Find(appId);
                if (current is null)
                {
                    continue;
                }

                if (current.Status == HubConnectionStatus.Online)
                {
                    return (true, "应用已上线");
                }

                if (current.Status == HubConnectionStatus.Error)
                {
                    return (false, current.LastError ?? "应用启动失败");
                }

                if (process.HasExited)
                {
                    var error = $"应用进程已退出（退出码 {process.ExitCode}）";
                    _registry.Update(appId, item => item.MarkError(error));
                    return (false, error);
                }

                await _monitor.ProbeAsync(current, force: true);
            }

            var timeoutMessage = $"等待应用上线超时（{timeout.TotalSeconds:0} 秒）";
            _registry.Update(appId, item => item.MarkError(timeoutMessage));
            return (false, timeoutMessage);
        }
        catch (Exception ex)
        {
            _registry.Update(appId, item => item.MarkError(ex.Message));
            return (false, $"启动失败：{ex.Message}");
        }
    }

    private static MessageBoxResult ConfirmLaunch(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return MessageBox.Show(message, "SDemo Hub", MessageBoxButton.YesNo, MessageBoxImage.Question);
        }

        return dispatcher.Invoke(() =>
            MessageBox.Show(message, "SDemo Hub", MessageBoxButton.YesNo, MessageBoxImage.Question));
    }

    private HubAppEntry? Find(string appId) =>
        _registry.Snapshot()
            .FirstOrDefault(entry => entry.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));

    public Task RefreshAsync() => _monitor.ProbeAllAsync();

    public void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // 打不开资源管理器不影响诊断信息展示。
        }
    }

    public string DiagnosticsSummary()
    {
        var lines = new List<string>
        {
            $"SDemo Hub {Version}",
            $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
            $"目录服务：{(IsDirectoryRunning ? "运行中" : "未启动")}",
            $"MCP：{HubMcpUrl}",
            $"HTTP：{HubDirectoryUrl}",
            $"公共目录：{AppsDirectory}",
            $"日志目录：{LogsDirectory}",
            $"在线：{OnlineCount} / 已发现：{Apps.Count}",
            $"文件问题：{Issues.Count}",
            string.Empty,
        };

        foreach (var app in Apps)
        {
            lines.Add($"[{app.Status}] {app.DisplayName} ({app.AppId}) v{app.Version} {app.Endpoint}");
            if (!string.IsNullOrWhiteSpace(app.Error))
            {
                lines.Add($"  {app.Error}");
            }
        }

        if (Issues.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("配置文件问题：");
            lines.AddRange(Issues.Select(issue => $"  {issue}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void OnRegistryChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private void OnWidgetsChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _registry.Changed -= OnRegistryChanged;
        _widgets.Changed -= OnWidgetsChanged;
    }
}


