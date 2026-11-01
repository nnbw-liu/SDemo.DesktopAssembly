# P2 冒烟脚本：启动外壳 → 校验注册表/权限/热键/截屏隐藏的基础链路 → 验证 wasm 组件真的被宿主渲染。
#
# 覆盖：
#   - host.json / client token / GET /v1/host / GET /v1/apps
#   - wasm 组件的状态（不拉起进程也能显示：state=stopped 但窗口存在）
#   - WebView2 渲染进程确实起来了（DA-58）
#   - 宿主窗口存在且标题是组件名；-Layer desktop 时额外校验父窗口是 WorkerW（DA-59）
#   - 热键注册在日志里出现（DA-70）
#
# 用法：pwsh tools/smoke-p2.ps1 [-Layer floating|desktop] [-SkipScreenshot] [-KeepRunning]

param(
    [ValidateSet('floating', 'desktop')]
    [string]$Layer = 'floating',
    [switch]$SkipScreenshot,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$appId = 'sdemo.wasmhello'
$exePath = Join-Path $repoRoot 'src\SDemo.Shell.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Shell.exe'
$runtimeDir = Join-Path $env:LOCALAPPDATA 'SDemo\DesktopAssembly\runtime'
$hostJsonPath = Join-Path $runtimeDir 'host.json'
$clientTokenPath = Join-Path $runtimeDir 'client-token'
$logDir = Join-Path $env:LOCALAPPDATA 'SDemo\logs\desktop-assembly'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hostProcess = $null
$failures = [System.Collections.Generic.List[string]]::new()

function Wait-ForFile([string]$path, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $path) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Invoke-HostApi([string]$baseUrl, [string]$path, [string]$token, [string]$method = 'GET', [object]$body = $null) {
    $headers = @{ Authorization = "Bearer $token" }
    $params = @{
        Uri         = "$baseUrl$path"
        Method      = $method
        Headers     = $headers
        TimeoutSec  = 15
        ContentType = 'application/json'
    }
    if ($null -ne $body) { $params['Body'] = ($body | ConvertTo-Json -Compress) }
    return Invoke-RestMethod @params
}

function Get-ObjectField($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Add-Failure([string]$message) {
    $failures.Add($message) | Out-Null
    Write-Host "  ✗ $message" -ForegroundColor Red
}

# --- Win32：枚举窗口、读标题、读父窗口类名 --------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class SmokeWin32
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    public static List<IntPtr> FindByTitle(string title, int ownerPid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, data) =>
        {
            Consider(hwnd, title, ownerPid, result);

            // 贴桌面层的 wasm 窗口是 WorkerW 的子窗口，EnumWindows 看不到，必须枚举子窗口。
            EnumChildWindows(hwnd, (child, _) =>
            {
                Consider(child, title, ownerPid, result);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void Consider(IntPtr hwnd, string title, int ownerPid, List<IntPtr> result)
    {
        var text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        if (text.ToString() != title) { return; }
        if (ownerPid > 0)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if ((int)pid != ownerPid) { return; }
        }

        result.Add(hwnd);
    }

    public static string ClassOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }
}
'@

try {
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "找不到 $exePath；先跑 dotnet build SDemo.Shell.slnf -c Release。"
    }

    $installed = Join-Path $env:APPDATA "SDemo\DesktopAssembly\apps\$appId\widget.json"
    if (-not (Test-Path -LiteralPath $installed)) {
        throw "未安装 $appId；先跑 pwsh tools/install-wasm-hello.ps1 -Force -Layer $Layer。"
    }

    if (Test-Path -LiteralPath $hostJsonPath) {
        Write-Host '发现残留 host.json；清理后继续（上次非正常退出）。'
        Remove-Item -LiteralPath $hostJsonPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientTokenPath -Force -ErrorAction SilentlyContinue
    }

    $webviewBefore = @(Get-Process -Name 'msedgewebview2' -ErrorAction SilentlyContinue).Count
    $runStarted = Get-Date

    Write-Host "启动外壳：$exePath"
    $hostProcess = Start-Process -FilePath $exePath -PassThru
    if (-not (Wait-ForFile $hostJsonPath 20)) {
        throw '启动后 20s 内没有生成 host.json，见日志目录。'
    }

    $hostState = Get-Content -LiteralPath $hostJsonPath -Raw | ConvertFrom-Json
    $token = (Get-Content -LiteralPath $clientTokenPath -Raw).Trim()
    $baseUrl = "http://127.0.0.1:$($hostState.port)"
    Write-Host "host.json：pid=$($hostState.pid) port=$($hostState.port) revision=$($hostState.registryRevision)"

    $hostInfo = Invoke-HostApi -baseUrl $baseUrl -path '/v1/host' -token $token
    Write-Host "GET /v1/host：hostVersion=$($hostInfo.hostVersion) revision=$($hostInfo.registryRevision)"

    $apps = Invoke-HostApi -baseUrl $baseUrl -path '/v1/apps' -token $token
    Write-Host "GET /v1/apps：revision=$($apps.revision) apps=$($apps.apps.Count)"
    $wasmApp = $apps.apps | Where-Object { $_.appId -eq $appId }
    if (-not $wasmApp) {
        Add-Failure "注册表里没有 $appId（清单校验可能没过，看 /v1/apps 与日志）"
    }
    else {
        Write-Host ("  - {0} state={1} renderer={2} hashValid={3} widgetId={4}" -f $wasmApp.appId, $wasmApp.state, $wasmApp.renderer, $wasmApp.hashValid, $wasmApp.widgetId)
        if (-not $wasmApp.hashValid) { Add-Failure '应用哈希校验未通过' }
        if ($wasmApp.renderer -ne 'wasm') { Add-Failure "renderer 不是 wasm：$($wasmApp.renderer)" }
    }

    # 等 WebView2 渲染进程与组件窗口出现（wasm 不需要启动应用进程）。
    $deadline = (Get-Date).AddSeconds(20)
    $window = $null
    $webviewAfter = 0
    while ((Get-Date) -lt $deadline) {
        $window = [SmokeWin32]::FindByTitle('Wasm Hello', $hostProcess.Id) | Select-Object -First 1
        $webviewAfter = @(Get-Process -Name 'msedgewebview2' -ErrorAction SilentlyContinue).Count
        if ($null -ne $window -and $webviewAfter -gt $webviewBefore) { break }
        Start-Sleep -Milliseconds 500
    }

    if ($null -eq $window) {
        Add-Failure '20s 内没找到标题为「Wasm Hello」的组件窗口（wasm 未渲染？看日志与 WebView2 Runtime）'
    }
    else {
        $visible = [SmokeWin32]::IsWindowVisible($window)
        $rect = New-Object SmokeWin32+Rect
        [void][SmokeWin32]::GetWindowRect($window, [ref]$rect)
        $className = [SmokeWin32]::ClassOf($window)
        $parent = [SmokeWin32]::GetParent($window)
        $parentClass = if ($parent -ne [IntPtr]::Zero) { [SmokeWin32]::ClassOf($parent) } else { '' }
        Write-Host ("组件窗口：hwnd={0} visible={1} rect=({2},{3},{4}x{5}) class={6} parent={7}" -f $window, $visible, $rect.Left, $rect.Top, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top), $className, $parentClass)

        if ($rect.Right - $rect.Left -lt 80 -or $rect.Bottom - $rect.Top -lt 80) { Add-Failure '组件窗口尺寸异常（<80×80）' }

        if ($Layer -eq 'desktop') {
            if ($parentClass -eq 'WorkerW') {
                Write-Host '桌面层：SetParent 到 WorkerW'
            }
            elseif ($parent -eq [IntPtr]::Zero) {
                # Win11 没有可用 WorkerW：宿主回退成「普通窗口压到 z-order 底部」。
                Write-Host '桌面层：TopLevelBottom（Win11 回落，z-order 底部）'
                if (-not $visible) { Add-Failure '桌面层 TopLevelBottom 模式下组件窗口不可见' }
            }
            else {
                Add-Failure "layer=desktop 但父窗口既不是 WorkerW 也不是顶层窗口（实际：$parentClass）"
            }
        }
        else {
            if (-not $visible) { Add-Failure '组件窗口不可见' }
            if ($parent -ne [IntPtr]::Zero) { Add-Failure "layer=floating 但窗口有父窗口：$parentClass" }
        }
    }

    if ($webviewAfter -le $webviewBefore) {
        Add-Failure 'msedgewebview2 渲染进程没有出现（DA-58 共享环境 / 运行时缺失）'
    }
    else {
        Write-Host "WebView2 渲染进程：$webviewBefore → $webviewAfter"
    }

    # 只看本次运行写下的日志行，避免被上几次冒烟或 Win+D 观察日志的残留行误判。
    Start-Sleep -Seconds 1
    $log = Join-Path $logDir ((Get-Date -Format 'yyyy-MM-dd') + '.log')
    $logLines = @()
    if (Test-Path -LiteralPath $log) {
        $allLines = Get-Content -LiteralPath $log -Tail 200
        $logLines = $allLines | Where-Object {
            $match = [regex]::Match($_, '^(\d{4}-\d{2}-\d{2}T[0-9:\-\.\+]+)\s')
            if (-not $match.Success) { return $false }
            try { return ([datetimeoffset]::Parse($match.Groups[1].Value) -ge $runStarted) } catch { return $false }
        }

        $hotkeyLine = $logLines | Where-Object { $_ -match '热键已注册' -and $_ -match 'sdemo.wasmhello' } | Select-Object -First 1
        if ($null -eq $hotkeyLine) {
            Add-Failure '日志里没有 sdemo.wasmhello 的热键注册记录（冲突或被占用？）'
        }
        else {
            Write-Host "热键：$($hotkeyLine.Trim())"
        }

        $wasmLine = $logLines | Where-Object { $_ -match 'wasm 组件已加载' } | Select-Object -First 1
        if ($null -eq $wasmLine) {
            Add-Failure '日志里没有「wasm 组件已加载」'
        }
        else {
            Write-Host "渲染：$($wasmLine.Trim())"
        }

        if ($Layer -eq 'desktop') {
            $layerLine = $logLines | Where-Object { $_ -match '已贴桌面层' -and $_ -match 'mode=' } | Select-Object -First 1
            if ($null -eq $layerLine) {
                Add-Failure '日志里没有「已贴桌面层」记录'
            }
            else {
                Write-Host "桌面层：$($layerLine.Trim())"
            }
        }
    }
    else {
        Add-Failure "找不到日志文件：$logDir"
    }

    if (-not $SkipScreenshot) {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)
        $shot = Join-Path ([System.IO.Path]::GetTempPath()) ("sdemo-smoke-p2-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.png')
        $bitmap.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        Write-Host "截屏：$shot"
    }
}
finally {
    if (-not $KeepRunning) {
        Get-Process -Name "$appId" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Get-Process -Name 'SDemo.Shell.WasmHello' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        # 受管应用可能比快照晚一步拉起，按安装目录兜底清理，避免下次启动被单实例互斥挡住。
        $appsRoot = Join-Path $env:APPDATA 'SDemo\DesktopAssembly\apps'
        Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                if ($_.Path -and $_.Path.StartsWith($appsRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                # 受保护进程拿不到 Path，忽略。
            }
        }

        if ($hostProcess -and -not $hostProcess.HasExited) {
            Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        }

        Start-Sleep -Milliseconds 800
        Remove-Item -LiteralPath $hostJsonPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientTokenPath -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $runtimeDir -Filter '*.json' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $runKey -Name 'SDemo.Shell' -ErrorAction SilentlyContinue
        Write-Host '已清理：进程 / 发现文件 / runtime / Run 项。'
    }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host ("冒烟失败：{0} 项" -f $failures.Count) -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host '冒烟通过：wasm 组件由宿主渲染，窗口/热键/WebView2 均就位。' -ForegroundColor Green
exit 0
