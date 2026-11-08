namespace SDemo.Hub.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// 应用启动时写入公共目录的连接配置。字段与主仓 docs/contracts/hub-app.schema.json 对齐。
/// 只信任握手返回的能力；这里的 kind/capabilities 仅作离线展示的提示。
/// </summary>
public sealed record HubAppConfig
{
    public int SchemaVersion { get; init; } = 1;

    public required string AppId { get; init; }

    public string? DisplayName { get; init; }

    public string? Version { get; init; }

    /// <summary>app | agent | hub。未知值按 app 处理。</summary>
    public string Kind { get; init; } = "app";

    public string? Executable { get; init; }

    public string[]? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public int Port { get; init; }

    public string? BaseUrl { get; init; }

    public string? Token { get; init; }

    public int Pid { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>running | stopped。</summary>
    public string State { get; init; } = "running";

    /// <summary>应用允许 Hub 等待上线的最长时间。</summary>
    public int StartupTimeoutMs { get; init; } = 8000;

    /// <summary>回环相对路径，如 /icon.png。</summary>
    public string? Icon { get; init; }

    public McpDescriptor? Mcp { get; init; }

    public AgentDescriptor? Agent { get; init; }

    public MainWindowDescriptor? MainWindow { get; init; }

    public WidgetDescriptor[]? Widgets { get; init; }

    [JsonIgnore]
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string ResolvedBaseUrl =>
        !string.IsNullOrWhiteSpace(BaseUrl)
            ? BaseUrl.TrimEnd('/')
            : $"http://127.0.0.1:{Port}";
}
