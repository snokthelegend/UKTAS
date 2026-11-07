@echo off
REM ===========================================================================
REM  ULTRAKILL TAS Tool - release packager.
REM
REM  Builds Release, lays out dist\UltrakillTAS-<version>\ with a drop-in "ultrakill" folder
REM  and zips it to dist\UltrakillTAS-<version>.zip.
REM
REM  Optional:  package.bat "D:\Games\ULTRAKILL"     (explicit game folder)
REM ===========================================================================
setlocal
set "SCRIPT=%~dp0package.ps1"
if not exist "%SCRIPT%" (
    echo package.ps1 was not found next to package.bat.
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
