using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Spike.Viewer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var targets = ParseTargets(args);
        var resultPath = GetArg(args, "--result")
            ?? Path.Combine(AppContext.BaseDirectory, "m0-result.json");

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("no targets; use --target <appId> <port> <token>");
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new ViewerWindow(targets, resultPath);
        window.Show();
        return app.Run();
    }

    private static List<Target> ParseTargets(string[] args)
    {
        var list = new List<Target>();
        for (var i = 0; i < args.Length - 3; i++)
        {
            if (args[i] != "--target")
            {
                continue;
            }

            if (!int.TryParse(args[i + 2], out var port))
            {
                Console.Error.WriteLine($"bad port: {args[i + 2]}");
                continue;
            }

            list.Add(new Target(args[i + 1], port, args[i + 3]));
            i += 3;
        }

        return list;
    }

    private static string? GetArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

public sealed record Target(string AppId, int Port, string Token);
