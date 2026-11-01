using System.Text.Json;
using SDemo.Shell.Contracts;
using SDemo.Shell.Discovery;
using SDemo.Shell.Registry;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Tests;

/// <summary>注册表（DA-01 / DA-14 / DA-15 / DA-19 / DA-75 / DA-90）：扫描、坏一个不影响其它、上限、重复、revision。</summary>
public sealed class AppRegistryTests
{
    private sealed class RegistryShell : IDisposable
    {
        public RegistryShell(string label = "registry")
        {
            Temp = new TempRoot(label);
            Store = new ShellStateStore(Temp.Paths);
            Store.Load();
            Registry = new AppRegistry(Temp.Paths, Store, new ManifestLoader(new FileHashVerifier()));
        }

        public TempRoot Temp { get; }

        public ShellStateStore Store { get; }

        public AppRegistry Registry { get; }

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public void Scan_TwoValidApps_AreLaunchableAndStopped()
    {
        using var shell = new RegistryShell("registry-two");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");
        TestManifests.CreateApp(shell.Temp, "sdemo.beta");

        shell.Registry.Scan();

        Assert.Equal(2, shell.Registry.Entries.Count);
        Assert.Equal(0, shell.Registry.InvalidCount);
        foreach (var entry in shell.Registry.Entries)
        {
            Assert.True(entry.IsLaunchable);
            Assert.True(entry.HashValid);
            Assert.True(entry.Enabled);
            Assert.True(entry.Visible);
            Assert.Equal(AppStates.Stopped, entry.State);
            Assert.Null(entry.LastError);
        }
    }

    [Fact]
    public void Scan_OneBadManifest_DoesNotAffectTheOther()
    {
        using var shell = new RegistryShell("registry-bad");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");
        TestManifests.CreateApp(shell.Temp, "sdemo.beta", app => app.Sha256 = new string('0', 64));

        shell.Registry.Scan();

        var alpha = shell.Registry.Find("sdemo.alpha")!;
        var beta = shell.Registry.Find("sdemo.beta")!;

        Assert.Equal(2, shell.Registry.Entries.Count);
        Assert.True(alpha.IsLaunchable);
        Assert.Equal(AppStates.Stopped, alpha.State);

        Assert.False(beta.IsLaunchable);
        Assert.False(beta.HashValid);
        Assert.Equal(AppStates.Invalid, beta.State);
        Assert.NotNull(beta.LastError);
        Assert.Equal(ErrorCodes.AppInvalid, beta.LastError!.Code);

        Assert.Equal(1, shell.Registry.InvalidCount);
    }

    [Fact]
    public void Scan_ThirdValidApp_IsBlockedByComponentLimit()
    {
        using var shell = new RegistryShell("registry-limit");
        shell.Store.Settings.Components.Limit = 2;
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");
        TestManifests.CreateApp(shell.Temp, "sdemo.beta");
        TestManifests.CreateApp(shell.Temp, "sdemo.gamma");

        shell.Registry.Scan();

        var alpha = shell.Registry.Find("sdemo.alpha")!;
        var beta = shell.Registry.Find("sdemo.beta")!;
        var gamma = shell.Registry.Find("sdemo.gamma")!;

        Assert.True(alpha.IsLaunchable);
        Assert.True(beta.IsLaunchable);

        Assert.True(gamma.BlockedByLimit);
        Assert.False(gamma.IsLaunchable);
        Assert.Equal(AppStates.Invalid, gamma.State);
        Assert.Contains("上限", gamma.BlockReason!, StringComparison.Ordinal);
        Assert.NotNull(gamma.LastError);
        Assert.Contains("上限", gamma.LastError!.Message, StringComparison.Ordinal);

        Assert.Equal(1, shell.Registry.InvalidCount);
        Assert.Equal(2, shell.Registry.ComponentLimit);
    }

    [Fact]
    public void Scan_DuplicateWidgetId_RejectsLaterEntry()
    {
        using var shell = new RegistryShell("registry-duplicate");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");
        TestManifests.CreateApp(shell.Temp, "sdemo.beta", tweakWidget: widget => widget.Id = "sdemo.alpha.widget");

        shell.Registry.Scan();

        var alpha = shell.Registry.Find("sdemo.alpha")!;
        var beta = shell.Registry.Find("sdemo.beta")!;

        Assert.Equal(2, shell.Registry.Entries.Count);
        Assert.True(alpha.IsLaunchable);
        Assert.False(beta.IsLaunchable);
        Assert.Equal(AppStates.Invalid, beta.State);
        Assert.NotNull(beta.LastError);
        Assert.Equal(1, shell.Registry.InvalidCount);
    }

    [Fact]
    public void SetEnabled_BumpsRevision_SetVisibleDoesNot()
    {
        using var shell = new RegistryShell("registry-revision");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");
        shell.Registry.Scan();

        var before = shell.Registry.Revision;

        Assert.True(shell.Registry.SetEnabled("sdemo.alpha.widget", false));
        Assert.False(shell.Registry.Find("sdemo.alpha")!.Enabled);
        Assert.Equal(before + 1, shell.Registry.Revision);

        var afterEnable = shell.Registry.Revision;
        Assert.True(shell.Registry.SetVisible("sdemo.alpha.widget", false));
        Assert.False(shell.Registry.Find("sdemo.alpha")!.Visible);
        Assert.Equal(afterEnable, shell.Registry.Revision);

        using var saved = TestFiles.ReadJson(shell.Temp.Paths.WidgetsFilePath);
        var widget = saved.RootElement.GetProperty("widgets").GetProperty("sdemo.alpha.widget");
        Assert.False(widget.GetProperty("enabled").GetBoolean());
        Assert.False(widget.GetProperty("visible").GetBoolean());
    }

    [Fact]
    public void Scan_FillsWidgetStateForNewWidget()
    {
        using var shell = new RegistryShell("registry-widget-state");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");

        shell.Registry.Scan();

        var state = shell.Store.FindWidgetState("sdemo.alpha.widget");
        Assert.NotNull(state);
        Assert.True(state!.Visible);
        Assert.True(state.Enabled);

        Assert.True(shell.Store.SaveWidgets());
        using var saved = TestFiles.ReadJson(shell.Temp.Paths.WidgetsFilePath);
        Assert.True(saved.RootElement.GetProperty("widgets").TryGetProperty("sdemo.alpha.widget", out _));
    }

    [Fact]
    public void Scan_PersistsWidgetsFileAutomatically()
    {
        using var shell = new RegistryShell("registry-widget-persist");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");

        shell.Registry.Scan();

        // shell-storage §7：首次发现组件时把默认状态落盘，不等宿主退出。
        Assert.True(File.Exists(shell.Temp.Paths.WidgetsFilePath));
        using var saved = TestFiles.ReadJson(shell.Temp.Paths.WidgetsFilePath);
        Assert.True(saved.RootElement.GetProperty("widgets").TryGetProperty("sdemo.alpha.widget", out _));
    }

    [Fact]
    public void Scan_RemovedApp_DisappearsFromEntries()
    {
        using var shell = new RegistryShell("registry-removed");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");

        shell.Registry.Scan();
        Assert.Single(shell.Registry.Entries);

        Directory.Delete(shell.Temp.AppDir("sdemo.alpha"), recursive: true);
        shell.Registry.Scan();

        Assert.Empty(shell.Registry.Entries);
        Assert.Null(shell.Registry.Find("sdemo.alpha"));
    }

    [Fact]
    public void FindByWidget_ReturnsOwningEntry()
    {
        using var shell = new RegistryShell("registry-find");
        TestManifests.CreateApp(shell.Temp, "sdemo.alpha");

        shell.Registry.Scan();

        var entry = shell.Registry.FindByWidget("sdemo.alpha.widget");
        Assert.NotNull(entry);
        Assert.Equal("sdemo.alpha", entry!.AppId);
        Assert.Null(shell.Registry.FindByWidget("sdemo.missing.widget"));
    }
}
