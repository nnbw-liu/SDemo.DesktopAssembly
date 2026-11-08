using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using SDemo.Hub.Contracts;

namespace SDemo.Hub;

/// <summary>
/// 周期性调用应用的 GET /v1/info，把「配置文件里的声明」升级为「握手确认的能力」。
/// 探测失败不删除条目：离线应用仍要显示，并保留上次成功的信息。
/// 在线状态连续失败按 1s / 2s / 5s 退避，第 3 次转 Error；成功一次即恢复。
/// </summary>
public sealed class HubConnectionMonitor : IDisposable
{
    private static readonly TimeSpan OnlineProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] FailureBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    private readonly HubAppRegistry _registry;
    private readonly IHubLog _log;
    private readonly HttpClient _http;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool _disposed;

    public HubConnectionMonitor(
        HubAppRegistry registry,
        IHubLog? log = null,
        TimeSpan? interval = null,
        HttpMessageHandler? handler = null)
    {
        _registry = registry;
        _log = log ?? NullHubLog.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(3);
        Interval = interval ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => _ = ProbeAllAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>调度 tick 间隔；具体重试时间由每条记录的 NextProbeAt 控制。</summary>
    public TimeSpan Interval { get; }

    public void Start()
    {
        _timer.Change(TimeSpan.Zero, Interval);
    }

    /// <summary>立即探测所有到期条目；并发调用只会有一次真正执行。</summary>
    public async Task ProbeAllAsync(CancellationToken cancellationToken = default)
    {
        if (!await _probeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _registry.Snapshot())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (entry.Status == HubConnectionStatus.Invalid || entry.NextProbeAt > now)
                {
                    continue;
                }

                await ProbeAsync(entry, cancellationToken, force: false).ConfigureAwait(false);
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>
    /// 探测一个应用。force=true 供「启动应用」轮询使用，绕过退避时间。
    /// </summary>
    public async Task ProbeAsync(HubAppEntry entry, CancellationToken cancellationToken = default, bool force = false)
    {
        if (entry.Status == HubConnectionStatus.Invalid)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && entry.NextProbeAt > now)
        {
            return;
        }

        // 启动早期配置文件还是旧的 stopped 状态：保持 Launching，等应用重写配置。
        if (!entry.Config.IsRunning)
        {
            if (entry.Status == HubConnectionStatus.Launching)
            {
                entry.NextProbeAt = now + TimeSpan.FromMilliseconds(250);
                return;
            }

            ApplyStatus(entry, HubConnectionStatus.Stopped, error: null);
            entry.NextProbeAt = now + OnlineProbeInterval;
            return;
        }

        if (entry.Config.Pid > 0 && !IsProcessAlive(entry.Config.Pid))
        {
            if (entry.Status == HubConnectionStatus.Launching)
            {
                Fail(entry, "应用进程已退出", terminal: true);
            }
            else
            {
                entry.FailureCount = 0;
                ApplyStatus(entry, HubConnectionStatus.Offline, "进程已退出");
                entry.NextProbeAt = now + OnlineProbeInterval;
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Config.Token))
        {
            Fail(entry, "连接配置缺少 token", terminal: entry.Status == HubConnectionStatus.Launching);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{entry.Config.ResolvedBaseUrl}/v1/info");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Config.Token);

        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (entry.Status == HubConnectionStatus.Launching)
                {
                    Fail(entry, $"握手被拒绝（HTTP {(int)response.StatusCode}）", terminal: true);
                }
                else
                {
                    ApplyStatus(entry, HubConnectionStatus.Unauthorized, $"握手被拒绝（HTTP {(int)response.StatusCode}）");
                    entry.NextProbeAt = now + OnlineProbeInterval;
                }

                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                Fail(entry, $"握手失败（HTTP {(int)response.StatusCode}）");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var info = HubJson.ParseInfo(document.RootElement.GetRawText());

            if (info is null)
            {
                Fail(entry, "握手响应不是合法 JSON");
                return;
            }

            if (info.SchemaVersion > 1)
            {
                entry.Info = info;
                entry.FailureCount = 0;
                ApplyStatus(entry, HubConnectionStatus.Incompatible, $"info.schemaVersion {info.SchemaVersion} 高于 Hub 支持的 1");
                entry.NextProbeAt = now + OnlineProbeInterval;
                return;
            }

            if (!string.Equals(info.AppId, entry.Config.AppId, StringComparison.OrdinalIgnoreCase))
            {
                Fail(entry, $"appId 不匹配（期望 {entry.Config.AppId}，实际 {info.AppId}）", terminal: true);
                return;
            }

            if (ValidateInfoUrls(entry, info) is { } infoError)
            {
                Fail(entry, infoError, terminal: true);
                return;
            }

            entry.Info = info;
            entry.FailureCount = 0;
            ApplyStatus(entry, HubConnectionStatus.Online, error: null);
            entry.NextProbeAt = now + OnlineProbeInterval;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Fail(entry, "握手超时");
        }
        catch (HttpRequestException ex)
        {
            Fail(entry, $"无法连接（{ex.Message}）");
        }
        catch (Exception ex)
        {
            _log.Warn($"探测 {entry.AppId} 失败", ex);
            Fail(entry, ex.Message);
        }
    }

    private static string? ValidateInfoUrls(HubAppEntry entry, HubAppInfo info)
    {
        if (info.Mcp?.Url is { Length: > 0 } mcpUrl &&
            HubDirectory.NormalizeLoopback(mcpUrl, entry.Config.ResolvedBaseUrl) is null)
        {
            return "握手返回的 mcp.url 不是回环地址";
        }

        if (info.Agent?.Url is { Length: > 0 } agentUrl &&
            HubDirectory.NormalizeLoopback(agentUrl, entry.Config.ResolvedBaseUrl) is null)
        {
            return "握手返回的 agent.url 不是回环地址";
        }

        if (info.Widgets is { Length: > HubConfigValidator.MaxWidgets })
        {
            return $"握手返回的组件超过 {HubConfigValidator.MaxWidgets} 个";
        }

        foreach (var widget in info.Widgets ?? [])
        {
            if (string.IsNullOrWhiteSpace(widget.Entry) ||
                widget.Entry[0] != '/' ||
                widget.Entry.Contains("..", StringComparison.Ordinal) ||
                widget.Entry.Any(char.IsWhiteSpace))
            {
                return $"握手返回的组件 {widget.Id} entry 不合法";
            }
        }

        return null;
    }

    private void Fail(HubAppEntry entry, string error, bool terminal = false)
    {
        var now = DateTimeOffset.UtcNow;
        entry.FailureCount++;
        entry.LastCheckedAt = now;
        var changed = entry.LastError != error;

        var delay = FailureBackoff[Math.Min(entry.FailureCount - 1, FailureBackoff.Length - 1)];
        entry.NextProbeAt = now + delay;

        var status = terminal || entry.FailureCount >= 3
            ? HubConnectionStatus.Error
            : entry.Status == HubConnectionStatus.Launching
                ? HubConnectionStatus.Launching
                : HubConnectionStatus.Connecting;

        if (changed || entry.Status != status)
        {
            entry.Status = status;
            entry.LastError = error;
            _registry.RaiseChanged();
        }
    }

    private void ApplyStatus(HubAppEntry entry, HubConnectionStatus status, string? error)
    {
        entry.LastCheckedAt = DateTimeOffset.Now;
        if (entry.Status == status && entry.LastError == error)
        {
            return;
        }

        entry.Status = status;
        entry.LastError = error;
        _registry.RaiseChanged();
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _http.Dispose();
        _probeGate.Dispose();
    }
}

