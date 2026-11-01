using System.Net.Http;
using System.Text;
using System.Text.Json;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.Tests;

/// <summary>
/// 回环端点（DA-31–DA-37 / DA-90–DA-93 / DA-98）：真实 HttpListener + HttpClient。
/// 覆盖两类 token、Host/Origin、64KB、发现文件、ensure 状态码与重启轮换。
/// </summary>
public sealed class HostServerTests
{
    private const string AppId = "sdemo.alpha";
    private const string WidgetId = AppId + ".widget";

    private static async Task<(int Status, string Body)> SendAsync(
        int port,
        HttpMethod method,
        string path,
        string? token = null,
        string? jsonBody = null,
        string? origin = null,
        string? ifNoneMatch = null)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body);
    }

    private static JsonDocument Parse(string body) => JsonDocument.Parse(body);

    private static void AssertErrorCode(string body, string expectedCode)
    {
        using var document = Parse(body);
        Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingToken_Returns401Unauthorized()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(shell.Server.Port, HttpMethod.Get, "/v1/host");

        Assert.Equal(401, status);
        AssertErrorCode(body, ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task WrongToken_Returns401TokenInvalid()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(shell.Server.Port, HttpMethod.Get, "/v1/host", "not-a-real-token");

        Assert.Equal(401, status);
        AssertErrorCode(body, ErrorCodes.TokenInvalid);
    }

    [Fact]
    public async Task ClientToken_ReadsHostStatus()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(shell.Server.Port, HttpMethod.Get, "/v1/host", shell.Server.ClientToken);

        Assert.Equal(200, status);
        using var document = Parse(body);
        Assert.Equal(shell.Registry.Revision, document.RootElement.GetProperty("registryRevision").GetInt32());
        Assert.Equal(ShellConstants.HostApiVersion, document.RootElement.GetProperty("hostApiVersion").GetString());
        Assert.True(document.RootElement.GetProperty("pid").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("hostVersion").GetString()));
    }

    [Fact]
    public async Task AppToken_OnClientSurface_IsScopeDenied()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();
        var appToken = shell.SeedAppToken();

        var (status, body) = await SendAsync(shell.Server.Port, HttpMethod.Get, "/v1/apps", appToken);

        Assert.Equal(403, status);
        AssertErrorCode(body, ErrorCodes.ScopeDenied);
    }

    [Fact]
    public async Task ClientToken_OnAppSurface_IsScopeDenied()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            $"/v1/apps/{AppId}/register",
            shell.Server.ClientToken,
            $$"""{"widgetId":"{{WidgetId}}","hwnd":1}""");

        Assert.Equal(403, status);
        AssertErrorCode(body, ErrorCodes.ScopeDenied);
    }

    [Fact]
    public async Task OriginHeader_IsRejected()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Get,
            "/v1/host",
            shell.Server.ClientToken,
            origin: "http://evil.example");

        Assert.Equal(403, status);
        AssertErrorCode(body, ErrorCodes.ScopeDenied);
    }

    [Fact]
    public async Task OversizedBody_Returns413TooLarge()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();
        var appToken = shell.SeedAppToken();

        var largeBody = "{\"title\":\"" + new string('x', ShellConstants.MaxRequestBytes + 1) + "\"}";
        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            "/v1/notify",
            appToken,
            largeBody);

        Assert.Equal(413, status);
        AssertErrorCode(body, ErrorCodes.TooLarge);
    }

    [Fact]
    public async Task GetApps_ListsInstalledAppAndHonorsIfNoneMatch()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(shell.Server.Port, HttpMethod.Get, "/v1/apps", shell.Server.ClientToken);
        Assert.Equal(200, status);

        using var document = Parse(body);
        var revision = document.RootElement.GetProperty("revision").GetInt32();
        var apps = document.RootElement.GetProperty("apps");
        Assert.Equal(AppId, apps[0].GetProperty("appId").GetString());
        Assert.Equal(AppStates.Stopped, apps[0].GetProperty("state").GetString());
        Assert.Equal(shell.Registry.Revision, revision);

        var (notModified, notModifiedBody) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Get,
            "/v1/apps",
            shell.Server.ClientToken,
            ifNoneMatch: revision.ToString());
        Assert.Equal(304, notModified);
        Assert.Empty(notModifiedBody);
    }

    [Fact]
    public async Task GetUnknownApp_Returns404AppNotFound()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Get,
            "/v1/apps/sdemo.missing",
            shell.Server.ClientToken);

        Assert.Equal(404, status);
        AssertErrorCode(body, ErrorCodes.AppNotFound);
    }

    [Fact]
    public async Task Ensure_DisabledApp_Returns409AppDisabled()
    {
        await using var shell = new HostTestShell();
        Assert.True(shell.Registry.SetEnabled(WidgetId, false));
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            $"/v1/apps/{AppId}/ensure",
            shell.Server.ClientToken,
            "{}");

        Assert.Equal(409, status);
        AssertErrorCode(body, ErrorCodes.AppDisabled);
        Assert.Equal(0, shell.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Ensure_UnknownApp_Returns404()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            "/v1/apps/sdemo.missing/ensure",
            shell.Server.ClientToken,
            "{}");

        Assert.Equal(404, status);
        AssertErrorCode(body, ErrorCodes.AppNotFound);
    }

    [Fact]
    public async Task Ensure_TimeoutOutOfRange_Returns400()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            $"/v1/apps/{AppId}/ensure",
            shell.Server.ClientToken,
            "{\"timeoutMs\":999999}");

        Assert.Equal(400, status);
        AssertErrorCode(body, ErrorCodes.BadRequest);
        Assert.Equal(0, shell.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Ensure_Success_ReturnsRuntimePort()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            $"/v1/apps/{AppId}/ensure",
            shell.Server.ClientToken,
            "{\"timeoutMs\":1000}");

        Assert.Equal(200, status);
        using var document = Parse(body);
        Assert.Equal(AppId, document.RootElement.GetProperty("appId").GetString());
        Assert.Equal(AppStates.Ready, document.RootElement.GetProperty("state").GetString());
        Assert.Equal(shell.Launcher.Port, document.RootElement.GetProperty("runtime").GetProperty("port").GetInt32());
        Assert.Equal(AppStates.Ready, shell.Entry.State);
        Assert.Equal(1, shell.Launcher.LaunchCount);
    }

    [Fact]
    public async Task WidgetRequest_IdleHold_AppliesWithoutPolicyHost()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();
        var appToken = shell.SeedAppToken();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            $"/v1/widgets/{WidgetId}/request",
            appToken,
            "{\"kind\":\"idleHold\",\"value\":true}");

        Assert.Equal(200, status);
        using var document = Parse(body);
        Assert.True(document.RootElement.GetProperty("applied").GetBoolean());
        Assert.True(shell.Entry.IdleHold);
    }

    [Fact]
    public async Task WidgetRequest_DesktopLayer_IsPolicyDenied()
    {
        await using var shell = new HostTestShell();
        shell.Policy.Decision = new WidgetPolicyDecision(false, ErrorCodes.PolicyDenied, "宿主未支持桌面层");
        await shell.Server.StartAsync();
        var appToken = shell.SeedAppToken();

        var (status, body) = await SendAsync(
            shell.Server.Port,
            HttpMethod.Post,
            $"/v1/widgets/{WidgetId}/request",
            appToken,
            "{\"kind\":\"layer\",\"value\":\"desktop\"}");

        Assert.Equal(403, status);
        AssertErrorCode(body, ErrorCodes.PolicyDenied);
        Assert.Single(shell.Policy.Calls);
        Assert.Equal("layer", shell.Policy.Calls[0].Request.Kind);
    }

    [Fact]
    public async Task Start_WritesHostFileWithAllFields()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        Assert.True(File.Exists(shell.Paths.HostFilePath));
        var hostFile = JsonSerializer.Deserialize<HostFile>(File.ReadAllText(shell.Paths.HostFilePath), ShellJson.File);
        Assert.NotNull(hostFile);
        Assert.True(hostFile!.Pid > 0);
        Assert.Equal(shell.Server.Port, hostFile.Port);
        Assert.NotEqual(default, hostFile.StartedAt);
        Assert.False(string.IsNullOrEmpty(hostFile.HostVersion));
        Assert.Equal(ShellConstants.HostApiVersion, hostFile.HostApiVersion);
        Assert.Equal(ShellConstants.ClientTokenFileName, hostFile.ClientTokenPath);
        Assert.Equal(shell.Registry.Revision, hostFile.RegistryRevision);

        Assert.True(File.Exists(shell.Paths.ClientTokenFilePath));
        Assert.Equal(shell.Server.ClientToken, File.ReadAllText(shell.Paths.ClientTokenFilePath));
    }

    [Fact]
    public async Task RemoveDiscoveryFiles_DeletesHostFileAndClientToken()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();

        Assert.True(File.Exists(shell.Paths.HostFilePath));
        Assert.True(File.Exists(shell.Paths.ClientTokenFilePath));

        shell.Server.RemoveDiscoveryFiles();

        Assert.False(File.Exists(shell.Paths.HostFilePath));
        Assert.False(File.Exists(shell.Paths.ClientTokenFilePath));
    }

    [Fact]
    public async Task RestartedShell_InvalidatesPreviousClientToken()
    {
        await using var shell = new HostTestShell();
        await shell.Server.StartAsync();
        var oldToken = shell.Server.ClientToken;
        await shell.Server.DisposeAsync();

        await using var second = shell.CreateServer();
        await second.StartAsync();
        Assert.NotEqual(oldToken, second.ClientToken);

        var (oldStatus, oldBody) = await SendAsync(second.Port, HttpMethod.Get, "/v1/host", oldToken);
        Assert.NotEqual(200, oldStatus);
        Assert.Equal(401, oldStatus);
        AssertErrorCode(oldBody, ErrorCodes.TokenInvalid);

        var (newStatus, _) = await SendAsync(second.Port, HttpMethod.Get, "/v1/host", second.ClientToken);
        Assert.Equal(200, newStatus);
    }
}
