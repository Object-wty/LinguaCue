@echo off
setlocal
cd /d "%~dp0"

rem Double-click this file to build all portable packages.
rem The PowerShell script auto-detects the model directory.

where pwsh.exe >nul 2>&1
if %errorlevel%==0 (
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Packages.ps1"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Packages.ps1"
)

set "exitCode=%errorlevel%"
if %exitCode%==0 (
    echo.
    echo Build completed. Files are in artifacts\packages\
) else (
    echo.
    echo Build failed with exit code %exitCode%. See the error above.
)
pause
exit /b %exitCode%
