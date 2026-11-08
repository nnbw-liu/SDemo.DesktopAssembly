namespace SDemo.Hub.Contracts;

/// <summary>GET /v1/info 的响应。握手是能力的唯一真相源。</summary>
public sealed record HubAppInfo
{
    public int SchemaVersion { get; init; } = 1;

    public required string AppId { get; init; }

    public string? DisplayName { get; init; }

    public string? Version { get; init; }

    /// <summary>app | agent | hub。</summary>
    public string Kind { get; init; } = "app";

    public string[]? Capabilities { get; init; }

    public McpDescriptor? Mcp { get; init; }

    public AgentDescriptor? Agent { get; init; }

    public MainWindowDescriptor? MainWindow { get; init; }

    public WidgetDescriptor[]? Widgets { get; init; }
}
