@echo off
setlocal
title NOIR first-party DVD structure check
echo This reads DVD information files using NOIR's own C# reader.
echo It does not start playback or change Windows settings.
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "%~dp0Inspect-DvdStructure.ps1"
if errorlevel 1 echo The DVD structure check failed. The error is shown above.
echo You can close this window when finished.
pause
