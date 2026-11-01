using System.Text.Json.Serialization;

namespace SDemo.Shell.Contracts;

/// <summary>Agent 侧只读注册表条目（host-endpoints §5.2 / DA-90）。</summary>
public sealed class AppDescriptor
{
    public string AppId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary><see cref="AppStates"/> 之一。</summary>
    public string State { get; set; } = AppStates.Stopped;

    public bool Enabled { get; set; } = true;

    public bool HashValid { get; set; } = true;

    public string? WidgetId { get; set; }

    public string? Renderer { get; set; }

    public string? Layer { get; set; }

    public string HostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    public string? RuntimePath { get; set; }

    public RuntimePointer? Runtime { get; set; }

    public McpManifest? Mcp { get; set; }

    public HostError? LastError { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class RuntimePointer
{
    public int Pid { get; set; }

    public int Port { get; set; }

    public string? RuntimePath { get; set; }
}

public sealed class HostError
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public static HostError Create(string code, string message) => new() { Code = code, Message = message };
}

public sealed class AppsResponse
{
    public int Revision { get; set; }

    public List<AppDescriptor> Apps { get; set; } = new();
}

public sealed class HostStatus
{
    public string HostVersion { get; set; } = string.Empty;

    public string HostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    public int Pid { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public int RegistryRevision { get; set; }
}

public sealed class EnsureRequest
{
    public int? TimeoutMs { get; set; }
}

public sealed class EnsureResponse
{
    public string AppId { get; set; } = string.Empty;

    public string State { get; set; } = AppStates.Stopped;

    public RuntimePointer Runtime { get; set; } = new();
}

/// <summary>应用的 <c>runtime.json</c>（host-protocol §4）。</summary>
public sealed class RuntimeFile
{
    public int Pid { get; set; }

    public int Port { get; set; }

    public string Token { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public string? AppVersion { get; set; }

    public string? HostApiVersion { get; set; }
}

/// <summary>宿主的 <c>host.json</c>（host-endpoints §3）。</summary>
public sealed class HostFile
{
    public int Pid { get; set; }

    public int Port { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public string HostVersion { get; set; } = string.Empty;

    public string HostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    public string ClientTokenPath { get; set; } = ShellConstants.ClientTokenFileName;

    public int RegistryRevision { get; set; }
}

/// <summary>应用自己的健康检查响应（GET /v1/health）。</summary>
public sealed class HealthResponse
{
    public bool Ok { get; set; }

    public int Pid { get; set; }

    public DateTimeOffset Since { get; set; }
}

public sealed class HandshakeRequest
{
    public string HostVersion { get; set; } = string.Empty;

    public string HostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    public string WidgetId { get; set; } = string.Empty;
}

public sealed class HandshakeResponse
{
    public string AppVersion { get; set; } = string.Empty;

    public string HostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    public List<string> Capabilities { get; set; } = new();
}

/// <summary>GET /v1/host/info（app 面）。</summary>
public sealed class HostInfoResponse
{
    public string HostVersion { get; set; } = string.Empty;

    public string HostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    /// <summary>dark / light。</summary>
    public string Theme { get; set; } = "light";

    public string Accent { get; set; } = "#2563EB";

    public string Language { get; set; } = "zh-CN";

    public List<MonitorDescriptor> Monitors { get; set; } = new();
}

public sealed class MonitorDescriptor
{
    public string Key { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public bool Primary { get; set; }

    public int Dpi { get; set; }

    public ScreenRect Bounds { get; set; }

    public ScreenRect WorkArea { get; set; }
}

/// <summary>native 组件注册（POST /v1/apps/{appId}/register）。</summary>
public sealed class AppRegistration
{
    public string WidgetId { get; set; } = string.Empty;

    /// <summary>窗口句柄；0 视为无效。</summary>
    public long Hwnd { get; set; }

    /// <summary>per-monitor-v2 等，仅用于诊断。</summary>
    public string? DpiAware { get; set; }
}

public sealed class AppRegistrationResponse
{
    public bool Accepted { get; set; }

    public string WidgetId { get; set; } = string.Empty;

    public string? Reason { get; set; }
}
