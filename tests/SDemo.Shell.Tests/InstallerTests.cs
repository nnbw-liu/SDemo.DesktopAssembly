using System.IO.Compression;
using SDemo.Shell.Contracts;
using SDemo.Shell.Discovery;
using SDemo.Shell.Install;
using SDemo.Shell.Registry;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Tests;

/// <summary>安装 / 卸载（DA-28 / DA-98）：先校验后落盘、zip slip、卸载只删程序目录。</summary>
public sealed class InstallerTests
{
    private const string AppId = "sdemo.install";

    private sealed class InstallerShell : IDisposable
    {
        public InstallerShell(string label)
        {
            Temp = new TempRoot(label);
            Store = new ShellStateStore(Temp.Paths);
            Store.Load();
            Loader = new ManifestLoader(new FileHashVerifier());
            Registry = new AppRegistry(Temp.Paths, Store, Loader);
            Installer = new AppInstaller(Temp.Paths, Registry, Loader);
        }

        public TempRoot Temp { get; }

        public ShellStateStore Store { get; }

        public ManifestLoader Loader { get; }

        public AppRegistry Registry { get; }

        public AppInstaller Installer { get; }

        public void Dispose() => Temp.Dispose();
    }

    private static string NewPackageDirectory(TempRoot temp)
    {
        var source = Path.Combine(temp.Root, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        return source;
    }

    private static string CreatePackage(
        TempRoot temp,
        string appId,
        Action<AppManifest>? tweakApp = null)
    {
        var source = NewPackageDirectory(temp);
        var executable = TestManifests.WriteFakeExecutable(source);
        var app = TestManifests.App(appId, TestManifests.Sha256Of(executable));
        tweakApp?.Invoke(app);
        TestManifests.WriteAppJson(source, app);
        TestManifests.WriteWidgetJson(source, TestManifests.Widget(appId));

        var zip = Path.Combine(temp.Root, "package-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, relative);
        }

        return zip;
    }

    [Fact]
    public void InstallFromZip_CreatesAppDirectoryAndRegistersEntry()
    {
        using var shell = new InstallerShell("install-ok");
        var zip = CreatePackage(shell.Temp, AppId);

        var result = shell.Installer.InstallFromZip(zip);

        Assert.True(result.Success, result.Message);
        Assert.Equal(AppId, result.AppId);
        Assert.True(Directory.Exists(shell.Temp.AppDir(AppId)));
        Assert.True(File.Exists(Path.Combine(shell.Temp.AppDir(AppId), "app.json")));
        Assert.True(File.Exists(Path.Combine(shell.Temp.AppDir(AppId), "widget.json")));
        Assert.True(File.Exists(Path.Combine(shell.Temp.AppDir(AppId), "payload", "app.exe")));

        var entry = shell.Registry.Find(AppId);
        Assert.NotNull(entry);
        Assert.True(entry!.IsLaunchable);
    }

    [Fact]
    public void InstallFromZip_HashMismatch_DoesNotInstall()
    {
        using var shell = new InstallerShell("install-hash");
        var zip = CreatePackage(shell.Temp, AppId, app => app.Sha256 = new string('0', 64));

        var result = shell.Installer.InstallFromZip(zip);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.False(Directory.Exists(shell.Temp.AppDir(AppId)));
        Assert.Empty(shell.Registry.Entries);
    }

    [Fact]
    public void InstallFromZip_ZipSlipEntry_FailsWithoutInstalling()
    {
        using var shell = new InstallerShell("install-slip");

        var source = NewPackageDirectory(shell.Temp);
        var executable = TestManifests.WriteFakeExecutable(source);
        var app = TestManifests.App(AppId, TestManifests.Sha256Of(executable));
        TestManifests.WriteAppJson(source, app);
        TestManifests.WriteWidgetJson(source, TestManifests.Widget(AppId));

        var zip = Path.Combine(shell.Temp.Root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../evil.txt");
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, relative);
            }
        }

        var result = shell.Installer.InstallFromZip(zip);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(shell.Temp.AppDir(AppId)));
    }

    [Fact]
    public void InstallFromZip_MissingAppJson_Fails()
    {
        using var shell = new InstallerShell("install-no-manifest");
        var zip = Path.Combine(shell.Temp.Root, "empty.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        var result = shell.Installer.InstallFromZip(zip);

        Assert.False(result.Success);
        Assert.Empty(shell.Registry.Entries);
    }

    [Fact]
    public void Uninstall_RemovesProgramDirectoryButKeepsUserData()
    {
        using var shell = new InstallerShell("install-uninstall");
        var appId = "sdemo.tests-uninstall-" + Guid.NewGuid().ToString("N");
        var zip = CreatePackage(shell.Temp, appId);

        var result = shell.Installer.InstallFromZip(zip);
        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(shell.Temp.AppDir(appId)));

        // AppInstaller.Uninstall 只删程序目录；这里在真实的用户数据目录先建一份文件，
        // 用唯一 appId 避免碰到已有数据，并在 finally 里清理本次创建的目录。
        var dataDirectory = ShellPaths.AppDataDirectoryFor(appId);
        var createdDataDirectory = !Directory.Exists(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var marker = Path.Combine(dataDirectory, "keep.txt");
        try
        {
            File.WriteAllText(marker, "user data must survive uninstall");

            var uninstall = shell.Installer.Uninstall(appId);

            Assert.True(uninstall.Success);
            Assert.Equal(appId, uninstall.AppId);
            Assert.False(Directory.Exists(shell.Temp.AppDir(appId)));
            Assert.True(Directory.Exists(dataDirectory));
            Assert.True(File.Exists(marker));
            Assert.Equal(dataDirectory, uninstall.DataDirectory);
            Assert.Null(shell.Registry.Find(appId));
        }
        finally
        {
            if (createdDataDirectory)
            {
                try
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }
                catch
                {
                    // 清理失败忽略。
                }
            }
        }
    }
}
