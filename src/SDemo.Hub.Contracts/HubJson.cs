using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Hub.Contracts;

/// <summary>连接配置与 /v1/info 的统一 JSON 约定。</summary>
public static class HubJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HubAppConfig? ParseConfig(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HubAppConfig>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static HubAppInfo? ParseInfo(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HubAppInfo>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
