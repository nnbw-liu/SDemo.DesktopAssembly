using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Processes;
using SDemo.Shell.Registry;
using SDemo.Shell.Runtime;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Hosting;

/// <summary>
/// 宿主回环监听器（DA-31–DA-37 / DA-90–DA-93 / DA-98）：
/// 唯一监听 <c>127.0.0.1:0</c>，app 面与 client 面两类 token，<c>host.json</c> 发现文件，ensure 单飞。
/// </summary>
public sealed class ShellHostServer : IAsyncDisposable
{
    private readonly ShellPaths _paths;
    private readonly AppRegistry _registry;
    private readonly AppProcessManager _manager;
    private readonly AppTokenStore _tokens;
    private readonly IWidgetWindowHost _widgetHost;
    private readonly IWidgetPolicyHost _policyHost;
    private readonly IHostInfoProvider _hostInfo;
    private readonly IUserNotifier _notifier;
    private readonly IShellLog _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _ensureGate =
        new(ShellConstants.MaxEnsureConcurrency, ShellConstants.MaxEnsureConcurrency);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private string _clientToken = string.Empty;
    private DateTimeOffset _startedAt;

    public ShellHostServer(
        ShellPaths paths,
        AppRegistry registry,
        AppProcessManager manager,
        AppTokenStore tokens,
        IWidgetWindowHost widgetHost,
        IWidgetPolicyHost policyHost,
        IHostInfoProvider hostInfo,
        IUserNotifier notifier,
        IShellLog? log = null,
        TimeProvider? time = null)
    {
        _paths = paths;
        _registry = registry;
        _manager = manager;
        _tokens = tokens;
        _widgetHost = widgetHost;
        _policyHost = policyHost;
        _hostInfo = hostInfo;
        _notifier = notifier;
        _log = log ?? NullShellLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    public int Port { get; private set; }

    public string HostUrl => $"http://127.0.0.1:{Port}";

    public string ClientToken => _clientToken;

    public bool IsListening => _listener?.IsListening == true;

    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>顺序不可颠倒：绑定端口 → client-token → host.json（host-endpoints §4）。</summary>
    public Task StartAsync()
    {
        _paths.EnsureDirectories();
        _clientToken = AppTokenStore.CreateToken();
        WriteClientToken();

        Exception? lastError = null;
        for (var attempt = 0; attempt < 3 && _listener is null; attempt++)
        {
            var port = FindFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                _log.Info($"宿主监听器已绑定 127.0.0.1:{port}。");
            }
            catch (Exception ex)
            {
                lastError = ex;
                listener.Close();
                _log.Warn($"绑定 127.0.0.1:{port} 失败（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(100);
            }
        }

        if (_listener is null)
        {
            throw new InvalidOperationException("无法绑定回环监听器（127.0.0.1:0），宿主快速失败。", lastError);
        }

        _startedAt = _time.GetLocalNow();
        WriteHostFile();

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>停止接受新请求，但保留发现文件（等应用退出后再删）。</summary>
    public async Task StopAcceptingAsync()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 已停止
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // 停止期间的异常无需上报
            }
        }

        _listener?.Close();
        _listener = null;
    }

    public void RemoveDiscoveryFiles()
    {
        TryDelete(_paths.HostFilePath);
        TryDelete(_paths.ClientTokenFilePath);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAcceptingAsync().ConfigureAwait(false);
        RemoveDiscoveryFiles();
        _ensureGate.Dispose();
        _cts?.Dispose();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void WriteClientToken()
    {
        // 每次壳启动轮换；文件在 %LOCALAPPDATA%，继承当前用户目录 ACL。
        File.WriteAllText(
            _paths.ClientTokenFilePath,
            _clientToken,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WriteHostFile()
    {
        var hostFile = new HostFile
        {
            Pid = Environment.ProcessId,
            Port = Port,
            StartedAt = _startedAt,
            HostVersion = HostVersion.Product,
            HostApiVersion = ShellConstants.HostApiVersion,
            ClientTokenPath = ShellConstants.ClientTokenFileName,
            RegistryRevision = _registry.Revision,
        };

        var text = ShellJson.NormalizeNewLines(JsonSerializer.Serialize(hostFile, ShellJson.File));
        AtomicFile.WriteAllText(_paths.HostFilePath, text);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var requestId = "r_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var stopwatch = Stopwatch.StartNew();
        var status = 500;
        response.Headers["X-Request-Id"] = requestId;
        response.Headers["Cache-Control"] = "no-store";

        try
        {
            status = await DispatchAsync(context, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            status = 503;
        }
        catch (Exception ex)
        {
            _log.Error($"处理请求失败：{request.HttpMethod} {request.Url?.AbsolutePath}", ex, requestId);
            status = 500;
            try
            {
                await WriteErrorAsync(response, status, ErrorCodes.InternalError, null, requestId).ConfigureAwait(false);
            }
            catch
            {
                // 连接可能已断
            }
        }
        finally
        {
            stopwatch.Stop();
            _log.Info(
                $"{request.HttpMethod} {request.Url?.AbsolutePath} → {status} ({stopwatch.ElapsedMilliseconds}ms)",
                requestId);
            try
            {
                response.Close();
            }
            catch
            {
                // 连接已断
            }
        }
    }

    private async Task<int> DispatchAsync(HttpListenerContext context, string requestId, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var method = request.HttpMethod.ToUpperInvariant();

        if (!IsHostHeaderValid(request))
        {
            return await WriteErrorAsync(response, 403, ErrorCodes.ScopeDenied, "Host 头不合法", requestId).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(request.Headers["Origin"]))
        {
            return await WriteErrorAsync(response, 403, ErrorCodes.ScopeDenied, "浏览器 Origin 请求被拒绝", requestId).ConfigureAwait(false);
        }

        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "/";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "v1", StringComparison.Ordinal))
        {
            return await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点", requestId).ConfigureAwait(false);
        }

        return segments[1] switch
        {
            "host" => await HandleHostAsync(context, segments, method, requestId).ConfigureAwait(false),
            "apps" => await HandleAppsAsync(context, segments, method, requestId, cancellationToken).ConfigureAwait(false),
            "notify" => await HandleNotifyAsync(context, method, requestId).ConfigureAwait(false),
            "widgets" => await HandleWidgetsAsync(context, segments, method, requestId).ConfigureAwait(false),
            _ => await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点", requestId).ConfigureAwait(false),
        };
    }

    private async Task<int> HandleHostAsync(HttpListenerContext context, string[] segments, string method, string requestId)
    {
        var request = context.Request;
        var response = context.Response;

        if (segments.Length == 2 && method == "GET")
        {
            if (!IsClientAuthorized(request))
            {
                return await ClientAuthFailureAsync(response, request, requestId).ConfigureAwait(false);
            }

            await WriteJsonAsync(response, 200, new HostStatus
            {
                HostVersion = HostVersion.Product,
                HostApiVersion = ShellConstants.HostApiVersion,
                Pid = Environment.ProcessId,
                StartedAt = _startedAt,
                RegistryRevision = _registry.Revision,
            }, requestId).ConfigureAwait(false);
            return 200;
        }

        if (segments.Length == 3 && string.Equals(segments[2], "info", StringComparison.Ordinal) && method == "GET")
        {
            var auth = AuthorizeApp(request, null);
            if (!auth.Ok)
            {
                return await WriteAuthFailureAsync(response, auth, requestId).ConfigureAwait(false);
            }

            await WriteJsonAsync(response, 200, _hostInfo.GetHostInfo(), requestId).ConfigureAwait(false);
            return 200;
        }

        return await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点", requestId).ConfigureAwait(false);
    }

    private async Task<int> HandleAppsAsync(
        HttpListenerContext context,
        string[] segments,
        string method,
        string requestId,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        if (segments.Length == 2 && method == "GET")
        {
            if (!IsClientAuthorized(request))
            {
                return await ClientAuthFailureAsync(response, request, requestId).ConfigureAwait(false);
            }

            var revision = _registry.Revision;
            var ifNoneMatch = request.Headers["If-None-Match"];
            if (ifNoneMatch is not null && int.TryParse(ifNoneMatch.Trim('"'), out var etag) && etag == revision)
            {
                response.StatusCode = 304;
                return 304;
            }

            var apps = _manager.DescribeApps();
            var filter = request.QueryString["state"];
            var filtered = string.IsNullOrWhiteSpace(filter)
                ? apps.ToList()
                : apps.Where(a => string.Equals(a.State, filter, StringComparison.OrdinalIgnoreCase)).ToList();

            await WriteJsonAsync(response, 200, new AppsResponse { Revision = revision, Apps = filtered }, requestId).ConfigureAwait(false);
            return 200;
        }

        if (segments.Length < 3)
        {
            return await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点", requestId).ConfigureAwait(false);
        }

        var appId = Uri.UnescapeDataString(segments[2]);

        if (segments.Length == 3 && method == "GET")
        {
            if (!IsClientAuthorized(request))
            {
                return await ClientAuthFailureAsync(response, request, requestId).ConfigureAwait(false);
            }

            var entry = _registry.Find(appId);
            if (entry is null)
            {
                return await WriteErrorAsync(response, 404, ErrorCodes.AppNotFound, null, requestId).ConfigureAwait(false);
            }

            await WriteJsonAsync(response, 200, _manager.Describe(entry), requestId).ConfigureAwait(false);
            return 200;
        }

        if (segments.Length == 4 && method == "POST")
        {
            var action = segments[3];
            if (string.Equals(action, "ensure", StringComparison.Ordinal))
            {
                return await HandleEnsureAsync(context, appId, requestId, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(action, "register", StringComparison.Ordinal))
            {
                return await HandleRegisterAsync(context, appId, requestId, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(action, "unregister", StringComparison.Ordinal))
            {
                return await HandleUnregisterAsync(context, appId, requestId, cancellationToken).ConfigureAwait(false);
            }
        }

        return await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点", requestId).ConfigureAwait(false);
    }

    private async Task<int> HandleEnsureAsync(
        HttpListenerContext context,
        string appId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsClientAuthorized(request))
        {
            return await ClientAuthFailureAsync(response, request, requestId).ConfigureAwait(false);
        }

        EnsureRequest? body = null;
        if (request.ContentLength64 > 0)
        {
            var read = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            if (read.Error is not null)
            {
                return await WriteErrorAsync(response, 413, ErrorCodes.TooLarge, read.Error, requestId).ConfigureAwait(false);
            }

            try
            {
                body = JsonSerializer.Deserialize<EnsureRequest>(read.Body!, ShellJson.Wire);
            }
            catch (JsonException)
            {
                return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "body 不是合法 JSON", requestId).ConfigureAwait(false);
            }
        }

        if (body?.TimeoutMs is < 0 or > ShellConstants.MaxEnsureTimeoutMs)
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "timeoutMs 越界", requestId).ConfigureAwait(false);
        }

        if (!_ensureGate.Wait(0))
        {
            return await WriteErrorAsync(response, 503, ErrorCodes.Busy, null, requestId).ConfigureAwait(false);
        }

        try
        {
            var outcome = await _manager.EnsureAsync(appId, body?.TimeoutMs, cancellationToken).ConfigureAwait(false);
            if (!outcome.Success)
            {
                var code = outcome.ErrorCode ?? ErrorCodes.AppCrashed;
                return await WriteErrorAsync(response, ErrorCodes.HttpStatus(code), code, outcome.Message, requestId)
                    .ConfigureAwait(false);
            }

            await WriteJsonAsync(response, 200, new EnsureResponse
            {
                AppId = appId,
                State = outcome.Descriptor?.State ?? AppStates.Ready,
                Runtime = outcome.Runtime ?? new RuntimePointer { RuntimePath = _paths.RuntimeFilePath(appId) },
            }, requestId).ConfigureAwait(false);
            return 200;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private async Task<int> HandleRegisterAsync(
        HttpListenerContext context,
        string appId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var auth = AuthorizeApp(request, appId);
        if (!auth.Ok)
        {
            return await WriteAuthFailureAsync(response, auth, requestId).ConfigureAwait(false);
        }

        var read = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return await WriteErrorAsync(response, 413, ErrorCodes.TooLarge, read.Error, requestId).ConfigureAwait(false);
        }

        AppRegistration? body;
        try
        {
            body = JsonSerializer.Deserialize<AppRegistration>(read.Body!, ShellJson.Wire);
        }
        catch (JsonException)
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "body 不是合法 JSON", requestId).ConfigureAwait(false);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.WidgetId) || body.Hwnd <= 0)
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "widgetId / hwnd 非法", requestId).ConfigureAwait(false);
        }

        var entry = _registry.Find(appId);
        if (entry is null)
        {
            return await WriteErrorAsync(response, 404, ErrorCodes.AppNotFound, null, requestId).ConfigureAwait(false);
        }

        if (!string.Equals(entry.WidgetId, body.WidgetId, StringComparison.Ordinal))
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "widgetId 不属于该应用", requestId).ConfigureAwait(false);
        }

        _manager.MarkActivity(appId);
        _widgetHost.OnWidgetRegistered(new RegisteredWidget(body.WidgetId, appId, body.Hwnd, body.DpiAware));
        _log.Info($"native 窗口已登记：{body.WidgetId} hwnd={body.Hwnd} dpi={body.DpiAware}", requestId);

        await WriteJsonAsync(response, 200, new AppRegistrationResponse
        {
            Accepted = true,
            WidgetId = body.WidgetId,
        }, requestId).ConfigureAwait(false);
        return 200;
    }

    private async Task<int> HandleUnregisterAsync(
        HttpListenerContext context,
        string appId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var auth = AuthorizeApp(request, appId);
        if (!auth.Ok)
        {
            return await WriteAuthFailureAsync(response, auth, requestId).ConfigureAwait(false);
        }

        AppRegistration? body = null;
        if (request.ContentLength64 > 0)
        {
            var read = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            if (read.Error is not null)
            {
                return await WriteErrorAsync(response, 413, ErrorCodes.TooLarge, read.Error, requestId).ConfigureAwait(false);
            }

            try
            {
                body = JsonSerializer.Deserialize<AppRegistration>(read.Body!, ShellJson.Wire);
            }
            catch (JsonException)
            {
                return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "body 不是合法 JSON", requestId).ConfigureAwait(false);
            }
        }

        _widgetHost.OnWidgetUnregistered(body?.WidgetId ?? string.Empty, body?.Hwnd ?? 0);
        await WriteJsonAsync(response, 200, new { accepted = true }, requestId).ConfigureAwait(false);
        return 200;
    }

    private async Task<int> HandleNotifyAsync(HttpListenerContext context, string method, string requestId)
    {
        var request = context.Request;
        var response = context.Response;

        if (method != "POST")
        {
            return await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点", requestId).ConfigureAwait(false);
        }

        var auth = AuthorizeApp(request, null);
        if (!auth.Ok)
        {
            return await WriteAuthFailureAsync(response, auth, requestId).ConfigureAwait(false);
        }

        var read = await ReadBodyAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return await WriteErrorAsync(response, 413, ErrorCodes.TooLarge, read.Error, requestId).ConfigureAwait(false);
        }

        NotifyRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<NotifyRequest>(read.Body!, ShellJson.Wire);
        }
        catch (JsonException)
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "body 不是合法 JSON", requestId).ConfigureAwait(false);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Title))
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "title 不能为空", requestId).ConfigureAwait(false);
        }

        _notifier.Notify(body);
        _manager.MarkActivity(auth.AppId!);
        await WriteJsonAsync(response, 200, new { accepted = true }, requestId).ConfigureAwait(false);
        return 200;
    }

    private async Task<int> HandleWidgetsAsync(
        HttpListenerContext context,
        string[] segments,
        string method,
        string requestId)
    {
        var response = context.Response;
        if (segments.Length != 4 || !string.Equals(segments[3], "request", StringComparison.Ordinal) || method != "POST")
        {
            return await WriteErrorAsync(response, 404, ErrorCodes.BadRequest, "未知端点", requestId).ConfigureAwait(false);
        }

        var widgetId = Uri.UnescapeDataString(segments[2]);
        var entry = _registry.FindByWidget(widgetId);
        if (entry is null)
        {
            return await WriteErrorAsync(response, 404, ErrorCodes.AppNotFound, "组件未安装", requestId).ConfigureAwait(false);
        }

        var auth = AuthorizeApp(context.Request, entry.AppId);
        if (!auth.Ok)
        {
            return await WriteAuthFailureAsync(response, auth, requestId).ConfigureAwait(false);
        }

        var read = await ReadBodyAsync(context.Request, CancellationToken.None).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return await WriteErrorAsync(response, 413, ErrorCodes.TooLarge, read.Error, requestId).ConfigureAwait(false);
        }

        WidgetPolicyRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<WidgetPolicyRequest>(read.Body!, ShellJson.Wire);
        }
        catch (JsonException)
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "body 不是合法 JSON", requestId).ConfigureAwait(false);
        }

        if (body is null || !WidgetRequestKinds.IsKnown(body.Kind))
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "kind 非法", requestId).ConfigureAwait(false);
        }

        if (string.Equals(body.Kind, WidgetRequestKinds.IdleHold, StringComparison.Ordinal))
        {
            if (!body.TryGetBoolean(out var hold))
            {
                return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "idleHold 的 value 必须是布尔", requestId)
                    .ConfigureAwait(false);
            }

            _manager.SetIdleHold(entry.AppId, hold);
            _manager.MarkActivity(entry.AppId);
            await WriteJsonAsync(response, 200, new { applied = true, kind = body.Kind }, requestId).ConfigureAwait(false);
            return 200;
        }

        if (string.Equals(body.Kind, WidgetRequestKinds.Layer, StringComparison.Ordinal))
        {
            if (!body.TryGetString(out var layer) || layer is not (WidgetRendering.Floating or WidgetRendering.Desktop))
            {
                return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, "layer 必须是 floating / desktop", requestId)
                    .ConfigureAwait(false);
            }
        }
        else if (!body.TryGetBoolean(out _))
        {
            return await WriteErrorAsync(response, 400, ErrorCodes.BadRequest, $"{body.Kind} 的 value 必须是布尔", requestId)
                .ConfigureAwait(false);
        }

        var decision = _policyHost.ApplyPolicy(widgetId, body);
        if (!decision.Applied)
        {
            return await WriteErrorAsync(
                response,
                403,
                decision.ErrorCode ?? ErrorCodes.PolicyDenied,
                decision.Message,
                requestId).ConfigureAwait(false);
        }

        _manager.MarkActivity(entry.AppId);
        await WriteJsonAsync(response, 200, new { applied = true, kind = body.Kind }, requestId).ConfigureAwait(false);
        return 200;
    }

    private bool IsClientAuthorized(HttpListenerRequest request)
    {
        var token = ExtractBearerToken(request);
        return token is not null && FixedTimeEquals(token, _clientToken);
    }

    private async Task<int> ClientAuthFailureAsync(HttpListenerResponse response, HttpListenerRequest request, string requestId)
    {
        var token = ExtractBearerToken(request);
        if (token is null)
        {
            return await WriteErrorAsync(response, 401, ErrorCodes.Unauthorized, null, requestId).ConfigureAwait(false);
        }

        if (_tokens.TryResolveAppId(token, out _))
        {
            return await WriteErrorAsync(response, 403, ErrorCodes.ScopeDenied, "app token 不能访问 client 面", requestId)
                .ConfigureAwait(false);
        }

        return await WriteErrorAsync(response, 401, ErrorCodes.TokenInvalid, null, requestId).ConfigureAwait(false);
    }

    private (bool Ok, int Status, string? Code, string? Message, string? AppId) AuthorizeApp(
        HttpListenerRequest request,
        string? expectedAppId)
    {
        var token = ExtractBearerToken(request);
        if (token is null)
        {
            return (false, 401, ErrorCodes.Unauthorized, "缺少 Bearer token", null);
        }

        if (FixedTimeEquals(token, _clientToken))
        {
            return (false, 403, ErrorCodes.ScopeDenied, "client token 不能访问 app 面", null);
        }

        if (expectedAppId is not null)
        {
            if (_tokens.TryValidate(expectedAppId, token, out var failure))
            {
                _manager.NoteAuthSuccess(expectedAppId);
                return (true, 200, null, null, expectedAppId);
            }

            _manager.NoteAuthFailure(expectedAppId);

            if (_tokens.TryResolveAppId(token, out var otherApp) && !string.Equals(otherApp, expectedAppId, StringComparison.Ordinal))
            {
                return (false, 403, ErrorCodes.ScopeDenied, "token 属于其它应用", null);
            }

            return (false, 401, ErrorCodes.TokenInvalid, failure, null);
        }

        if (_tokens.TryResolveAppId(token, out var appId))
        {
            _manager.NoteAuthSuccess(appId);
            return (true, 200, null, null, appId);
        }

        return (false, 401, ErrorCodes.TokenInvalid, "无法识别 token", null);
    }

    private async Task<int> WriteAuthFailureAsync(
        HttpListenerResponse response,
        (bool Ok, int Status, string? Code, string? Message, string? AppId) auth,
        string requestId)
    {
        return await WriteErrorAsync(response, auth.Status, auth.Code ?? ErrorCodes.Unauthorized, auth.Message, requestId)
            .ConfigureAwait(false);
    }

    private static string? ExtractBearerToken(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private bool IsHostHeaderValid(HttpListenerRequest request)
    {
        var host = request.UserHostName;
        return !string.IsNullOrEmpty(host) && string.Equals(host, $"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(byte[]? Body, string? Error)> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > ShellConstants.MaxRequestBytes)
        {
            return (null, "请求体超过 64KB");
        }

        try
        {
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = await request.InputStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > ShellConstants.MaxRequestBytes)
                {
                    return (null, "请求体超过 64KB");
                }

                memory.Write(buffer, 0, read);
            }

            return (memory.ToArray(), null);
        }
        catch (HttpListenerException)
        {
            return (null, "读取请求体失败");
        }
    }

    private async Task<int> WriteJsonAsync(HttpListenerResponse response, int status, object value, string requestId)
    {
        var body = JsonSerializer.Serialize(value, ShellJson.Wire);
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["X-Request-Id"] = requestId;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        return status;
    }

    private Task<int> WriteErrorAsync(
        HttpListenerResponse response,
        int status,
        string code,
        string? message,
        string requestId) =>
        WriteJsonAsync(response, status, ErrorEnvelope.Create(code, message, requestId), requestId);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清理发现文件失败：{path}", ex);
        }
    }
}
