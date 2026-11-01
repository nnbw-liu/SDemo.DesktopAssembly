using System.Text.Json;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.Settings;

/// <summary>设置表单的控件类型（widget-contract §8 的 Schema 子集）。</summary>
public enum ConfigFieldKind
{
    Boolean,

    Integer,

    Number,

    Text,

    Enum,

    Path,
}

/// <summary>一个可由宿主渲染的设置项。</summary>
public sealed class ConfigField
{
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    public ConfigFieldKind Kind { get; init; }

    public JsonElement? Default { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public IReadOnlyList<string> EnumValues { get; init; } = Array.Empty<string>();
}

/// <summary><c>widget.json.config</c> 的解析结果：字段列表 + 不支持的结构（object / array / oneOf）。</summary>
public sealed class ConfigSchema
{
    public static ConfigSchema Empty { get; } = new();

    public IReadOnlyList<ConfigField> Fields { get; init; } = Array.Empty<ConfigField>();

    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    public bool IsSupported => Issues.Count == 0;

    public ConfigField? Find(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// DA-62：宿主用描述符渲染设置表单，不加载应用代码。
/// 支持 boolean / integer / number / string / string(enum) / string+format=path；
/// object / array / oneOf / $ref 一律显式报「不支持」，不静默降级。
/// </summary>
public static class WidgetConfigSchema
{
    /// <summary>解析 <c>config</c>。语法层面绝不抛异常。</summary>
    public static ConfigSchema Parse(JsonElement? config)
    {
        if (config is not { } root || root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ConfigSchema.Empty;
        }

        var issues = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add("config 必须是 type=object 的 Schema");
            return new ConfigSchema { Issues = issues };
        }

        foreach (var keyword in UnsupportedRootKeywords)
        {
            if (root.TryGetProperty(keyword, out _))
            {
                issues.Add($"config 使用了不支持的结构：{keyword}");
                return new ConfigSchema { Issues = issues };
            }
        }

        if (root.TryGetProperty("type", out var typeElement)
            && (typeElement.ValueKind != JsonValueKind.String
                || !string.Equals(typeElement.GetString(), "object", StringComparison.Ordinal)))
        {
            issues.Add("config.type 目前只支持 object");
            return new ConfigSchema { Issues = issues };
        }

        if (!root.TryGetProperty("properties", out var properties))
        {
            return new ConfigSchema { Issues = issues };
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            issues.Add("config.properties 必须是对象");
            return new ConfigSchema { Issues = issues };
        }

        var fields = new List<ConfigField>();
        foreach (var property in properties.EnumerateObject())
        {
            var field = ParseField(property.Name, property.Value, issues);
            if (field is not null)
            {
                fields.Add(field);
            }
        }

        return new ConfigSchema { Fields = fields, Issues = issues };
    }

    /// <summary>
    /// 计算生效值：已存值能通过类型/范围校验就沿用，否则回落默认值；没有默认值的项不出现在结果里。
    /// 返回值直接作为 <c>configChanged</c> 的载荷。
    /// </summary>
    public static Dictionary<string, JsonElement> ApplyDefaults(
        ConfigSchema schema,
        IReadOnlyDictionary<string, JsonElement>? stored)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (stored is not null
                && stored.TryGetValue(field.Key, out var value)
                && Coerce(field, value) is { } coerced)
            {
                result[field.Key] = coerced;
                continue;
            }

            if (field.Default is { } fallback)
            {
                result[field.Key] = fallback.Clone();
            }
        }

        return result;
    }

    /// <summary>把单个值夹到字段允许的类型与范围；不接受时返回 null（调用方回落默认值）。</summary>
    public static JsonElement? Coerce(ConfigField field, JsonElement value)
    {
        switch (field.Kind)
        {
            case ConfigFieldKind.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.Clone() : null;

            case ConfigFieldKind.Integer:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
                {
                    return null;
                }

                var rounded = Math.Round(number, MidpointRounding.AwayFromZero);
                var clamped = Clamp(rounded, field.Minimum, field.Maximum);
                return JsonSerializer.SerializeToElement((long)clamped, ShellJson.File);
            }

            case ConfigFieldKind.Number:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
                {
                    return null;
                }

                return JsonSerializer.SerializeToElement(Clamp(number, field.Minimum, field.Maximum), ShellJson.File);
            }

            case ConfigFieldKind.Enum:
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var text = value.GetString();
                return text is not null && field.EnumValues.Contains(text, StringComparer.Ordinal)
                    ? JsonSerializer.SerializeToElement(text, ShellJson.File)
                    : null;
            }

            case ConfigFieldKind.Path:
            case ConfigFieldKind.Text:
                return value.ValueKind == JsonValueKind.String ? value.Clone() : null;

            default:
                return null;
        }
    }

    private static readonly string[] UnsupportedRootKeywords = ["oneOf", "anyOf", "allOf", "$ref"];

    private static ConfigField? ParseField(string key, JsonElement node, List<string> issues)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            issues.Add($"config.properties.{key} 必须是对象");
            return null;
        }

        foreach (var keyword in UnsupportedRootKeywords)
        {
            if (node.TryGetProperty(keyword, out _))
            {
                issues.Add($"config.properties.{key} 使用了不支持的结构：{keyword}");
                return null;
            }
        }

        if (!node.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            issues.Add($"config.properties.{key} 缺少 type");
            return null;
        }

        var type = typeElement.GetString();
        var title = ReadString(node, "title") ?? key;
        var description = ReadString(node, "description");
        var format = ReadString(node, "format");

        switch (type)
        {
            case "boolean":
            {
                var @default = ReadDefault(node, key, issues);
                if (@default is { ValueKind: not (JsonValueKind.True or JsonValueKind.False) })
                {
                    issues.Add($"config.properties.{key}.default 必须是布尔");
                    @default = null;
                }

                return new ConfigField
                {
                    Key = key,
                    Title = title,
                    Description = description,
                    Kind = ConfigFieldKind.Boolean,
                    Default = @default,
                };
            }

            case "integer":
            case "number":
            {
                var kind = type == "integer" ? ConfigFieldKind.Integer : ConfigFieldKind.Number;
                var minimum = ReadNumber(node, "minimum", key, issues);
                var maximum = ReadNumber(node, "maximum", key, issues);
                if (minimum is not null && maximum is not null && minimum > maximum)
                {
                    issues.Add($"config.properties.{key} 的 minimum 大于 maximum");
                }

                var @default = ReadDefault(node, key, issues);
                if (@default is { } d && d.ValueKind != JsonValueKind.Number)
                {
                    issues.Add($"config.properties.{key}.default 必须是数字");
                    @default = null;
                }

                if (@default is { ValueKind: JsonValueKind.Number } numeric
                    && numeric.TryGetDouble(out var number)
                    && (number < minimum || number > maximum))
                {
                    issues.Add($"config.properties.{key}.default 超出 minimum/maximum");
                    @default = null;
                }

                return new ConfigField
                {
                    Key = key,
                    Title = title,
                    Description = description,
                    Kind = kind,
                    Minimum = minimum,
                    Maximum = maximum,
                    Default = @default,
                };
            }

            case "string":
            {
                if (string.Equals(format, "path", StringComparison.Ordinal))
                {
                    return new ConfigField
                    {
                        Key = key,
                        Title = title,
                        Description = description,
                        Kind = ConfigFieldKind.Path,
                        Default = ReadDefault(node, key, issues),
                    };
                }

                if (node.TryGetProperty("enum", out var enumElement))
                {
                    if (enumElement.ValueKind != JsonValueKind.Array)
                    {
                        issues.Add($"config.properties.{key}.enum 必须是字符串数组");
                        return null;
                    }

                    var values = new List<string>();
                    foreach (var item in enumElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            issues.Add($"config.properties.{key}.enum 必须是字符串数组");
                            return null;
                        }

                        values.Add(item.GetString()!);
                    }

                    if (values.Count == 0)
                    {
                        issues.Add($"config.properties.{key}.enum 不能为空");
                        return null;
                    }

                    var @default = ReadDefault(node, key, issues);
                    if (@default is { ValueKind: JsonValueKind.String } text
                        && !values.Contains(text.GetString() ?? string.Empty, StringComparer.Ordinal))
                    {
                        issues.Add($"config.properties.{key}.default 不在 enum 取值内");
                        @default = null;
                    }

                    return new ConfigField
                    {
                        Key = key,
                        Title = title,
                        Description = description,
                        Kind = ConfigFieldKind.Enum,
                        EnumValues = values,
                        Default = @default,
                    };
                }

                return new ConfigField
                {
                    Key = key,
                    Title = title,
                    Description = description,
                    Kind = ConfigFieldKind.Text,
                    Default = ReadDefault(node, key, issues),
                };
            }

            case "object":
            case "array":
                issues.Add($"config.properties.{key} 的类型 {type} 不在宿主支持范围内（只支持 boolean/integer/number/string）");
                return null;

            default:
                issues.Add($"config.properties.{key} 的类型 {type} 不在宿主支持范围内");
                return null;
        }
    }

    private static JsonElement? ReadDefault(JsonElement node, string key, List<string> issues)
    {
        if (!node.TryGetProperty("default", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.Clone();
    }

    private static string? ReadString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadNumber(JsonElement node, string name, string key, List<string> issues)
    {
        if (!node.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            issues.Add($"config.properties.{key}.{name} 必须是数字");
            return null;
        }

        return number;
    }

    private static double Clamp(double value, double? minimum, double? maximum)
    {
        if (minimum is { } min && value < min)
        {
            value = min;
        }

        if (maximum is { } max && value > max)
        {
            value = max;
        }

        return value;
    }
}
