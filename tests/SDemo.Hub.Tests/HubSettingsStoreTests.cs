using SDemo.Hub;
using SDemo.Hub.Contracts;
using Xunit;

namespace SDemo.Hub.Tests;

public sealed class HubSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdemo-hub-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Approve_PersistsAcrossReload_AndIsCaseInsensitive()
    {
        var paths = new HubPaths(_root);
        var executable = Path.Combine(_root, "apps", "SDemo.Planner.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);

        var store = new HubSettingsStore(paths);
        Assert.False(store.IsApproved(executable));

        store.Approve(executable);

        var reloaded = new HubSettingsStore(paths);
        Assert.True(reloaded.IsApproved(executable.ToUpperInvariant()));
        Assert.Contains("approvedLaunchers", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Defaults_EnableAutostart_AndPersistUpdates()
    {
        var paths = new HubPaths(_root);
        var store = new HubSettingsStore(paths);
        Assert.True(store.Settings.LaunchAtLogin);
        Assert.False(store.Settings.StartHidden);
        Assert.Equal("auto", store.Settings.Theme);

        store.Update(settings =>
        {
            settings.LaunchAtLogin = false;
            settings.StartHidden = true;
            settings.Theme = "dark";
        });

        var reloaded = new HubSettingsStore(paths).Settings;
        Assert.False(reloaded.LaunchAtLogin);
        Assert.True(reloaded.StartHidden);
        Assert.Equal("dark", reloaded.Theme);
    }

    [Fact]
    public void Approve_DoesNotDuplicateEntries()
    {
        var paths = new HubPaths(_root);
        var executable = Path.Combine(_root, "SDemo.Planner.exe");
        var store = new HubSettingsStore(paths);

        store.Approve(executable);
        store.Approve(executable);

        Assert.Single(new HubSettingsStore(paths).Settings.ApprovedLaunchers);
    }

    [Fact]
    public void V2Settings_PersistAcrossReload()
    {
        var paths = new HubPaths(_root);
        var store = new HubSettingsStore(paths);
        Assert.False(store.Settings.HideFromCapture);
        Assert.Empty(store.Settings.HiddenApps);

        store.Update(settings =>
        {
            settings.HideFromCapture = true;
            settings.HiddenApps.Add("sdemo.notes");
            settings.ToggleWidgetsHotkey = "Ctrl+Alt+H";
            settings.ToggleClickThroughHotkey = "Ctrl+Alt+T";
        });

        var reloaded = new HubSettingsStore(paths).Settings;
        Assert.True(reloaded.HideFromCapture);
        Assert.Equal(["sdemo.notes"], reloaded.HiddenApps);
        Assert.Equal("Ctrl+Alt+H", reloaded.ToggleWidgetsHotkey);
        Assert.Equal("Ctrl+Alt+T", reloaded.ToggleClickThroughHotkey);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响断言。
        }
    }
}
