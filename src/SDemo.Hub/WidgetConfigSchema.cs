using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SDemo.Hub.Contracts;

namespace SDemo.Hub;

/// <summary>
/// 组件设置（widget-contract §8）的本地实现：校验声明的 Schema 子集，
/// 把默认值/存量值夹进 minimum..maximum，并把值拼成组件页的查询串。
/// 只做「声明 → 表单 → widgets.json → entry 查询串」这条链，不碰应用内部设置。
/// </summary>
public static class WidgetConfigSchema
{
    public const int MaxProperties = 16;
    public const int MaxStringLength = 512;
    public const int MaxEnumValues = 32;
    public const int MaxTitleLength = 32;
    public const int MaxDescriptionLength = 200;

    public const string Boolean = "boolean";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string String = "string";

    private static readonly Regex PropertyKeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, WidgetConfigProperty> Empty =
        new Dictionary<string, WidgetConfigProperty>(StringComparer.Ordinal);

    /// <summary>组件声明的参数；没有 config 时返回空集合。</summary>
    public static IReadOnlyDictionary<string, WidgetConfigProperty> Properties(WidgetDescriptor? widget) =>
        widget?.Config?.Properties is { Count: > 0 } properties ? properties : Empty;

    public static bool HasProperties(WidgetDescriptor? widget) => Properties(widget).Count > 0;

    /// <summary>校验一个组件设置的声明；合法返回 null，否则返回中文错误。</summary>
    public static string? Validate(WidgetDescriptor widget)
    {
        var config = widget.Config;
        if (config is null)
        {
            return null;
        }

        if (config.Type is { } type && !string.Equals(type, "object", StringComparison.OrdinalIgnoreCase))
        {
            return $"组件 {widget.Id} 的 config.type 只支持 object";
        }

        var properties = config.Properties;
        if (properties is null || properties.Count == 0)
        {
            return $"组件 {widget.Id} 的 config 必须声明 properties";
        }

        if (properties.Count > MaxProperties)
        {
            return $"组件 {widget.Id} 的 config 最多支持 {MaxProperties} 个参数";
        }

        foreach (var pair in properties)
        {
            var error = ValidateProperty(widget.Id, pair.Key, pair.Value);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? ValidateProperty(string widgetId, string key, WidgetConfigProperty property)
    {
        if (string.IsNullOrEmpty(key) || !PropertyKeyPattern.IsMatch(key))
        {
            return $"组件 {widgetId} 的参数名 {key} 不合法（字母或数字开头，只含字母/数字/-/_，最长 32）";
        }

        if (property.Title is { Length: > MaxTitleLength })
        {
            return $"组件 {widgetId} 的参数 {key} 的 title 最长 {MaxTitleLength} 个字符";
        }

        if (property.Description is { Length: > MaxDescriptionLength })
        {
            return $"组件 {widgetId} 的参数 {key} 的 description 最长 {MaxDescriptionLength} 个字符";
        }

        var kind = NormalizeType(property.Type);
        if (kind is not (Boolean or Integer or Number or String))
        {
            return $"组件 {widgetId} 的参数 {key} 的 type 只支持 boolean / integer / number / string";
        }

        if (kind != String && property.Enum is not null)
        {
            return $"组件 {widgetId} 的参数 {key} 的 enum 只支持 string 类型";
        }

        if (kind != String && property.Format is not null)
        {
            return $"组件 {widgetId} 的参数 {key} 的 format 只支持 string 类型";
        }

        if (kind == String && property.Format is { } format && !string.Equals(format, "path", StringComparison.OrdinalIgnoreCase))
        {
            return $"组件 {widgetId} 的参数 {key} 的 format 只支持 path";
        }

        if (property.Minimum is { } minimum && property.Maximum is { } maximum && minimum > maximum)
        {
            return $"组件 {widgetId} 的参数 {key} 的 minimum 大于 maximum";
        }

        if (kind == Integer
            && ((property.Minimum is { } min && !IsIntegral(min)) || (property.Maximum is { } max && !IsIntegral(max))))
        {
            return $"组件 {widgetId} 的参数 {key} 的 minimum/maximum 必须是整数";
        }

        return kind switch
        {
            Boolean => ValidateBooleanDefault(widgetId, key, property),
            Integer or Number => ValidateNumberDefault(widgetId, key, property, kind),
            _ => ValidateStringProperty(widgetId, key, property),
        };
    }

    private static string? ValidateBooleanDefault(string widgetId, string key, WidgetConfigProperty property)
    {
        if (property.Default is not { } value)
        {
            return null;
        }

        return TryBoolean(value, out _)
            ? null
            : $"组件 {widgetId} 的参数 {key} 的 default 必须是布尔值";
    }

    private static string? ValidateNumberDefault(string widgetId, string key, WidgetConfigProperty property, string kind)
    {
        if (property.Default is not { } value)
        {
            return null;
        }

        if (!TryNumber(value, out var number))
        {
            return $"组件 {widgetId} 的参数 {key} 的 default 必须是数字";
        }

        if (kind == Integer && !IsIntegral(number))
        {
            return $"组件 {widgetId} 的参数 {key} 的 default 必须是整数";
        }

        if ((property.Minimum is { } minimum && number < minimum) || (property.Maximum is { } maximum && number > maximum))
        {
            return $"组件 {widgetId} 的参数 {key} 的 default 超出 minimum..maximum";
        }

        return null;
    }

    private static string? ValidateStringProperty(string widgetId, string key, WidgetConfigProperty property)
    {
        var options = property.Enum;
        if (options is not null)
        {
            if (options.Length is 0 or > MaxEnumValues)
            {
                return $"组件 {widgetId} 的参数 {key} 的 enum 必须有 1..{MaxEnumValues} 个选项";
            }

            if (options.Distinct(StringComparer.Ordinal).Count() != options.Length)
            {
                return $"组件 {widgetId} 的参数 {key} 的 enum 存在重复选项";
            }

            foreach (var option in options)
            {
                if (string.IsNullOrEmpty(option) || option.Length > 64)
                {
                    return $"组件 {widgetId} 的参数 {key} 的 enum 选项必须为 1..64 个字符";
                }
            }
        }

        if (property.Default is not { } value)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return $"组件 {widgetId} 的参数 {key} 的 default 必须是字符串";
        }

        var text = value.GetString() ?? string.Empty;
        if (text.Length > MaxStringLength)
        {
            return $"组件 {widgetId} 的参数 {key} 的 default 最长 {MaxStringLength} 个字符";
        }

        if (options is not null && !options.Contains(text, StringComparer.Ordinal))
        {
            return $"组件 {widgetId} 的参数 {key} 的 default 不在 enum 选项内";
        }

        return null;
    }

    /// <summary>
    /// 把一个不可信的存量值收敛成合法值：类型不符回落 default，数字夹进 minimum..maximum。
    /// 声明已通过 <see cref="Validate"/>，这里不再返回错误。
    /// </summary>
    public static JsonElement Coerce(WidgetConfigProperty property, JsonElement? value)
    {
        switch (NormalizeType(property.Type))
        {
            case Boolean:
                {
                    var boolean = value is { } element && TryBoolean(element, out var parsed)
                        ? parsed
                        : DefaultBoolean(property);
                    return JsonSerializer.SerializeToElement(boolean);
                }

            case Integer:
                {
                    var number = ResolveNumber(property, value);
                    var minimum = property.Minimum ?? long.MinValue;
                    var maximum = property.Maximum ?? long.MaxValue;
                    var rounded = Math.Round(number, MidpointRounding.AwayFromZero);
                    var clamped = Math.Clamp(rounded, minimum, maximum);
                    return JsonSerializer.SerializeToElement((long)clamped);
                }

            case Number:
                {
                    var number = ResolveNumber(property, value);
                    var minimum = property.Minimum ?? double.MinValue;
                    var maximum = property.Maximum ?? double.MaxValue;
                    return JsonSerializer.SerializeToElement(Math.Clamp(number, minimum, maximum));
                }

            default:
                {
                    var text = value is { } element && element.ValueKind == JsonValueKind.String
                        ? element.GetString() ?? string.Empty
                        : DefaultString(property);
                    if (property.Enum is { Length: > 0 } options && !options.Contains(text, StringComparer.Ordinal))
                    {
                        text = options[0];
                    }

                    if (text.Length > MaxStringLength)
                    {
                        text = text[..MaxStringLength];
                    }

                    if (string.Equals(property.Format, "path", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text.Trim();
                    }

                    return JsonSerializer.SerializeToElement(text);
                }
        }
    }

    /// <summary>
    /// 按组件声明补齐缺失值、剔除未声明键、夹紧越界值。
    /// 用户改过的合法值不会被重置，只有声明变更导致的越界/失效值才会回落。
    /// </summary>
    public static bool Normalize(WidgetDescriptor? widget, IDictionary<string, JsonElement> values, out bool changed)
    {
        changed = false;
        var properties = Properties(widget);
        if (properties.Count == 0)
        {
            if (values.Count > 0)
            {
                values.Clear();
                changed = true;
            }

            return true;
        }

        var stale = values.Keys.Where(key => !properties.ContainsKey(key)).ToArray();
        foreach (var key in stale)
        {
            values.Remove(key);
            changed = true;
        }

        foreach (var pair in properties)
        {
            var current = values.TryGetValue(pair.Key, out var existing) ? (JsonElement?)existing : null;
            var coerced = Coerce(pair.Value, current);
            if (current is not { } value || !Equivalent(value, coerced))
            {
                changed = true;
            }

            values[pair.Key] = coerced;
        }

        return true;
    }

    /// <summary>两个值在表单语义上是否相同（数字比较值，不比较原始文本）。</summary>
    public static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => left.TryGetDouble(out var l) && right.TryGetDouble(out var r) && l == r,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }

    /// <summary>把持久化的值拼成 entry 的查询串（含前导 ?）；没有参数时返回空串。</summary>
    public static string BuildQuery(WidgetDescriptor? widget, IReadOnlyDictionary<string, JsonElement>? values)
    {
        var properties = Properties(widget);
        if (properties.Count == 0 || values is null || values.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var pair in properties)
        {
            if (!values.TryGetValue(pair.Key, out var value))
            {
                continue;
            }

            var text = ToQueryText(pair.Value, value);
            if (text is null)
            {
                continue;
            }

            builder.Append(builder.Length == 0 ? '?' : '&');
            builder.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(text));
        }

        return builder.ToString();
    }

    /// <summary>设置表单里展示的值文本。</summary>
    public static string ToDisplay(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.ToString(),
    };

    public static string NormalizeType(string? type) => type?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? ToQueryText(WidgetConfigProperty property, JsonElement value) =>
        NormalizeType(property.Type) switch
        {
            Boolean => TryBoolean(value, out var boolean) ? (boolean ? "true" : "false") : null,
            Integer => TryNumber(value, out var integer)
                ? ((long)Math.Round(integer, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)
                : null,
            Number => TryNumber(value, out var number) ? number.ToString("R", CultureInfo.InvariantCulture) : null,
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : null,
        };

    private static double ResolveNumber(WidgetConfigProperty property, JsonElement? value)
    {
        if (value is { } element && TryNumber(element, out var parsed) && double.IsFinite(parsed))
        {
            return parsed;
        }

        return DefaultNumber(property) ?? 0d;
    }

    private static double? DefaultNumber(WidgetConfigProperty property)
    {
        if (property.Default is { } value && TryNumber(value, out var number) && double.IsFinite(number))
        {
            return number;
        }

        return null;
    }

    private static bool DefaultBoolean(WidgetConfigProperty property) =>
        property.Default is { } value && TryBoolean(value, out var boolean) && boolean;

    private static string DefaultString(WidgetConfigProperty property)
    {
        if (property.Default is { } value && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return property.Enum is { Length: > 0 } options ? options[0] : string.Empty;
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryBoolean(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(element.GetString(), out value);
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var number) && number is 0 or 1)
                {
                    value = number == 1;
                    return true;
                }

                value = false;
                return false;
            default:
                value = false;
                return false;
        }
    }

    private static bool IsIntegral(double value) => double.IsFinite(value) && Math.Floor(value) == value;
}
