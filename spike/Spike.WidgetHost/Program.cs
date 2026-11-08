using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Spike.WidgetHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var appId = GetArg(args, "--app-id") ?? "sdemo.spike";
        var token = GetArg(args, "--token") ?? Guid.NewGuid().ToString("N");
        var runSeconds = int.TryParse(GetArg(args, "--run-seconds"), out var s) ? s : 0;
        var displayName = GetArg(args, "--display-name") ?? appId;
        var hostName = appId + ".localhost";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot",
            Args = args,
        });

        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton(new WidgetHostOptions(appId, token));

        var app = builder.Build();

        app.UseStaticFiles();

        // Hub 的 API 走 127.0.0.1 + Bearer；只有组件页面（WebView2）走主机别名 + cookie。
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;

            if (path.StartsWithSegments("/v1/info") || path.StartsWithSegments("/v1/main-window"))
            {
                var authorization = ctx.Request.Headers.Authorization.ToString();
                if (!string.Equals(authorization, "Bearer " + token, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsync("unauthorized");
                    return;
                }

                await next();
                return;
            }

            if (path.StartsWithSegments("/health"))
            {
                await next();
                return;
            }

            if (!string.Equals(ctx.Request.Host.Host, hostName, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync($"bad host: {ctx.Request.Host.Host}; expected {hostName}");
                return;
            }

            if (path.StartsWithSegments("/widget")
                || path.StartsWithSegments("/whoami")
                || path.StartsWithSegments("/_blazor"))
            {
                var cookie = ctx.Request.Cookies["sdemo_session"];
                if (!string.Equals(cookie, token, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsync("unauthorized");
                    return;
                }
            }

            await next();
        });

        app.UseAntiforgery();
        app.MapStaticAssets();

        app.MapGet("/", () => Results.Redirect("/widget"));
        app.MapGet("/health", () => Results.Json(new { appId, state = "running" }));
        app.MapGet("/v1/info", () => Results.Json(new
        {
            schemaVersion = 1,
            appId,
            displayName,
            version = "0.0.1-spike",
            kind = "app",
            capabilities = new[] { "widgets" },
            mcp = (object?)null,
            agent = (object?)null,
            mainWindow = new { title = displayName, width = 960, height = 720 },
            widgets = new[]
            {
                new
                {
                    id = "main",
                    displayName = "Spike 组件",
                    entry = "/widget",
                    size = new { width = 420, height = 320 },
                    minSize = new { width = 320, height = 240 },
                    defaultVisible = true,
                },
            },
        }));
        app.MapPost("/v1/main-window/open", () => Results.Json(new { ok = true }));
        app.MapGet("/whoami", (HttpContext ctx) =>
            Results.Text($"{appId}|{ctx.Request.Cookies["sdemo_session"]}"));

        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Start();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        var port = new Uri(address).Port;

        WriteConfig(new
        {
            schemaVersion = 1,
            appId,
            displayName,
            version = "0.0.1-spike",
            kind = "app",
            executable = Environment.ProcessPath,
            arguments = new[] { "--app-id", appId, "--token", token },
            workingDirectory = AppContext.BaseDirectory,
            port,
            baseUrl = $"http://127.0.0.1:{port}",
            token,
            pid = Environment.ProcessId,
            startedAt = DateTimeOffset.Now,
            state = "running",
            mcp = (object?)null,
            agent = (object?)null,
            mainWindow = new { title = displayName, width = 960, height = 720 },
            widgets = new[]
            {
                new
                {
                    id = "main",
                    displayName = "Spike 组件",
                    entry = "/widget",
                    size = new { width = 420, height = 320 },
                    minSize = new { width = 320, height = 240 },
                    defaultVisible = true,
                },
            },
        });

        Console.WriteLine($"READY {appId} {port} {token} {hostName}");

        var exit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.Set();
        };

        if (runSeconds > 0)
        {
            exit.Wait(TimeSpan.FromSeconds(runSeconds));
        }
        else
        {
            exit.Wait();
        }

        app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Console.WriteLine("STOPPED");
    }

    private static void WriteConfig(object config)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDemo", "Hub", "apps");
        Directory.CreateDirectory(dir);

        var appId = (string)config.GetType().GetProperty("appId")!.GetValue(config)!;
        var path = Path.Combine(dir, appId + ".json");
        var tmp = path + ".tmp";

        File.WriteAllText(
            tmp,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        File.Move(tmp, path, overwrite: true);
        Console.WriteLine($"CONFIG {path}");
    }

    private static string? GetArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
