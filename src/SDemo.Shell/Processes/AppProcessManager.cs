using System.Collections.Concurrent;
using System.Diagnostics;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Registry;
using SDemo.Shell.Runtime;

namespace SDemo.Shell.Processes;

/// <summary>进程管家的可调参数（测试用小值加速）。</summary>
public sealed class AppProcessOptions
{
    public TimeSpan[] BackoffDelays { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    public TimeSpan RuntimePollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>应用稳定运行超过这个时长才把崩溃计数清零；避免「就绪→崩溃」循环被当作成功而无限重启。</summary>
    public TimeSpan CrashStabilityWindow { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxConsecutiveFailures { get; init; } = 3;
}

/// <summary>ensure 的结果（HTTP 层按 ErrorCode 映射状态码）。</summary>
public sealed class EnsureOutcome
{
    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public AppDescriptor? Descriptor { get; init; }

    public RuntimePointer? Runtime { get; init; }

    public bool Success => ErrorCode is null;

    public static EnsureOutcome Fail(string code, string? message) => new() { ErrorCode = code, Message = message };
}

/// <summary>
/// 应用进程管家（DA-02 / DA-03 / DA-04 / DA-20–DA-27 / DA-29 / DA-30 / DA-92）：
/// 单实例、runtime 等待、握手、空闲退出、崩溃退避重启、退出顺序。所有状态变化都经 AppRegistry。
/// </summary>
public sealed class AppProcessManager : IAsyncDisposable
{
    private readonly AppRegistry _registry;
    private readonly IAppProcessLauncher _launcher;
    private readonly IRuntimeFileStore _runtimeFiles;
    private readonly IAppTransport _transport;
    private readonly AppTokenStore _tokens;
    private readonly IHashVerifier _hasher;
    private readonly ShellPaths _paths;
    private readonly IShellLog _log;
    private readonly AppProcessOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Task<EnsureOutcome>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _authFailures = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    public AppProcessManager(
        AppRegistry registry,
        IAppProcessLauncher launcher,
        IRuntimeFileStore runtimeFiles,
        IAppTransport transport,
        AppTokenStore tokens,
        IHashVerifier hasher,
        ShellPaths paths,
        IShellLog? log = null,
        AppProcessOptions? options = null,
        TimeProvider? time = null)
    {
        _registry = registry;
        _launcher = launcher;
        _runtimeFiles = runtimeFiles;
        _transport = transport;
        _tokens = tokens;
        _hasher = hasher;
        _paths = paths;
        _log = log ?? NullShellLog.Instance;
        _options = options ?? new AppProcessOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>宿主回调地址；必须在拉起任何应用前设置（host-endpoints §4）。</summary>
    public string HostUrl { get; set; } = string.Empty;

    private DateTimeOffset UtcNow => _time.GetUtcNow();

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    public void MarkActivity(string appId)
    {
        if (_registry.Find(appId) is { } entry)
        {
            entry.LastActivity = UtcNow;
        }
    }

    public void SetIdleHold(string appId, bool hold)
    {
        if (_registry.Find(appId) is { } entry)
        {
            entry.IdleHold = hold;
            entry.LastActivity = UtcNow;
            _log.Info($"应用 {appId} 空闲豁免：{hold}");
        }
    }

    public string? ResolveToken(string appId) => _tokens.ResolveToken(appId);

    /// <summary>保证应用可被调用；不显示组件。幂等、单飞、阻塞等待就绪。</summary>
    public async Task<EnsureOutcome> EnsureAsync(string appId, int? timeoutMs, CancellationToken cancellationToken = default)
    {
        var entry = _registry.Find(appId);
        if (entry is null)
        {
            return EnsureOutcome.Fail(ErrorCodes.AppNotFound, ErrorCodes.Describe(ErrorCodes.AppNotFound));
        }

        if (!entry.Enabled)
        {
            return EnsureOutcome.Fail(ErrorCodes.AppDisabled, ErrorCodes.Describe(ErrorCodes.AppDisabled));
        }

        if (!entry.IsLaunchable)
        {
            return EnsureOutcome.Fail(ErrorCodes.AppInvalid, entry.FailureSummary ?? ErrorCodes.Describe(ErrorCodes.AppInvalid));
        }

        if (IsRunningReady(entry))
        {
            return Success(entry);
        }

        while (true)
        {
            if (_inflight.TryGetValue(appId, out var existing))
            {
                return await AwaitFlightAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            var created = LaunchFlightAsync(entry, timeoutMs);
            if (_inflight.TryAdd(appId, created))
            {
                _ = created.ContinueWith(
                    static (_, state) =>
                    {
                        var tuple = ((ConcurrentDictionary<string, Task<EnsureOutcome>>, Task<EnsureOutcome>, string))state!;
                        tuple.Item1.TryRemove(new KeyValuePair<string, Task<EnsureOutcome>>(tuple.Item3, tuple.Item2));
                    },
                    (_inflight, created, appId),
                    TaskScheduler.Default);
                return await AwaitFlightAsync(created, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>宿主 → 应用下发组件指令（show / hide / themeChanged / configChanged 等）。</summary>
    public async Task SendCommandAsync(string appId, string widgetId, WidgetCommand command, CancellationToken cancellationToken = default)
    {
        var entry = _registry.Find(appId);
        var token = _tokens.ResolveToken(appId);
        if (entry?.Runtime is not { } runtime || token is null)
        {
            return;
        }

        await _transport.SendCommandAsync(runtime.Port, token, widgetId, command, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<AppDescriptor> DescribeApps() => _registry.Entries.Select(Describe).ToArray();

    public AppDescriptor Describe(AppEntry entry)
    {
        var descriptor = entry.ToDescriptor(_paths);
        if (entry.State is not (AppStates.Ready or AppStates.Idle))
        {
            descriptor.Runtime = null;
        }

        return descriptor;
    }

    public void NoteAuthSuccess(string appId) => _authFailures.TryRemove(appId, out _);

    /// <summary>连续 3 次鉴权失败：停止应用并置 failed（host-endpoints §6）。</summary>
    public void NoteAuthFailure(string appId)
    {
        var count = _authFailures.AddOrUpdate(appId, 1, static (_, current) => current + 1);
        if (count < 3)
        {
            return;
        }

        _authFailures.TryRemove(appId, out _);
        if (_registry.Find(appId) is { } entry)
        {
            _log.Error($"应用 {appId} 连续 3 次鉴权失败，停止该应用。");
            _ = StopAfterAuthFailureAsync(entry);
        }
    }

    public void NoteWindowLost(AppEntry entry)
    {
        _registry.SetState(entry, entry.State, HostError.Create(ErrorCodes.WidgetWindowLost, ErrorCodes.Describe(ErrorCodes.WidgetWindowLost)));
        if (entry.Runtime is { } runtime && _tokens.ResolveToken(entry.AppId) is { } token)
        {
            _ = _transport.SendCommandAsync(
                runtime.Port,
                token,
                entry.WidgetId,
                new WidgetCommand { Command = WidgetCommands.Refresh },
                CancellationToken.None);
        }
    }

    /// <summary>主动停止一个应用（卸载 / 重启组件前调用）。</summary>
    public async Task StopAppAsync(string appId)
    {
        var entry = _registry.Find(appId);
        if (entry is null)
        {
            return;
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync(entry).ConfigureAwait(false);
            _registry.SetState(entry, AppStates.Stopped, clearError: true);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task ShutdownAllAsync()
    {
        foreach (var entry in _registry.Entries.Where(static e => e.Process is not null))
        {
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _registry.SetState(entry, AppStates.Idle);
                await StopProcessAsync(entry).ConfigureAwait(false);
                _registry.SetState(entry, AppStates.Stopped, clearError: true);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
        }

        await ShutdownAllAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task<EnsureOutcome> AwaitFlightAsync(Task<EnsureOutcome> flight, CancellationToken cancellationToken)
    {
        try
        {
            return await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return EnsureOutcome.Fail(ErrorCodes.AppStartTimeout, "等待启动被取消");
        }
    }

    private async Task<EnsureOutcome> LaunchFlightAsync(AppEntry entry, int? timeoutMs)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunningReady(entry))
            {
                return Success(entry);
            }

            if (!VerifyHashAtLaunch(entry))
            {
                return EnsureOutcome.Fail(ErrorCodes.AppInvalid, entry.FailureSummary ?? "可执行文件哈希不符");
            }

            var startupTimeout = TimeSpan.FromMilliseconds(Math.Clamp(entry.Manifest.StartupTimeoutMs, 1_000, 60_000));
            if (timeoutMs is { } requested)
            {
                startupTimeout = TimeSpan.FromMilliseconds(Math.Clamp(requested, 100, ShellConstants.MaxEnsureTimeoutMs));
            }

            EnsureOutcome last = EnsureOutcome.Fail(ErrorCodes.AppStartTimeout, "启动超时");
            for (var attempt = 1; attempt <= _options.MaxConsecutiveFailures; attempt++)
            {
                if (attempt > 1)
                {
                    var delay = _options.BackoffDelays[Math.Min(attempt - 2, _options.BackoffDelays.Length - 1)];
                    _log.Warn($"应用 {entry.AppId} 第 {attempt} 次拉起，退避 {delay.TotalMilliseconds}ms");
                    await Task.Delay(delay, _time, CancellationToken.None).ConfigureAwait(false);
                }

                var attemptResult = await LaunchOnceAsync(entry, startupTimeout).ConfigureAwait(false);
                if (attemptResult.Success)
                {
                    return Success(entry);
                }

                last = attemptResult.Outcome;
                if (attemptResult.NoRetry)
                {
                    MarkFailed(entry, last);
                    return last;
                }
            }

            MarkFailed(entry, last);
            return last;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<AttemptResult> LaunchOnceAsync(AppEntry entry, TimeSpan startupTimeout)
    {
        IAppProcess? process = null;
        var token = AppTokenStore.CreateToken();
        try
        {
            // 单实例（DA-22）：用户自行启动 / 上次宿主残留的实例仍在时，直接接管它，不产生第二个进程。
            var adopted = await TryAdoptExistingAsync(entry, startupTimeout).ConfigureAwait(false);
            if (adopted is not null)
            {
                return adopted;
            }

            _registry.SetState(entry, AppStates.Launching, clearError: true);
            process = _launcher.Launch(new AppLaunchSpec(
                entry.AppId,
                entry.ExecutablePath,
                entry.Manifest.Arguments,
                entry.AppDirectory,
                token,
                HostUrl));

            AttachProcess(entry, process, token);
            var wait = await WaitForReadyAsync(entry, process, token, startupTimeout).ConfigureAwait(false);
            if (!wait.Success)
            {
                CleanupProcess(entry, process);
                return wait;
            }

            entry.Runtime = wait.Runtime;
            entry.LastActivity = UtcNow;
            entry.ReadySince = UtcNow;
            entry.PendingRestartAt = null;
            _registry.SetState(entry, AppStates.Ready, clearError: true);
            _log.Info($"应用 {entry.AppId} 已就绪（pid {process.Id}，端口 {wait.Runtime!.Port}）。");
            return AttemptResult.Ok(wait.Runtime);
        }
        catch (Exception ex)
        {
            _log.Error($"应用 {entry.AppId} 拉起异常。", ex);
            if (process is not null)
            {
                CleanupProcess(entry, process);
            }

            return AttemptResult.Fail(ErrorCodes.AppCrashed, ex.Message, noRetry: false);
        }
    }

    /// <summary>
    /// 接管已运行的实例（host-protocol §4）：runtime.json 里的 pid 存活、健康检查与握手通过时直接连上去。
    /// 连不上就清理残留（终止无法接管的进程）再走正常拉起，避免「互斥量被占 → 新进程退出 → 启动超时」。
    /// </summary>
    private async Task<AttemptResult?> TryAdoptExistingAsync(AppEntry entry, TimeSpan startupTimeout)
    {
        var runtime = _runtimeFiles.Read(entry.AppId);
        if (runtime is null || !IsProcessAlive(runtime.Pid))
        {
            return null;
        }

        if (!IsProcessIdentityMatches(runtime.Pid, entry.ExecutablePath))
        {
            // 陈旧 runtime.json：pid 被系统复用成别的进程，不能接管也不能杀，直接清理文件。
            _log.Warn($"runtime.json 的 pid {runtime.Pid} 不是 {entry.AppId} 的进程，清理陈旧指针。");
            _runtimeFiles.Delete(entry.AppId);
            _tokens.Clear(entry.AppId);
            return null;
        }

        var deadline = UtcNow + startupTimeout;
        while (UtcNow < deadline)
        {
            var health = await _transport.HealthAsync(runtime.Port, runtime.Token, CancellationToken.None).ConfigureAwait(false);
            if (health.Unauthorized)
            {
                return AttemptResult.Fail(ErrorCodes.TokenInvalid, "既有实例的健康检查返回 401", noRetry: true);
            }

            if (health.Success && health.Value!.Ok)
            {
                var handshake = await _transport.HandshakeAsync(
                    runtime.Port,
                    runtime.Token,
                    new HandshakeRequest
                    {
                        HostVersion = HostVersion.Product,
                        HostApiVersion = ShellConstants.HostApiVersion,
                        WidgetId = entry.WidgetId,
                    },
                    CancellationToken.None).ConfigureAwait(false);

                if (handshake.Unauthorized)
                {
                    return AttemptResult.Fail(ErrorCodes.TokenInvalid, "既有实例的握手返回 401", noRetry: true);
                }

                if (handshake.Success)
                {
                    if (!HostApiVersion.IsCompatible(handshake.Value!.HostApiVersion))
                    {
                        return AttemptResult.Fail(
                            ErrorCodes.VersionMismatch,
                            $"既有实例 hostApiVersion {handshake.Value.HostApiVersion} 不兼容",
                            noRetry: true);
                    }

                    var adopted = _launcher.Attach(runtime.Pid);
                    if (adopted is null)
                    {
                        return AttemptResult.Fail(ErrorCodes.AppInvalid, "无法接管已存在的进程", noRetry: true);
                    }

                    AttachProcess(entry, adopted, runtime.Token);
                    entry.Runtime = runtime;
                    entry.LastActivity = UtcNow;
                    entry.ReadySince = UtcNow;
                    entry.PendingRestartAt = null;
                    _registry.SetState(entry, AppStates.Ready, clearError: true);
                    _log.Info($"接管已存在的实例 {entry.AppId}（pid {runtime.Pid}，端口 {runtime.Port}）。");
                    return AttemptResult.Ok(runtime);
                }
            }

            await Task.Delay(_options.RuntimePollInterval, _time, CancellationToken.None).ConfigureAwait(false);
        }

        // pid 还在但连不上：当作残骸，终止后重新拉起。
        _log.Warn($"检测到无法接管的残留进程 {entry.AppId}（pid {runtime.Pid}），清理后重新拉起。");
        try
        {
            var stale = _launcher.Attach(runtime.Pid);
            stale?.Kill();
            stale?.Dispose();
        }
        catch
        {
            // 尽力而为
        }

        _runtimeFiles.Delete(entry.AppId);
        _tokens.Clear(entry.AppId);
        return null;
    }

    private async Task<AttemptResult> WaitForReadyAsync(
        AppEntry entry,
        IAppProcess process,
        string token,
        TimeSpan startupTimeout)
    {
        var deadline = UtcNow + startupTimeout;
        while (UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return AttemptResult.Fail(ErrorCodes.AppCrashed, "进程在就绪前退出", noRetry: false);
            }

            var runtime = _runtimeFiles.Read(entry.AppId);
            if (runtime is not null)
            {
                if (runtime.Pid != process.Id)
                {
                    if (!IsProcessAlive(runtime.Pid))
                    {
                        _runtimeFiles.Delete(entry.AppId);
                    }
                    else
                    {
                        return AttemptResult.Fail(ErrorCodes.AppInvalid, "runtime.json 指向另一个存活进程", noRetry: true);
                    }
                }
                else if (!string.Equals(runtime.Token, token, StringComparison.Ordinal))
                {
                    return AttemptResult.Fail(ErrorCodes.TokenInvalid, "runtime.json 的 token 与宿主下发的不一致", noRetry: true);
                }
                else if (runtime.Port > 0)
                {
                    var health = await _transport.HealthAsync(runtime.Port, token, CancellationToken.None).ConfigureAwait(false);
                    if (health.Unauthorized)
                    {
                        return AttemptResult.Fail(ErrorCodes.TokenInvalid, "健康检查返回 401", noRetry: true);
                    }

                    if (health.Success && health.Value!.Ok)
                    {
                        var handshake = await _transport.HandshakeAsync(
                            runtime.Port,
                            token,
                            new HandshakeRequest
                            {
                                HostVersion = HostVersion.Product,
                                HostApiVersion = ShellConstants.HostApiVersion,
                                WidgetId = entry.WidgetId,
                            },
                            CancellationToken.None).ConfigureAwait(false);

                        if (handshake.Unauthorized)
                        {
                            return AttemptResult.Fail(ErrorCodes.TokenInvalid, "握手返回 401", noRetry: true);
                        }

                        if (handshake.Success)
                        {
                            if (!HostApiVersion.IsCompatible(handshake.Value!.HostApiVersion))
                            {
                                return AttemptResult.Fail(
                                    ErrorCodes.VersionMismatch,
                                    $"应用 hostApiVersion {handshake.Value.HostApiVersion} 不兼容",
                                    noRetry: true);
                            }

                            return AttemptResult.Ok(runtime);
                        }
                    }
                }
            }

            await Task.Delay(_options.RuntimePollInterval, _time, CancellationToken.None).ConfigureAwait(false);
        }

        return AttemptResult.Fail(ErrorCodes.AppStartTimeout, $"启动超时（{startupTimeout.TotalMilliseconds:0}ms）", noRetry: false);
    }

    private void AttachProcess(AppEntry entry, IAppProcess process, string token)
    {
        entry.Process = process;
        _tokens.SetLaunchedToken(entry.AppId, token);
        process.Exited += (_, _) => OnProcessExited(entry, process);
    }

    private void OnProcessExited(AppEntry entry, IAppProcess process)
    {
        string action;
        TimeSpan? restartDelay = null;

        lock (entry.SyncRoot)
        {
            if (entry.Process != process)
            {
                process.Dispose();
                return;
            }

            entry.Process = null;
            entry.Runtime = null;
            _tokens.Clear(entry.AppId);
            _runtimeFiles.Delete(entry.AppId);

            if (entry.StopRequested || entry.State == AppStates.Stopped)
            {
                entry.StopRequested = false;
                action = "stopped";
            }
            else if (entry.State == AppStates.Launching)
            {
                action = "launching";
            }
            else
            {
                // 「就绪后很快崩溃」的循环不能被当成成功重启；计数只有稳定运行一段时间才清零。
                entry.ReadySince = null;
                entry.ConsecutiveFailures++;
                if (entry.ConsecutiveFailures >= _options.MaxConsecutiveFailures)
                {
                    action = "failed";
                }
                else
                {
                    restartDelay = _options.BackoffDelays[Math.Min(entry.ConsecutiveFailures - 1, _options.BackoffDelays.Length - 1)];
                    entry.PendingRestartAt = UtcNow + restartDelay.Value;
                    action = "restart";
                }
            }
        }

        process.Dispose();

        switch (action)
        {
            case "stopped":
                _registry.SetState(entry, AppStates.Stopped, clearError: true);
                break;
            case "failed":
                _log.Error($"应用 {entry.AppId} 连续 {entry.ConsecutiveFailures} 次崩溃，转错误态。");
                _registry.SetState(entry, AppStates.Failed, HostError.Create(ErrorCodes.AppCrashed, ErrorCodes.Describe(ErrorCodes.AppCrashed)));
                break;
            case "restart":
                _log.Warn($"应用 {entry.AppId} 异常退出，{restartDelay?.TotalMilliseconds ?? 0}ms 后重启。");
                _registry.SetState(entry, AppStates.Stopped, HostError.Create(ErrorCodes.AppCrashed, ErrorCodes.Describe(ErrorCodes.AppCrashed)));
                break;
            default:
                break;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.IdleCheckInterval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("空闲 / 重启循环出错。", ex);
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow;
        foreach (var entry in _registry.Entries)
        {
            if (entry.PendingRestartAt is { } at && now >= at)
            {
                entry.PendingRestartAt = null;
                if (entry.Visible && entry.Enabled && entry.IsLaunchable)
                {
                    _ = EnsureAsync(entry.AppId, null, cancellationToken);
                }

                continue;
            }

            // 稳定运行超过阈值：崩溃计数归零（「就绪→崩溃」的快速循环仍按连续失败计数）。
            if (entry.State is AppStates.Ready or AppStates.Idle
                && entry.ReadySince is { } readySince
                && now - readySince >= _options.CrashStabilityWindow)
            {
                entry.ConsecutiveFailures = 0;
                entry.ReadySince = null;
            }

            if (entry.State is not (AppStates.Ready or AppStates.Idle)
                || entry.Visible
                || entry.IdleHold
                || entry.Manifest.IdleTimeoutMs <= 0)
            {
                continue;
            }

            var idleFor = now - entry.LastActivity;
            if (idleFor >= TimeSpan.FromMilliseconds(entry.Manifest.IdleTimeoutMs))
            {
                _ = StopForIdleAsync(entry);
            }
        }
    }

    private async Task StopForIdleAsync(AppEntry entry)
    {
        if (!await entry.Gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (entry.State is not (AppStates.Ready or AppStates.Idle))
            {
                return;
            }

            _registry.SetState(entry, AppStates.Idle);
            _log.Info($"应用 {entry.AppId} 空闲超时，准备退出。");
            await StopProcessAsync(entry).ConfigureAwait(false);
            _registry.SetState(entry, AppStates.Stopped, clearError: true);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task StopAfterAuthFailureAsync(AppEntry entry)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync(entry).ConfigureAwait(false);
            _registry.SetState(entry, AppStates.Failed, HostError.Create(ErrorCodes.TokenInvalid, "连续 3 次鉴权失败"));
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task StopProcessAsync(AppEntry entry)
    {
        var process = entry.Process;
        if (process is null)
        {
            return;
        }

        entry.StopRequested = true;
        var runtime = entry.Runtime;
        var token = _tokens.ResolveToken(entry.AppId);

        if (runtime is not null && token is not null && !process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ShellConstants.ShutdownGraceMs));
                await _transport.ShutdownAsync(runtime.Port, token, ShellConstants.ShutdownGraceMs, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 应用可能已退出或拒绝；继续走强制终止。
            }
        }

        if (!process.HasExited)
        {
            process.WaitForExit(TimeSpan.FromMilliseconds(ShellConstants.ShutdownGraceMs));
        }

        if (!process.HasExited)
        {
            _log.Warn($"应用 {entry.AppId} 未在 {ShellConstants.ShutdownGraceMs}ms 内退出，强制终止。");
            process.Kill();
        }

        process.Dispose();
        lock (entry.SyncRoot)
        {
            if (entry.Process == process)
            {
                entry.Process = null;
            }

            entry.Runtime = null;
            entry.StopRequested = false;
        }

        _tokens.Clear(entry.AppId);
        _runtimeFiles.Delete(entry.AppId);
    }

    private void CleanupProcess(AppEntry entry, IAppProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.WaitForExit(TimeSpan.FromMilliseconds(1_000));
        }
        catch
        {
            // 清理尽力而为
        }

        process.Dispose();
        lock (entry.SyncRoot)
        {
            if (entry.Process == process)
            {
                entry.Process = null;
            }

            entry.Runtime = null;
        }

        _tokens.Clear(entry.AppId);
        _runtimeFiles.Delete(entry.AppId);
    }

    private bool VerifyHashAtLaunch(AppEntry entry)
    {
        var actual = _hasher.ComputeSha256(entry.ExecutablePath);
        if (actual is not null && string.Equals(actual, entry.Manifest.Sha256, StringComparison.Ordinal))
        {
            return true;
        }

        entry.HashValid = false;
        var error = HostError.Create(ErrorCodes.AppInvalid, "可执行文件哈希不符，拒绝启动（DA-76）");
        _registry.SetState(entry, AppStates.Invalid, error);
        _log.Error($"应用 {entry.AppId} 哈希校验失败：期望 {entry.Manifest.Sha256}，实际 {actual ?? "<不可读>"}");
        return false;
    }

    private bool IsRunningReady(AppEntry entry) =>
        entry.State is AppStates.Ready or AppStates.Idle
        && entry.Runtime is not null
        && entry.Process is { HasExited: false };

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessIdentityMatches(int pid, string executablePath)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var expected = Path.GetFileNameWithoutExtension(executablePath);
            return string.Equals(process.ProcessName, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private EnsureOutcome Success(AppEntry entry) => new()
    {
        Descriptor = Describe(entry),
        Runtime = entry.Runtime is null
            ? null
            : new RuntimePointer
            {
                Pid = entry.Runtime.Pid,
                Port = entry.Runtime.Port,
                RuntimePath = _paths.RuntimeFilePath(entry.AppId),
            },
    };

    private void MarkFailed(AppEntry entry, EnsureOutcome outcome)
    {
        var code = outcome.ErrorCode ?? ErrorCodes.AppCrashed;
        var error = HostError.Create(code, outcome.Message ?? ErrorCodes.Describe(code));
        _registry.SetState(entry, AppStates.Failed, error);
        _log.Error($"应用 {entry.AppId} 启动失败（{code}）：{error.Message}");
    }

    private sealed class AttemptResult
    {
        public bool Success { get; private init; }

        public bool NoRetry { get; private init; }

        public RuntimeFile? Runtime { get; private init; }

        public EnsureOutcome Outcome { get; private init; } = EnsureOutcome.Fail(ErrorCodes.AppCrashed, "未知失败");

        public static AttemptResult Ok(RuntimeFile runtime) => new() { Success = true, Runtime = runtime };

        public static AttemptResult Fail(string code, string message, bool noRetry) => new()
        {
            Success = false,
            NoRetry = noRetry,
            Outcome = EnsureOutcome.Fail(code, message),
        };
    }
}
