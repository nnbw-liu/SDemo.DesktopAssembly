using SDemo.Shell.Contracts;
using SDemo.Shell.Runtime;

namespace SDemo.Shell.Tests;

/// <summary>
/// 进程管家（DA-02 / DA-03 / DA-04 / DA-20–DA-27 / DA-29 / DA-30 / DA-92）：
/// ensure 单飞、就绪判定、401 / 版本 / 超时、空闲退出、退出顺序、崩溃退避。
/// </summary>
public sealed class ProcessManagerTests
{
    private const string AppId = "sdemo.alpha";

    [Fact]
    public async Task Ensure_Success_ReachesReadyWithRuntimePort()
    {
        await using var shell = new ProcessTestShell();

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);

        Assert.True(outcome.Success);
        Assert.Null(outcome.ErrorCode);
        Assert.Equal(1, shell.Launcher.LaunchCount);
        Assert.Equal(AppStates.Ready, shell.Entry.State);
        Assert.NotNull(shell.Entry.Runtime);
        Assert.Equal(shell.Launcher.Port, shell.Entry.Runtime!.Port);
        Assert.Equal(shell.Launcher.Processes[0].Id, shell.Entry.Runtime.Pid);
        Assert.Equal(shell.Launcher.Port, outcome.Runtime!.Port);

        var spec = shell.Launcher.Specs[0];
        var runtime = shell.RuntimeFiles.Read(AppId);
        Assert.NotNull(runtime);
        Assert.Equal(spec.Token, runtime!.Token);
        Assert.Equal(shell.Launcher.Processes[0].Id, runtime.Pid);
        Assert.Equal(shell.Launcher.Port, runtime.Port);
        Assert.True(shell.Transport.HealthCalls >= 1);
        Assert.True(shell.Transport.HandshakeCalls >= 1);
    }

    [Fact]
    public async Task Ensure_FiveConcurrentCalls_LaunchOnlyOnce()
    {
        await using var shell = new ProcessTestShell();
        shell.Transport.HealthDelayMs = 100;

        var calls = Enumerable.Range(0, 5)
            .Select(_ => shell.Manager.EnsureAsync(AppId, 2_000))
            .ToArray();
        var outcomes = await Task.WhenAll(calls);

        Assert.All(outcomes, outcome => Assert.True(outcome.Success));
        Assert.Equal(1, shell.Launcher.LaunchCount);
        Assert.Equal(AppStates.Ready, shell.Entry.State);
    }

    [Fact]
    public async Task Ensure_Health401_TokenInvalidWithoutRetry()
    {
        await using var shell = new ProcessTestShell();
        shell.Transport.HealthHandler = (_, _) => new AppCallResult<HealthResponse>(null, 401, "健康检查 401");

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);

        Assert.Equal(ErrorCodes.TokenInvalid, outcome.ErrorCode);
        Assert.Equal(1, shell.Launcher.LaunchCount);
        Assert.Equal(AppStates.Failed, shell.Entry.State);
        Assert.Equal(ErrorCodes.TokenInvalid, shell.Entry.LastError!.Code);
    }

    [Fact]
    public async Task Ensure_HandshakeVersionMismatch_NoRetry()
    {
        await using var shell = new ProcessTestShell();
        shell.Transport.HandshakeHandler = (_, _, _) => new AppCallResult<HandshakeResponse>(
            new HandshakeResponse { HostApiVersion = "2.0" },
            200);

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);

        Assert.Equal(ErrorCodes.VersionMismatch, outcome.ErrorCode);
        Assert.Equal(1, shell.Launcher.LaunchCount);
        Assert.Equal(1, shell.Transport.HandshakeCalls);
        Assert.Equal(AppStates.Failed, shell.Entry.State);
        Assert.Equal(ErrorCodes.VersionMismatch, shell.Entry.LastError!.Code);
    }

    [Fact]
    public async Task Ensure_NoRuntimeFile_TimesOutAfterThreeAttemptsAndKills()
    {
        await using var shell = new ProcessTestShell();
        shell.Launcher.WriteRuntime = false;

        var outcome = await shell.Manager.EnsureAsync(AppId, 200);

        Assert.Equal(ErrorCodes.AppStartTimeout, outcome.ErrorCode);
        Assert.Equal(3, shell.Launcher.LaunchCount);
        Assert.All(shell.Launcher.Processes, process => Assert.True(process.KillCount >= 1));
        Assert.Equal(AppStates.Failed, shell.Entry.State);
        Assert.Equal(ErrorCodes.AppStartTimeout, shell.Entry.LastError!.Code);
    }

    [Fact]
    public async Task Idle_ShutsDownInvisibleReadyApp()
    {
        await using var shell = new ProcessTestShell(AppId, app => app.IdleTimeoutMs = 50);

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);
        Assert.True(outcome.Success);

        Assert.True(shell.Registry.SetVisible(shell.WidgetId, false));
        shell.Manager.Start();

        var stopped = await TestFiles.WaitForAsync(
            () => shell.Transport.Shutdowns.Count >= 1 && shell.Entry.State == AppStates.Stopped,
            1_000);
        Assert.True(stopped, "空闲超时后应 shutdown 并转 stopped");
        Assert.Null(shell.Entry.Runtime);
    }

    [Fact]
    public async Task ShutdownAll_SendsShutdownThenKillsAndStops()
    {
        await using var shell = new ProcessTestShell();
        shell.Launcher.Configure = (process, _) => process.ExitOnWait = false;

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);
        Assert.True(outcome.Success);
        var process = shell.Launcher.Processes[0];

        await shell.Manager.ShutdownAllAsync();

        Assert.True(shell.Transport.Shutdowns.Count >= 1);
        Assert.True(process.KillCount >= 1);
        Assert.Equal(AppStates.Stopped, shell.Entry.State);
        Assert.Null(shell.Entry.Runtime);
        Assert.Null(shell.Entry.Process);
    }

    [Fact]
    public async Task SetIdleHold_PreventsIdleShutdown()
    {
        await using var shell = new ProcessTestShell(AppId, app => app.IdleTimeoutMs = 50);

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);
        Assert.True(outcome.Success);

        Assert.True(shell.Registry.SetVisible(shell.WidgetId, false));
        shell.Manager.SetIdleHold(AppId, true);
        shell.Manager.Start();

        await Task.Delay(500);

        Assert.Equal(AppStates.Ready, shell.Entry.State);
        Assert.Empty(shell.Transport.Shutdowns);
    }

    [Fact]
    public async Task MarkActivity_PreventsIdleShutdown()
    {
        await using var shell = new ProcessTestShell(AppId, app => app.IdleTimeoutMs = 50);

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);
        Assert.True(outcome.Success);

        Assert.True(shell.Registry.SetVisible(shell.WidgetId, false));
        shell.Manager.Start();

        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < deadline)
        {
            shell.Manager.MarkActivity(AppId);
            await Task.Delay(10);
        }

        Assert.Equal(AppStates.Ready, shell.Entry.State);
        Assert.Empty(shell.Transport.Shutdowns);
    }

    [Fact]
    public async Task CrashBeforeReady_RetriesThreeTimesThenAppCrashed()
    {
        await using var shell = new ProcessTestShell();
        shell.Launcher.Configure = (process, _) => process.RaiseExited();

        var outcome = await shell.Manager.EnsureAsync(AppId, 500);

        Assert.Equal(ErrorCodes.AppCrashed, outcome.ErrorCode);
        Assert.Equal(3, shell.Launcher.LaunchCount);
        Assert.Equal(AppStates.Failed, shell.Entry.State);
        Assert.Equal(ErrorCodes.AppCrashed, shell.Entry.LastError!.Code);
    }

    [Fact]
    public async Task CrashAfterReady_TriggersRestart()
    {
        await using var shell = new ProcessTestShell();
        shell.Launcher.AutoExitAfterMs = 100;

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);
        Assert.True(outcome.Success);

        shell.Manager.Start();

        var restarted = await TestFiles.WaitForAsync(() => shell.Launcher.LaunchCount >= 2, 1_500);
        Assert.True(restarted, "就绪后崩溃应触发退避重启");

        // 停掉后续重启，避免测试收尾时还在拉起。
        Assert.True(shell.Registry.SetVisible(shell.WidgetId, false));
    }

    [Fact]
    public async Task CrashAfterReady_ThreeCrashesTransitionsToFailed()
    {
        await using var shell = new ProcessTestShell();
        shell.Launcher.AutoExitAfterMs = 150;

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);
        Assert.True(outcome.Success);

        shell.Manager.Start();

        var failed = await TestFiles.WaitForAsync(
            () => shell.Entry.State == AppStates.Failed,
            2_000);
        Assert.True(failed, "连续 3 次崩溃后应转 failed");
        Assert.Equal(ErrorCodes.AppCrashed, shell.Entry.LastError!.Code);
        Assert.Equal(3, shell.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Ensure_UnknownOrDisabledApp_FailsFast()
    {
        await using var shell = new ProcessTestShell();

        var unknown = await shell.Manager.EnsureAsync("sdemo.missing", 200);
        Assert.Equal(ErrorCodes.AppNotFound, unknown.ErrorCode);
        Assert.Equal(0, shell.Launcher.LaunchCount);

        Assert.True(shell.Registry.SetEnabled(shell.WidgetId, false));
        var disabled = await shell.Manager.EnsureAsync(AppId, 200);
        Assert.Equal(ErrorCodes.AppDisabled, disabled.ErrorCode);
        Assert.Equal(0, shell.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Ensure_SecondCallWhileReady_DoesNotLaunchAgain()
    {
        await using var shell = new ProcessTestShell();

        var first = await shell.Manager.EnsureAsync(AppId, 1_000);
        Assert.True(first.Success);
        var second = await shell.Manager.EnsureAsync(AppId, 1_000);

        Assert.True(second.Success);
        Assert.Equal(1, shell.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Ensure_ExistingRuntimeInstance_IsAdoptedWithoutLaunch()
    {
        // 既有实例的身份校验走真实进程表：用测试进程自身当「已在运行的应用」，
        // 其 exe 名写进清单，保证 IsProcessIdentityMatches 能通过。
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取测试进程路径");
        var executableName = Path.GetFileName(processPath);
        const int existingPort = 49_777;

        await using var shell = new ProcessTestShell(AppId, tweakAppWithDirectory: (app, directory) =>
        {
            var relative = "payload/" + executableName;
            var path = Path.Combine(directory, "payload", executableName);
            TestFiles.WriteRaw(path, "fake already-running executable");
            app.Executable = relative;
            app.Sha256 = TestManifests.Sha256Of(path);
        });

        var existingPid = Environment.ProcessId;
        var existingToken = AppTokenStore.CreateToken();
        shell.RuntimeFiles.Write(AppId, new RuntimeFile
        {
            Pid = existingPid,
            Port = existingPort,
            Token = existingToken,
            StartedAt = DateTimeOffset.UtcNow,
            HostApiVersion = ShellConstants.HostApiVersion,
        });

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);

        Assert.True(outcome.Success);
        Assert.Equal(0, shell.Launcher.LaunchCount);
        Assert.Equal(1, shell.Launcher.AttachCount);
        Assert.Equal(existingPid, shell.Launcher.AttachedPid);
        Assert.Equal(AppStates.Ready, shell.Entry.State);
        Assert.NotNull(shell.Entry.Runtime);
        Assert.Equal(existingPid, shell.Entry.Runtime!.Pid);
        Assert.Equal(existingPort, shell.Entry.Runtime.Port);
        Assert.Equal(existingToken, shell.Entry.Runtime.Token);
        Assert.Equal(existingPid, shell.Entry.Process!.Id);
        Assert.Equal(existingPort, outcome.Runtime!.Port);
        Assert.True(shell.Transport.HealthCalls >= 1);
        Assert.True(shell.Transport.HandshakeCalls >= 1);
        Assert.Equal(existingToken, shell.Transport.LastHealthToken);
        Assert.Equal(existingToken, shell.Transport.LastHandshakeToken);
    }

    [Fact]
    public async Task Ensure_StaleRuntimeWithDeadPid_LaunchesNormally()
    {
        await using var shell = new ProcessTestShell();
        shell.RuntimeFiles.Write(AppId, new RuntimeFile
        {
            Pid = int.MaxValue,
            Port = 49_778,
            Token = AppTokenStore.CreateToken(),
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            HostApiVersion = ShellConstants.HostApiVersion,
        });

        var outcome = await shell.Manager.EnsureAsync(AppId, 1_000);

        Assert.True(outcome.Success);
        Assert.Equal(0, shell.Launcher.AttachCount);
        Assert.Equal(1, shell.Launcher.LaunchCount);
        Assert.Equal(AppStates.Ready, shell.Entry.State);
        Assert.Equal(shell.Launcher.Port, shell.Entry.Runtime!.Port);
    }
}
