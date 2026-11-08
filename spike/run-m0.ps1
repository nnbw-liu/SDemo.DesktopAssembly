#requires -Version 7.0
<#
.SYNOPSIS
  M0 spike：验证 WPF + Kestrel + Blazor Server + WebView2 + <appId>.localhost cookie 隔离。

.DESCRIPTION
  发布并启动两个示例应用宿主（sdemo.alpha / sdemo.beta），再用 WebView2 依次打开
  两个组件页，验证：
  1) publish 产物能启动并服务 Blazor Server；
  2) WebView2 能解析 <appId>.localhost；
  3) host-only cookie 按应用隔离；
  4) Blazor circuit 可交互（按钮计数 0 -> 1）。

  结果写入 spike/viewer-result.json；退出码 0 表示通过。
#>
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$outDir = Join-Path $root 'out'
$hostOut = Join-Path $outDir 'host'
$hostProj = Join-Path $root 'Spike.WidgetHost\Spike.WidgetHost.csproj'
$viewerProj = Join-Path $root 'Spike.Viewer\Spike.Viewer.csproj'
$viewerExe = Join-Path $root 'Spike.Viewer\bin\Release\net10.0-windows10.0.19041.0\Spike.Viewer.exe'
$hostExe = Join-Path $hostOut 'Spike.WidgetHost.exe'
$resultPath = Join-Path $root 'viewer-result.json'

$tokenA = 'a' * 64
$tokenB = 'b' * 64

Write-Host '== publish host ==' -ForegroundColor Cyan
dotnet publish $hostProj -c Release -o $hostOut | Out-Host

Write-Host '== build viewer ==' -ForegroundColor Cyan
dotnet build $viewerProj -c Release | Out-Host

$processes = @()
try {
    Write-Host '== start hosts ==' -ForegroundColor Cyan
    $pa = Start-Process -FilePath $hostExe `
        -ArgumentList @('--app-id', 'sdemo.alpha', '--token', $tokenA, '--run-seconds', '240') `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $root 'alpha.out') `
        -RedirectStandardError (Join-Path $root 'alpha.err')
    $processes += $pa

    $pb = Start-Process -FilePath $hostExe `
        -ArgumentList @('--app-id', 'sdemo.beta', '--token', $tokenB, '--run-seconds', '240') `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $root 'beta.out') `
        -RedirectStandardError (Join-Path $root 'beta.err')
    $processes += $pb

    $deadline = (Get-Date).AddSeconds(20)
    $readyA = $null
    $readyB = $null
    while ((Get-Date) -lt $deadline -and (-not $readyA -or -not $readyB)) {
        Start-Sleep -Milliseconds 300
        $readyA = (Select-String -Path (Join-Path $root 'alpha.out') -Pattern '^READY ' -ErrorAction SilentlyContinue |
            Select-Object -Last 1).Line
        $readyB = (Select-String -Path (Join-Path $root 'beta.out') -Pattern '^READY ' -ErrorAction SilentlyContinue |
            Select-Object -Last 1).Line
    }

    if (-not $readyA -or -not $readyB) {
        throw "hosts did not become ready.`nA: $readyA`nB: $readyB"
    }

    $portA = [int]($readyA -split ' ')[2]
    $portB = [int]($readyB -split ' ')[2]
    Write-Host "alpha :$portA   beta :$portB"

    if (Test-Path $resultPath) { Remove-Item $resultPath -Force }

    Write-Host '== run viewer ==' -ForegroundColor Cyan
    $viewer = Start-Process -FilePath $viewerExe `
        -ArgumentList @(
            '--target', 'sdemo.alpha', "$portA", $tokenA,
            '--target', 'sdemo.beta', "$portB", $tokenB,
            '--result', $resultPath) `
        -PassThru -Wait `
        -RedirectStandardOutput (Join-Path $root 'viewer.out') `
        -RedirectStandardError (Join-Path $root 'viewer.err')

    Get-Content (Join-Path $root 'viewer.out') | Out-Host
    Get-Content $resultPath | Out-Host

    if ($viewer.ExitCode -ne 0) {
        Get-Content (Join-Path $root 'viewer.err') | Out-Host
        throw "viewer failed with exit code $($viewer.ExitCode)"
    }

    Write-Host 'M0 PASS' -ForegroundColor Green
}
finally {
    foreach ($p in $processes) {
        if ($p -and -not $p.HasExited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
