using System.IO;
using SDemo.Hub;

namespace SDemo.Hub.App;

/// <summary>把 Hub 运行日志落到 %LOCALAPPDATA%\SDemo\Hub\logs，WinExe 没有控制台时也能排查。</summary>
public sealed class FileHubLog : IHubLog
{
    /// <summary>单个日志文件上限；超过后滚动为 .1.log，避免长期运行撑满磁盘。</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly string _directory;
    private readonly object _gate = new();

    public FileHubLog(HubPaths paths)
    {
        _directory = paths.LogsDirectory;
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"hub-{DateTime.Now:yyyyMMdd}.log");
                RotateIfNeeded(path);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不能影响主流程。
        }
    }

    /// <summary>只保留当前与上一份滚动日志，避免无限增长。</summary>
    private static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes)
            {
                return;
            }

            var archive = Path.ChangeExtension(path, ".1.log");
            File.Move(path, archive, overwrite: true);
        }
        catch
        {
            // 滚动失败时继续追加；下一次写入再尝试。
        }
    }
}

/// <summary>控制台 + 文件；开发时看控制台，WinExe 时看文件。</summary>
public sealed class CompositeHubLog : IHubLog
{
    private readonly IHubLog[] _logs;

    public CompositeHubLog(params IHubLog[] logs) => _logs = logs;

    public void Info(string message)
    {
        foreach (var log in _logs)
        {
            log.Info(message);
        }
    }

    public void Warn(string message, Exception? exception = null)
    {
        foreach (var log in _logs)
        {
            log.Warn(message, exception);
        }
    }
}
