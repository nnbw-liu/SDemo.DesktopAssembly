# Win+D 真机 A/B 实测（P1 / Q11）
#
# 用 Shell.Application.ToggleDesktop 触发与 Win+D 相同的「显示桌面」，在组件可见期间采样：
#   - IsWindowVisible / IsIconic：组件是否被收起；
#   - 前台窗口类名：用于区分桌面（Progman/WorkerW）与普通窗口；
#   - 连续采样，观察宿主「前台轮询状态机」是否把它重新显示出来。
# 结束后再 ToggleDesktop 一次，恢复用户原来的窗口。
#
# 结论：A=组件从头到尾可见；B=被收起但被状态机恢复；C=被收起且未恢复（失败）。
# 结果追加到 %LOCALAPPDATA%\SDemo\logs\desktop-assembly\win-d-spike.log，并输出到控制台。
#
# 用法：pwsh tools/spike-win-d.ps1 [-KeepRunning]

param([switch]$KeepRunning)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot 'src\SDemo.Shell.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Shell.exe'
$runtimeDir = Join-Path $env:LOCALAPPDATA 'SDemo\DesktopAssembly\runtime'
$hostJsonPath = Join-Path $runtimeDir 'host.json'
$clientTokenPath = Join-Path $runtimeDir 'client-token'
$appRuntimePath = Join-Path $runtimeDir 'sdemo.hello.json'
$logDir = Join-Path $env:LOCALAPPDATA 'SDemo\logs\desktop-assembly'
$spikeLog = Join-Path $logDir 'win-d-spike.log'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hostProcess = $null

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class WinSpikeNative
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    public static IntPtr FindVisibleWindowForProcess(uint targetProcessId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId == targetProcessId && IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Wait-ForFile([string]$path, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $path) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Get-ForegroundClass {
    $hwnd = [WinSpikeNative]::GetForegroundWindow()
    $name = New-Object System.Text.StringBuilder 256
    $null = [WinSpikeNative]::GetClassName($hwnd, $name, $name.Capacity)
    return $name.ToString()
}

function Get-WidgetWindow([int]$appPid) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $process = Get-Process -Id $appPid -ErrorAction SilentlyContinue
        if ($null -ne $process -and $process.MainWindowHandle -ne 0) {
            return [IntPtr]$process.MainWindowHandle
        }

        $found = [WinSpikeNative]::FindVisibleWindowForProcess([uint32]$appPid)
        if ($found -ne [IntPtr]::Zero) {
            return $found
        }

        Start-Sleep -Milliseconds 250
    }

    return [IntPtr]::Zero
}

function Get-Snapshot([IntPtr]$hwnd) {
    $fg = Get-ForegroundClass
    return [pscustomobject]@{
        Time     = (Get-Date).ToString('HH:mm:ss.fff')
        Visible  = [WinSpikeNative]::IsWindowVisible($hwnd)
        Iconic   = [WinSpikeNative]::IsIconic($hwnd)
        Foreground = $fg
    }
}

try {
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "找不到 $exePath；先跑 dotnet build SDemo.Shell.slnf -c Release。"
    }

    if (Test-Path -LiteralPath $hostJsonPath) {
        Remove-Item -LiteralPath $hostJsonPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientTokenPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host '启动外壳并等待 Hello Widget …'
    $hostProcess = Start-Process -FilePath $exePath -PassThru
    if (-not (Wait-ForFile $hostJsonPath 20)) { throw '20s 内未生成 host.json。' }
    if (-not (Wait-ForFile $appRuntimePath 20)) { throw '20s 内 Hello Widget 未写出 runtime.json。' }

    $appRuntime = Get-Content -LiteralPath $appRuntimePath -Raw | ConvertFrom-Json
    $appPid = [int]$appRuntime.pid
    $hwnd = Get-WidgetWindow $appPid
    if ($hwnd -eq [IntPtr]::Zero) { throw '找不到 Hello Widget 窗口句柄。' }

    # 等首次定位完成（组件挂到右上角、不是骨架态）。
    Start-Sleep -Seconds 3

    $before = Get-Snapshot $hwnd
    Write-Host ("Win+D 前：visible={0} iconic={1} foreground={2}" -f $before.Visible, $before.Iconic, $before.Foreground)

    $shell = New-Object -ComObject Shell.Application
    $shell.ToggleDesktop()   # 等价 Win+D

    $samples = @()
    $hiddenAt = $null
    $recoveredAt = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 100
        $snap = Get-Snapshot $hwnd
        $samples += $snap
        $hidden = (-not $snap.Visible) -or $snap.Iconic
        if ($hidden -and $null -eq $hiddenAt) { $hiddenAt = $i }
        if ($null -ne $hiddenAt -and -not $hidden -and $null -eq $recoveredAt -and $i -gt $hiddenAt) { $recoveredAt = $i }
    }

    # 恢复用户原来的桌面（第二下 Win+D）。
    $shell.ToggleDesktop()
    Start-Sleep -Milliseconds 800
    $afterRestore = Get-Snapshot $hwnd

    $firstHiddenSample = if ($null -ne $hiddenAt) { $samples[$hiddenAt] } else { $null }
    $recoveredSample = if ($null -ne $recoveredAt) { $samples[$recoveredAt] } else { $null }
    $desktopSamples = @($samples | Where-Object { $_.Foreground -in @('Progman', 'WorkerW', 'Shell_TrayWnd') })
    $desktopSeen = $desktopSamples.Count -gt 0
    $foregroundSummary = ($samples | Group-Object Foreground | Sort-Object Count -Descending | Select-Object -First 5 |
        ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ', '

    if (-not $desktopSeen) {
        $conclusion = 'N/A'
        $detail = "采样期间未观察到桌面前台（ToggleDesktop 可能未生效）；前台类名分布：$foregroundSummary"
    }
    elseif ($null -eq $hiddenAt) {
        $conclusion = 'A'
        $detail = "Win+D 后组件始终可见（IsWindowVisible=true 且未最小化）；前台类名：$foregroundSummary"
    }
    elseif ($null -ne $recoveredAt) {
        $conclusion = 'B'
        $detail = ("被收起后约 {0}ms 由状态机恢复（收起时前台 {1}）；前台类名：{2}" -f (($recoveredAt - $hiddenAt) * 100), $firstHiddenSample.Foreground, $foregroundSummary)
    }
    else {
        $conclusion = 'C'
        $detail = ("被收起且采样窗口内未恢复（收起时前台 {0}）；前台类名：{1}" -f $firstHiddenSample.Foreground, $foregroundSummary)
    }

    $screen = "{0}x{1}" -f [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    $line = "{0} 显示器={1} 缩放=未知 A/B/C={2} 说明={3} 恢复后 visible={4} iconic={5}" -f `
        (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss+08:00'), $screen, $conclusion, $detail, $afterRestore.Visible, $afterRestore.Iconic

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    Add-Content -LiteralPath $spikeLog -Value $line -Encoding utf8NoBOM

    Write-Host ''
    Write-Host "结论：$conclusion"
    Write-Host "说明：$detail"
    Write-Host "采样：$($samples.Count) 次；恢复桌面后 visible=$($afterRestore.Visible) iconic=$($afterRestore.Iconic)"
    Write-Host "已追加到：$spikeLog"
    Write-Host $line
}
finally {
    if (-not $KeepRunning) {
        Get-Process -Name SDemo.Shell.HelloWidget -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        if ($hostProcess -and -not $hostProcess.HasExited) {
            Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        }

        Start-Sleep -Milliseconds 800
        Remove-Item -LiteralPath $hostJsonPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientTokenPath -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $runtimeDir -Filter '*.json' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $runKey -Name 'SDemo.Shell' -ErrorAction SilentlyContinue
        Write-Host '已清理：组件进程 / 外壳进程 / runtime / Run 项。'
    }
}
