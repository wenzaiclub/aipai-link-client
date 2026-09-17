# 下载官方 EasyTier 引擎（LGPL-3.0）到指定目录，供客户端运行时调用。
#
#   powershell -ExecutionPolicy Bypass -File tools\fetch-engine.ps1 -OutDir engine
#
# 版本刻意写死：服务端的锚点节点要和客户端引擎版本对得上，
# 跟着 latest 跑容易在升级当天连不上。

param(
    [string]$OutDir = "engine",
    [string]$Version = "v2.6.4"
)

$ErrorActionPreference = "Stop"

$pkg = "easytier-windows-x86_64-$Version"
$url = "https://github.com/EasyTier/EasyTier/releases/download/$Version/$pkg.zip"

Write-Host "==> 下载 $url"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("easytier-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    $zip = Join-Path $tmp "engine.zip"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $tmp "x") -Force

    $root = Join-Path $tmp "x"
    Get-ChildItem -Path $root -Recurse -Filter "easytier-core*" | Select-Object -First 1 |
        Copy-Item -Destination (Join-Path $OutDir "easytier-core.exe") -Force
    Get-ChildItem -Path $root -Recurse -Filter "easytier-cli*" | Select-Object -First 1 |
        Copy-Item -Destination (Join-Path $OutDir "easytier-cli.exe") -Force
    Get-ChildItem -Path $root -Recurse -Filter "LICENSE*" -ErrorAction SilentlyContinue |
        Select-Object -First 1 | Copy-Item -Destination $OutDir -Force -ErrorAction SilentlyContinue

    Write-Host "==> 引擎已放到 $OutDir"
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
