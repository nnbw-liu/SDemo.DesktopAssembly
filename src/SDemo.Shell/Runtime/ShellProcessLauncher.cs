using System.Diagnostics;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Runtime;

/// <summary>真实进程启动器：参数逐项传、token 经环境变量下发（DA-34，不经 shell 拼接）。</summary>
public sealed class ShellProcessLauncher : IAppProcessLauncher
{
    private readonly IShellLog _log;

    public ShellProcessLauncher(IShellLog? log = null) => _log = log ?? NullShellLog.Instance;

    public IAppProcess Launch(AppLaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[ShellConstants.AppTokenEnvironmentVariable] = spec.Token;
        startInfo.Environment[ShellConstants.HostUrlEnvironmentVariable] = spec.HostUrl;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"进程启动失败：{spec.ExecutablePath}");
        }

        _log.Info($"已拉起应用 {spec.AppId}（pid {process.Id}）。");
        return new ShellAppProcess(process, _log);
    }

    public IAppProcess? Attach(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            process.EnableRaisingEvents = true;
            _log.Info($"接管已存在的进程 pid {pid}。");
            return new ShellAppProcess(process, _log);
        }
        catch (Exception ex)
        {
            _log.Warn($"无法接管进程 pid {pid}。", ex);
            return null;
        }
    }
}

internal sealed class ShellAppProcess : IAppProcess
{
    private readonly Process _process;
    private readonly IShellLog _log;
    private bool _disposed;

    public ShellAppProcess(Process process, IShellLog log)
    {
        _process = process;
        _log = log;
        _process.Exited += OnExited;
    }

    public int Id => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public event EventHandler? Exited;

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"终止进程失败：pid {Id}", ex);
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            return _process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds));
        }
        catch
        {
            return false;
        }
    }

    public bool TryOpenSingleInstanceMutex(string mutexName)
    {
        try
        {
            if (Mutex.TryOpenExisting(mutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch
        {
            // 全局命名空间 / 权限问题：当作不持有。
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Exited -= OnExited;
        _process.Dispose();
    }

    private void OnExited(object? sender, EventArgs e) => Exited?.Invoke(this, EventArgs.Empty);
}
