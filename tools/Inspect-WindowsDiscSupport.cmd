@echo off
setlocal
title NOIR Windows DVD component check
echo This checks Windows DVD components and codec activation.
echo It does not open a disc, play video, or change Windows settings.
echo.
set "reportFolder=%LOCALAPPDATA%\NoirMediaPlayer\NativeDiscTests"
if not exist "%reportFolder%" mkdir "%reportFolder%"
if not exist "%reportFolder%" (
    echo The diagnostic report folder could not be created.
    pause
    exit /b 1
)
set "reportFile=%reportFolder%\components-%RANDOM%-%RANDOM%-%RANDOM%.json"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "%~dp0Inspect-WindowsDiscSupport.ps1" -ReportPath "%reportFile%"
if errorlevel 1 (
    echo The component check failed. The error is shown above.
    pause
    exit /b 1
)
echo Report: "%reportFile%"
echo You can close this window after the report has been read.
pause
