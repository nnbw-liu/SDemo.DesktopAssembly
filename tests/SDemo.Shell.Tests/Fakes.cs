using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Discovery;
using SDemo.Shell.Hosting;
using SDemo.Shell.Processes;
using SDemo.Shell.Registry;
using SDemo.Shell.Runtime;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Tests;

/// <summary>假进程：Id 自增、可主动 Exited、Kill 置 HasExited、WaitForExit 可配置。</summary>
internal sealed class FakeAppProcess : IAppProcess
{
    private readonly object _gate = new();
    private bool _exited;

    public FakeAppProcess(int id) => Id = id;

    public int Id { get; }

    public int KillCount { get; private set; }

    public int WaitCount { get; private set; }

    /// <summary>true：WaitForExit 期间优雅退出；false：等不到，逼管家调用 Kill。</summary>
    public bool ExitOnWait { get; set; } = true;

    public bool ExitCodeValue { get; set; }

    public bool MutexHeld { get; set; }

    public bool Disposed { get; private set; }

    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _exited;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _exited ? (ExitCodeValue ? 1 : 0) : null;
            }
        }
    }

    public event EventHandler? Exited;

    /// <summary>模拟进程结束（可由测试线程主动调用）。</summary>
    public void RaiseExited()
    {
        var fire = false;
        lock (_gate)
        {
            if (!_exited)
            {
                _exited = true;
                fire = true;
            }
        }

        if (fire)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Kill()
    {
        KillCount++;
        RaiseExited();
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        WaitCount++;
        if (ExitOnWait)
        {
            RaiseExited();
        }

        return HasExited;
    }

    public bool TryOpenSingleInstanceMutex(string mutexName) => MutexHeld;

    public void Dispose() => Disposed = true;
}

/// <summary>假启动器：记录 spec / 进程，可选写 runtime.json、可选自动退出。</summary>
internal sealed class FakeLauncher : IAppProcessLauncher
{
    private readonly object _gate = new();
    private readonly List<AppLaunchSpec> _specs = new();
    private readonly List<FakeAppProcess> _processes = new();
    private int _nextProcessId = 100;

    public int LaunchCount { get; private set; }

    public IReadOnlyList<AppLaunchSpec> Specs
    {
        get
        {
            lock (_gate)
            {
                return _specs.ToArray();
            }
        }
    }

    public IReadOnlyList<FakeAppProcess> Processes
    {
        get
        {
            lock (_gate)
            {
                return _processes.ToArray();
            }
        }
    }

    public bool WriteRuntime { get; set; } = true;

    public int Port { get; set; } = 49_321;

    public FakeRuntimeStore? RuntimeStore { get; set; }

    /// <summary>进程创建后立刻回调（用于“就绪前退出”之类的场景）。</summary>
    public Action<FakeAppProcess, AppLaunchSpec>? Configure { get; set; }

    public int? AutoExitAfterMs { get; set; }

    public int AttachCount { get; private set; }

    public int? AttachedPid { get; private set; }

    /// <summary>接管已存在进程（DA-22）：默认返回一个假的受管进程。</summary>
    public Func<int, IAppProcess?>? AttachHandler { get; set; }

    public IAppProcess? Attach(int pid)
    {
        AttachCount++;
        AttachedPid = pid;
        return AttachHandler?.Invoke(pid) ?? new FakeAppProcess(pid);
    }

    public IAppProcess Launch(AppLaunchSpec spec)
    {
        FakeAppProcess process;
        lock (_gate)
        {
            process = new FakeAppProcess(++_nextProcessId);
            LaunchCount++;
            _specs.Add(spec);
            _processes.Add(process);
        }

        Configure?.Invoke(process, spec);

        if (WriteRuntime && RuntimeStore is not null)
        {
            RuntimeStore.Write(spec.AppId, new RuntimeFile
            {
                Pid = process.Id,
                Port = Port,
                Token = spec.Token,
                StartedAt = DateTimeOffset.UtcNow,
                HostApiVersion = ShellConstants.HostApiVersion,
            });
        }

        if (AutoExitAfterMs is { } delay)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                process.RaiseExited();
            });
        }

        return process;
    }
}

/// <summary>内存版 runtime.json 存储。</summary>
internal sealed class FakeRuntimeStore : IRuntimeFileStore
{
    private readonly Dictionary<string, RuntimeFile> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string GetPath(string appId) => $"runtime/{appId}.json";

    public RuntimeFile? Read(string appId)
    {
        lock (_gate)
        {
            return _files.TryGetValue(appId, out var file) ? Clone(file) : null;
        }
    }

    public void Delete(string appId)
    {
        lock (_gate)
        {
            _files.Remove(appId);
        }
    }

    public IReadOnlyList<string> EnumerateAppIds()
    {
        lock (_gate)
        {
            return _files.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray();
        }
    }

    public void Write(string appId, RuntimeFile file)
    {
        lock (_gate)
        {
            _files[appId] = Clone(file);
        }
    }

    public bool Has(string appId)
    {
        lock (_gate)
        {
            return _files.ContainsKey(appId);
        }
    }

    private static RuntimeFile Clone(RuntimeFile file) => new()
    {
        Pid = file.Pid,
        Port = file.Port,
        Token = file.Token,
        StartedAt = file.StartedAt,
        AppVersion = file.AppVersion,
        HostApiVersion = file.HostApiVersion,
    };
}

/// <summary>假应用 HTTP 通道：健康 / 握手可配置，并记录 shutdown。</summary>
internal sealed class FakeTransport : IAppTransport
{
    private readonly object _gate = new();
    private readonly List<(int Port, string Token, int GraceMs)> _shutdowns = new();
    private int _healthCalls;
    private int _handshakeCalls;
    private int _commandCalls;

    public Func<int, string, AppCallResult<HealthResponse>>? HealthHandler { get; set; }

    public Func<int, string, HandshakeRequest, AppCallResult<HandshakeResponse>>? HandshakeHandler { get; set; }

    public bool ShutdownResult { get; set; } = true;

    public int HealthDelayMs { get; set; }

    public int HandshakeDelayMs { get; set; }

    public int HealthCalls => Volatile.Read(ref _healthCalls);

    public int HandshakeCalls => Volatile.Read(ref _handshakeCalls);

    public int CommandCalls => Volatile.Read(ref _commandCalls);

    public string? LastHealthToken { get; private set; }

    public string? LastHandshakeToken { get; private set; }

    public IReadOnlyList<(int Port, string Token, int GraceMs)> Shutdowns
    {
        get
        {
            lock (_gate)
            {
                return _shutdowns.ToArray();
            }
        }
    }

    public async Task<AppCallResult<HealthResponse>> HealthAsync(int port, string token, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _healthCalls);
        LastHealthToken = token;
        if (HealthDelayMs > 0)
        {
            await Task.Delay(HealthDelayMs, cancellationToken).ConfigureAwait(false);
        }

        if (HealthHandler is not null)
        {
            return HealthHandler(port, token);
        }

        return new AppCallResult<HealthResponse>(
            new HealthResponse { Ok = true, Pid = 1, Since = DateTimeOffset.UtcNow },
            200);
    }

    public async Task<AppCallResult<HandshakeResponse>> HandshakeAsync(
        int port,
        string token,
        HandshakeRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _handshakeCalls);
        LastHandshakeToken = token;
        if (HandshakeDelayMs > 0)
        {
            await Task.Delay(HandshakeDelayMs, cancellationToken).ConfigureAwait(false);
        }

        if (HandshakeHandler is not null)
        {
            return HandshakeHandler(port, token, request);
        }

        return new AppCallResult<HandshakeResponse>(
            new HandshakeResponse
            {
                AppVersion = "0.1.0",
                HostApiVersion = ShellConstants.HostApiVersion,
                Capabilities = new List<string>(),
            },
            200);
    }

    public Task<bool> ShutdownAsync(int port, string token, int graceMs, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _shutdowns.Add((port, token, graceMs));
        }

        return Task.FromResult(ShutdownResult);
    }

    public Task<bool> SendCommandAsync(
        int port,
        string token,
        string widgetId,
        WidgetCommand command,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _commandCalls);
        return Task.FromResult(true);
    }
}

/// <summary>假哈希校验：默认透传真实 SHA-256。</summary>
internal sealed class FakeHashVerifier : IHashVerifier
{
    public Func<string, string?>? Compute { get; set; } = path => new FileHashVerifier().ComputeSha256(path);

    public int Calls { get; private set; }

    public string? ComputeSha256(string path)
    {
        Calls++;
        return Compute?.Invoke(path);
    }
}

internal sealed class FakeWidgetWindowHost : IWidgetWindowHost
{
    public List<RegisteredWidget> Registered { get; } = new();

    public List<(string WidgetId, long Hwnd)> Unregistered { get; } = new();

    public void OnWidgetRegistered(RegisteredWidget widget)
    {
        lock (Registered)
        {
            Registered.Add(widget);
        }
    }

    public void OnWidgetUnregistered(string widgetId, long hwnd)
    {
        lock (Unregistered)
        {
            Unregistered.Add((widgetId, hwnd));
        }
    }
}

internal sealed class FakeWidgetPolicyHost : IWidgetPolicyHost
{
    public WidgetPolicyDecision Decision { get; set; } = new(true);

    public List<(string WidgetId, WidgetPolicyRequest Request)> Calls { get; } = new();

    public WidgetPolicyDecision ApplyPolicy(string widgetId, WidgetPolicyRequest request)
    {
        lock (Calls)
        {
            Calls.Add((widgetId, request));
        }

        return Decision;
    }
}

internal sealed class FakeHostInfoProvider : IHostInfoProvider
{
    public HostInfoResponse GetHostInfo() => new()
    {
        HostVersion = HostVersion.Product,
        HostApiVersion = ShellConstants.HostApiVersion,
        Theme = "light",
        Accent = "#2563EB",
        Language = "zh-CN",
        Monitors = new List<MonitorDescriptor>(),
    };
}

internal sealed class FakeUserNotifier : IUserNotifier
{
    public List<NotifyRequest> Notifications { get; } = new();

    public void Notify(NotifyRequest request)
    {
        lock (Notifications)
        {
            Notifications.Add(request);
        }
    }
}

/// <summary>进程管家测试装配：真实 registry / store / loader + fake 进程、runtime、transport。</summary>
internal sealed class ProcessTestShell : IAsyncDisposable
{
    public ProcessTestShell(
        string appId = "sdemo.alpha",
        Action<AppManifest>? tweakApp = null,
        Action<AppManifest, string>? tweakAppWithDirectory = null)
    {
        AppId = appId;
        Temp = new TempRoot("process");
        Paths = Temp.Paths;
        Log = NullShellLog.Instance;

        Store = new ShellStateStore(Paths, Log);
        Store.Load();
        Hasher = new FakeHashVerifier();
        Loader = new ManifestLoader(Hasher, Log);
        Registry = new AppRegistry(Paths, Store, Loader, Log);
        TestManifests.CreateApp(Temp, appId, app =>
        {
            tweakApp?.Invoke(app);
            tweakAppWithDirectory?.Invoke(app, Temp.AppDir(appId));
        });
        Registry.Scan();

        RuntimeFiles = new FakeRuntimeStore();
        Transport = new FakeTransport();
        Launcher = new FakeLauncher { RuntimeStore = RuntimeFiles, Port = 49_321 };
        Tokens = new AppTokenStore(RuntimeFiles, Log, _ => true);
        Manager = new AppProcessManager(
            Registry,
            Launcher,
            RuntimeFiles,
            Transport,
            Tokens,
            Hasher,
            Paths,
            Log,
            new AppProcessOptions
            {
                BackoffDelays = new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30) },
                RuntimePollInterval = TimeSpan.FromMilliseconds(20),
                IdleCheckInterval = TimeSpan.FromMilliseconds(20),
                MaxConsecutiveFailures = 3,
            });
        Manager.HostUrl = "http://127.0.0.1:1";
    }

    public string AppId { get; }

    public TempRoot Temp { get; }

    public ShellPaths Paths { get; }

    public IShellLog Log { get; }

    public ShellStateStore Store { get; }

    public FakeHashVerifier Hasher { get; }

    public ManifestLoader Loader { get; }

    public AppRegistry Registry { get; }

    public FakeRuntimeStore RuntimeFiles { get; }

    public FakeTransport Transport { get; }

    public FakeLauncher Launcher { get; }

    public AppTokenStore Tokens { get; }

    public AppProcessManager Manager { get; }

    public AppEntry Entry => Registry.Find(AppId) ?? throw new InvalidOperationException("注册表里没有 " + AppId);

    public string WidgetId => Entry.WidgetId;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Manager.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // fake 环境下的收尾异常不影响断言。
        }

        Temp.Dispose();
    }
}

/// <summary>回环端点测试装配：真实 registry / manager + fake 进程侧，真实 HttpListener。</summary>
internal sealed class HostTestShell : IAsyncDisposable
{
    public HostTestShell(
        string appId = "sdemo.alpha",
        Action<AppManifest>? tweakApp = null,
        Action<WidgetManifest>? tweakWidget = null)
    {
        AppId = appId;
        Temp = new TempRoot("host");
        Paths = Temp.Paths;
        Log = NullShellLog.Instance;

        Store = new ShellStateStore(Paths, Log);
        Store.Load();
        Hasher = new FakeHashVerifier();
        Loader = new ManifestLoader(Hasher, Log);
        Registry = new AppRegistry(Paths, Store, Loader, Log);
        TestManifests.CreateApp(Temp, appId, tweakApp, tweakWidget);
        Registry.Scan();

        RuntimeFiles = new FakeRuntimeStore();
        Transport = new FakeTransport();
        Launcher = new FakeLauncher { RuntimeStore = RuntimeFiles, Port = 49_321 };
        Tokens = new AppTokenStore(RuntimeFiles, Log, _ => true);
        Manager = new AppProcessManager(
            Registry,
            Launcher,
            RuntimeFiles,
            Transport,
            Tokens,
            Hasher,
            Paths,
            Log,
            new AppProcessOptions
            {
                BackoffDelays = new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30) },
                RuntimePollInterval = TimeSpan.FromMilliseconds(20),
                IdleCheckInterval = TimeSpan.FromMilliseconds(20),
            });
        Manager.HostUrl = "http://127.0.0.1:1";

        WidgetHost = new FakeWidgetWindowHost();
        Policy = new FakeWidgetPolicyHost();
        HostInfo = new FakeHostInfoProvider();
        Notifier = new FakeUserNotifier();
        Server = CreateServer();
    }

    public string AppId { get; }

    public TempRoot Temp { get; }

    public ShellPaths Paths { get; }

    public IShellLog Log { get; }

    public ShellStateStore Store { get; }

    public FakeHashVerifier Hasher { get; }

    public ManifestLoader Loader { get; }

    public AppRegistry Registry { get; }

    public FakeRuntimeStore RuntimeFiles { get; }

    public FakeTransport Transport { get; }

    public FakeLauncher Launcher { get; }

    public AppTokenStore Tokens { get; }

    public AppProcessManager Manager { get; }

    public FakeWidgetWindowHost WidgetHost { get; }

    public FakeWidgetPolicyHost Policy { get; }

    public FakeHostInfoProvider HostInfo { get; }

    public FakeUserNotifier Notifier { get; }

    public ShellHostServer Server { get; }

    public AppEntry Entry => Registry.Find(AppId) ?? throw new InvalidOperationException("注册表里没有 " + AppId);

    public string WidgetId => Entry.WidgetId;

    public ShellHostServer CreateServer() =>
        new(Paths, Registry, Manager, Tokens, WidgetHost, Policy, HostInfo, Notifier, Log);

    /// <summary>用临时 runtime.json + processProbe=true 造一个可被 TryValidate 接受的 app token。</summary>
    public string SeedAppToken(int pid = 4_242, int port = 5_001)
    {
        var token = AppTokenStore.CreateToken();
        RuntimeFiles.Write(AppId, new RuntimeFile
        {
            Pid = pid,
            Port = port,
            Token = token,
            StartedAt = DateTimeOffset.UtcNow,
            HostApiVersion = ShellConstants.HostApiVersion,
        });
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 监听器可能已停。
        }

        try
        {
            await Manager.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // fake 进程收尾异常忽略。
        }

        Temp.Dispose();
    }
}
