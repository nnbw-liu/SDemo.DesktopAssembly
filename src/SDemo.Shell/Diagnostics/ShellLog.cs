using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace SDemo.Shell.Diagnostics;

public enum ShellLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>一条日志（同时用于文件与诊断面板）。</summary>
public sealed record ShellLogEntry(DateTimeOffset Time, ShellLogLevel Level, string Message, string? RequestId, string? Exception);

/// <summary>壳日志抽象（DA-73：按天分文件、单文件上限 10MB、requestId 串联）。</summary>
public interface IShellLog
{
    string FilePath { get; }

    IReadOnlyList<ShellLogEntry> Recent { get; }

    void Log(ShellLogLevel level, string message, Exception? exception = null, string? requestId = null);
}

public static class ShellLogExtensions
{
    public static void Debug(this IShellLog log, string message, string? requestId = null) =>
        log.Log(ShellLogLevel.Debug, message, null, requestId);

    public static void Info(this IShellLog log, string message, string? requestId = null) =>
        log.Log(ShellLogLevel.Info, message, null, requestId);

    public static void Warn(this IShellLog log, string message, Exception? exception = null, string? requestId = null) =>
        log.Log(ShellLogLevel.Warn, message, exception, requestId);

    public static void Error(this IShellLog log, string message, Exception? exception = null, string? requestId = null) =>
        log.Log(ShellLogLevel.Error, message, exception, requestId);
}

/// <summary>空日志（测试与无日志场景）。</summary>
public sealed class NullShellLog : IShellLog
{
    public static NullShellLog Instance { get; } = new();

    private NullShellLog()
    {
    }

    public string FilePath => string.Empty;

    public IReadOnlyList<ShellLogEntry> Recent => Array.Empty<ShellLogEntry>();

    public void Log(ShellLogLevel level, string message, Exception? exception = null, string? requestId = null)
    {
    }
}

/// <summary>可切换目标的日志代理：让状态文件在日志器就绪前后都能拿到同一个 IShellLog。</summary>
public sealed class ForwardShellLog : IShellLog
{
    public IShellLog Target { get; set; } = NullShellLog.Instance;

    public string FilePath => Target.FilePath;

    public IReadOnlyList<ShellLogEntry> Recent => Target.Recent;

    public void Log(ShellLogLevel level, string message, Exception? exception = null, string? requestId = null) =>
        Target.Log(level, message, exception, requestId);
}

/// <summary>文件日志：每天一个文件，写满 10MB 后转一个 <c>.1.log</c>，旧文件按 keepDays 清理。</summary>
public sealed class FileShellLog : IShellLog, IDisposable
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxRecentEntries = 300;

    private readonly object _gate = new();
    private readonly Queue<ShellLogEntry> _recent = new();
    private readonly string _directory;
    private readonly ShellLogLevel _minimumLevel;
    private readonly TimeProvider _time;
    private DateOnly _currentDay;
    private StreamWriter? _writer;
    private bool _disposed;

    public FileShellLog(ShellPaths paths, ShellLogLevel minimumLevel = ShellLogLevel.Info, int keepDays = 14, TimeProvider? time = null)
    {
        _directory = paths.LogsDirectory;
        _minimumLevel = minimumLevel;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
        _currentDay = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        CleanupOldLogs(keepDays);
        OpenWriter();
        FilePath = Path.Combine(_directory, $"{_currentDay:yyyy-MM-dd}.log");
    }

    public string FilePath { get; private set; }

    public IReadOnlyList<ShellLogEntry> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    public void Log(ShellLogLevel level, string message, Exception? exception = null, string? requestId = null)
    {
        if (level < _minimumLevel || _disposed)
        {
            return;
        }

        var now = _time.GetLocalNow();
        var entry = new ShellLogEntry(now, level, message, requestId, exception?.ToString());
        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > MaxRecentEntries)
            {
                _recent.Dequeue();
            }

            try
            {
                RollDayIfNeeded(now);
                if (_writer is null)
                {
                    OpenWriter();
                }

                if (_writer is null)
                {
                    return;
                }

                var line = FormatLine(entry);
                _writer.WriteLine(line);
                _writer.Flush();

                if (_writer.BaseStream.Length >= MaxFileBytes)
                {
                    RotateOverflow(now);
                }
            }
            catch
            {
                // 日志失败不能影响宿主主流程。
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string FormatLine(ShellLogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(entry.Level.ToString().ToLowerInvariant()).Append(']');
        if (!string.IsNullOrEmpty(entry.RequestId))
        {
            builder.Append(" [").Append(entry.RequestId).Append(']');
        }

        builder.Append(' ').Append(entry.Message);
        if (!string.IsNullOrEmpty(entry.Exception))
        {
            builder.AppendLine();
            builder.Append(entry.Exception);
        }

        return builder.ToString();
    }

    private void RollDayIfNeeded(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.DateTime);
        if (day == _currentDay)
        {
            return;
        }

        _writer?.Dispose();
        _writer = null;
        _currentDay = day;
        FilePath = Path.Combine(_directory, $"{day:yyyy-MM-dd}.log");
        OpenWriter();
    }

    private void OpenWriter()
    {
        FilePath = Path.Combine(_directory, $"{_currentDay:yyyy-MM-dd}.log");
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
    }

    private void RotateOverflow(DateTimeOffset now)
    {
        var overflow = Path.Combine(_directory, $"{now:yyyy-MM-dd}.1.log");
        _writer?.Dispose();
        _writer = null;
        try
        {
            if (File.Exists(overflow))
            {
                File.Delete(overflow);
            }

            File.Move(FilePath, overflow);
        }
        catch
        {
            // 失败就继续写原文件。
        }

        OpenWriter();
    }

    private void CleanupOldLogs(int keepDays)
    {
        try
        {
            var cutoff = DateOnly.FromDateTime(_time.GetLocalNow().DateTime).AddDays(-Math.Max(keepDays, 1));
            foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var dayText = name.Length >= 10 ? name[..10] : name;
                if (DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && day < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响启动。
        }
    }
}
