using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.HelloWidget;

/// <summary>
/// 应用 → 宿主的 native 窗口登记（host-protocol §6.2）。
/// 宿主没起来时安静重试 3 次后放弃，绝不把异常抛给 UI 线程。
/// </summary>
internal sealed class HostRegistration
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly string _appId;
    private readonly string _widgetId;
    private readonly string _dpiAware;

    public HostRegistration(string token, string appId, string widgetId, string dpiAware)
    {
        _appId = appId;
        _widgetId = widgetId;
        _dpiAware = dpiAware;
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>上报窗口句柄；最多 3 次，失败只写调试日志。</summary>
    public async Task RegisterQuietly(long hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            new AppRegistration { WidgetId = _widgetId, Hwnd = hwnd, DpiAware = _dpiAware },
            ShellJson.Wire);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // 每轮重新解析：宿主可能比应用晚几毫秒才写出 host.json。
            var hostUrl = ResolveHostUrl();
            if (hostUrl is null)
            {
                Log($"宿主未运行（第 {attempt} 次）");
            }
            else
            {
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await _http
                        .PostAsync(BuildUrl(hostUrl, "register"), content)
                        .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        Log($"register 成功（第 {attempt} 次）");
                        return;
                    }

                    Log($"register 返回 HTTP {(int)response.StatusCode}（第 {attempt} 次）");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException)
                {
                    Log($"register 第 {attempt} 次失败：{ex.Message}");
                }
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt)).ConfigureAwait(false);
            }
        }

        Log("register 放弃（宿主未就绪）");
    }

    /// <summary>窗口关闭时尽量通知宿主；失败不影响退出。</summary>
    public async Task UnregisterQuietly(long hwnd)
    {
        var hostUrl = ResolveHostUrl();
        if (hostUrl is null || hwnd == 0)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(
                new AppRegistration { WidgetId = _widgetId, Hwnd = hwnd },
                ShellJson.Wire);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http
                .PostAsync(BuildUrl(hostUrl, "unregister"), content)
                .ConfigureAwait(false);
            Log(response.IsSuccessStatusCode
                ? "unregister 成功"
                : $"unregister 返回 HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            Log($"unregister 失败：{ex.Message}");
        }
    }

    private string BuildUrl(string hostUrl, string action) =>
        $"{hostUrl}/v1/apps/{Uri.EscapeDataString(_appId)}/{action}";

    /// <summary>优先 SDEMO_HOST_URL；否则读 host.json 的 port 拼回环地址。</summary>
    private static string? ResolveHostUrl()
    {
        var injected = Environment.GetEnvironmentVariable(ShellConstants.HostUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(injected))
        {
            return injected.Trim().TrimEnd('/');
        }

        var hostFile = Path.Combine(AppServer.RuntimeDirectory, ShellConstants.HostFileName);
        try
        {
            if (!File.Exists(hostFile))
            {
                return null;
            }

            var json = File.ReadAllText(hostFile, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<HostFile>(json, ShellJson.Wire);
            if (file is null || file.Port is <= 0 or > 65535)
            {
                return null;
            }

            // pid 已退出说明是上次壳残留的 host.json。
            using var process = Process.GetProcessById(file.Pid);
            if (process.HasExited)
            {
                return null;
            }

            return $"http://127.0.0.1:{file.Port}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Log($"读取 host.json 失败：{ex.Message}");
            return null;
        }
    }

    private static void Log(string message) => Debug.WriteLine($"[sdemo.hello] {message}");
}
