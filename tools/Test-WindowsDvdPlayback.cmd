@echo off
setlocal
title NOIR Windows DVD playback test
echo.
echo This opens the SEPARATE Windows DVD playback test.
echo Insert a DVD in drive G: before continuing. Blu-ray discs use the main NOIR player.
echo.

if not exist "G:\VIDEO_TS\VIDEO_TS.IFO" (
    echo A DVD could not be read in G:.
    echo Insert a DVD, close this window, then double-click this launcher again.
    echo.
    pause
    exit /b 1
)

set "testProject=%~dp0NoirMediaPlayer.DvdTestHost\NoirMediaPlayer.DvdTestHost.csproj"
set "testProgram=%~dp0NoirMediaPlayer.DvdTestHost\bin\x64\Release\net10.0-windows\NoirMediaPlayer.DvdTestHost.exe"
if not exist "%testProgram%" (
    echo Building the native DVD test...
    dotnet build "%testProject%" --configuration Release --verbosity minimal -p:Platform=x64 -p:UseSharedCompilation=false -p:RestoreIgnoreFailedSources=true -m:1 -nr:false
    if errorlevel 1 (
        echo.
        echo The build failed. Copy the error from this window into the chat.
        pause
        exit /b 1
    )
)

set "reportFolder=%LOCALAPPDATA%\NoirMediaPlayer\NativeDiscTests"
if not exist "%reportFolder%" mkdir "%reportFolder%"
if not exist "%reportFolder%" (
    echo The diagnostic report folder could not be created.
    pause
    exit /b 1
)
set "reportFile=%reportFolder%\dvd-native-%RANDOM%-%RANDOM%-%RANDOM%.jsonl"
echo Opening: NOIR - Windows DVD playback test
echo Report: "%reportFile%"
echo.
echo Check picture and sound, DVD menus, chapters, and resizing.
echo Close the test window when finished.
start "" /wait "%testProgram%" --source G:\VIDEO_TS --report "%reportFile%"
if errorlevel 1 (
    echo.
    echo The test reported an error. Copy the message from the test window or this report:
    echo "%reportFile%"
)
echo.
echo Test finished. Diagnostic report:
echo "%reportFile%"
pause
