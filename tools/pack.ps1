# 打包脚本（DA-86）：publish + 清单校验 + 回填 sha256 + 压缩
#
# 产物：dist\SDemo-Shell-<ver>.zip（根目录 SDemo-Shell-<ver>\，含 apps\ 模板）
#       dist\SDemo-Shell-<ver>.zip.sha256（zip 自身的哈希，便于分发校验）
#
# 用法：
#   pwsh tools/pack.ps1                          # 只打壳 + 空 apps 模板
#   pwsh tools/pack.ps1 -Apps samples/hello-app  # 连同样例应用一起打包（自动回填 app.json 的 sha256）
#
# 约定：sha256 一律由脚本按真实文件算出来写进清单，不接受手填。

param(
    [string]$Configuration = 'Release',
    [string]$OutDir = 'dist',
    [string[]]$Apps = @(),
    [switch]$SelfContained,
    [switch]$SkipValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot 'src\SDemo.Shell.Host\SDemo.Shell.Host.csproj'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

function Get-ProductVersion {
    param([string]$Path)
    $text = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($text, '<Version>\s*([^<]+?)\s*</Version>')
    if (-not $match.Success) {
        throw "Directory.Build.props 里找不到 <Version>：$Path"
    }

    return $match.Groups[1].Value.Trim()
}

function Repair-ManifestSha256 {
    param(
        [Parameter(Mandatory)][string]$AppDirectory,
        [string]$ExpectSha256 = ''
    )

    $appJson = Join-Path $AppDirectory 'app.json'
    if (-not (Test-Path -LiteralPath $appJson)) {
        throw "缺少 app.json：$appJson"
    }

    $text = Get-Content -LiteralPath $appJson -Raw -Encoding UTF8
    $manifest = $text | ConvertFrom-Json
    $executable = Join-Path $AppDirectory ([string]$manifest.executable)
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "清单 executable 指向的文件不存在：$executable"
    }

    $actual = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectSha256 -and $actual -ne $ExpectSha256) {
        throw "回填前后哈希不一致：$actual != $ExpectSha256"
    }

    if (-not [regex]::IsMatch($text, '"sha256"\s*:\s*"[^"]*"')) {
        throw "app.json 里没有可回填的 sha256 字段：$appJson"
    }

    $replaced = [regex]::Replace($text, '"sha256"\s*:\s*"[^"]*"', ('"sha256": "' + $actual + '"'))

    $null = $replaced | ConvertFrom-Json
    [System.IO.File]::WriteAllText($appJson, $replaced, [System.Text.UTF8Encoding]::new($false))
    return $actual
}

$version = Get-ProductVersion -Path $propsPath
$packageName = "SDemo-Shell-$version"
$stageRoot = Join-Path $OutDir ('.stage-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stageRoot $packageName
$exitCode = 0

try {
    if (-not (Test-Path -LiteralPath $hostProject)) {
        throw "找不到宿主工程：$hostProject"
    }

    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

    # --- 1. publish 宿主 -------------------------------------------------------
    Write-Host "发布宿主 SDemo.Shell $version（$Configuration / win-x64）…"
    $publishArgs = @(
        'publish', $hostProject,
        '-c', $Configuration,
        '-r', 'win-x64',
        '-o', $packageRoot,
        '--nologo'
    )
    if (-not $SelfContained) {
        $publishArgs += @('--self-contained', 'false')
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（退出码 $LASTEXITCODE）。"
    }

    $exePath = Join-Path $packageRoot 'SDemo.Shell.exe'
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "publish 结果里缺少 SDemo.Shell.exe：$exePath"
    }

    # --- 2. apps 模板 + 可选应用 ------------------------------------------------
    $appsRoot = Join-Path $packageRoot 'apps'
    New-Item -ItemType Directory -Path $appsRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $appsRoot 'README.txt') -Encoding UTF8 -Value @(
        '把应用目录（含 app.json / widget.json / payload\）放进这里，壳启动时会扫描并校验。',
        '也可以用托盘菜单「安装应用（zip）」导入安装包。',
        '用户数据不在这个目录：%APPDATA%\SDemo\Apps\<appId>\（卸载不删）。'
    )

    foreach ($appPath in $Apps) {
        $resolved = (Resolve-Path -LiteralPath $appPath).Path
        $appJsonPath = Join-Path $resolved 'app.json'
        if (-not (Test-Path -LiteralPath $appJsonPath)) {
            throw "应用目录缺少 app.json：$resolved"
        }

        $appManifest = Get-Content -LiteralPath $appJsonPath -Raw | ConvertFrom-Json
        $appId = [string]$appManifest.id
        if (-not $appId) {
            throw "app.json 缺少 id：$appJsonPath"
        }

        $target = Join-Path $appsRoot $appId
        if (Test-Path -LiteralPath $target) {
            throw "两个 -Apps 的 appId 重复：$appId"
        }

        Copy-Item -LiteralPath $resolved -Destination $target -Recurse -Force

        # 打包前先把真实哈希写进清单（DA-76 / DA-86：sha256 不许手写）。
        $hash = Repair-ManifestSha256 -AppDirectory $target -ExpectSha256 ''
        Write-Host ("  应用 {0}：sha256 {1}" -f $appId, $hash)

        if (-not $SkipValidation) {
            $lint = Join-Path (Split-Path -Parent $repoRoot) 'tools\contract-lint.ps1'
            if (Test-Path -LiteralPath $lint) {
                & pwsh -NoProfile -File $lint -Quiet -NoInstalled $target
                if ($LASTEXITCODE -ne 0) {
                    throw "契约校验未通过：$appId（详见上方输出）"
                }
            }
            else {
                Write-Warning "找不到主仓 tools/contract-lint.ps1，跳过 Schema 校验。"
            }
        }
    }

    # --- 3. 安装说明 -----------------------------------------------------------
    $readme = @(
        "SDemo DesktopAssembly $version",
        '',
        '平台：Windows 10 19041+ / Windows 11，x64。',
        '运行时：需要 .NET 10 Desktop Runtime（本包为 framework-dependent；加 -SelfContained 可独立分发）。',
        'wasm 组件额外需要 WebView2 Runtime（Win11 与多数 Win10 已内置）。',
        '',
        '安装：解压 zip，运行 SDemo.Shell.exe；首次启动会扫描 apps\ 并注册开机自启（可在托盘关闭）。',
        '升级：退出外壳 → 替换程序文件（apps\ 可保留）→ 重新启动。升级后应用的 sha256 会重新登记。',
        '卸载：删除程序目录；用户数据在 %APPDATA%\SDemo\Apps\<appId>\，按需自行清理。',
        '日志：%LOCALAPPDATA%\SDemo\logs\desktop-assembly\yyyy-MM-dd.log',
        ''
    )
    Set-Content -LiteralPath (Join-Path $packageRoot 'README.txt') -Encoding UTF8 -Value $readme

    # --- 4. 压缩 + 校验和 ------------------------------------------------------
    $zipPath = Join-Path $OutDir "$packageName.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "压缩 → $zipPath"
    Compress-Archive -Path $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar = Join-Path $OutDir "$packageName.zip.sha256"
    [System.IO.File]::WriteAllText($sidecar, "$zipHash  $packageName.zip`n", [System.Text.UTF8Encoding]::new($false))

    Write-Host ''
    Write-Host '打包完成：'
    Write-Host ("  zip      : {0}（{1:N1} MB）" -f $zipPath, ((Get-Item -LiteralPath $zipPath).Length / 1MB))
    Write-Host ("  sha256   : {0}" -f $zipHash)
    Write-Host ("  校验和   : {0}" -f $sidecar)
    Write-Host ("  版本     : {0}" -f $version)
}
catch {
    [Console]::Error.WriteLine("打包失败：" + $_.Exception.Message)
    $exitCode = 1
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $exitCode
