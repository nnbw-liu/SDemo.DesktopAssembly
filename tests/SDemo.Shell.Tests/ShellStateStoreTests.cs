using System.Text.Json;
using SDemo.Shell.Contracts;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Tests;

/// <summary>状态文件（shell-storage）：默认值、LF/2 空格、.bak、损坏隔离、只读、未知字段、坏条目跳过。</summary>
public sealed class ShellStateStoreTests
{
    private static ShellStateStore NewStore(TempRoot temp)
    {
        var store = new ShellStateStore(temp.Paths);
        store.Load();
        return store;
    }

    [Fact]
    public void Load_WithoutFiles_UsesDefaults()
    {
        using var temp = new TempRoot("state-default");
        var store = NewStore(temp);

        Assert.False(store.IsReadOnly);
        Assert.Empty(store.Widgets.Widgets);
        Assert.Equal(ShellConstants.DefaultComponentsLimit, store.Settings.Components.Limit);
        Assert.True(store.Settings.Startup.LaunchAtLogin);
        Assert.True(store.Settings.Startup.StartHidden);
        Assert.Equal("zh-CN", store.Settings.Language);
        Assert.Equal(ShellConstants.ShellStateSchemaVersion, store.Widgets.SchemaVersion);
        Assert.Empty(store.Warnings);
    }

    [Fact]
    public void SaveWidgets_WritesLfAndTwoSpaceIndent()
    {
        using var temp = new TempRoot("state-format");
        var store = NewStore(temp);
        store.Widgets.Widgets["sdemo.alpha.widget"] = new WidgetState { Visible = false };

        Assert.True(store.SaveWidgets());
        var text = File.ReadAllText(temp.Paths.WidgetsFilePath);

        Assert.StartsWith("{\n  \"schemaVersion\"", text);
        Assert.Contains("\n  \"widgets\"", text);
        Assert.DoesNotContain("\r", text);
        Assert.True(File.Exists(temp.Paths.WidgetsFilePath));
    }

    [Fact]
    public void SaveWidgets_SecondSaveCreatesBackup()
    {
        using var temp = new TempRoot("state-bak");
        var store = NewStore(temp);
        store.Widgets.Widgets["sdemo.alpha.widget"] = new WidgetState();

        Assert.True(store.SaveWidgets());
        Assert.False(File.Exists(temp.Paths.WidgetsFilePath + ".bak"));

        Assert.True(store.SaveWidgets());
        Assert.True(File.Exists(temp.Paths.WidgetsFilePath + ".bak"));
        Assert.NotEmpty(File.ReadAllText(temp.Paths.WidgetsFilePath + ".bak"));
    }

    [Fact]
    public void CorruptMainFile_IsQuarantinedAndRestoredFromBackup()
    {
        using var temp = new TempRoot("state-corrupt");
        var store = NewStore(temp);
        store.Widgets.Widgets["sdemo.alpha.widget"] = new WidgetState { Visible = false };

        Assert.True(store.SaveWidgets());
        Assert.True(store.SaveWidgets());

        TestFiles.WriteRaw(temp.Paths.WidgetsFilePath, "{ this is not json ");

        var reloaded = new ShellStateStore(temp.Paths);
        reloaded.Load();

        Assert.False(File.Exists(temp.Paths.WidgetsFilePath));
        var quarantined = Directory.GetFiles(temp.Paths.AppDataRoot, "widgets.corrupt-*.json");
        Assert.Single(quarantined);
        Assert.True(reloaded.Widgets.Widgets.ContainsKey("sdemo.alpha.widget"));
        Assert.False(reloaded.Widgets.Widgets["sdemo.alpha.widget"].Visible);
        Assert.False(reloaded.IsReadOnly);
    }

    [Fact]
    public void HigherSchemaVersion_EntersReadOnlyAndRefusesSave()
    {
        using var temp = new TempRoot("state-readonly");
        TestFiles.WriteRaw(
            temp.Paths.WidgetsFilePath,
            "{\n  \"schemaVersion\": 2,\n  \"widgets\": {}\n}");

        var store = NewStore(temp);

        Assert.True(store.IsReadOnly);
        Assert.Contains(store.Warnings, warning => warning.Contains("schemaVersion", StringComparison.Ordinal));

        var before = File.ReadAllText(temp.Paths.WidgetsFilePath);
        Assert.False(store.SaveWidgets());
        Assert.Equal(before, File.ReadAllText(temp.Paths.WidgetsFilePath));
    }

    [Fact]
    public void UnknownFields_SurviveReadWrite()
    {
        using var temp = new TempRoot("state-extra");
        TestFiles.WriteRaw(
            temp.Paths.WidgetsFilePath,
            """
            {
              "schemaVersion": 1,
              "customRoot": "keep-me",
              "widgets": {
                "sdemo.alpha.widget": {
                  "visible": false,
                  "customWidget": { "n": 7 }
                }
              }
            }
            """);

        var store = NewStore(temp);

        Assert.True(store.Widgets.Extra!.ContainsKey("customRoot"));
        var state = store.Widgets.Widgets["sdemo.alpha.widget"];
        Assert.False(state.Visible);
        Assert.True(state.Extra!.ContainsKey("customWidget"));

        Assert.True(store.SaveWidgets());
        using var saved = TestFiles.ReadJson(temp.Paths.WidgetsFilePath);

        Assert.Equal("keep-me", saved.RootElement.GetProperty("customRoot").GetString());
        var savedWidget = saved.RootElement.GetProperty("widgets").GetProperty("sdemo.alpha.widget");
        Assert.Equal(7, savedWidget.GetProperty("customWidget").GetProperty("n").GetInt32());
        Assert.False(savedWidget.GetProperty("visible").GetBoolean());
    }

    [Fact]
    public void InvalidWidgetEntry_IsSkippedButKeptOnSave()
    {
        using var temp = new TempRoot("state-bad-entry");
        TestFiles.WriteRaw(
            temp.Paths.WidgetsFilePath,
            """
            {
              "schemaVersion": 1,
              "widgets": {
                "bad.entry": { "opacity": "abc" },
                "sdemo.alpha.widget": { "visible": false }
              }
            }
            """);

        var store = NewStore(temp);

        Assert.False(store.Widgets.Widgets.ContainsKey("bad.entry"));
        Assert.True(store.Widgets.Widgets.ContainsKey("sdemo.alpha.widget"));
        Assert.Contains(store.Warnings, warning => warning.Contains("bad.entry", StringComparison.Ordinal));

        Assert.True(store.SaveWidgets());
        using var saved = TestFiles.ReadJson(temp.Paths.WidgetsFilePath);
        var widgets = saved.RootElement.GetProperty("widgets");

        Assert.True(widgets.TryGetProperty("bad.entry", out var bad));
        Assert.Equal("abc", bad.GetProperty("opacity").GetString());
        Assert.True(widgets.TryGetProperty("sdemo.alpha.widget", out _));
    }

    [Fact]
    public void EnsureWidgetState_CreatesManifestDefaults()
    {
        using var temp = new TempRoot("state-ensure");
        var store = NewStore(temp);
        var manifest = TestManifests.Widget("sdemo.alpha", widget =>
        {
            widget.Behavior!.AlwaysOnTopDefault = false;
            widget.Behavior.Opacity = new WidgetOpacitySpec { Default = 0.8, Min = 0.5 };
            widget.Position = new WidgetPositionSpec { Corner = "bottom-left", OffsetX = 48, OffsetY = 16 };
        });

        var state = store.EnsureWidgetState("sdemo.alpha.widget", manifest);

        Assert.True(state.Enabled);
        Assert.True(state.Visible);
        Assert.False(state.AlwaysOnTop);
        Assert.Equal(WidgetRendering.Floating, state.Layer);
        Assert.Equal(0.8, state.Opacity);
        Assert.Equal("bottom-left", state.Anchor.Corner);
        Assert.Equal(48, state.Anchor.OffsetX);
        Assert.Equal(16, state.Anchor.OffsetY);
        Assert.NotNull(state.Permissions);
        Assert.Equal("granted", state.Permissions!["http-localhost"]);
        Assert.Equal("ask", state.Permissions["notifications"]);

        var again = store.EnsureWidgetState("sdemo.alpha.widget", manifest);
        Assert.Same(state, again);
    }

    [Fact]
    public void Settings_ComponentLimit_IsClampedDuringLoad()
    {
        using var temp = new TempRoot("state-settings");
        var settings = new SettingsFile
        {
            Components = new ComponentSettings { Limit = 99 },
            Logging = new LoggingSettings { KeepDays = 999, Level = "verbose" },
        };
        TestFiles.WriteJson(temp.Paths.SettingsFilePath, settings);

        var store = NewStore(temp);

        Assert.Equal(ShellConstants.MaxComponentsLimit, store.Settings.Components.Limit);
        Assert.Equal(90, store.Settings.Logging.KeepDays);
        Assert.Equal("info", store.Settings.Logging.Level);
    }
}
