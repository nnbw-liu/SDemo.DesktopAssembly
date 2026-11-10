using SDemo.Hub.Contracts;

namespace SDemo.Hub;

public enum HubConnectionStatus
{
    Unknown,
    Launching,
    Connecting,
    Online,
    Offline,
    Error,
    Incompatible,
    Unauthorized,
    Stopped,
    Invalid,
}

/// <summary>注册表里的一条应用。UI 只读它，后台探测只改它。</summary>
public sealed class HubAppEntry
{
    public HubAppEntry(HubAppConfig config, string sourcePath)
    {
        Config = config;
        SourcePath = sourcePath;
        LastSeenAt = DateTimeOffset.Now;
    }

    public HubAppConfig Config { get; internal set; }

    public string SourcePath { get; }

    public HubConnectionStatus Status { get; internal set; } = HubConnectionStatus.Unknown;

    public HubAppInfo? Info { get; internal set; }

    public string? LastError { get; internal set; }

    public DateTimeOffset LastSeenAt { get; internal set; }

    public DateTimeOffset LastCheckedAt { get; internal set; }

    /// <summary>连续握手失败次数，用于 1s/2s/5s 退避；成功一次即清零。</summary>
    public int FailureCount { get; internal set; }

    /// <summary>下一次允许探测的时间点，防止失败后每个 tick 都重试。</summary>
    public DateTimeOffset NextProbeAt { get; internal set; }

    public string AppId => Config.AppId;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Info?.DisplayName) ? Info!.DisplayName! :
        !string.IsNullOrWhiteSpace(Config.DisplayName) ? Config.DisplayName! :
        Config.AppId;

    public string Version => Info?.Version ?? Config.Version ?? "—";

    public string Kind => Info?.Kind ?? Config.Kind;

    public string Endpoint => Config.ResolvedBaseUrl;

    public WidgetDescriptor[] Widgets => Status == HubConnectionStatus.Incompatible
        ? []
        : Info?.Widgets ?? Config.Widgets ?? [];

    public string? McpUrl
    {
        get
        {
            var descriptor = Info?.Mcp ?? Config.Mcp;
            if (descriptor is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.Url))
            {
                return descriptor.Url;
            }

            return string.IsNullOrWhiteSpace(descriptor.Path)
                ? null
                : $"{Endpoint}{descriptor.Path}";
        }
    }

    /// <summary>
    /// Agent 端点。契约允许 agent 只给 <c>path</c>（相对 baseUrl），所以这里必须像 <see cref="McpUrl"/>
    /// 一样把 path 拼成完整 URL；只取 <c>url</c> 会让只声明 path 的应用永远进不了 Agent 目录。
    /// </summary>
    public string? AgentUrl
    {
        get
        {
            var descriptor = Info?.Agent ?? Config.Agent;
            if (descriptor is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.Url))
            {
                return descriptor.Url;
            }

            return string.IsNullOrWhiteSpace(descriptor.Path)
                ? null
                : $"{Endpoint}{descriptor.Path}";
        }
    }

    public bool IsRunning => Config.IsRunning;

    public bool HasMainWindow => Info?.MainWindow is not null || Config.MainWindow is not null;

    /// <summary>用户显式启动后进入 Launching，清空上次失败计数。</summary>
    public void BeginLaunch()
    {
        Status = HubConnectionStatus.Launching;
        LastError = null;
        FailureCount = 0;
        NextProbeAt = DateTimeOffset.UtcNow;
    }

    /// <summary>启动超时/进程退出等硬失败，直接进入 Error。</summary>
    public void MarkError(string error)
    {
        Status = HubConnectionStatus.Error;
        LastError = error;
    }
}



