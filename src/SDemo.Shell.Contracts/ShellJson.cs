using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Shell.Contracts;

/// <summary>壳内统一的 JSON 序列化设置。</summary>
public static class ShellJson
{
    /// <summary>HTTP 线上格式：camelCase、忽略 null、大小写不敏感读取。</summary>
    public static JsonSerializerOptions Wire { get; } = CreateWire();

    /// <summary>落盘格式：camelCase + 2 空格缩进 + LF 换行（shell-storage §2）。</summary>
    public static JsonSerializerOptions File { get; } = CreateFile();

    private static JsonSerializerOptions CreateWire()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = false,
        };
        // 注意：StringOrNumberJsonConverter 只通过 [JsonConverter] 用在 mcp.port 上，
        // 不能全局注册——否则 "000…0" 这类哈希字符串会被写成数字 0。
        return options;
    }

    private static JsonSerializerOptions CreateFile()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
        return options;
    }

    /// <summary>统一换行为 LF，避免 Windows 上写出 CRLF。</summary>
    public static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
