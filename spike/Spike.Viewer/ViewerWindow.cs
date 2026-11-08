using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Spike.Viewer;

public sealed class ViewerWindow : Window
{
    private readonly IReadOnlyList<Target> _targets;
    private readonly string _resultPath;
    private readonly WebView2 _webView;
    private readonly string _userDataFolder;

    public ViewerWindow(IReadOnlyList<Target> targets, string resultPath)
    {
        _targets = targets;
        _resultPath = resultPath;
        _userDataFolder = Path.Combine(Path.GetTempPath(), "sdemo-m0-webview2-" + Environment.ProcessId);

        Title = "SDemo M0 Spike Viewer";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        Content = _webView;
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        RunResult result;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            var targetResults = new List<TargetResult>();
            foreach (var target in _targets)
            {
                targetResults.Add(await TestTargetAsync(target));
            }

            var isolation = await TestTargetAsync(_targets[0], click: false);
            var pass = targetResults.All(r => r.Pass) && isolation.Pass;

            result = new RunResult(pass, targetResults, isolation, null);
        }
        catch (Exception ex)
        {
            result = new RunResult(false, new List<TargetResult>(), null, ex.ToString());
        }

        WriteResult(result);
        Console.WriteLine(result.Pass ? "RESULT PASS" : "RESULT FAIL");
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));

        Application.Current.Shutdown(result.Pass ? 0 : 1);
    }

    private async Task<TargetResult> TestTargetAsync(Target target, bool click = true)
    {
        var host = target.AppId + ".localhost";
        var baseUrl = $"http://{host}:{target.Port}";

        var cookie = _webView.CoreWebView2!.CookieManager.CreateCookie(
            "sdemo_session", target.Token, host, "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
        cookie.IsSecure = false;
        _webView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);

        await NavigateAsync(baseUrl + "/widget");

        var ready = await WaitForScriptAsync(
            "document.body?.dataset?.ready === '1'", 20_000);
        if (!ready)
        {
            return new TargetResult(target.AppId, "", "", "", "", false,
                "circuit not ready (page may be 401/404)");
        }

        var app = await EvalStringAsync("document.getElementById('app')?.textContent ?? ''");
        var before = await EvalStringAsync("document.getElementById('count')?.textContent ?? ''");
        var who = await EvalStringAsync(
            "(function(){var x=new XMLHttpRequest();x.open('GET','/whoami',false);x.send();return x.responseText;})()");

        var count = before;
        if (click)
        {
            await _webView.ExecuteScriptAsync("document.getElementById('inc').click();");
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                count = await EvalStringAsync("document.getElementById('count')?.textContent ?? ''");
                if (count != before)
                {
                    break;
                }

                await Task.Delay(100);
            }
        }

        var pass = app == target.AppId
            && who == $"{target.AppId}|{target.Token}"
            && (!click || count == "1");

        return new TargetResult(target.AppId, app, before, count, who, pass, null);
    }

    private async Task NavigateAsync(string url)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView.CoreWebView2!.NavigationCompleted -= Handler;
            completion.TrySetResult(e.IsSuccess);
        }

        _webView.CoreWebView2!.NavigationCompleted += Handler;
        _webView.CoreWebView2.Navigate(url);

        var success = await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
        if (!success)
        {
            throw new InvalidOperationException("navigation failed: " + url);
        }
    }

    private async Task<bool> WaitForScriptAsync(string script, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await EvalStringAsync(script) == "true")
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // 页面尚未完成导航，重试。
            }

            await Task.Delay(150);
        }

        return false;
    }

    private async Task<string> EvalStringAsync(string script)
    {
        var raw = await _webView.ExecuteScriptAsync(script);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.String => doc.RootElement.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => doc.RootElement.GetRawText(),
        };
    }

    private void WriteResult(RunResult result)
    {
        var dir = Path.GetDirectoryName(_resultPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_resultPath, JsonSerializer.Serialize(result, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

public sealed record TargetResult(
    string AppId,
    string App,
    string Before,
    string Count,
    string Who,
    bool Pass,
    string? Error);

public sealed record RunResult(
    bool Pass,
    IReadOnlyList<TargetResult> Targets,
    TargetResult? Isolation,
    string? Error);
