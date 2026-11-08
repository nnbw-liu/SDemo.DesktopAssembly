param(
    [string]$Configuration = 'Release',
    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SDemo.Hub.App\SDemo.Hub.App.csproj'
$publishDir = Join-Path $root 'out\hub-publish'

Write-Host "发布 SDemo Hub ($Configuration) ..."
dotnet publish $project -c $Configuration -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

$exe = Join-Path $publishDir 'SDemo.HubApp.exe'
if (-not (Test-Path $exe)) { throw "发布产物缺少 $exe" }

$version = (Get-Item $exe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0' }
$version = $version.Split('+')[0].Trim()

$dist = Join-Path $root $OutDir
$stage = Join-Path $dist "SDemo-Hub-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $stage -Recurse -Force

$zip = Join-Path $dist "SDemo-Hub-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "版本: $version"
Write-Host "目录: $stage"
Write-Host "压缩包: $zip"
Write-Host "SHA256: $hash"
