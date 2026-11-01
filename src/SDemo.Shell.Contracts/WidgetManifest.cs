using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Shell.Contracts;

/// <summary>
/// <c>widget.json</c>：给宿主看的组件描述（尺寸、层级、位置意图、生命周期），
/// 不含任何业务语义（widget-contract §1）。
/// </summary>
public sealed class WidgetManifest
{
    public int SchemaVersion { get; set; } = ShellConstants.ManifestSchemaVersion;

    public string Id { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string HostApiVersion { get; set; } = ShellConstants.HostApiVersion;

    /// <summary><see cref="WidgetRendering.Native"/> / <see cref="WidgetRendering.Wasm"/>。</summary>
    public string Renderer { get; set; } = WidgetRendering.Native;

    public WidgetNativeSpec? Native { get; set; }

    public WidgetWasmSpec? Wasm { get; set; }

    public WidgetSize? Size { get; set; }

    /// <summary><see cref="WidgetRendering.Floating"/> / <see cref="WidgetRendering.Desktop"/>。</summary>
    public string Layer { get; set; } = WidgetRendering.Floating;

    public WidgetPositionSpec? Position { get; set; }

    public WidgetBehaviorSpec? Behavior { get; set; }

    public WidgetFocusSpec? Focus { get; set; }

    public WidgetThemeSpec? Theme { get; set; }

    public WidgetActivationSpec? Activation { get; set; }

    /// <summary>缺省空数组 = 最小权限。</summary>
    public List<string>? Permissions { get; set; }

    /// <summary>设置表单的 JSON Schema 子集，P1 只透传不渲染（DA-62 不在 P1）。</summary>
    public JsonElement? Config { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class WidgetNativeSpec
{
    public string Process { get; set; } = string.Empty;

    public bool Layered { get; set; } = true;

    public bool Resizable { get; set; } = true;

    public WidgetMinSize? MinSize { get; set; }
}

public sealed class WidgetMinSize
{
    public int Width { get; set; }

    public int Height { get; set; }
}

public sealed class WidgetWasmSpec
{
    public string WebRoot { get; set; } = "payload";

    public string Entry { get; set; } = "index.html";

    /// <summary>宿主解析后的载荷目录绝对路径（校验通过后回填，不参与序列化）。</summary>
    [JsonIgnore]
    public string? ResolvedRoot { get; set; }

    /// <summary>宿主解析后的入口文件绝对路径（校验通过后回填，不参与序列化）。</summary>
    [JsonIgnore]
    public string? ResolvedEntry { get; set; }
}

public sealed class WidgetSize
{
    public int Width { get; set; }

    public int Height { get; set; }

    public int? MaxWidth { get; set; }

    public int? MaxHeight { get; set; }
}

public sealed class WidgetPositionSpec
{
    /// <summary>anchored / remember。</summary>
    public string Mode { get; set; } = "anchored";

    public string Corner { get; set; } = "top-right";

    public int OffsetX { get; set; }

    public int OffsetY { get; set; }

    public int SnapPx { get; set; }

    public bool RememberPerMonitor { get; set; } = true;
}

public sealed class WidgetBehaviorSpec
{
    public bool AlwaysOnTopDefault { get; set; } = true;

    public string DragMode { get; set; } = "app";

    /// <summary>toggleWithHotkey / never / always 等；v1 只实现开关式。</summary>
    public string ClickThrough { get; set; } = "toggleWithHotkey";

    public bool HideBehindFullscreenApp { get; set; } = true;

    public WidgetOpacitySpec? Opacity { get; set; }

    public string KeepAlive { get; set; } = "onDemand";
}

public sealed class WidgetOpacitySpec
{
    public double Default { get; set; } = 0.92;

    public double Min { get; set; } = 0.55;
}

public sealed class WidgetFocusSpec
{
    public bool StealOnShow { get; set; }

    public bool ActivateOnClick { get; set; } = true;
}

public sealed class WidgetThemeSpec
{
    /// <summary>auto / light / dark。</summary>
    public string Mode { get; set; } = "auto";

    public bool FollowAccent { get; set; } = true;

    /// <summary>acrylic / opaque。</summary>
    public string Surface { get; set; } = "acrylic";
}

public sealed class WidgetActivationSpec
{
    public string? Hotkey { get; set; }

    public bool LaunchOnStartup { get; set; }

    public bool ShowInTray { get; set; } = true;
}

/// <summary>清单校验 / 加载结论。</summary>
public enum ManifestStatus
{
    /// <summary>可以加载与拉起。</summary>
    Valid,

    /// <summary>清单非法，整份不加载；布局保留。</summary>
    Invalid,

    /// <summary>清单合法但宿主当前不支持（例如 wasm / 桌面层）；禁用并提示。</summary>
    Unsupported,
}

/// <summary>一份清单的校验结果：错误聚合、不抛异常（widget-contract §9）。</summary>
public sealed class ManifestValidation
{
    public ManifestStatus Status { get; init; } = ManifestStatus.Valid;

    public IReadOnlyList<ManifestIssue> Issues { get; init; } = Array.Empty<ManifestIssue>();

    public string? Summary { get; init; }

    public bool IsValid => Status == ManifestStatus.Valid;

    public IEnumerable<ManifestIssue> Errors => Issues.Where(i => i.IsError);

    public static ManifestValidation Valid() => new();

    public static ManifestValidation Fail(ManifestStatus status, string code, string message, string? field = null) =>
        new()
        {
            Status = status,
            Summary = message,
            Issues = new[] { new ManifestIssue(code, message, field) },
        };
}
