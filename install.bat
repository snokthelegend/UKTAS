@echo off
REM ===========================================================================
REM  ULTRAKILL TAS Tool - one-click installer.
REM
REM  Double-click this file. It finds your ULTRAKILL folder, installs BepInEx
REM  if needed and copies the plugin into place. Nothing to compile.
REM
REM  Optional:  install.bat "D:\Games\ULTRAKILL"     (explicit game folder)
REM ===========================================================================
setlocal
set "SCRIPT=%~dp0install.ps1"
if not exist "%SCRIPT%" (
    echo install.ps1 was not found next to install.bat. Extract the whole zip first.
    pause
    exit /b 1
)

set "GAME_ARG=%~1"
if defined GAME_ARG (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -GameDir "%GAME_ARG%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
)
exit /b %ERRORLEVEL%
