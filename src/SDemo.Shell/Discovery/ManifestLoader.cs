using System.Text.Json;
using System.Text.RegularExpressions;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Discovery;

/// <summary>一份应用的加载结果：两份清单 + 校验结论 + 可执行文件信息。</summary>
public sealed class AppLoadResult
{
    public required AppManifest Manifest { get; init; }

    public required WidgetManifest Widget { get; init; }

    public required string AppDirectory { get; init; }

    public string ExecutablePath { get; set; } = string.Empty;

    public bool ExecutableResolved { get; set; }

    public ManifestValidation AppValidation { get; set; } = ManifestValidation.Valid();

    public ManifestValidation WidgetValidation { get; set; } = ManifestValidation.Valid();

    public bool HashValid { get; set; } = true;

    public string? HashDetail { get; set; }

    public ManifestStatus Status { get; set; } = ManifestStatus.Valid;

    public bool IsLaunchable => Status == ManifestStatus.Valid && HashValid && ExecutableResolved;

    /// <summary>统一成一条可显示的错误（诊断 / LastError 用）。</summary>
    public string? FailureSummary { get; set; }

    public HostError? ToHostError()
    {
        if (IsLaunchable)
        {
            return null;
        }

        var code = Status switch
        {
            ManifestStatus.Unsupported => ManifestIssueCodes.RendererUnsupported,
            _ when !HashValid => ManifestIssueCodes.HashMismatch,
            _ => ManifestIssueCodes.Required,
        };
        return HostError.Create(code, FailureSummary ?? "清单校验未通过");
    }
}

/// <summary>
/// 清单读取与校验（DA-12–DA-16 / DA-19 / DA-76 / DA-77）。
/// 逐字段校验、错误聚合成列表、不抛异常；坏一个组件不影响其它组件。
/// </summary>
public sealed class ManifestLoader
{
    private const string IdPattern = "^[a-z0-9]+(\\.[a-z0-9-]+)+$";
    private const string Sha256Pattern = "^[0-9a-f]{64}$";

    private static readonly Regex IdRegex = new(IdPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ShaRegex = new(Sha256Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHashVerifier _hasher;
    private readonly IShellLog _log;

    public ManifestLoader(IHashVerifier hasher, IShellLog? log = null)
    {
        _hasher = hasher;
        _log = log ?? NullShellLog.Instance;
    }

    /// <summary>
    /// 读取并校验一个安装目录。安装脚本 / 安装包暂存目录可以传入 <paramref name="directoryNameForIdentity"/>
    /// 以跳过「目录名 == appId」这一条（安装完成后再用真实目录名校验一次）。
    /// </summary>
    public AppLoadResult Load(string appDirectory, string? directoryNameForIdentity = null)
    {
        var directoryName = directoryNameForIdentity ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(appDirectory));
        var fallbackApp = new AppManifest { Id = directoryName, Name = directoryName, Version = "0" };
        var fallbackWidget = new WidgetManifest
        {
            Id = $"{directoryName}.widget",
            AppId = directoryName,
            Name = directoryName,
            Version = "0",
            Renderer = WidgetRendering.Native,
        };

        var appBag = new ValidationBag();
        var widgetBag = new ValidationBag();

        var appManifest = ReadAppManifest(appDirectory, fallbackApp, appBag);
        var widgetManifest = ReadWidgetManifest(appDirectory, fallbackWidget, widgetBag);

        var result = new AppLoadResult
        {
            AppDirectory = appDirectory,
            Manifest = appManifest,
            Widget = widgetManifest,
        };

        ValidateApp(appManifest, appDirectory, appBag, result, directoryName);
        ValidateWidget(appManifest, widgetManifest, widgetBag);

        result.AppValidation = appBag.Build();
        result.WidgetValidation = widgetBag.Build();
        result.Status = Worst(result.AppValidation.Status, result.WidgetValidation.Status);
        result.FailureSummary = result.AppValidation.Summary ?? result.WidgetValidation.Summary;

        if (!result.HashValid)
        {
            result.FailureSummary = result.HashDetail ?? "可执行文件哈希不符";
        }

        if (result.FailureSummary is null && result.Status != ManifestStatus.Valid)
        {
            result.FailureSummary = result.WidgetValidation.Summary ?? "清单不完整";
        }

        return result;
    }

    private AppManifest ReadAppManifest(string appDirectory, AppManifest fallback, ValidationBag bag)
    {
        var path = Path.Combine(appDirectory, "app.json");
        if (!File.Exists(path))
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 app.json", "app.json");
            return fallback;
        }

        try
        {
            var text = File.ReadAllText(path);
            if (text.TrimStart().StartsWith('['))
            {
                bag.Error(ManifestIssueCodes.JsonInvalid, "app.json 不允许数组", "app.json");
                return fallback;
            }

            var manifest = JsonSerializer.Deserialize<AppManifest>(text, ShellJson.File);
            if (manifest is null)
            {
                bag.Error(ManifestIssueCodes.JsonInvalid, "app.json 解析为 null", "app.json");
                return fallback;
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            bag.Error(ManifestIssueCodes.JsonInvalid, $"app.json 解析失败：{ex.Message}", "app.json");
            _log.Warn($"app.json 解析失败：{path}", ex);
            return fallback;
        }
        catch (IOException ex)
        {
            bag.Error(ManifestIssueCodes.JsonInvalid, $"app.json 读取失败：{ex.Message}", "app.json");
            return fallback;
        }
    }

    private WidgetManifest ReadWidgetManifest(string appDirectory, WidgetManifest fallback, ValidationBag bag)
    {
        var path = Path.Combine(appDirectory, "widget.json");
        if (!File.Exists(path))
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 widget.json", "widget.json");
            return fallback;
        }

        try
        {
            var text = File.ReadAllText(path);
            if (text.TrimStart().StartsWith('['))
            {
                bag.Error(ManifestIssueCodes.JsonInvalid, "v1 一个应用只支持一个组件，widget.json 不允许数组", "widget.json");
                return fallback;
            }

            var manifest = JsonSerializer.Deserialize<WidgetManifest>(text, ShellJson.File);
            if (manifest is null)
            {
                bag.Error(ManifestIssueCodes.JsonInvalid, "widget.json 解析为 null", "widget.json");
                return fallback;
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            bag.Error(ManifestIssueCodes.JsonInvalid, $"widget.json 解析失败：{ex.Message}", "widget.json");
            _log.Warn($"widget.json 解析失败：{path}", ex);
            return fallback;
        }
        catch (IOException ex)
        {
            bag.Error(ManifestIssueCodes.JsonInvalid, $"widget.json 读取失败：{ex.Message}", "widget.json");
            return fallback;
        }
    }

    private void ValidateApp(AppManifest app, string appDirectory, ValidationBag bag, AppLoadResult result, string expectedDirectoryName)
    {
        if (app.SchemaVersion > ShellConstants.ManifestSchemaVersion)
        {
            bag.Error(ManifestIssueCodes.SchemaVersion, "app.json 的 schemaVersion 高于宿主，需要更新 DesktopAssembly", "schemaVersion");
            return;
        }

        if (app.SchemaVersion < 1)
        {
            bag.Error(ManifestIssueCodes.SchemaVersion, "app.json 的 schemaVersion 非法", "schemaVersion");
        }

        if (!IsValidId(app.Id))
        {
            bag.Error(ManifestIssueCodes.Pattern, $"应用 id 不合法：{app.Id}", "id");
        }

        if (!string.Equals(app.Id, expectedDirectoryName, StringComparison.Ordinal))
        {
            bag.Error(ManifestIssueCodes.AppIdMismatch, $"app.json.id({app.Id}) 与目录名({expectedDirectoryName}) 不一致", "id");
        }

        if (string.IsNullOrWhiteSpace(app.Name))
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 name", "name");
        }

        if (string.IsNullOrWhiteSpace(app.Version))
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 version", "version");
        }

        if (!ShellApiVersionCheck(app.MinHostApiVersion, bag, "minHostApiVersion", "应用要求更高的宿主 API 版本"))
        {
            return;
        }

        if (!TryResolveExecutable(appDirectory, app.Executable, out var executablePath, out var reason))
        {
            bag.Error(ManifestIssueCodes.ExecutableMissing, $"executable 不可用：{reason}", "executable");
        }
        else
        {
            result.ExecutablePath = executablePath;
            result.ExecutableResolved = true;
        }

        foreach (var argument in app.Arguments)
        {
            if (string.IsNullOrEmpty(argument))
            {
                bag.Warn(ManifestIssueCodes.Required, "arguments 中存在空字符串", "arguments");
            }
        }

        if (!ShaRegex.IsMatch(app.Sha256 ?? string.Empty))
        {
            bag.Error(ManifestIssueCodes.HashMissing, "sha256 必须是 64 位小写十六进制", "sha256");
        }

        var expectedMutex = $"SDemo.App.{app.Id}";
        if (!string.Equals(app.SingleInstanceMutex, expectedMutex, StringComparison.Ordinal))
        {
            bag.Error(ManifestIssueCodes.Pattern, $"singleInstanceMutex 必须是 {expectedMutex}", "singleInstanceMutex");
        }

        if (app.StartupTimeoutMs is < 1_000 or > 60_000)
        {
            bag.Error(ManifestIssueCodes.Range, "startupTimeoutMs 必须在 1000–60000", "startupTimeoutMs");
        }

        if (app.IdleTimeoutMs is < 0 or > 86_400_000)
        {
            bag.Error(ManifestIssueCodes.Range, "idleTimeoutMs 必须在 0–86400000", "idleTimeoutMs");
        }

        if (!StartupPolicies.IsKnown(app.Startup))
        {
            bag.Error(ManifestIssueCodes.Pattern, "startup 必须是 onDemand / host / manual", "startup");
        }

        ValidateMcp(app.Mcp, bag);

        if (result.ExecutableResolved && ShaRegex.IsMatch(app.Sha256 ?? string.Empty))
        {
            var actual = _hasher.ComputeSha256(result.ExecutablePath);
            if (actual is null)
            {
                result.HashValid = false;
                result.HashDetail = "无法读取可执行文件以校验哈希";
                bag.Error(ManifestIssueCodes.HashMismatch, result.HashDetail, "sha256");
            }
            else if (!string.Equals(actual, app.Sha256, StringComparison.Ordinal))
            {
                result.HashValid = false;
                result.HashDetail = $"可执行文件哈希不符（期望 {app.Sha256}，实际 {actual}）";
                bag.Error(ManifestIssueCodes.HashMismatch, result.HashDetail, "sha256");
            }
        }
    }

    private void ValidateMcp(McpManifest? mcp, ValidationBag bag)
    {
        if (mcp is null)
        {
            return;
        }

        if (!string.Equals(mcp.Transport, "http", StringComparison.Ordinal))
        {
            bag.Error(ManifestIssueCodes.Pattern, "mcp.transport 目前只支持 http", "mcp.transport");
        }

        if (string.IsNullOrWhiteSpace(mcp.Path) || !mcp.Path.StartsWith('/'))
        {
            bag.Error(ManifestIssueCodes.Pattern, "mcp.path 必须以 / 开头", "mcp.path");
        }

        var port = mcp.Port ?? "runtime";
        if (!string.Equals(port, "runtime", StringComparison.Ordinal))
        {
            if (!int.TryParse(port, out var number) || number is < 1 or > 65_535)
            {
                bag.Error(ManifestIssueCodes.Range, "mcp.port 必须是 runtime 或 1–65535", "mcp.port");
            }
        }

        if (!string.Equals(mcp.Token, "runtime", StringComparison.Ordinal))
        {
            bag.Error(ManifestIssueCodes.Pattern, "mcp.token 只允许 runtime", "mcp.token");
        }
    }

    private void ValidateWidget(AppManifest app, WidgetManifest widget, ValidationBag bag)
    {
        if (widget.SchemaVersion > ShellConstants.ManifestSchemaVersion)
        {
            bag.Error(ManifestIssueCodes.SchemaVersion, "widget.json 的 schemaVersion 高于宿主，需要更新 DesktopAssembly", "schemaVersion");
            return;
        }

        if (!IsValidId(widget.Id))
        {
            bag.Error(ManifestIssueCodes.Pattern, $"组件 id 不合法：{widget.Id}", "id");
        }

        if (!IsValidId(widget.AppId))
        {
            bag.Error(ManifestIssueCodes.Pattern, $"组件 appId 不合法：{widget.AppId}", "appId");
        }

        if (!string.Equals(widget.AppId, app.Id, StringComparison.Ordinal))
        {
            bag.Error(ManifestIssueCodes.AppIdMismatch, $"widget.json.appId({widget.AppId}) 与 app.json.id({app.Id}) 不一致", "appId");
        }

        if (!string.Equals(widget.Id, app.Id + ".widget", StringComparison.Ordinal))
        {
            bag.Warn(ManifestIssueCodes.Pattern, $"组件 id 建议为 {app.Id}.widget", "id");
        }

        if (string.IsNullOrWhiteSpace(widget.Name))
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 name", "name");
        }

        if (string.IsNullOrWhiteSpace(widget.Version))
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 version", "version");
        }

        if (widget.Renderer is not (WidgetRendering.Native or WidgetRendering.Wasm))
        {
            bag.Error(ManifestIssueCodes.Pattern, "renderer 必须是 native 或 wasm", "renderer");
            return;
        }

        if (widget.Layer is not (WidgetRendering.Floating or WidgetRendering.Desktop))
        {
            bag.Error(ManifestIssueCodes.LayerInvalid, "layer 必须是 floating 或 desktop", "layer");
            return;
        }

        if (widget.Layer == WidgetRendering.Desktop && widget.Renderer == WidgetRendering.Native)
        {
            bag.Error(ManifestIssueCodes.LayerInvalid, "layer=desktop 与 renderer=native 组合非法", "layer");
        }

        // 版本不匹配只禁用该组件，不影响其它组件（D14）；ShellApiVersionCheck 已记一条 Unsupported。
        ShellApiVersionCheck(widget.HostApiVersion, bag, "hostApiVersion", "宿主 API 版本不足，组件已禁用");

        ValidateRenderer(widget, bag);
        ValidateSize(widget, bag);
        ValidatePosition(widget, bag);
        ValidateBehavior(widget, bag);
        ValidateTheme(widget, bag);
        ValidatePermissions(widget, bag);

        if (widget.Layer == WidgetRendering.Desktop && widget.Renderer != WidgetRendering.Native)
        {
            bag.Unsupported(ManifestIssueCodes.LayerInvalid, "宿主未支持桌面层（layer=desktop，P2）", "layer");
        }
    }

    private void ValidateRenderer(WidgetManifest widget, ValidationBag bag)
    {
        if (widget.Renderer == WidgetRendering.Native)
        {
            if (widget.Native is null)
            {
                bag.Error(ManifestIssueCodes.Required, "renderer=native 时必须提供 native", "native");
                return;
            }

            if (string.IsNullOrWhiteSpace(widget.Native.Process))
            {
                bag.Error(ManifestIssueCodes.Required, "native.process 不能为空", "native.process");
            }

            if (widget.Native.MinSize is { } minSize && (minSize.Width <= 0 || minSize.Height <= 0))
            {
                bag.Error(ManifestIssueCodes.Range, "native.minSize 宽高必须大于 0", "native.minSize");
            }

            return;
        }

        if (widget.Wasm is null)
        {
            bag.Error(ManifestIssueCodes.Required, "renderer=wasm 时必须提供 wasm", "wasm");
            return;
        }

        if (string.IsNullOrWhiteSpace(widget.Wasm.Entry))
        {
            bag.Error(ManifestIssueCodes.Required, "wasm.entry 不能为空", "wasm.entry");
        }

        // v1 只做 native（Q1）：清单合法但禁用并提示，不影响其它组件。
        bag.Unsupported(ManifestIssueCodes.RendererUnsupported, "宿主未支持 wasm 渲染器（v1 只做 native）", "renderer");
    }

    private void ValidateSize(WidgetManifest widget, ValidationBag bag)
    {
        if (widget.Size is null)
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 size", "size");
            return;
        }

        if (widget.Size.Width <= 0 || widget.Size.Height <= 0)
        {
            bag.Error(ManifestIssueCodes.Range, "size 宽高必须大于 0", "size");
        }

        if (widget.Size.MaxWidth is { } maxWidth && maxWidth < widget.Size.Width)
        {
            bag.Warn(ManifestIssueCodes.Range, "size.maxWidth 小于 size.width，运行时会被夹紧", "size.maxWidth");
        }

        if (widget.Size.MaxHeight is { } maxHeight && maxHeight < widget.Size.Height)
        {
            bag.Warn(ManifestIssueCodes.Range, "size.maxHeight 小于 size.height，运行时会被夹紧", "size.maxHeight");
        }
    }

    private void ValidatePosition(WidgetManifest widget, ValidationBag bag)
    {
        if (widget.Position is null)
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 position", "position");
            return;
        }

        if (widget.Position.Mode is not ("anchored" or "remember"))
        {
            bag.Error(ManifestIssueCodes.Pattern, "position.mode 必须是 anchored 或 remember", "position.mode");
        }

        if (!IsKnownCorner(widget.Position.Corner))
        {
            bag.Error(ManifestIssueCodes.Pattern, "position.corner 非法", "position.corner");
        }

        if (widget.Position.OffsetX is < -10_000 or > 10_000 || widget.Position.OffsetY is < -10_000 or > 10_000)
        {
            bag.Error(ManifestIssueCodes.Range, "position.offsetX/offsetY 超出 ±10000", "position");
        }

        if (widget.Position.SnapPx is < 0 or > 500)
        {
            bag.Error(ManifestIssueCodes.Range, "position.snapPx 必须在 0–500", "position.snapPx");
        }
    }

    private void ValidateBehavior(WidgetManifest widget, ValidationBag bag)
    {
        if (widget.Behavior is null)
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 behavior", "behavior");
            return;
        }

        if (widget.Behavior.Opacity is not null)
        {
            if (widget.Behavior.Opacity.Default is < 0 or > 1)
            {
                bag.Error(ManifestIssueCodes.Range, "behavior.opacity.default 必须在 0–1", "behavior.opacity.default");
            }

            if (widget.Behavior.Opacity.Min is < 0 or > 1)
            {
                bag.Error(ManifestIssueCodes.Range, "behavior.opacity.min 必须在 0–1", "behavior.opacity.min");
            }

            if (widget.Behavior.Opacity.Min > widget.Behavior.Opacity.Default)
            {
                bag.Error(ManifestIssueCodes.Range, "behavior.opacity.min 不能大于 default", "behavior.opacity.min");
            }
        }

        if (string.IsNullOrWhiteSpace(widget.Behavior.DragMode))
        {
            bag.Error(ManifestIssueCodes.Required, "behavior.dragMode 不能为空", "behavior.dragMode");
        }

        if (widget.Behavior.ClickThrough is not ("toggleWithHotkey" or "never" or "always"))
        {
            bag.Warn(ManifestIssueCodes.Pattern, "behavior.clickThrough 不是已知取值，按开关式处理", "behavior.clickThrough");
        }

        if (widget.Behavior.KeepAlive is not ("onDemand" or "always"))
        {
            bag.Warn(ManifestIssueCodes.Pattern, "behavior.keepAlive 不是已知取值，按 onDemand 处理", "behavior.keepAlive");
        }

        if (widget.Focus is null)
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 focus", "focus");
        }

        if (widget.Theme is null)
        {
            bag.Error(ManifestIssueCodes.Required, "缺少 theme", "theme");
        }
    }

    private void ValidateTheme(WidgetManifest widget, ValidationBag bag)
    {
        if (widget.Theme is null)
        {
            return;
        }

        if (widget.Theme.Mode is not ("auto" or "light" or "dark"))
        {
            bag.Warn(ManifestIssueCodes.Pattern, "theme.mode 不是 auto/light/dark，按 auto 处理", "theme.mode");
        }

        if (widget.Theme.Surface is not ("acrylic" or "opaque"))
        {
            bag.Warn(ManifestIssueCodes.Pattern, "theme.surface 不是 acrylic/opaque，按 opaque 处理", "theme.surface");
        }

        if (widget.Activation is { Hotkey: not null } activation)
        {
            // 热键解析归 P2；这里只校验非空，避免把非法字符串留到运行时。
            if (string.IsNullOrWhiteSpace(activation.Hotkey))
            {
                bag.Warn(ManifestIssueCodes.Pattern, "activation.hotkey 为空，按未配置处理", "activation.hotkey");
            }
        }
    }

    private void ValidatePermissions(WidgetManifest widget, ValidationBag bag)
    {
        if (widget.Permissions is null)
        {
            return;
        }

        foreach (var permission in widget.Permissions)
        {
            if (string.IsNullOrWhiteSpace(permission))
            {
                bag.Error(ManifestIssueCodes.Required, "permissions 中存在空项", "permissions");
                continue;
            }

            if (permission is not ("http-localhost" or "notifications"))
            {
                bag.Warn(ManifestIssueCodes.Pattern, $"未知权限：{permission}（v1 只展示不授权）", "permissions");
            }
        }
    }

    private static bool ShellApiVersionCheck(string? value, ValidationBag bag, string field, string message)
    {
        if (!HostApiVersion.IsCompatible(value))
        {
            bag.Unsupported(ManifestIssueCodes.HostApiVersion, message, field);
            return false;
        }

        return true;
    }

    private static bool IsValidId(string? value) => value is not null && IdRegex.IsMatch(value);

    private static bool IsKnownCorner(string? corner) =>
        corner is "top-left" or "top-right" or "bottom-left" or "bottom-right";

    private static ManifestStatus Worst(ManifestStatus left, ManifestStatus right)
    {
        if (left == ManifestStatus.Invalid || right == ManifestStatus.Invalid)
        {
            return ManifestStatus.Invalid;
        }

        if (left == ManifestStatus.Unsupported || right == ManifestStatus.Unsupported)
        {
            return ManifestStatus.Unsupported;
        }

        return ManifestStatus.Valid;
    }

    /// <summary>解析可执行文件真实路径，并拒绝跳出安装目录 / 符号链接（DA-77）。</summary>
    private static bool TryResolveExecutable(string appDirectory, string? relative, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(relative))
        {
            reason = "为必填字段";
            return false;
        }

        if (Path.IsPathRooted(relative))
        {
            reason = "不接受绝对路径";
            return false;
        }

        if (relative.Contains("..", StringComparison.Ordinal))
        {
            reason = "不接受 .. 路径";
            return false;
        }

        var root = Path.GetFullPath(appDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            reason = "解析后不在安装目录内";
            return false;
        }

        if (!File.Exists(candidate))
        {
            reason = "文件不存在";
            return false;
        }

        var current = candidate;
        while (current is not null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                reason = "路径包含符号链接 / junction";
                return false;
            }

            current = Path.GetDirectoryName(current);
        }

        fullPath = candidate;
        return true;
    }
}

/// <summary>校验问题收集器：错误优先，其次 unsupported，最后才是 warning。</summary>
internal sealed class ValidationBag
{
    private readonly List<ManifestIssue> _issues = new();
    private bool _unsupported;

    public void Error(string code, string message, string? field = null) =>
        _issues.Add(new ManifestIssue(code, message, field));

    public void Warn(string code, string message, string? field = null) =>
        _issues.Add(new ManifestIssue(code, message, field) { IsError = false });

    public void Unsupported(string code, string message, string? field = null)
    {
        _unsupported = true;
        _issues.Add(new ManifestIssue(code, message, field) { IsError = false });
    }

    public ManifestValidation Build()
    {
        var errors = _issues.Where(i => i.IsError).ToList();
        if (errors.Count > 0)
        {
            return new ManifestValidation
            {
                Status = ManifestStatus.Invalid,
                Issues = _issues,
                Summary = errors[0].Message,
            };
        }

        var blocking = _issues.FirstOrDefault(i => i.Code is ManifestIssueCodes.RendererUnsupported or ManifestIssueCodes.HostApiVersion or ManifestIssueCodes.LayerInvalid);
        if (_unsupported)
        {
            return new ManifestValidation
            {
                Status = ManifestStatus.Unsupported,
                Issues = _issues,
                Summary = blocking?.Message ?? "宿主不支持该组件",
            };
        }

        return new ManifestValidation { Status = ManifestStatus.Valid, Issues = _issues };
    }
}
