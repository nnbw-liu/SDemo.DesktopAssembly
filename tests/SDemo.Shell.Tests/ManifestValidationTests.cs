using SDemo.Shell.Contracts;
using SDemo.Shell.Discovery;

namespace SDemo.Shell.Tests;

/// <summary>清单校验（DA-12–DA-16 / DA-76 / DA-77）：合法、哈希、缺件、非法组合、版本、路径逃逸。</summary>
public sealed class ManifestValidationTests
{
    private static AppLoadResult Load(
        TempRoot temp,
        string appId,
        Action<AppManifest>? tweakApp = null,
        Action<WidgetManifest>? tweakWidget = null,
        string? directoryNameForIdentity = null)
    {
        TestManifests.CreateApp(temp, appId, tweakApp, tweakWidget);
        var loader = new ManifestLoader(new FileHashVerifier());
        return loader.Load(temp.AppDir(appId), directoryNameForIdentity);
    }

    private static AppLoadResult LoadRaw(TempRoot temp, string appId)
    {
        var loader = new ManifestLoader(new FileHashVerifier());
        return loader.Load(temp.AppDir(appId));
    }

    private static int IssueCount(AppLoadResult result) =>
        result.AppValidation.Issues.Count + result.WidgetValidation.Issues.Count;

    private static bool HasIssue(AppLoadResult result, string code) =>
        result.AppValidation.Issues.Concat(result.WidgetValidation.Issues)
            .Any(issue => string.Equals(issue.Code, code, StringComparison.Ordinal));

    [Fact]
    public void ValidNativeApp_IsLaunchable()
    {
        using var temp = new TempRoot("manifest-valid");
        var result = Load(temp, "sdemo.alpha");

        Assert.Equal(ManifestStatus.Valid, result.Status);
        Assert.True(result.IsLaunchable);
        Assert.True(result.HashValid);
        Assert.True(result.ExecutableResolved);
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.Empty(result.WidgetValidation.Errors);
        Assert.Empty(result.AppValidation.Errors);
        Assert.Null(result.ToHostError());
    }

    [Fact]
    public void HashMismatch_InvalidatesHashAndLaunchability()
    {
        using var temp = new TempRoot("manifest-hash");
        var result = Load(temp, "sdemo.alpha", app => app.Sha256 = new string('0', 64));

        Assert.False(result.HashValid);
        Assert.False(result.IsLaunchable);
        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.True(HasIssue(result, ManifestIssueCodes.HashMismatch));
        Assert.NotNull(result.HashDetail);
    }

    [Fact]
    public void MissingWidgetJson_InvalidWithIssue()
    {
        using var temp = new TempRoot("manifest-no-widget");
        TestManifests.CreateApp(temp, "sdemo.alpha");
        File.Delete(Path.Combine(temp.AppDir("sdemo.alpha"), "widget.json"));

        var result = LoadRaw(temp, "sdemo.alpha");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.Required));
        Assert.NotEmpty(result.WidgetValidation.Errors);
    }

    [Fact]
    public void MissingAppJson_InvalidWithIssue()
    {
        using var temp = new TempRoot("manifest-no-app");
        TestManifests.CreateApp(temp, "sdemo.alpha");
        File.Delete(Path.Combine(temp.AppDir("sdemo.alpha"), "app.json"));

        var result = LoadRaw(temp, "sdemo.alpha");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.Required));
        Assert.NotEmpty(result.AppValidation.Errors);
    }

    [Fact]
    public void MalformedJson_InvalidWithoutThrowing()
    {
        using var temp = new TempRoot("manifest-broken-json");
        TestManifests.CreateApp(temp, "sdemo.alpha");
        TestFiles.WriteRaw(Path.Combine(temp.AppDir("sdemo.alpha"), "app.json"), "{ this is not json ");

        var result = LoadRaw(temp, "sdemo.alpha");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.JsonInvalid));
    }

    [Fact]
    public void WasmRenderer_IsUnsupportedNotInvalid()
    {
        using var temp = new TempRoot("manifest-wasm");
        var result = Load(
            temp,
            "sdemo.alpha",
            tweakWidget: widget =>
            {
                widget.Renderer = WidgetRendering.Wasm;
                widget.Wasm = new WidgetWasmSpec { WebRoot = "payload", Entry = "index.html" };
                widget.Native = null;
            });

        Assert.Equal(ManifestStatus.Unsupported, result.Status);
        Assert.Equal(ManifestStatus.Unsupported, result.WidgetValidation.Status);
        Assert.False(result.IsLaunchable);
        Assert.Empty(result.WidgetValidation.Errors);
        Assert.True(HasIssue(result, ManifestIssueCodes.RendererUnsupported));
    }

    [Fact]
    public void DesktopLayerWithNativeRenderer_IsInvalid()
    {
        using var temp = new TempRoot("manifest-desktop-native");
        var result = Load(temp, "sdemo.alpha", tweakWidget: widget => widget.Layer = WidgetRendering.Desktop);

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.LayerInvalid));
        Assert.NotEmpty(result.WidgetValidation.Errors);
    }

    [Fact]
    public void WidgetHostApiVersionTooHigh_IsUnsupported()
    {
        using var temp = new TempRoot("manifest-widget-api");
        var result = Load(temp, "sdemo.alpha", tweakWidget: widget => widget.HostApiVersion = "2.0");

        Assert.Equal(ManifestStatus.Unsupported, result.Status);
        Assert.Equal(ManifestStatus.Unsupported, result.WidgetValidation.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.HostApiVersion));
    }

    [Fact]
    public void MinHostApiVersionTooHigh_IsUnsupported()
    {
        using var temp = new TempRoot("manifest-min-api");
        var result = Load(temp, "sdemo.alpha", app => app.MinHostApiVersion = "2.0");

        Assert.Equal(ManifestStatus.Unsupported, result.Status);
        Assert.Equal(ManifestStatus.Unsupported, result.AppValidation.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.HostApiVersion));
    }

    [Fact]
    public void ExecutableParentTraversal_IsInvalid()
    {
        using var temp = new TempRoot("manifest-exec-parent");
        var result = Load(temp, "sdemo.alpha", app => app.Executable = "../x.exe");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.False(result.ExecutableResolved);
        Assert.True(HasIssue(result, ManifestIssueCodes.ExecutableMissing));
    }

    [Fact]
    public void ExecutableAbsolutePath_IsInvalid()
    {
        using var temp = new TempRoot("manifest-exec-absolute");
        var result = Load(temp, "sdemo.alpha", app => app.Executable = @"C:\Windows\notepad.exe");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.False(result.ExecutableResolved);
        Assert.True(HasIssue(result, ManifestIssueCodes.ExecutableMissing));
    }

    [Fact]
    public void WidgetJsonArray_IsInvalid()
    {
        using var temp = new TempRoot("manifest-widget-array");
        TestManifests.CreateApp(temp, "sdemo.alpha");
        TestFiles.WriteRaw(Path.Combine(temp.AppDir("sdemo.alpha"), "widget.json"), "[]");

        var result = LoadRaw(temp, "sdemo.alpha");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.JsonInvalid));
    }

    [Fact]
    public void AppIdNotMatchingDirectory_IsInvalid()
    {
        using var temp = new TempRoot("manifest-id-mismatch");
        var directory = temp.AppDir("sdemo.alpha");
        Directory.CreateDirectory(directory);
        var executable = TestManifests.WriteFakeExecutable(directory);
        var app = TestManifests.App("sdemo.beta", TestManifests.Sha256Of(executable));
        var widget = TestManifests.Widget("sdemo.beta");
        TestManifests.WriteAppJson(directory, app);
        TestManifests.WriteWidgetJson(directory, widget);

        var result = LoadRaw(temp, "sdemo.alpha");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.AppIdMismatch));
    }

    [Fact]
    public void DirectoryNameOverride_BypassesAppIdCheck()
    {
        using var temp = new TempRoot("manifest-id-bypass");
        var directory = temp.AppDir("sdemo.alpha");
        Directory.CreateDirectory(directory);
        var executable = TestManifests.WriteFakeExecutable(directory);
        var app = TestManifests.App("sdemo.beta", TestManifests.Sha256Of(executable));
        var widget = TestManifests.Widget("sdemo.beta");
        TestManifests.WriteAppJson(directory, app);
        TestManifests.WriteWidgetJson(directory, widget);

        var loader = new ManifestLoader(new FileHashVerifier());
        var strict = loader.Load(directory);
        Assert.Equal(ManifestStatus.Invalid, strict.Status);

        var staged = loader.Load(directory, "sdemo.beta");
        Assert.Equal(ManifestStatus.Valid, staged.Status);
        Assert.True(staged.IsLaunchable);
        Assert.Equal(0, IssueCount(staged));
    }

    [Fact]
    public void BadSingleInstanceMutex_IsInvalid()
    {
        using var temp = new TempRoot("manifest-mutex");
        var result = Load(temp, "sdemo.alpha", app => app.SingleInstanceMutex = "SDemo.Shell");

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.Pattern));
    }

    [Fact]
    public void StartupTimeoutOutOfRange_IsInvalid()
    {
        using var temp = new TempRoot("manifest-timeout");
        var result = Load(temp, "sdemo.alpha", app =>
        {
            app.StartupTimeoutMs = 100;
            app.IdleTimeoutMs = -1;
        });

        Assert.Equal(ManifestStatus.Invalid, result.Status);
        Assert.False(result.IsLaunchable);
        Assert.True(HasIssue(result, ManifestIssueCodes.Range));
        Assert.NotEmpty(result.AppValidation.Errors);
    }
}
