namespace SDemo.Shell.Contracts;

/// <summary>可执行文件哈希校验（DA-76）。</summary>
public interface IHashVerifier
{
    /// <summary>返回小写 64 位十六进制；文件不存在或不可读时返回 null。</summary>
    string? ComputeSha256(string path);
}

/// <summary>应用进程启动参数（arguments 逐项传递，不经 shell 拼接）。</summary>
public sealed record AppLaunchSpec(
    string AppId,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string Token,
    string HostUrl);

/// <summary>进程启动器抽象；测试用 fake 替换。</summary>
public interface IAppProcessLauncher
{
    IAppProcess Launch(AppLaunchSpec spec);

    /// <summary>
    /// 接管已存在的进程（用户自行启动、或上一次宿主异常退出留下的实例）。
    /// 默认实现返回 null，测试 fake 可只实现 <see cref="Launch"/>。
    /// </summary>
    IAppProcess? Attach(int pid) => null;
}

/// <summary>一个受管应用进程。</summary>
public interface IAppProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    event EventHandler? Exited;

    void Kill();

    bool WaitForExit(TimeSpan timeout);

    /// <summary>探测应用是否已经持有单实例互斥量（用户自行启动时用）。</summary>
    bool TryOpenSingleInstanceMutex(string mutexName);
}

/// <summary>runtime.json 读写（host-protocol §4）。</summary>
public interface IRuntimeFileStore
{
    string GetPath(string appId);

    RuntimeFile? Read(string appId);

    void Delete(string appId);

    /// <summary>枚举 runtime 目录下仍存在的应用 id（用于 token 反查与残留清理）。</summary>
    IReadOnlyList<string> EnumerateAppIds();
}

/// <summary>宿主 → 应用的本机 HTTP 通道（host-protocol §6.1）。</summary>
public interface IAppTransport
{
    Task<AppCallResult<HealthResponse>> HealthAsync(int port, string token, CancellationToken cancellationToken);

    Task<AppCallResult<HandshakeResponse>> HandshakeAsync(int port, string token, HandshakeRequest request, CancellationToken cancellationToken);

    Task<bool> ShutdownAsync(int port, string token, int graceMs, CancellationToken cancellationToken);

    Task<bool> SendCommandAsync(int port, string token, string widgetId, WidgetCommand command, CancellationToken cancellationToken);
}

/// <summary>一次应用 HTTP 调用的结果：区分 401（token 失效，不再重试）与网络失败（可重试）。</summary>
public sealed record AppCallResult<T>(T? Value, int StatusCode = 0, string? Error = null)
{
    public bool Success => Value is not null && StatusCode is >= 200 and < 300;

    public bool Unauthorized => StatusCode == 401;

    public bool IsTransportFailure => StatusCode == 0;
}

/// <summary>
/// 窗口管控接口。实现层必须把调用限制在 host-protocol §7 的 6 个 Win32 操作内；
/// 所有矩形参数是物理像素，DIP 换算由布局层负责。
/// </summary>
public interface IWindowSurface
{
    bool IsWindowAlive(long hwnd);

    bool IsWindowVisible(long hwnd);

    ScreenRect GetWindowRect(long hwnd);

    void ApplyRect(long hwnd, ScreenRect physicalRect, bool noActivate, bool? alwaysOnTop = null);

    void SetVisible(long hwnd, bool visible, bool activate);

    void SetClickThrough(long hwnd, bool enabled);

    void SetToolWindow(long hwnd);

    void Close(long hwnd);

    /// <summary>当前前台窗口相关信息，供 Win+D 状态机判定（不装钩子、不操作别人的窗口）。</summary>
    ForegroundWindowInfo GetForegroundWindow();
}

/// <summary>前台窗口快照（只读）。</summary>
public sealed record ForegroundWindowInfo(long Hwnd, string? ClassName, string? ProcessName, bool IsDesktop)
{
    public static ForegroundWindowInfo None { get; } = new(0, null, null, false);
}

/// <summary>显示器集合与变更通知。</summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> Monitors { get; }

    event EventHandler? Changed;

    MonitorInfo? Primary { get; }

    MonitorInfo? FindByKey(string? key);
}

/// <summary>native 组件窗口登记回调（由 UI 层实现，内部切到 UI 线程）。</summary>
public interface IWidgetWindowHost
{
    void OnWidgetRegistered(RegisteredWidget widget);

    void OnWidgetUnregistered(string widgetId, long hwnd);
}

/// <summary>策略请求执行者（只允许 layer / alwaysOnTop / clickThrough；idleHold 由进程管家处理）。</summary>
public interface IWidgetPolicyHost
{
    WidgetPolicyDecision ApplyPolicy(string widgetId, WidgetPolicyRequest request);
}

/// <summary>GET /v1/host/info 的数据来源。</summary>
public interface IHostInfoProvider
{
    HostInfoResponse GetHostInfo();
}

/// <summary>宿主弹通知（不做队列，DA-37 / Q10）。</summary>
public interface IUserNotifier
{
    void Notify(NotifyRequest request);
}
