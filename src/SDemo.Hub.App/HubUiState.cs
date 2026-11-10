using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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

/// <summary>组件设置表单的一行（widget-contract §8 的子集投影）。</summary>
public sealed record WidgetConfigItem(
    string Key,
    string Title,
    string? Description,
    string Kind,
    double? Minimum,
    double? Maximum,
    IReadOnlyList<string>? Options,
    string? Format,
    JsonElement Value)
{
    public bool IsBoolean => Kind == WidgetConfigSchema.Boolean;

    public bool IsNumber => Kind is WidgetConfigSchema.Integer or WidgetConfigSchema.Number;

    public bool IsInteger => Kind == WidgetConfigSchema.Integer;

    public bool IsEnum => Options is { Count: > 0 };

    public string Display => WidgetConfigSchema.ToDisplay(Value);

    public bool IsOn => Value.ValueKind == JsonValueKind.True;
}

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

    /// <summary>组件窗口标题栏的齿轮按钮：请求控制面展开该组件的设置区。</summary>
    public (string AppId, string WidgetId)? PendingWidgetSettings { get; private set; }

    public void RequestWidgetSettings(string appId, string widgetId)
    {
        PendingWidgetSettings = (appId, widgetId);
        Navigate("overview");
    }

    /// <summary>页面消费设置请求后调用，避免之后的状态刷新反复抢焦点。</summary>
    public (string AppId, string WidgetId)? ConsumePendingWidgetSettings()
    {
        var pending = PendingWidgetSettings;
        PendingWidgetSettings = null;
        return pending;
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
        .Where(entry => !IsHidden(entry.AppId))
        .Select(ToView)
        .ToArray();

    /// <summary>被用户隐藏的应用；控制台列表不展示，但注册表/目录仍保留。</summary>
    public IReadOnlyList<HubAppView> HiddenApps => _registry.Snapshot()
        .Where(entry => IsHidden(entry.AppId))
        .Select(ToView)
        .ToArray();

    public bool IsAppHidden(string appId) => IsHidden(appId);

    public void SetAppHidden(string appId, bool hidden)
    {
        _settings.Update(settings =>
        {
            settings.HiddenApps.RemoveAll(id => id.Equals(appId, StringComparison.OrdinalIgnoreCase));
            if (hidden)
            {
                settings.HiddenApps.Add(appId);
            }
        });
        Changed?.Invoke();
    }

    private bool IsHidden(string appId) =>
        _settings.Settings.HiddenApps.Any(id => id.Equals(appId, StringComparison.OrdinalIgnoreCase));

    private static HubAppView ToView(HubAppEntry entry) => new(
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
        !string.IsNullOrWhiteSpace(entry.Config.Executable) && File.Exists(entry.Config.Executable));

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

    /// <summary>
    /// 应用声明过的组件参数投影成表单行。组件不在桌面上时展示声明里的默认值
    /// （设置只对桌面上的实例生效，值存在 widgets.json）。
    /// </summary>
    public IReadOnlyList<WidgetConfigItem> WidgetConfig(string appId, WidgetDescriptor widget)
    {
        var properties = WidgetConfigSchema.Properties(widget);
        if (properties.Count == 0)
        {
            return [];
        }

        var stored = _widgets.ConfigValues(appId, widget.Id);
        var items = new List<WidgetConfigItem>(properties.Count);
        foreach (var pair in properties)
        {
            var property = pair.Value;
            var value = WidgetConfigSchema.Coerce(
                property,
                stored.TryGetValue(pair.Key, out var saved) ? saved : null);
            items.Add(new WidgetConfigItem(
                pair.Key,
                string.IsNullOrWhiteSpace(property.Title) ? pair.Key : property.Title,
                property.Description,
                WidgetConfigSchema.NormalizeType(property.Type),
                property.Minimum,
                property.Maximum,
                property.Enum,
                property.Format,
                value));
        }

        return items;
    }

    public (bool Ok, string Message) SetWidgetConfigBool(string appId, string widgetId, string key, bool value) =>
        _widgets.SetWidgetConfig(appId, widgetId, key, JsonSerializer.SerializeToElement(value));

    public (bool Ok, string Message) SetWidgetConfigNumber(string appId, string widgetId, string key, string? text, bool integer)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, "请输入数字");
        }

        if (integer)
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                ? _widgets.SetWidgetConfig(appId, widgetId, key, JsonSerializer.SerializeToElement(whole))
                : (false, "请输入整数");
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
               && double.IsFinite(number)
            ? _widgets.SetWidgetConfig(appId, widgetId, key, JsonSerializer.SerializeToElement(number))
            : (false, "请输入数字");
    }

    public (bool Ok, string Message) SetWidgetConfigText(string appId, string widgetId, string key, string? text) =>
        _widgets.SetWidgetConfig(appId, widgetId, key, JsonSerializer.SerializeToElement(text ?? string.Empty));

    /// <summary>打开系统的文件/目录选择框，给 format:"path" 的参数取值用。</summary>
    public string? PickWidgetPath(bool folder)
    {
        var owner = Application.Current?.MainWindow;
        if (folder)
        {
            var folderDialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择目录" };
            return folderDialog.ShowDialog(owner) == true ? folderDialog.FolderName : null;
        }

        var fileDialog = new Microsoft.Win32.OpenFileDialog { Title = "选择文件", CheckFileExists = true };
        return fileDialog.ShowDialog(owner) == true ? fileDialog.FileName : null;
    }

    public bool IsWidgetOnDesktop(string appId, string widgetId) => _widgets.IsOnDesktop(appId, widgetId);

    public string WidgetTheme(string appId, string widgetId) => _widgets.Theme(appId, widgetId);

    public void SetWidgetTheme(string appId, string widgetId, string? theme) =>
        _widgets.SetTheme(appId, widgetId, theme);

    public bool WidgetClickThrough(string appId, string widgetId) => _widgets.ClickThrough(appId, widgetId);

    public void SetWidgetClickThrough(string appId, string widgetId, bool enabled) =>
        _widgets.SetClickThrough(appId, widgetId, enabled);

    public bool WidgetTopmost(string appId, string widgetId) => _widgets.Topmost(appId, widgetId);

    public void SetWidgetTopmost(string appId, string widgetId, bool topmost) =>
        _widgets.SetTopmost(appId, widgetId, topmost);

    public void SendWidgetToBottom(string appId, string widgetId) =>
        _widgets.SendToBottom(appId, widgetId);

    public (double Width, double Height) WidgetSize(string appId, string widgetId) =>
        _widgets.Size(appId, widgetId);

    public double WidgetOpacity(string appId, string widgetId) => _widgets.Opacity(appId, widgetId);

    public void SetWidgetOpacity(string appId, string widgetId, double opacity) =>
        _widgets.SetOpacity(appId, widgetId, opacity);

    public bool WidgetVisible(string appId, string widgetId) => _widgets.IsWidgetVisible(appId, widgetId);

    public void SetWidgetVisible(string appId, string widgetId, bool visible) =>
        _widgets.SetWidgetVisible(appId, widgetId, visible);

    public bool WidgetEnabled(string appId, string widgetId) => _widgets.IsWidgetEnabled(appId, widgetId);

    public void SetWidgetEnabled(string appId, string widgetId, bool enabled) =>
        _widgets.SetWidgetEnabled(appId, widgetId, enabled);

    public string? ToggleWidgetsHotkey => _widgets.ToggleWidgetsHotkey;

    public string? ToggleClickThroughHotkey => _widgets.ToggleClickThroughHotkey;

    public (bool Ok, string Message) SetShellHotkey(bool clickThrough, string? text) =>
        _widgets.SetShellHotkey(clickThrough, text);

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
            $"WebView2：{WebView2Version}",
            $"内存：{MemoryUsage}",
            $"目录服务：{(IsDirectoryRunning ? "运行中" : "未启动")}",
            $"MCP：{HubMcpUrl}",
            $"HTTP：{HubDirectoryUrl}",
            $"公共目录：{AppsDirectory}",
            $"日志目录：{LogsDirectory}",
            $"在线：{OnlineCount} / 已发现：{Apps.Count}" + (HiddenApps.Count > 0 ? $"（隐藏 {HiddenApps.Count}）" : string.Empty),
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

        var recentErrors = RecentErrors;
        if (recentErrors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("最近错误：");
            lines.AddRange(recentErrors.Select(error => $"  {error}"));
        }

        if (Issues.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("配置文件问题：");
            lines.AddRange(Issues.Select(issue => $"  {issue}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string WebView2Version
    {
        get
        {
            try
            {
                return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch
            {
                return "未安装或不可用";
            }
        }
    }

    public string MemoryUsage
    {
        get
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return $"{process.WorkingSet64 / 1024d / 1024d:0} MB（工作集）";
            }
            catch
            {
                return "不可用";
            }
        }
    }

    /// <summary>最近 5 条 WARN/ERROR；从今天的日志与上一份滚动日志里取。</summary>
    public IReadOnlyList<string> RecentErrors
    {
        get
        {
            var lines = ReadLogLines(maxLines: 800)
                .Where(line => line.Contains("[WARN]", StringComparison.Ordinal) || line.Contains("[ERROR]", StringComparison.Ordinal))
                .TakeLast(5)
                .ToArray();
            return lines;
        }
    }

    /// <summary>诊断页展示的日志尾部；按天文件读不到时回退到 .1.log。</summary>
    public string LogTail(int lines = 200)
    {
        var tail = ReadLogLines(maxLines: Math.Clamp(lines, 1, 2000));
        return tail.Count == 0 ? "（今天还没有日志）" : string.Join(Environment.NewLine, tail);
    }

    private IReadOnlyList<string> ReadLogLines(int maxLines)
    {
        var today = Path.Combine(LogsDirectory, $"hub-{DateTime.Now:yyyyMMdd}.log");
        var archive = Path.ChangeExtension(today, ".1.log");
        var queue = new Queue<string>();
        foreach (var path in new[] { archive, today })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    queue.Enqueue(line);
                    if (queue.Count > maxLines)
                    {
                        queue.Dequeue();
                    }
                }
            }
            catch (IOException)
            {
                // 日志正被写入时忽略这一份，下一次刷新再读。
            }
        }

        return queue.ToArray();
    }

    public void Rescan() => _discovery.Scan();

    private void OnRegistryChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private void OnWidgetsChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _registry.Changed -= OnRegistryChanged;
        _widgets.Changed -= OnWidgetsChanged;
    }
}


