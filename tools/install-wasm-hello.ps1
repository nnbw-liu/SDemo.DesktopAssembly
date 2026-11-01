# Wasm Hello 安装脚本（S2 验收）
#
# 做四件事：
#   1. dotnet publish samples/SDemo.Shell.WasmHello（进程侧样例，framework-dependent）；
#   2. 把 publish 产物与 samples/wasm-hello-app/payload/index.html 一起放进 apps\sdemo.wasmhello\payload\；
#   3. 按 payload 里 exe 的真实 SHA256 回填 app.json 的 sha256；
#   4. 落 widget.json（可用 -Layer desktop 切成贴桌面层样例）。
#
# 只写安装目录，不碰 %APPDATA%\SDemo\Apps\sdemo.wasmhello 数据目录。

param(
    [string]$Configuration = 'Release',
    [ValidateSet('floating', 'desktop')]
    [string]$Layer = 'floating',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$appId = 'sdemo.wasmhello'
$projectPath = Join-Path $repoRoot 'samples\SDemo.Shell.WasmHello\SDemo.Shell.WasmHello.csproj'
$sourceManifestDir = Join-Path $repoRoot 'samples\wasm-hello-app'
$appsRoot = Join-Path $env:APPDATA 'SDemo\DesktopAssembly\apps'
$targetRoot = Join-Path $appsRoot $appId
$payloadDir = Join-Path $targetRoot 'payload'

$tempPublish = $null
$exitCode = 0
try {
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "找不到工程文件：$projectPath"
    }

    if ((Test-Path -LiteralPath (Join-Path $targetRoot 'app.json')) -and -not $Force) {
        throw "已安装到 $targetRoot；要覆盖请加 -Force。"
    }

    # --- 1. publish 到临时目录 -------------------------------------------------
    $tempPublish = Join-Path ([System.IO.Path]::GetTempPath()) ("sdemo-wasm-publish-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempPublish -Force | Out-Null

    Write-Host "发布 $appId（$Configuration → 临时目录）…"
    $publishArgs = @('publish', $projectPath, '-c', $Configuration, '-o', $tempPublish, '--nologo')
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（退出码 $LASTEXITCODE）。"
    }

    $publishedExe = Join-Path $tempPublish 'SDemo.Shell.WasmHello.exe'
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "publish 结果里缺少 SDemo.Shell.WasmHello.exe：$publishedExe"
    }

    # --- 2. 复制 payload（web 产物 + 进程产物） ---------------------------------
    if ($Force -and (Test-Path -LiteralPath $targetRoot)) {
        $resolvedTarget = (Resolve-Path -LiteralPath $targetRoot).Path
        if (-not $resolvedTarget.StartsWith((Join-Path $env:APPDATA 'SDemo\DesktopAssembly\apps'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝删除非安装目录：$resolvedTarget"
        }

        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }

    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    Copy-Item -Path (Join-Path $tempPublish '*') -Destination $payloadDir -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $sourceManifestDir 'payload\index.html') -Destination (Join-Path $payloadDir 'index.html') -Force

    # --- 3. 回填 sha256 -------------------------------------------------------
    $manifestPath = Join-Path $sourceManifestDir 'app.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "找不到清单模板：$manifestPath"
    }

    $manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    $manifest = $manifestText | ConvertFrom-Json
    $executableRelative = [string]$manifest.executable
    $installedExe = Join-Path $targetRoot $executableRelative
    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw "清单 executable 指向的文件未安装：$installedExe"
    }

    $hash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $replaced = [regex]::Replace($manifestText, '"sha256"\s*:\s*"[^"]*"', ('"sha256": "' + $hash + '"'))
    if ($replaced -eq $manifestText) {
        throw "app.json 模板里没有可回填的 sha256 字段。"
    }

    $null = $replaced | ConvertFrom-Json
    $targetAppJson = Join-Path $targetRoot 'app.json'
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    [System.IO.File]::WriteAllText($targetAppJson, $replaced, [System.Text.UTF8Encoding]::new($false))

    # --- 4. widget.json（按 -Layer 改写） --------------------------------------
    $widgetSource = Join-Path $sourceManifestDir 'widget.json'
    if (-not (Test-Path -LiteralPath $widgetSource)) {
        throw "找不到组件清单模板：$widgetSource"
    }

    $widgetText = Get-Content -LiteralPath $widgetSource -Raw -Encoding UTF8
    $widgetText = [regex]::Replace($widgetText, '"layer"\s*:\s*"[^"]*"', ('"layer": "' + $Layer + '"'))
    if ($Layer -eq 'desktop') {
        # DA-59：桌面层与置顶互斥。
        $widgetText = [regex]::Replace($widgetText, '"alwaysOnTopDefault"\s*:\s*true', '"alwaysOnTopDefault": false')
    }

    $null = $widgetText | ConvertFrom-Json
    [System.IO.File]::WriteAllText((Join-Path $targetRoot 'widget.json'), $widgetText, [System.Text.UTF8Encoding]::new($false))

    $written = Get-Content -LiteralPath $targetAppJson -Raw | ConvertFrom-Json
    if ($written.sha256 -ne $hash) {
        throw "app.json 回填校验失败：$($written.sha256) != $hash"
    }

    # 重装（-Force）同时清掉该组件的状态，让新的 widget.json（尤其 layer）按默认生效。
    if ($Force) {
        $widgetsJson = Join-Path $env:APPDATA 'SDemo\DesktopAssembly\widgets.json'
        if (Test-Path -LiteralPath $widgetsJson) {
            $state = Get-Content -LiteralPath $widgetsJson -Raw | ConvertFrom-Json
            $widgetId = [string](Get-Content -LiteralPath (Join-Path $targetRoot 'widget.json') -Raw | ConvertFrom-Json).id
            if ($state.widgets.PSObject.Properties[$widgetId]) {
                $state.widgets.PSObject.Properties.Remove($widgetId)
                $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $widgetsJson -Encoding UTF8
                Write-Host "已清除旧组件状态：$widgetId"
            }
        }
    }

    Write-Host ''
    Write-Host "已安装 $appId 到：$targetRoot"
    Write-Host "  executable : $installedExe"
    Write-Host "  sha256     : $hash"
    Write-Host "  layer      : $Layer"
    Write-Host "  payload    : $(Join-Path $payloadDir 'index.html')"
    Write-Host ''
    Write-Host '卸载（只删安装目录，保留用户数据）：'
    Write-Host ("  Remove-Item -LiteralPath '{0}' -Recurse -Force" -f $targetRoot)
    Write-Host ("  数据目录（保留）：{0}" -f (Join-Path $env:APPDATA 'SDemo\Apps\sdemo.wasmhello'))
}
catch {
    [Console]::Error.WriteLine("安装失败：" + $_.Exception.Message)
    $exitCode = 1
}
finally {
    if ($tempPublish -and (Test-Path -LiteralPath $tempPublish)) {
        Remove-Item -LiteralPath $tempPublish -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $exitCode
