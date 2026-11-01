using System.Text.Json;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Discovery;
using SDemo.Shell.Registry;
using SDemo.Shell.Settings;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Tests;

/// <summary>权限（DA-66）：默认最小权限、首次询问、拒绝后不升级、未知权限只展示。</summary>
public sealed class PermissionPolicyTests
{
    [Fact]
    public void CreateInitial_GrantsLocalhostAndAsksNotifications()
    {
        var decisions = PermissionPolicy.CreateInitial(["http-localhost", "notifications", "unknown-permission"]);

        Assert.Equal(PermissionPolicy.Granted, decisions["http-localhost"]);
        Assert.Equal(PermissionPolicy.Ask, decisions["notifications"]);
        Assert.False(decisions.ContainsKey("unknown-permission"));
    }

    [Fact]
    public void DecisionFor_WithoutStoredValue_UsesDefault()
    {
        var state = new WidgetState();
        Assert.True(PermissionPolicy.IsGranted(state, PermissionPolicy.HttpLocalhost));
        Assert.True(PermissionPolicy.NeedsPrompt(state, PermissionPolicy.Notifications));
        Assert.False(PermissionPolicy.IsGranted(state, "unknown-permission"));
    }

    [Fact]
    public void SetDecision_PersistsAndReverses()
    {
        var state = new WidgetState();
        Assert.True(PermissionPolicy.SetDecision(state, PermissionPolicy.Notifications, true));
        Assert.True(PermissionPolicy.IsGranted(state, PermissionPolicy.Notifications));
        Assert.False(PermissionPolicy.NeedsPrompt(state, PermissionPolicy.Notifications));

        Assert.True(PermissionPolicy.SetDecision(state, PermissionPolicy.Notifications, false));
        Assert.False(PermissionPolicy.IsGranted(state, PermissionPolicy.Notifications));
        Assert.False(PermissionPolicy.SetDecision(state, PermissionPolicy.Notifications, false));
    }

    [Fact]
    public void Normalize_ForcesLocalhostAndDropsUnknown()
    {
        var state = new WidgetState
        {
            Permissions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["http-localhost"] = PermissionPolicy.Denied,
                ["notifications"] = "garbage",
                ["unknown-permission"] = PermissionPolicy.Granted,
            },
        };

        PermissionPolicy.Normalize(state);

        Assert.Equal(PermissionPolicy.Granted, state.Permissions!["http-localhost"]);
        Assert.Equal(PermissionPolicy.Ask, state.Permissions!["notifications"]);
        Assert.False(state.Permissions!.ContainsKey("unknown-permission"));
    }

    [Fact]
    public void SetDecision_IgnoresUnknownPermission()
    {
        var state = new WidgetState();
        Assert.False(PermissionPolicy.SetDecision(state, "not-supported", true));
        Assert.Null(state.Permissions);
    }
}

/// <summary>组件诊断（DA-09 / DA-69）：状态、端口、最后错误、日志路径、config、权限、热键。</summary>
public sealed class WidgetDiagnosticsTests
{
    [Fact]
    public void BuildWidget_IncludesOperationalFacts()
    {
        using var temp = new TempRoot("diagnostics");
        var (app, _) = TestManifests.CreateApp(temp, "sdemo.alpha");
        var loader = new ManifestLoader(new FileHashVerifier());
        var load = loader.Load(temp.AppDir("sdemo.alpha"));
        var state = new WidgetState();
        var entry = new AppEntry(load, state)
        {
            State = AppStates.Ready,
            Runtime = new RuntimeFile { Pid = 4242, Port = 5150, Token = "t" },
            Hwnd = 9_001,
            LastError = HostError.Create(ErrorCodes.WidgetWindowLost, "窗口句柄失效"),
        };

        var text = WidgetDiagnostics.BuildWidget(entry, temp.Paths);

        Assert.Contains("sdemo.alpha", text);
        Assert.Contains("pid=4242", text);
        Assert.Contains("port=5150", text);
        Assert.Contains("hwnd=9001", text);
        Assert.Contains(ErrorCodes.WidgetWindowLost, text);
        Assert.Contains("宿主日志", text);
        Assert.Contains("http-localhost", text);
        Assert.Contains(app.Id, text);
    }

    [Fact]
    public void BuildOverview_ListsEntriesAndRecentLog()
    {
        using var temp = new TempRoot("diagnostics-overview");
        TestManifests.CreateApp(temp, "sdemo.alpha");
        var loader = new ManifestLoader(new FileHashVerifier());
        var load = loader.Load(temp.AppDir("sdemo.alpha"));
        var entry = new AppEntry(load, new WidgetState());
        var log = new RecordingLog();

        var text = WidgetDiagnostics.BuildOverview([entry], temp.Paths, log, "WebView2 Runtime 156.0");

        Assert.Contains("DesktopAssembly", text);
        Assert.Contains("sdemo.alpha", text);
        Assert.Contains("WebView2 Runtime 156.0", text);
        Assert.Contains("最近日志", text);
        Assert.Contains("测试日志", text);
    }

    private sealed class RecordingLog : IShellLog
    {
        public string FilePath => "memory";

        public IReadOnlyList<ShellLogEntry> Recent { get; } =
            new[] { new ShellLogEntry(DateTimeOffset.Now, ShellLogLevel.Info, "测试日志", null, null) };

        public void Log(ShellLogLevel level, string message, Exception? exception = null, string? requestId = null)
        {
        }
    }
}

/// <summary>P2 状态文件：privacy / hotkey override / 权限规范化 / config 默认值。</summary>
public sealed class ShellStateP2Tests
{
    [Fact]
    public void Settings_NormalizeCanonicalizesHotkeysAndDropsInvalid()
    {
        var settings = new SettingsFile
        {
            Hotkeys = new HotkeySettings
            {
                ToggleWidgets = "ctrl+shift+w",
                ToggleClickThrough = "T",
            },
        };

        settings.Normalize();

        Assert.Equal("Ctrl+Shift+W", settings.Hotkeys.ToggleWidgets);
        Assert.Null(settings.Hotkeys.ToggleClickThrough);
    }

    [Fact]
    public void Settings_RoundTripPreservesPrivacy()
    {
        using var temp = new TempRoot("settings-privacy");
        var store = new ShellStateStore(temp.Paths);
        store.Load();
        store.Settings.Privacy.HideFromCapture = true;
        Assert.True(store.SaveSettings());

        var reloaded = new ShellStateStore(temp.Paths);
        reloaded.Load();
        Assert.True(reloaded.Settings.Privacy.HideFromCapture);
    }

    [Fact]
    public void WidgetState_InvalidHotkeyOverrideIsClearedOnLoad()
    {
        using var temp = new TempRoot("state-hotkey");
        var store = new ShellStateStore(temp.Paths);
        store.Load();
        var manifest = TestManifests.Widget("sdemo.alpha");
        var state = store.EnsureWidgetState(manifest.Id, manifest);
        state.HotkeyOverride = "Nonsense";
        Assert.True(store.SaveWidgets());

        var reloaded = new ShellStateStore(temp.Paths);
        reloaded.Load();
        var reloadedState = reloaded.FindWidgetState(manifest.Id)!;
        Assert.Null(reloadedState.HotkeyOverride);
    }

    [Fact]
    public void EnsureWidgetState_AppliesConfigDefaultsAndPermissions()
    {
        using var temp = new TempRoot("state-config");
        var store = new ShellStateStore(temp.Paths);
        store.Load();
        var manifest = TestManifests.Widget("sdemo.alpha", widget =>
        {
            widget.Config = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    maxItems = new { type = "integer", minimum = 3, maximum = 30, @default = 8 },
                },
            });
            widget.Permissions = ["http-localhost", "notifications"];
        });

        var state = store.EnsureWidgetState(manifest.Id, manifest);

        Assert.NotNull(state.Config);
        Assert.Equal(8, state.Config!["maxItems"].GetInt32());
        Assert.Equal(PermissionPolicy.Granted, state.Permissions!["http-localhost"]);
        Assert.Equal(PermissionPolicy.Ask, state.Permissions["notifications"]);

        var again = store.EnsureWidgetState(manifest.Id, manifest);
        again.Config!["maxItems"] = JsonSerializer.SerializeToElement(12, ShellJson.File);
        var merged = store.EnsureWidgetState(manifest.Id, manifest);
        Assert.Equal(12, merged.Config!["maxItems"].GetInt32());
    }

    [Fact]
    public void DisplayLayerDefaults_DesktopWidgetIsNotTopmost()
    {
        using var temp = new TempRoot("state-desktop");
        var store = new ShellStateStore(temp.Paths);
        store.Load();
        var manifest = TestManifests.Widget("sdemo.alpha", widget =>
        {
            widget.Renderer = WidgetRendering.Wasm;
            widget.Wasm = new WidgetWasmSpec();
            widget.Native = null;
            widget.Layer = WidgetRendering.Desktop;
            widget.Behavior!.AlwaysOnTopDefault = true;
        });

        var state = store.EnsureWidgetState(manifest.Id, manifest);

        Assert.Equal(WidgetRendering.Desktop, state.Layer);
        Assert.False(state.AlwaysOnTop);
    }
}
