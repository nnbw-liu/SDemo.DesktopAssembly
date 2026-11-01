using SDemo.Shell.Contracts;
using SDemo.Shell.Discovery;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Registry;

/// <summary>
/// 一个已安装组件的运行状态容器。清单是只读描述，状态（state / runtime / hwnd / 失败计数）由进程管家写。
/// 布局与用户偏好来自 <see cref="WidgetState"/>（store 独占写；这里是同一对象的引用）。
/// </summary>
public sealed class AppEntry
{
    public AppEntry(AppLoadResult load, WidgetState widgetState)
    {
        Manifest = load.Manifest;
        Widget = load.Widget;
        AppDirectory = load.AppDirectory;
        ExecutablePath = load.ExecutablePath;
        AppValidation = load.AppValidation;
        WidgetValidation = load.WidgetValidation;
        HashValid = load.HashValid;
        HashDetail = load.HashDetail;
        LoadStatus = load.Status;
        WidgetState = widgetState;
        LastActivity = DateTimeOffset.UtcNow;
        SyncFromWidgetState();
    }

    public string AppId => Manifest.Id;

    public string WidgetId => Widget.Id;

    public AppManifest Manifest { get; private set; }

    public WidgetManifest Widget { get; private set; }

    public string AppDirectory { get; private set; }

    public string ExecutablePath { get; private set; }

    public ManifestValidation AppValidation { get; private set; }

    public ManifestValidation WidgetValidation { get; private set; }

    public ManifestStatus LoadStatus { get; private set; }

    public bool HashValid { get; internal set; }

    public string? HashDetail { get; private set; }

    public bool BlockedByLimit { get; internal set; }

    public string? BlockReason { get; internal set; }

    public bool Enabled { get; internal set; }

    public bool Visible { get; internal set; }

    /// <summary><see cref="AppStates"/> 之一。</summary>
    public string State { get; internal set; } = AppStates.Stopped;

    public HostError? LastError { get; internal set; }

    public RuntimeFile? Runtime { get; internal set; }

    public IAppProcess? Process { get; internal set; }

    public long? Hwnd { get; set; }

    public string? DpiAware { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset LastActivity { get; internal set; }

    /// <summary>进入 Ready 的时间；稳定运行超过阈值后才清零崩溃计数。</summary>
    public DateTimeOffset? ReadySince { get; set; }

    public bool IdleHold { get; internal set; }

    public DateTimeOffset? PendingRestartAt { get; set; }

    /// <summary>停止是宿主主动要求的（崩溃恢复时不当作崩溃）。</summary>
    public bool StopRequested { get; internal set; }

    public string Layer { get; private set; } = WidgetRendering.Floating;

    public bool AlwaysOnTop { get; private set; }

    public bool ClickThrough { get; private set; }

    public double Opacity { get; private set; } = 1.0;

    public WidgetState WidgetState { get; internal set; }

    public object SyncRoot { get; } = new();

    /// <summary>串行化拉起 / 停止，避免 ensure 与崩溃重启互相打架。</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool IsLaunchable => LoadStatus == ManifestStatus.Valid && HashValid && !BlockedByLimit;

    /// <summary>生效热键（DA-70）：用户覆盖优先；null 表示不注册。</summary>
    public string? EffectiveHotkey =>
        WidgetState.HotkeyOverride is { } overrideValue
            ? (overrideValue.Length == 0 ? null : overrideValue)
            : Widget.Activation?.Hotkey;

    /// <summary>生效托盘入口（DA-72）：用户覆盖优先，缺省看清单，最后默认显示。</summary>
    public bool EffectiveShowInTray =>
        WidgetState.ShowInTray ?? Widget.Activation?.ShowInTray ?? true;

    public string? FailureSummary => !HashValid
        ? HashDetail ?? "可执行文件哈希不符"
        : BlockedByLimit
            ? BlockReason
            : AppValidation.Summary ?? WidgetValidation.Summary;

    public void Reload(AppLoadResult load, WidgetState widgetState)
    {
        Manifest = load.Manifest;
        Widget = load.Widget;
        AppDirectory = load.AppDirectory;
        ExecutablePath = load.ExecutablePath;
        AppValidation = load.AppValidation;
        WidgetValidation = load.WidgetValidation;
        HashValid = load.HashValid;
        HashDetail = load.HashDetail;
        LoadStatus = load.Status;
        WidgetState = widgetState;
        SyncFromWidgetState();
    }

    public void SyncFromWidgetState()
    {
        Enabled = WidgetState.Enabled;
        Visible = WidgetState.Visible;
        Layer = WidgetState.Layer;
        AlwaysOnTop = WidgetState.AlwaysOnTop;
        ClickThrough = WidgetState.ClickThrough;
        Opacity = WidgetState.Opacity;
    }

    public HostError? ComputeInvalidError()
    {
        if (BlockedByLimit)
        {
            return HostError.Create(ManifestIssueCodes.ComponentLimit, BlockReason ?? "组件数量超出上限");
        }

        if (!HashValid)
        {
            return HostError.Create(ErrorCodes.AppInvalid, HashDetail ?? "可执行文件哈希不符");
        }

        if (LoadStatus == ManifestStatus.Unsupported)
        {
            return HostError.Create(ErrorCodes.AppInvalid, AppValidation.Summary ?? WidgetValidation.Summary ?? "宿主未支持该组件");
        }

        if (LoadStatus == ManifestStatus.Invalid)
        {
            return HostError.Create(ErrorCodes.AppInvalid, FailureSummary ?? "清单非法");
        }

        return null;
    }

    public AppDescriptor ToDescriptor(ShellPaths paths)
    {
        var runtime = Runtime;
        return new AppDescriptor
        {
            AppId = AppId,
            DisplayName = string.IsNullOrWhiteSpace(Manifest.Name) ? AppId : Manifest.Name,
            Version = Manifest.Version,
            State = State,
            Enabled = Enabled,
            HashValid = HashValid,
            WidgetId = WidgetId,
            Renderer = Widget.Renderer,
            Layer = Layer,
            HostApiVersion = Widget.HostApiVersion,
            RuntimePath = paths.RuntimeFilePath(AppId),
            Runtime = runtime is null
                ? null
                : new RuntimePointer
                {
                    Pid = runtime.Pid,
                    Port = runtime.Port,
                    RuntimePath = paths.RuntimeFilePath(AppId),
                },
            Mcp = Manifest.Mcp,
            LastError = LastError,
        };
    }
}

