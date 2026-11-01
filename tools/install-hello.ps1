# Hello Widget 安装脚本（S1 验收）
#
# 做三件事：
#   1. dotnet publish samples/SDemo.Shell.HelloWidget 到临时目录（framework-dependent）；
#   2. 把 publish 产物全部复制到 %APPDATA%\SDemo\DesktopAssembly\apps\sdemo.hello\payload\；
#   3. 按 payload 里 exe 的真实 SHA256 回填 app.json 的 sha256，再连同 widget.json 一起落盘。
#
# 只写安装目录，不碰 %APPDATA%\SDemo\Apps\sdemo.hello 数据目录。

param(
    [string]$Configuration = 'Release',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$appId = 'sdemo.hello'
$projectPath = Join-Path $repoRoot 'samples\SDemo.Shell.HelloWidget\SDemo.Shell.HelloWidget.csproj'
$sourceManifestDir = Join-Path $repoRoot 'samples\hello-app'
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

    $running = @(Get-Process -Name 'SDemo.Shell.HelloWidget' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $pids = ($running | ForEach-Object { $_.Id }) -join ', '
        throw "Hello Widget 正在运行（pid $pids）。请先关闭窗口或退出 DesktopAssembly 外壳，再重新安装。"
    }

    # --- 1. publish 到临时目录 -------------------------------------------------
    $tempPublish = Join-Path ([System.IO.Path]::GetTempPath()) ("sdemo-hello-publish-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempPublish -Force | Out-Null

    Write-Host "发布 $appId（$Configuration → 临时目录）…"
    $publishArgs = @(
        'publish', $projectPath,
        '-c', $Configuration,
        '-o', $tempPublish,
        '--nologo'
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（退出码 $LASTEXITCODE）。"
    }

    $publishedExe = Join-Path $tempPublish 'SDemo.Shell.HelloWidget.exe'
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "publish 结果里缺少 SDemo.Shell.HelloWidget.exe：$publishedExe"
    }

    # --- 2. 复制 payload -------------------------------------------------------
    if ($Force -and (Test-Path -LiteralPath $targetRoot)) {
        # 覆盖安装：先删旧安装目录（仅 apps\sdemo.hello，不碰数据目录）。
        $resolvedTarget = (Resolve-Path -LiteralPath $targetRoot).Path
        if (-not $resolvedTarget.StartsWith((Join-Path $env:APPDATA 'SDemo\DesktopAssembly\apps'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝删除非安装目录：$resolvedTarget"
        }

        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }

    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    try {
        Copy-Item -Path (Join-Path $tempPublish '*') -Destination $payloadDir -Recurse -Force
    }
    catch {
        throw "复制 payload 失败：目标文件被占用。请先关闭 Hello Widget 窗口或退出 DesktopAssembly 外壳，再重试。原始错误：$($_.Exception.Message)"
    }

    # --- 3. 回填 sha256 + 落 app.json / widget.json -------------------------------
    $manifestPath = Join-Path $sourceManifestDir 'app.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "找不到清单模板：$manifestPath"
    }

    $manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    $manifest = $manifestText | ConvertFrom-Json

    $executableRelative = [string]$manifest.executable
    $installedExe = Join-Path $payloadDir (Split-Path -Leaf $executableRelative)
    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw "清单 executable 指向的文件未安装：$installedExe"
    }

    $hash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $replaced = [regex]::Replace($manifestText, '"sha256"\s*:\s*"[^"]*"', ('"sha256": "' + $hash + '"'))
    if ($replaced -eq $manifestText) {
        throw "app.json 模板里没有可回填的 sha256 字段。"
    }

    # 回填后仍必须是合法 JSON。
    $null = $replaced | ConvertFrom-Json

    $targetAppJson = Join-Path $targetRoot 'app.json'
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    [System.IO.File]::WriteAllText($targetAppJson, $replaced, [System.Text.UTF8Encoding]::new($false))

    $widgetSource = Join-Path $sourceManifestDir 'widget.json'
    if (-not (Test-Path -LiteralPath $widgetSource)) {
        throw "找不到组件清单模板：$widgetSource"
    }

    Copy-Item -LiteralPath $widgetSource -Destination (Join-Path $targetRoot 'widget.json') -Force

    # 复核：落盘后的哈希必须等于 exe 真实哈希。
    $written = Get-Content -LiteralPath $targetAppJson -Raw | ConvertFrom-Json
    if ($written.sha256 -ne $hash) {
        throw "app.json 回填校验失败：$($written.sha256) != $hash"
    }

    Write-Host ''
    Write-Host "已安装 $appId 到：$targetRoot"
    Write-Host "  executable : $installedExe"
    Write-Host "  sha256     : $hash"
    Write-Host "  widget.json: $(Join-Path $targetRoot 'widget.json')"
    Write-Host ''
    Write-Host '卸载（只删安装目录，保留用户数据）：'
    Write-Host ("  Remove-Item -LiteralPath '{0}' -Recurse -Force" -f $targetRoot)
    Write-Host ("  数据目录（保留）：{0}" -f (Join-Path $env:APPDATA 'SDemo\Apps\sdemo.hello'))
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
