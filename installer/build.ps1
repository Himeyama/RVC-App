#requires -version 7
<#
.SYNOPSIS
    RvcRealtimeGui を publish し、NSIS でインストーラー(exe)を作成する。
.PARAMETER Version
    インストーラーに埋め込むバージョン番号 (既定: 1.0.0)
.EXAMPLE
    ./installer/build.ps1 -Version 1.2.0
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "RvcRealtimeGui\RvcRealtimeGui.csproj"
$publishDir = Join-Path $repoRoot "RvcRealtimeGui\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish"
$stageDir = Join-Path $PSScriptRoot ".stage"

Write-Host "==> dotnet publish"
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir | Out-Null

Write-Host "==> stage: dotnet publish 出力をコピー"
Copy-Item "$publishDir\*" $stageDir -Recurse -Force

Write-Host "==> stage: git archive で Python ソース一式を展開 (python-src)"
$pythonSrcDir = Join-Path $stageDir "python-src"
New-Item -ItemType Directory -Path $pythonSrcDir | Out-Null
$archiveTar = Join-Path $env:TEMP "rvc-python-src.tar"
git -C $repoRoot archive --format=tar -o $archiveTar HEAD -- . ':!RvcRealtimeGui' ':!installer'
if ($LASTEXITCODE -ne 0) { throw "git archive failed" }
tar -xf $archiveTar -C $pythonSrcDir
if ($LASTEXITCODE -ne 0) { throw "tar extraction failed" }
Remove-Item $archiveTar -Force

$makensis = "C:\Program Files (x86)\NSIS\makensis.exe"
if (-not (Test-Path $makensis)) {
    $cmd = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($cmd) { $makensis = $cmd.Source }
    else { throw "makensis.exe が見つかりません。NSIS をインストールしてください。" }
}

Write-Host "==> makensis"
& $makensis "/DSTAGE_DIR=$stageDir" "/DAPP_VERSION=$Version" (Join-Path $PSScriptRoot "RvcRealtimeGui.nsi")
if ($LASTEXITCODE -ne 0) { throw "makensis failed" }

Remove-Item $stageDir -Recurse -Force

Write-Host "==> done: $PSScriptRoot\RvcRealtimeGui-Setup-$Version.exe"
