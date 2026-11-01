using System.IO;
using Microsoft.Web.WebView2.Core;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Wpf.Widgets;

/// <summary>
/// 桌面端所有 WebView2 共用同一个 <see cref="CoreWebView2Environment"/>（DA-58）：
/// 不出现 N 套 browser 进程与缓存目录；user data 放 <c>%LOCALAPPDATA%\...\webview2</c>。
/// </summary>
public static class WasmEnvironment
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _shared;
    private static string? _version;
    private static string? _error;

    /// <summary>运行时探测结论（诊断面板 / 首次启动提示用）。</summary>
    public static string Status => _error is null
        ? (_version is null ? "未初始化" : $"WebView2 Runtime {_version}")
        : $"不可用：{_error}";

    public static bool IsAvailable => _error is null;

    public static Task<CoreWebView2Environment> GetAsync(string userDataFolder, IShellLog? log = null)
    {
        lock (Gate)
        {
            return _shared ??= CreateAsync(userDataFolder, log ?? NullShellLog.Instance);
        }
    }

    /// <summary>只读探测：不创建环境，只问运行时版本（用于面板提示）。</summary>
    public static bool TryProbe(out string status)
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            _version = version;
            _error = null;
            status = $"WebView2 Runtime {version}";
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            status = $"不可用：{ex.Message}";
            return false;
        }
    }

    private static async Task<CoreWebView2Environment> CreateAsync(string userDataFolder, IShellLog log)
    {
        try
        {
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder).ConfigureAwait(false);
            TryProbe(out var status);
            log.Info($"wasm 运行时就绪：{status}（user data {userDataFolder}）");
            return environment;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            log.Error($"创建 WebView2 环境失败：{ex.Message}", ex);
            throw;
        }
    }
}
