using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Runtime;

/// <summary>宿主 → 应用的本机 HTTP 客户端（host-protocol §6.1）。所有请求都只打 127.0.0.1。</summary>
public sealed class HttpAppTransport : IAppTransport
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private readonly IShellLog _log;

    public HttpAppTransport(IShellLog? log = null) => _log = log ?? NullShellLog.Instance;

    public async Task<AppCallResult<HealthResponse>> HealthAsync(int port, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, port, "/v1/health", token);
            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 401)
            {
                return new AppCallResult<HealthResponse>(null, status, "健康检查 401");
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return new AppCallResult<HealthResponse>(null, status, $"健康检查返回 {status}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var health = JsonSerializer.Deserialize<HealthResponse>(body, ShellJson.Wire);
            return new AppCallResult<HealthResponse>(health, status);
        }
        catch (Exception ex)
        {
            return new AppCallResult<HealthResponse>(null, 0, ex.Message);
        }
    }

    public async Task<AppCallResult<HandshakeResponse>> HandshakeAsync(
        int port,
        string token,
        HandshakeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateRequest(HttpMethod.Post, port, "/v1/handshake", token);
            message.Content = JsonContent(request);
            using var response = await Client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 401)
            {
                return new AppCallResult<HandshakeResponse>(null, status, "握手 401");
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return new AppCallResult<HandshakeResponse>(null, status, $"握手返回 {status}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var handshake = JsonSerializer.Deserialize<HandshakeResponse>(body, ShellJson.Wire);
            return new AppCallResult<HandshakeResponse>(handshake, status);
        }
        catch (Exception ex)
        {
            return new AppCallResult<HandshakeResponse>(null, 0, ex.Message);
        }
    }

    public async Task<bool> ShutdownAsync(int port, string token, int graceMs, CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateRequest(HttpMethod.Post, port, "/v1/shutdown", token);
            message.Content = JsonContent(new { graceMs });
            using var response = await Client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 300;
        }
        catch (Exception ex)
        {
            _log.Debug($"shutdown 失败（应用可能已退出）：{ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendCommandAsync(
        int port,
        string token,
        string widgetId,
        WidgetCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = $"/v1/widgets/{Uri.EscapeDataString(widgetId)}/command";
            using var message = CreateRequest(HttpMethod.Post, port, path, token);
            message.Content = JsonContent(command);
            using var response = await Client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 300;
        }
        catch (Exception ex)
        {
            _log.Debug($"下发指令 {command.Command} 失败：{ex.Message}");
            return false;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, int port, string path, string token)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Version = HttpVersion.Version11;
        return request;
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, ShellJson.Wire), Encoding.UTF8, "application/json");
}
