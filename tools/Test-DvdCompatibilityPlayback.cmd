@echo off
setlocal
title NOIR DVD compatibility playback
echo This opens the updated MAIN NOIR player using the DVD compatibility backend.
echo This is not the separate Windows DVD playback test.
echo.
if not exist "G:\VIDEO_TS\VIDEO_TS.IFO" (
    echo Insert a DVD in G:, then run this launcher again.
    pause
    exit /b 1
)
set "playerProgram=%~dp0..\src\NoirMediaPlayer\bin\x64\Release\net10.0-windows\win-x64\NoirMediaPlayer.exe"
if not exist "%playerProgram%" (
    echo Build the main player in Release x64 first.
    pause
    exit /b 1
)
if not exist "%~dp0..\src\NoirMediaPlayer\bin\x64\Release\net10.0-windows\win-x64\libvlc\win-x64\plugins\access\libdvdnav_plugin.dll" (
    echo The DVD navigation plug-in has not been copied. Rebuild the main player.
    pause
    exit /b 1
)
echo Check picture, sound, menus, chapters and resizing in the main player.
echo If playback fails, leave the error dialog open; its diagnostic is saved automatically.
start "" "%playerProgram%" G:\
