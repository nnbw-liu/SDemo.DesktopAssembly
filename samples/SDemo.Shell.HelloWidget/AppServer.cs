using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.HelloWidget;

/// <summary>
/// 应用的 app 面 HTTP 服务（host-protocol §6.1）：
/// 只监听 127.0.0.1 的随机端口，除 /v1/health 外全部校验 Bearer token。
/// </summary>
internal sealed class AppServer : IDisposable
{
    private readonly string _token;
    private readonly string _appId;
    private readonly string _appVersion;
    private readonly DateTimeOffset _startedAt;

    private HttpListener? _listener;
    private Task? _loop;
    private int _stopped;
    private int _shutdownScheduled;
    private int _shutdownMissed;

    public AppServer(string token, string appId, string appVersion)
    {
        _token = token;
        _appId = appId;
        _appVersion = appVersion;

        // 契约示例使用 +08:00 偏移；runtime.json 与 /v1/health 的 since 共用同一个值。
        _startedAt = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8));
    }

    /// <summary>绑定后系统分配的端口。</summary>
    public int Port { get; private set; }

    /// <summary>窗口创建后由 Program 注入，供指令 / 状态查询使用。</summary>
    public WidgetWindow? Widget { get; set; }

    /// <summary>收到 /v1/shutdown 后（已延迟 100ms）回调；由 UI 线程执行关窗。</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>
    /// 宿主可能在窗口就绪前就发 shutdown（例如启动即退出）：
    /// 此时还没有回调，先记下，等 Program 挂上回调后补发。
    /// </summary>
    public bool ConsumeMissedShutdown() => Interlocked.Exchange(ref _shutdownMissed, 0) == 1;

    internal static string RuntimeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo",
        "DesktopAssembly",
        "runtime");

    internal static string GetRuntimePath(string appId) => Path.Combine(RuntimeDirectory, appId + ".json");

    /// <summary>绑定端口 + 写 runtime.json；失败最多重试 3 次（换端口）。</summary>
    public void Start()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var port = FindFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                WriteRuntimeFile();
                _loop = Task.Run(LoopAsync);
                return;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
                Debug.WriteLine($"[{_appId}] 绑定 127.0.0.1:{port} 失败（第 {attempt + 1} 次）：{ex.Message}");
                listener.Close();
            }
        }

        throw new InvalidOperationException("无法绑定本机回环端口。", lastError);
    }

    /// <summary>停止监听（幂等）。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放。
        }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放。
        }

        try
        {
            _loop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // 停止监听会让 GetContextAsync 抛出，属预期。
        }
    }

    /// <summary>删除 runtime.json（退出 / 关窗 / 启动失败都要调用，幂等）。</summary>
    public static void DeleteRuntimeFile(string appId)
    {
        try
        {
            var path = GetRuntimePath(appId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[{appId}] 删除 runtime.json 失败：{ex.Message}");
        }
    }

    private static int FindFreePort()
    {
        // HTTP.sys 不接受端口 0（HttpListener 会报“参数错误”），
        // 因此先用 TcpListener 探一个系统分配的空闲端口再绑定。
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private void WriteRuntimeFile()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        var file = new RuntimeFile
        {
            Pid = Environment.ProcessId,
            Port = Port,
            Token = _token,
            StartedAt = _startedAt,
            AppVersion = _appVersion,
            HostApiVersion = ShellConstants.HostApiVersion,
        };

        // UTF-8 无 BOM + LF（shell-storage §2）。
        var json = ShellJson.NormalizeNewLines(JsonSerializer.Serialize(file, ShellJson.File));
        var path = GetRuntimePath(_appId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private async Task LoopAsync()
    {
        while (Volatile.Read(ref _stopped) == 0)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException)
            {
                break;
            }

            try
            {
                await HandleAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_appId}] 处理请求失败：{ex}");
                try
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // 响应已关闭，忽略。
                }
            }
        }
    }

    private enum Route
    {
        None,
        Handshake,
        Shutdown,
        WidgetCommand,
        WidgetState,
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = (request.Url?.AbsolutePath ?? string.Empty).TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        var (route, widgetId) = MatchRoute(path, request.HttpMethod);

        // /v1/health 不校验 token（host-protocol §5）。
        if (route == Route.None && path == "/v1/health" && request.HttpMethod == "GET")
        {
            await WriteJsonAsync(response, 200, new HealthResponse
            {
                Ok = true,
                Pid = Environment.ProcessId,
                Since = _startedAt,
            }).ConfigureAwait(false);
            return;
        }

        if (route == Route.None)
        {
            await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点").ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(request))
        {
            await WriteErrorAsync(response, 401, ErrorCodes.Unauthorized, null).ConfigureAwait(false);
            return;
        }

        if (route is Route.WidgetCommand or Route.WidgetState
            && !string.Equals(widgetId, Program.WidgetId, StringComparison.Ordinal))
        {
            await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知组件").ConfigureAwait(false);
            return;
        }

        switch (route)
        {
            case Route.Handshake:
                await HandleHandshakeAsync(request, response).ConfigureAwait(false);
                break;
            case Route.Shutdown:
                await HandleShutdownAsync(response).ConfigureAwait(false);
                break;
            case Route.WidgetCommand:
                await HandleCommandAsync(request, response).ConfigureAwait(false);
                break;
            case Route.WidgetState:
                await HandleStateAsync(response).ConfigureAwait(false);
                break;
        }
    }

    private static (Route Route, string? WidgetId) MatchRoute(string path, string method)
    {
        if (method == "POST" && path == "/v1/handshake")
        {
            return (Route.Handshake, null);
        }

        if (method == "POST" && path == "/v1/shutdown")
        {
            return (Route.Shutdown, null);
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 4 && segments[0] == "v1" && segments[1] == "widgets")
        {
            var widgetId = Uri.UnescapeDataString(segments[2]);
            if (method == "POST" && segments[3] == "command")
            {
                return (Route.WidgetCommand, widgetId);
            }

            if (method == "GET" && segments[3] == "state")
            {
                return (Route.WidgetState, widgetId);
            }
        }

        return (Route.None, null);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header)
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = header["Bearer ".Length..].Trim();
        var expected = Encoding.UTF8.GetBytes(_token);
        var actual = Encoding.UTF8.GetBytes(provided);

        // 定长比较，避免逐字节短路泄漏 token 前缀。
        return actual.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private async Task HandleHandshakeAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadBodyAsync(request, response).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        HandshakeRequest? handshake;
        try
        {
            handshake = JsonSerializer.Deserialize<HandshakeRequest>(body, ShellJson.Wire);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "handshake 请求体不是合法 JSON").ConfigureAwait(false);
            return;
        }

        if (handshake is null)
        {
            await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "handshake 请求体为空").ConfigureAwait(false);
            return;
        }

        if (!HostApiVersion.IsCompatible(handshake.HostApiVersion, ShellConstants.HostApiVersion))
        {
            await WriteErrorAsync(
                response,
                409,
                ErrorCodes.VersionMismatch,
                $"宿主 hostApiVersion={handshake.HostApiVersion}，应用要求 {ShellConstants.HostApiVersion}").ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(response, 200, new HandshakeResponse
        {
            AppVersion = _appVersion,
            HostApiVersion = ShellConstants.HostApiVersion,
            Capabilities = new List<string> { "native-window" },
        }).ConfigureAwait(false);
    }

    private async Task HandleShutdownAsync(HttpListenerResponse response)
    {
        // 先回 200，再在后台 100ms 后关窗、停监听、删 runtime.json、退出进程。
        await WriteJsonAsync(response, 200, new { ok = true }).ConfigureAwait(false);

        if (Interlocked.Exchange(ref _shutdownScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(100).ConfigureAwait(false);
            var handler = ShutdownRequested;
            if (handler is null)
            {
                Interlocked.Exchange(ref _shutdownMissed, 1);
                return;
            }

            handler();
        });
    }

    private async Task HandleCommandAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadBodyAsync(request, response).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        WidgetCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<WidgetCommand>(body, ShellJson.Wire);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "command 请求体不是合法 JSON").ConfigureAwait(false);
            return;
        }

        if (command is null || !WidgetCommands.IsKnown(command.Command))
        {
            await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "未知或缺失 command").ConfigureAwait(false);
            return;
        }

        var widget = Widget;
        if (widget is null)
        {
            await WriteErrorAsync(response, 503, ErrorCodes.Busy, "窗口尚未就绪").ConfigureAwait(false);
            return;
        }

        widget.ApplyCommand(command);
        await WriteJsonAsync(response, 200, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleStateAsync(HttpListenerResponse response)
    {
        var widget = Widget;
        var (visible, opacity) = widget?.GetState() ?? (false, 0d);
        await WriteJsonAsync(response, 200, new { visible, opacity }).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentLength64 > ShellConstants.MaxRequestBytes)
        {
            await WriteErrorAsync(response, 413, ErrorCodes.TooLarge, "请求体超过 64KB").ConfigureAwait(false);
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > ShellConstants.MaxRequestBytes)
            {
                await WriteErrorAsync(response, 413, ErrorCodes.TooLarge, "请求体超过 64KB").ConfigureAwait(false);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ShellJson.Wire);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string code, string? message) =>
        WriteJsonAsync(
            response,
            statusCode,
            ErrorEnvelope.Create(code, message, requestId: null, hint: code == ErrorCodes.TooLarge ? "请求体上限 64KB" : null));
}
