@echo off
setlocal
chcp 65001 >nul

set "ROOT=%~dp0"

if exist "%ROOT%publish\AuthIsland.exe" (
    start "" "%ROOT%publish\AuthIsland.exe"
    exit /b 0
)

if exist "%ROOT%AuthIsland\bin\Release\net8.0-windows\AuthIsland.exe" (
    start "" "%ROOT%AuthIsland\bin\Release\net8.0-windows\AuthIsland.exe"
    exit /b 0
)

if exist "%ROOT%AuthIsland\bin\Release\net8.0-windows\AuthIsland.dll" (
    where dotnet >nul 2>nul
    if not errorlevel 1 (
        start "" dotnet "%ROOT%AuthIsland\bin\Release\net8.0-windows\AuthIsland.dll"
        exit /b 0
    )
)

echo 没有找到可执行文件。
echo 请先运行 build.ps1（需要 .NET 8 SDK），或使用 publish 目录下的自包含版本。
pause
