using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.App;

/// <summary>
/// Hub 自己的回环 HTTP 服务：MCP 目录 + HTTP 镜像 + 短期 ticket。
/// 只绑定 127.0.0.1；所有响应都不包含应用 token。
/// </summary>
public sealed class HubServer : IAsyncDisposable
{
    public const string AppId = "sdemo.hub";
    public const string DisplayName = "SDemo Hub";
    public const string Version = "0.2.0";
    public const string ProtocolVersion = "2025-06-18";

    private readonly HubPaths _paths;
    private readonly HubAppRegistry _registry;
    private readonly IHubLog _log;
    private readonly TicketStore _tickets = new();
    private WebApplication? _app;
    private string _token = string.Empty;
    private DateTimeOffset _startedAt;

    public HubServer(HubPaths paths, HubAppRegistry registry, IHubLog log)
    {
        _paths = paths;
        _registry = registry;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    public int Port { get; private set; }

    public string BaseUrl => Port > 0 ? $"http://127.0.0.1:{Port}" : string.Empty;

    public string McpUrl => Port > 0 ? $"{BaseUrl}/mcp" : string.Empty;

    public string DirectoryUrl => Port > 0 ? $"{BaseUrl}/v1/directory" : string.Empty;

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _startedAt = DateTimeOffset.Now;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "SDemo.HubApp",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(_tickets);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsLoopbackRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.MapGet("/v1/health", () => Results.Json(new
        {
            ok = true,
            appId = AppId,
            version = Version,
        }));

        app.MapGet("/v1/directory", async (HttpContext context) =>
        {
            if (!Authorize(context, allowTicket: true))
            {
                await Unauthorized(context);
                return;
            }

            var includeOffline = context.Request.Query["includeOffline"].ToString()
                .Equals("true", StringComparison.OrdinalIgnoreCase);
            var apps = HubDirectory.Apps(_registry.Snapshot(), includeOffline);
            await context.Response.WriteAsJsonAsync(new { apps }, HubJson.Options);
        });

        app.MapPost("/v1/directory/tickets", async (HttpContext context) =>
        {
            if (!Authorize(context, allowTicket: false))
            {
                await Unauthorized(context);
                return;
            }

            if (!_tickets.TryIssue(out var ticket, out var expiresAt))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = "HUB_DIRECTORY_BUSY",
                        message = "ticket 数量已达上限，请稍后重试",
                    },
                }, HubJson.Options);
                return;
            }

            _log.Info($"签发目录 ticket，有效期至 {expiresAt:HH:mm:ss}，来源 {context.Connection.RemoteIpAddress}");
            await context.Response.WriteAsJsonAsync(new { ticket, expiresAt }, HubJson.Options);
        });

        app.MapPost("/mcp", HandleMcpAsync);

        await app.StartAsync();
        _app = app;

        var address = app.Urls.FirstOrDefault();
        Port = address is null ? 0 : new Uri(address).Port;
        if (Port == 0)
        {
            throw new InvalidOperationException("Hub 目录服务未取得监听端口");
        }

        IsRunning = true;
        WriteConfig(running: true);
        _log.Info($"Hub 目录服务已启动：{BaseUrl}（MCP {McpUrl}）");
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        try
        {
            WriteConfig(running: false);
        }
        catch (Exception ex)
        {
            _log.Warn("写 Hub stopped 配置失败", ex);
        }

        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.Warn("停止 Hub 目录服务失败", ex);
            }

            await _app.DisposeAsync();
            _app = null;
        }

        _tickets.Clear();
        _log.Info("Hub 目录服务已停止");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task HandleMcpAsync(HttpContext context)
    {
        if (!Authorize(context, allowTicket: true))
        {
            await Unauthorized(context);
            return;
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body);
        }
        catch (JsonException)
        {
            await WriteRpcError(context, null, -32700, "Parse error");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteRpcError(context, null, -32600, "Invalid Request");
                return;
            }

            var hasId = root.TryGetProperty("id", out var idElement);
            if (!hasId)
            {
                // MCP 通知（例如 notifications/initialized）不需要响应。
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            var id = JsonNode.Parse(idElement.GetRawText());
            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                await WriteRpcError(context, id, -32600, "Invalid Request");
                return;
            }

            var method = methodElement.GetString()!;
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            switch (method)
            {
                case "initialize":
                    await WriteRpcResult(context, id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "sdemo-hub",
                            ["version"] = Version,
                        },
                    });
                    return;

                case "ping":
                    await WriteRpcResult(context, id, new JsonObject());
                    return;

                case "tools/list":
                    await WriteRpcResult(context, id, new JsonObject
                    {
                        ["tools"] = BuildToolList(),
                    });
                    return;

                case "tools/call":
                    await HandleToolCallAsync(context, id, parameters);
                    return;

                default:
                    await WriteRpcError(context, id, -32601, $"Method not found: {method}");
                    return;
            }
        }
    }

    private async Task HandleToolCallAsync(HttpContext context, JsonNode? id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            await WriteRpcError(context, id, -32602, "Invalid params: name is required");
            return;
        }

        var name = nameElement.GetString()!;
        var arguments = parameters.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
            ? args
            : default;

        switch (name)
        {
            case "list_apps":
            {
                var includeOffline = ReadBool(arguments, "includeOffline");
                var kind = ReadString(arguments, "kind") ?? "all";
                var apps = HubDirectory.Apps(_registry.Snapshot(), includeOffline, kind);
                await WriteToolResult(context, id, new { apps });
                return;
            }

            case "list_agents":
            {
                var includeOffline = ReadBool(arguments, "includeOffline");
                var agents = HubDirectory.Agents(_registry.Snapshot(), includeOffline);
                await WriteToolResult(context, id, new { agents });
                return;
            }

            case "list_mcp_servers":
            {
                var includeOffline = ReadBool(arguments, "includeOffline");
                var servers = HubDirectory.McpServers(_registry.Snapshot(), includeOffline);
                await WriteToolResult(context, id, new { servers });
                return;
            }

            case "get_app":
            {
                var appId = ReadString(arguments, "appId");
                if (string.IsNullOrWhiteSpace(appId))
                {
                    await WriteRpcError(context, id, -32602, "Invalid params: appId is required");
                    return;
                }

                var app = HubDirectory.Find(_registry.Snapshot(), appId);
                if (app is null)
                {
                    await WriteToolError(context, id, "HUB_APP_NOT_FOUND", $"未找到应用 {appId}");
                    return;
                }

                await WriteToolResult(context, id, app);
                return;
            }

            default:
                await WriteRpcError(context, id, -32601, $"Unknown tool: {name}");
                return;
        }
    }

    private static JsonArray BuildToolList() =>
    [
        Tool(
            "list_apps",
            "列出当前 SDemo 应用；默认只返回在线项。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["includeOffline"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                    ["kind"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("all", "app", "agent", "both"),
                        ["default"] = "all",
                    },
                },
                ["additionalProperties"] = false,
            }),
        Tool(
            "list_agents",
            "列出声明了 Agent 地址的应用；默认只返回在线项。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["includeOffline"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                },
                ["additionalProperties"] = false,
            }),
        Tool(
            "list_mcp_servers",
            "列出声明了 MCP 地址的应用；默认只返回在线项。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["includeOffline"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                },
                ["additionalProperties"] = false,
            }),
        Tool(
            "get_app",
            "按 appId 获取单个应用详情。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["appId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                },
                ["required"] = new JsonArray("appId"),
                ["additionalProperties"] = false,
            }),
    ];

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static bool ReadBool(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task WriteToolResult(HttpContext context, JsonNode? id, object payload)
    {
        var text = JsonSerializer.Serialize(payload, HubJson.Options);
        await WriteRpcResult(context, id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["isError"] = false,
        });
    }

    private async Task WriteToolError(HttpContext context, JsonNode? id, string code, string message)
    {
        await WriteRpcResult(context, id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(new { error = new { code, message } }, HubJson.Options),
                },
            },
            ["isError"] = true,
        });
    }

    private static async Task WriteRpcResult(HttpContext context, JsonNode? id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        };

        await context.Response.WriteAsJsonAsync(response, HubJson.Options);
    }

    private static async Task WriteRpcError(HttpContext context, JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        await context.Response.WriteAsJsonAsync(response, HubJson.Options);
    }

    private bool Authorize(HttpContext context, bool allowTicket)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var credential = header["Bearer ".Length..].Trim();
        if (FixedEquals(credential, _token))
        {
            return true;
        }

        if (allowTicket && _tickets.TryValidate(credential))
        {
            // 审计：ticket 只记录使用，不记录明文；来源限定回环地址。
            _log.Info($"目录 ticket 校验通过，来源 {context.Connection.RemoteIpAddress}");
            return true;
        }

        return false;
    }

    private static async Task Unauthorized(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = "HUB_DIRECTORY_AUTH_FAILED",
                message = "目录 token 或 ticket 无效",
            },
        }, HubJson.Options);
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        if (!context.Request.Host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue("Origin", out var origin) ||
            string.IsNullOrWhiteSpace(origin.ToString()))
        {
            return true;
        }

        return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private void WriteConfig(bool running)
    {
        Directory.CreateDirectory(_paths.AppsDirectory);

        // 0.2.0 之前写在 Hub 根目录，迁移后清理，避免 Agent 读到两份。
        try
        {
            if (File.Exists(_paths.LegacyHubConfigFile))
            {
                File.Delete(_paths.LegacyHubConfigFile);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响新配置写入。
        }

        var config = new HubAppConfig
        {
            SchemaVersion = 1,
            AppId = AppId,
            DisplayName = DisplayName,
            Version = Version,
            Kind = "hub",
            Port = Port,
            BaseUrl = BaseUrl,
            Token = running ? _token : null,
            Pid = running ? Environment.ProcessId : 0,
            StartedAt = _startedAt,
            State = running ? "running" : "stopped",
            Mcp = new McpDescriptor { Transport = "http", Path = "/mcp" },
        };

        var json = JsonSerializer.Serialize(config, HubJson.Options);
        var temp = _paths.HubConfigFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _paths.HubConfigFile, overwrite: true);
    }

    private sealed class TicketStore
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _tickets = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public bool TryIssue(out string ticket, out DateTimeOffset expiresAt)
        {
            lock (_gate)
            {
                Prune();
                if (_tickets.Count >= 32)
                {
                    ticket = string.Empty;
                    expiresAt = default;
                    return false;
                }

                ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                expiresAt = DateTimeOffset.Now.Add(Lifetime);
                _tickets[ticket] = expiresAt;
                return true;
            }
        }

        public bool TryValidate(string ticket)
        {
            if (!_tickets.TryGetValue(ticket, out var expiresAt))
            {
                return false;
            }

            if (expiresAt <= DateTimeOffset.Now)
            {
                _tickets.TryRemove(ticket, out _);
                return false;
            }

            return true;
        }

        public void Clear() => _tickets.Clear();

        private void Prune()
        {
            var now = DateTimeOffset.Now;
            foreach (var pair in _tickets)
            {
                if (pair.Value <= now)
                {
                    _tickets.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}
