namespace SDemo.Hub;

public interface IHubLog
{
    void Info(string message);

    void Warn(string message, Exception? exception = null);
}

public sealed class NullHubLog : IHubLog
{
    public static NullHubLog Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? exception = null)
    {
    }
}

public sealed class ConsoleHubLog : IHubLog
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {message}");
            if (exception is not null)
            {
                Console.WriteLine(exception);
            }
        }
    }
}
