# 构建 AuthIsland
# 用法: .\build.ps1  (需要 .NET 8 SDK)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "→ 构建 AuthIsland ..." -ForegroundColor Cyan
dotnet build .\AuthIsland\AuthIsland.csproj -c Release --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "构建失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $PSScriptRoot "AuthIsland\bin\Release\net8.0-windows\AuthIsland.exe"
Write-Host ""
Write-Host "✓ 构建完成" -ForegroundColor Green
Write-Host "  $exe"
Write-Host ""
Write-Host "启动: & '$exe'"
