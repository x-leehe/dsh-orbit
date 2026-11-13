# 产出两个单文件 exe：
#   dist\DSH-Orbit.exe      自包含（自带 .NET 运行时，拷走就能跑）
#   dist-lite\DSH-Orbit.exe 框架依赖（体积小，需要 .NET 10 桌面运行时）
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

foreach ($name in 'DSH-Orbit', 'DshStatus', 'DshStatusWidget') {
    Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o dist
Remove-Item (Join-Path $root 'dist\*.xml') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root 'dist\DshStatus.exe') -Force -ErrorAction SilentlyContinue

dotnet publish -c Release -o dist-lite
Remove-Item (Join-Path $root 'dist-lite\*.xml') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root 'dist-lite\DshStatus.exe') -Force -ErrorAction SilentlyContinue

$full = Join-Path $root 'dist\DSH-Orbit.exe'
$lite = Join-Path $root 'dist-lite\DSH-Orbit.exe'
if (-not (Test-Path $full)) { throw "发布失败：$full 不存在" }
if (-not (Test-Path $lite)) { throw "发布失败：$lite 不存在" }

$fullMb = [math]::Round((Get-Item $full).Length / 1MB, 1)
$liteMb = [math]::Round((Get-Item $lite).Length / 1MB, 2)
Write-Host ""
Write-Host "已生成：$full（$fullMb MB，自带运行时）" -ForegroundColor Green
Write-Host "已生成：$lite（$liteMb MB，需 .NET 10 桌面运行时）" -ForegroundColor Green
Write-Host "  双击：启动挂件（只出卡片）"
Write-Host "  命令行：DSH-Orbit --show-window"
