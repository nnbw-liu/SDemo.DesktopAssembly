using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Shell.Contracts;

/// <summary>
/// <c>app.json</c>：怎么把应用进程跑起来（host-protocol §2）。
/// 窗口长什么样由 <see cref="WidgetManifest"/> 描述，两者不混。
/// </summary>
public sealed class AppManifest
{
    public int SchemaVersion { get; set; } = ShellConstants.ManifestSchemaVersion;

    /// <summary>反向域名，必须等于安装目录名。</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>相对 <c>apps\&lt;appId&gt;\</c> 的路径；不接受绝对路径与 <c>..</c>。</summary>
    public string Executable { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = new();

    /// <summary>登记的可执行文件哈希（小写 64 位十六进制）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    public string SingleInstanceMutex { get; set; } = string.Empty;

    public int StartupTimeoutMs { get; set; } = 8_000;

    /// <summary>0 表示常驻不退出。</summary>
    public int IdleTimeoutMs { get; set; } = 300_000;

    /// <summary><see cref="StartupPolicies"/> 之一。</summary>
    public string Startup { get; set; } = StartupPolicies.OnDemand;

    public string MinHostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    public McpManifest? Mcp { get; set; }

    /// <summary>未知字段原样保留（向前兼容，widget-contract §4）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary><c>app.json.mcp</c>：只声明地址与版本，不含工具语义（DA-89）。</summary>
public sealed class McpManifest
{
    public string Transport { get; set; } = "http";

    public string Path { get; set; } = "/mcp";

    /// <summary><c>"runtime"</c> 或 1–65535 的端口号。</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Port { get; set; } = "runtime";

    public string Token { get; set; } = "runtime";

    public string? ToolsVersion { get; set; }

    public List<string>? ToolHints { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>允许 <c>"runtime"</c> 或数字两种写法的字符串字段。</summary>
public sealed class StringOrNumberJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var number) => number.ToString(),
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException("期望字符串或数字。"),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.Equals(value, "runtime", StringComparison.Ordinal))
        {
            writer.WriteStringValue(value);
            return;
        }

        if (long.TryParse(value, out var number))
        {
            writer.WriteNumberValue(number);
            return;
        }

        writer.WriteStringValue(value);
    }
}

/// <summary>清单校验问题（错误聚合，不抛异常）。</summary>
public sealed record ManifestIssue(string Code, string Message, string? Field = null)
{
    public bool IsError { get; init; } = true;
}

/// <summary>清单错误码常量，便于日志与测试断言。</summary>
public static class ManifestIssueCodes
{
    public const string JsonInvalid = "MANIFEST_JSON_INVALID";
    public const string SchemaVersion = "MANIFEST_SCHEMA_VERSION";
    public const string Required = "MANIFEST_REQUIRED";
    public const string Pattern = "MANIFEST_PATTERN";
    public const string Range = "MANIFEST_RANGE";
    public const string ExecutableMissing = "APP_EXECUTABLE_MISSING";
    public const string ExecutableOutsideApp = "APP_EXECUTABLE_OUTSIDE";
    public const string HashMissing = "APP_HASH_MISSING";
    public const string HashMismatch = "APP_HASH_MISMATCH";
    public const string HostApiVersion = "MANIFEST_HOST_API_VERSION";
    public const string RendererUnsupported = "WIDGET_RENDERER_UNSUPPORTED";
    public const string LayerInvalid = "WIDGET_LAYER_INVALID";
    public const string AppIdMismatch = "MANIFEST_APP_ID_MISMATCH";
    public const string DuplicateWidgetId = "WIDGET_ID_DUPLICATE";
    public const string DuplicateAppId = "APP_ID_DUPLICATE";
    public const string ComponentLimit = "COMPONENT_LIMIT_EXCEEDED";
}
