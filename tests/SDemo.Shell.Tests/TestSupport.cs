using System.Text;
using System.Text.Json;
using SDemo.Shell.Contracts;
using SDemo.Shell.Discovery;

namespace SDemo.Shell.Tests;

/// <summary>每个测试一个唯一临时根目录；失败也不污染真实 %APPDATA% / %LOCALAPPDATA%。</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot(string label = "test")
    {
        Root = Path.Combine(Path.GetTempPath(), "sdemo-shell-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Paths = ShellPaths.ForTestRoot(Root);
        Paths.EnsureDirectories();
    }

    public string Root { get; }

    public ShellPaths Paths { get; }

    public string AppDir(string appId) => Path.Combine(Paths.AppsDirectory, appId);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Windows 上偶发句柄未释放导致删不掉：忽略，不影响断言结果。
        }
    }
}

internal static class TestFiles
{
    public static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(
            path,
            ShellJson.NormalizeNewLines(JsonSerializer.Serialize(value, ShellJson.File)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static void WriteRaw(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    public static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2_000, int intervalMs = 10)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(intervalMs).ConfigureAwait(false);
        }

        return condition();
    }
}

/// <summary>合法清单模板：字段值取自 <c>samples/hello-app</c>，测试里按需 tweak。</summary>
internal static class TestManifests
{
    public const string ExecutableRelativePath = "payload/app.exe";

    public static AppManifest App(string id, string sha256, Action<AppManifest>? tweak = null)
    {
        var manifest = new AppManifest
        {
            SchemaVersion = ShellConstants.ManifestSchemaVersion,
            Id = id,
            Name = id,
            Version = "0.1.0",
            Executable = ExecutableRelativePath,
            Sha256 = sha256,
            SingleInstanceMutex = "SDemo.App." + id,
            StartupTimeoutMs = 8_000,
            IdleTimeoutMs = 300_000,
            Startup = StartupPolicies.OnDemand,
            MinHostApiVersion = ShellConstants.HostApiVersion,
        };
        tweak?.Invoke(manifest);
        return manifest;
    }

    public static WidgetManifest Widget(string appId, Action<WidgetManifest>? tweak = null)
    {
        var manifest = new WidgetManifest
        {
            SchemaVersion = ShellConstants.ManifestSchemaVersion,
            Id = appId + ".widget",
            AppId = appId,
            Name = appId,
            Description = "test widget",
            Version = "0.1.0",
            HostApiVersion = ShellConstants.HostApiVersion,
            Renderer = WidgetRendering.Native,
            Native = new WidgetNativeSpec
            {
                Process = "app.exe",
                Layered = true,
                Resizable = true,
                MinSize = new WidgetMinSize { Width = 260, Height = 180 },
            },
            Size = new WidgetSize { Width = 320, Height = 240 },
            Layer = WidgetRendering.Floating,
            Position = new WidgetPositionSpec
            {
                Mode = "anchored",
                Corner = "top-right",
                OffsetX = 24,
                OffsetY = 24,
                SnapPx = 12,
                RememberPerMonitor = true,
            },
            Behavior = new WidgetBehaviorSpec
            {
                AlwaysOnTopDefault = true,
                DragMode = "app",
                ClickThrough = "toggleWithHotkey",
                HideBehindFullscreenApp = true,
                Opacity = new WidgetOpacitySpec { Default = 0.92, Min = 0.55 },
                KeepAlive = "onDemand",
            },
            Focus = new WidgetFocusSpec { StealOnShow = false, ActivateOnClick = true },
            Theme = new WidgetThemeSpec { Mode = "auto", FollowAccent = true, Surface = "acrylic" },
            Activation = new WidgetActivationSpec { LaunchOnStartup = false, ShowInTray = true },
            Permissions = new List<string> { "http-localhost", "notifications" },
        };
        tweak?.Invoke(manifest);
        return manifest;
    }

    /// <summary>放一个假 exe 并返回路径（内容不使用真实 PE，只为哈希可算）。</summary>
    public static string WriteFakeExecutable(string appDirectory)
    {
        var payload = Path.Combine(appDirectory, "payload");
        Directory.CreateDirectory(payload);
        var path = Path.Combine(payload, "app.exe");
        TestFiles.WriteRaw(path, "SDemo fake executable " + Guid.NewGuid().ToString("N"));
        return path;
    }

    public static string Sha256Of(string path) =>
        new FileHashVerifier().ComputeSha256(path) ?? throw new InvalidOperationException("无法计算哈希：" + path);

    public static void WriteAppJson(string appDirectory, AppManifest manifest) =>
        TestFiles.WriteJson(Path.Combine(appDirectory, "app.json"), manifest);

    public static void WriteWidgetJson(string appDirectory, WidgetManifest manifest) =>
        TestFiles.WriteJson(Path.Combine(appDirectory, "widget.json"), manifest);

    /// <summary>造一个最小 wasm 载荷：<c>payload\index.html</c>。</summary>
    public static void WriteWasmPayload(
        TempRoot temp,
        string appId,
        string entry = "index.html",
        string html = "<!doctype html><html><body>wasm widget</body></html>")
    {
        var payload = Path.Combine(temp.AppDir(appId), "payload");
        Directory.CreateDirectory(payload);
        TestFiles.WriteRaw(Path.Combine(payload, entry), html);
    }

    /// <summary>在临时 apps 目录造一个合法应用，返回写入的两份清单。</summary>
    public static (AppManifest App, WidgetManifest Widget) CreateApp(
        TempRoot temp,
        string appId,
        Action<AppManifest>? tweakApp = null,
        Action<WidgetManifest>? tweakWidget = null)
    {
        var directory = temp.AppDir(appId);
        Directory.CreateDirectory(directory);
        var executable = WriteFakeExecutable(directory);
        var app = App(appId, Sha256Of(executable));
        tweakApp?.Invoke(app);
        var widget = Widget(appId);
        tweakWidget?.Invoke(widget);
        WriteAppJson(directory, app);
        WriteWidgetJson(directory, widget);
        return (app, widget);
    }
}
