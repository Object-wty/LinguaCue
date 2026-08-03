@echo off
setlocal
cd /d "%~dp0"

rem 直接双击此文件即可生成四个平台的便携包。
rem PowerShell 脚本会自动寻找 models、artifacts\model-source、Debug/Release 输出或 environment 中的模型。

where pwsh.exe >nul 2>&1
if %errorlevel%==0 (
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Packages.ps1"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Packages.ps1"
)

set "exitCode=%errorlevel%"
if %exitCode%==0 (
    echo.
    echo 打包完成，文件位于 artifacts\packages\
) else (
    echo.
    echo 打包失败，退出码 %exitCode%。请查看上面的错误信息。
)
pause
exit /b %exitCode%
