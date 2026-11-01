# P1 冒烟脚本：启动外壳 → 等 host.json → 打 client 面端点 → ensure Hello Widget → 截屏 → 清理。
#
# 用途：验证「外壳能启动、监听器就位、应用被拉起、runtime.json 就绪」。UI 观感（拖动/置顶/穿透/Win+D）
# 仍需按 docs/s1-verification.md 手工验收。
#
# 用法：pwsh tools/smoke-p1.ps1 [-SkipScreenshot] [-KeepRunning]

param(
    [switch]$SkipScreenshot,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot 'src\SDemo.Shell.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Shell.exe'
$runtimeDir = Join-Path $env:LOCALAPPDATA 'SDemo\DesktopAssembly\runtime'
$hostJsonPath = Join-Path $runtimeDir 'host.json'
$clientTokenPath = Join-Path $runtimeDir 'client-token'
$logDir = Join-Path $env:LOCALAPPDATA 'SDemo\logs\desktop-assembly'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hostProcess = $null
$appProcessIds = @()

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

# JSON 里 null 字段会被省略（ShellJson.Wire），直接访问不存在的属性在 StrictMode 下会抛错。
function Get-ObjectField($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

try {
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "找不到 $exePath；先跑 dotnet build SDemo.Shell.slnf -c Release。"
    }

    if (Test-Path -LiteralPath $hostJsonPath) {
        Write-Host '发现残留 host.json；清理后继续（上次非正常退出）。'
        Remove-Item -LiteralPath $hostJsonPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientTokenPath -Force -ErrorAction SilentlyContinue
    }

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
    Write-Host "GET /v1/host：hostVersion=$($hostInfo.hostVersion) pid=$($hostInfo.pid) revision=$($hostInfo.registryRevision)"

    $apps = Invoke-HostApi -baseUrl $baseUrl -path '/v1/apps' -token $token
    Write-Host "GET /v1/apps：revision=$($apps.revision) apps=$($apps.apps.Count)"
    foreach ($app in $apps.apps) {
        $runtime = Get-ObjectField $app 'runtime'
        $runtimePort = '-'
        $runtimePid = '-'
        if ($null -ne $runtime) {
            $runtimePort = Get-ObjectField $runtime 'port'
            $runtimePid = Get-ObjectField $runtime 'pid'
        }

        Write-Host ("  - {0} state={1} enabled={2} hashValid={3} runtime={4} pid={5}" -f $app.appId, $app.state, $app.enabled, $app.hashValid, $runtimePort, $runtimePid)
        if ($null -ne $runtime -and $runtimePid) { $appProcessIds += [int]$runtimePid }
    }

    if (($apps.apps | Where-Object { $_.appId -eq 'sdemo.hello' })) {
        Write-Host 'POST /v1/apps/sdemo.hello/ensure …'
        $ensure = Invoke-HostApi -baseUrl $baseUrl -path '/v1/apps/sdemo.hello/ensure' -token $token -method 'POST' -body @{ timeoutMs = 8000 }
        $ensureRuntime = Get-ObjectField $ensure 'runtime'
        $ensurePort = '-'
        $ensurePid = '-'
        if ($null -ne $ensureRuntime) {
            $ensurePort = Get-ObjectField $ensureRuntime 'port'
            $ensurePid = Get-ObjectField $ensureRuntime 'pid'
        }

        Write-Host "ensure：state=$($ensure.state) pid=$ensurePid port=$ensurePort"
        if ($null -ne $ensureRuntime -and $ensurePid) { $appProcessIds += [int]$ensurePid }
    }
    else {
        Write-Host '未安装 sdemo.hello；跳过 ensure（先跑 tools/install-hello.ps1）。'
    }

    Start-Sleep -Seconds 2

    if (-not $SkipScreenshot) {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)
        $shot = Join-Path ([System.IO.Path]::GetTempPath()) ("sdemo-smoke-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.png')
        $bitmap.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        Write-Host "截屏：$shot"
    }

    Write-Host ''
    Write-Host '最近日志：'
    $log = Get-ChildItem -LiteralPath $logDir -Filter '*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) { Get-Content -LiteralPath $log.FullName -Tail 20 | ForEach-Object { Write-Host "  $_" } }
}
finally {
    if (-not $KeepRunning) {
        foreach ($processId in ($appProcessIds | Sort-Object -Unique)) {
            if ($processId -gt 0) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
        }

        # 控制器可能比快照晚一步拉起应用；按安装目录与进程名兜底清理，避免下次启动被单实例互斥挡住。
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
        Get-Process -Name 'SDemo.Shell.HelloWidget' -ErrorAction SilentlyContinue | ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }

        if ($hostProcess -and -not $hostProcess.HasExited) {
            Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        }

        Start-Sleep -Milliseconds 800
        Remove-Item -LiteralPath $hostJsonPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientTokenPath -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $runtimeDir -Filter '*.json' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

        # 冒烟会按默认设置写自启；为开发机清理，避免 Run 项指向构建产物。
        Remove-ItemProperty -Path $runKey -Name 'SDemo.Shell' -ErrorAction SilentlyContinue
        Write-Host '已清理：进程 / 发现文件 / runtime / Run 项。'
    }
}

