@echo off
REM ===========================================================================
REM  ULTRAKILL TAS Tool - build and deploy
REM
REM  Usage:
REM    build_and_deploy.bat                              auto-detect / saved path
REM    build_and_deploy.bat "D:\Games\ULTRAKILL"          explicit install folder
REM    build_and_deploy.bat --build-only
REM    build_and_deploy.bat --deploy-only
REM    build_and_deploy.bat --use-saved --nopause         CI / unattended
REM
REM  Works on any Windows 10/11 PC. Everything missing is installed for the
REM  current user, no admin needed:
REM    - BepInEx 5 (x64) into the game folder
REM    - a portable .NET 8 SDK into %LOCALAPPDATA%\Microsoft\dotnet
REM      (only if no .NET SDK or Visual Studio MSBuild is present)
REM  The selected game folder is saved in build_config.ini next to this script.
REM ===========================================================================

REM The real work runs in a child cmd. If anything crashes, the window still
REM stays open here so the error can be read.
if "%~1"=="--inner" goto inner_start
setlocal
set "UKTAS_NOPAUSE=0"
for %%A in (%*) do if /i "%%~A"=="--nopause" set "UKTAS_NOPAUSE=1"
cmd /d /c ""%~f0" --inner %*"
set "UKTAS_RC=%ERRORLEVEL%"
if "%UKTAS_NOPAUSE%"=="1" exit /b %UKTAS_RC%
echo.
if not "%UKTAS_RC%"=="0" echo Script finished with errors (exit code %UKTAS_RC%). Read the messages above.
pause
exit /b %UKTAS_RC%

:inner_start
REM shift /1 drops "--inner" but keeps %0, so %~dp0 still points here.
shift /1
setlocal EnableExtensions DisableDelayedExpansion
title ULTRAKILL TAS Tool - Build and Deploy
pushd "%~dp0"
if errorlevel 1 goto err_pushd

set "PROJECT_FILE=UltrakillTAS.csproj"
set "CONFIGURATION=Release"
set "OUTPUT_DLL=bin\Release\UltrakillTAS.dll"
set "OUTPUT_PDB=bin\Release\UltrakillTAS.pdb"
set "LOG_FILE=%~dp0build_and_deploy.log"
set "CONFIG_FILE=%~dp0build_config.ini"
set "LEGACY_PATH_FILE=%~dp0game_path.txt"
set "NUGET_SOURCE=https://api.nuget.org/v3/index.json"
set "BEPINEX_VERSION=5.4.23.6"
set "BEPINEX_URL=https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip"
set "BEPINEX_SHA256=6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755"
set "DOTNET_INSTALL_URL=https://dot.net/v1/dotnet-install.ps1"
set "ULTRAKILL_APPID=1229490"
set "NOPAUSE=0"
set "BUILD_ONLY=0"
set "DEPLOY_ONLY=0"
set "USE_SAVED=0"
set "ARG_GAME_DIR="
set "GAME_DIR="
set "GAME_INPUT="
set "SAVED_GAME_DIR="
set "DETECTED_GAME_DIR="
set "PROMPT_DEFAULT="
set "ATTEMPTS=0"
set "FAIL_MSG="
set "BUILD_TOOL="
set "DOTNET_EXE="
set "MSBUILD_EXE="
set "PSEXE="
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "PF86=%ProgramFiles%"
if not defined TEMP set "TEMP=%~dp0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "C_INFO="
set "C_OK="
set "C_WARN="
set "C_ERR="
set "C_DIM="
set "C_RESET="
call :init_colors
call :find_powershell

REM ---------------------------------------------------------------------------
REM  Arguments
REM ---------------------------------------------------------------------------
:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--nopause" goto arg_nopause
if /i "%~1"=="--build-only" goto arg_build_only
if /i "%~1"=="--deploy-only" goto arg_deploy_only
if /i "%~1"=="--use-saved" goto arg_use_saved
if /i "%~1"=="--no-prompt" goto arg_use_saved
if /i "%~1"=="--help" goto usage
if /i "%~1"=="-h" goto usage
if /i "%~1"=="/?" goto usage
if defined ARG_GAME_DIR goto args_unknown
set "ARG_GAME_DIR=%~1"
shift
goto parse_args

:arg_nopause
set "NOPAUSE=1"
set "USE_SAVED=1"
shift
goto parse_args

:arg_build_only
set "BUILD_ONLY=1"
shift
goto parse_args

:arg_deploy_only
set "DEPLOY_ONLY=1"
shift
goto parse_args

:arg_use_saved
set "USE_SAVED=1"
shift
goto parse_args

:args_unknown
echo [ERROR] Unknown argument: %~1
goto usage_fail

:args_done
echo ===========================================================================
echo  %C_INFO%ULTRAKILL TAS Tool%C_RESET% - Build and Deploy
echo ===========================================================================
echo  Project : %~dp0%PROJECT_FILE%
echo  Config  : %CONFIG_FILE%
echo  Log     : %LOG_FILE%
echo.

if not exist "%PROJECT_FILE%" goto err_no_project
if not defined PSEXE goto err_no_powershell

REM Start a fresh log so every later step can append to it.
> "%LOG_FILE%" echo ===== ULTRAKILL TAS build %DATE% %TIME% =====
if not exist "%LOG_FILE%" goto err_log

REM ---------------------------------------------------------------------------
REM  Resolve the game folder
REM ---------------------------------------------------------------------------
if defined ARG_GAME_DIR set "GAME_INPUT=%ARG_GAME_DIR%"
if not defined GAME_INPUT if defined ULTRAKILL_DIR set "GAME_INPUT=%ULTRAKILL_DIR%"
if defined GAME_INPUT goto explicit_path

call :load_config
if defined SAVED_GAME_DIR goto saved_path_available

:no_saved_path
echo   Searching for ULTRAKILL (Steam libraries and common folders)...
call :detect_game
if defined DETECTED_GAME_DIR goto adopt_detected

if "%USE_SAVED%"=="1" goto no_game
echo   %C_WARN%[INFO]%C_RESET% ULTRAKILL was not found automatically.
echo   Tip: in Steam, right-click ULTRAKILL ^> Manage ^> Browse local files,
echo        then copy the folder path from the Explorer address bar.
call :prompt_for_game ""
if errorlevel 1 goto no_game
goto game_ready

:saved_path_available
call :valid_game "%SAVED_GAME_DIR%"
if errorlevel 1 goto invalid_saved
set "GAME_DIR=%SAVED_GAME_DIR%"
if "%USE_SAVED%"=="1" goto use_saved_notice
echo.
echo   %C_INFO%Saved game folder:%C_RESET% %SAVED_GAME_DIR%
call :prompt_for_game "%SAVED_GAME_DIR%"
if errorlevel 1 goto no_game
goto game_ready

:use_saved_notice
echo   %C_OK%[OK]%C_RESET% Using saved game folder: %SAVED_GAME_DIR%
goto save_and_ready

:invalid_saved
echo   %C_WARN%[WARN]%C_RESET% Saved path is not valid on this PC: %SAVED_GAME_DIR%
set "SAVED_GAME_DIR="
set "GAME_DIR="
goto no_saved_path

:adopt_detected
set "GAME_DIR=%DETECTED_GAME_DIR%"
echo   %C_OK%[OK]%C_RESET% Found ULTRAKILL: %DETECTED_GAME_DIR%
goto save_and_ready

:explicit_path
call :normalize_input
call :valid_game "%GAME_INPUT%"
if errorlevel 1 goto bad_game
set "GAME_DIR=%GAME_INPUT%"
goto save_and_ready

:save_and_ready
if not defined GAME_DIR goto bad_game
for %%I in ("%GAME_DIR%") do set "GAME_DIR=%%~fI"
call :save_config
goto game_ready

REM ---------------------------------------------------------------------------
REM  Prompt with a persistent default. Returns 0 with GAME_DIR set, or 1.
REM ---------------------------------------------------------------------------
:prompt_for_game
set "PROMPT_DEFAULT=%~1"
set "ATTEMPTS=0"
:prompt_loop
set /a ATTEMPTS+=1
set "GAME_INPUT="
if defined PROMPT_DEFAULT echo   Press Enter to keep it, or paste a new path:
if not defined PROMPT_DEFAULT echo   Paste the full path to your ULTRAKILL folder:
set /p "GAME_INPUT=  > "
if not defined GAME_INPUT if defined PROMPT_DEFAULT set "GAME_INPUT=%PROMPT_DEFAULT%"
if not defined GAME_INPUT goto prompt_empty
call :normalize_input
if not defined GAME_INPUT goto prompt_empty
call :valid_game "%GAME_INPUT%"
if not errorlevel 1 goto prompt_ok
echo   %C_ERR%[ERROR]%C_RESET% That folder does not contain ULTRAKILL.exe or ULTRAKILL_Data.
if %ATTEMPTS% GEQ 3 goto prompt_give_up
goto prompt_loop
:prompt_empty
echo   %C_ERR%[ERROR]%C_RESET% No path entered.
if %ATTEMPTS% GEQ 3 goto prompt_give_up
goto prompt_loop
:prompt_give_up
set "GAME_DIR="
exit /b 1
:prompt_ok
for %%I in ("%GAME_INPUT%") do set "GAME_DIR=%%~fI"
call :save_config
exit /b 0

REM Strips quotes, accepts a path to ULTRAKILL.exe itself, makes it absolute.
:normalize_input
if not defined GAME_INPUT exit /b 0
set "GAME_INPUT=%GAME_INPUT:"=%"
if not defined GAME_INPUT exit /b 0
for %%I in ("%GAME_INPUT%") do if /i "%%~nxI"=="ULTRAKILL.exe" set "GAME_INPUT=%%~dpI."
for %%I in ("%GAME_INPUT%") do set "GAME_INPUT=%%~fI"
if "%GAME_INPUT:~-1%"=="\" if not "%GAME_INPUT:~-2%"==":\" set "GAME_INPUT=%GAME_INPUT:~0,-1%"
exit /b 0

REM ---------------------------------------------------------------------------
REM  Validation / configuration / detection
REM ---------------------------------------------------------------------------
:valid_game
if "%~1"=="" exit /b 1
if exist "%~1\ULTRAKILL.exe" exit /b 0
if exist "%~1\ULTRAKILL_Data\" exit /b 0
exit /b 1

:load_config
set "SAVED_GAME_DIR="
if exist "%CONFIG_FILE%" for /f "usebackq eol=; tokens=1,* delims==" %%A in ("%CONFIG_FILE%") do if /i "%%A"=="GAME_DIR" set "SAVED_GAME_DIR=%%B"
if not defined SAVED_GAME_DIR if exist "%LEGACY_PATH_FILE%" for /f "usebackq delims=" %%G in ("%LEGACY_PATH_FILE%") do if not defined SAVED_GAME_DIR set "SAVED_GAME_DIR=%%G"
if defined SAVED_GAME_DIR for %%I in ("%SAVED_GAME_DIR%") do set "SAVED_GAME_DIR=%%~fI"
exit /b 0

:save_config
if not defined GAME_DIR exit /b 0
> "%CONFIG_FILE%" echo ; ULTRAKILL TAS Tool build configuration
>> "%CONFIG_FILE%" echo GAME_DIR=%GAME_DIR%
if not exist "%CONFIG_FILE%" echo   %C_WARN%[WARN]%C_RESET% Could not write %CONFIG_FILE%; the path will not be remembered.
exit /b 0

:detect_game
set "DETECTED_GAME_DIR="
REM 1) Steam's own records: the uninstall entry for ULTRAKILL, then every
REM    library listed in libraryfolders.vdf (games on other drives).
set "UKTAS_PS_DETECT=$ErrorActionPreference='SilentlyContinue'; $c=New-Object System.Collections.ArrayList; foreach($k in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App %ULTRAKILL_APPID%','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App %ULTRAKILL_APPID%')){ $p=Get-ItemProperty -Path $k; if($p -and $p.InstallLocation){ [void]$c.Add([string]$p.InstallLocation) } }; $roots=New-Object System.Collections.ArrayList; foreach($k in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')){ $p=Get-ItemProperty -Path $k; if($p -and $p.SteamPath){ [void]$roots.Add([string]$p.SteamPath) }; if($p -and $p.InstallPath){ [void]$roots.Add([string]$p.InstallPath) } }; foreach($r in $roots){ $r=$r.Replace('/','\'); [void]$c.Add((Join-Path $r 'steamapps\common\ULTRAKILL')); $v=Join-Path $r 'steamapps\libraryfolders.vdf'; if(Test-Path -LiteralPath $v){ foreach($m in [regex]::Matches([IO.File]::ReadAllText($v),'\x22path\x22\s+\x22([^\x22]+)\x22')){ $l=$m.Groups[1].Value.Replace('\\','\'); [void]$c.Add((Join-Path $l 'steamapps\common\ULTRAKILL')) } } }; foreach($g in $c){ if($g -and (Test-Path -LiteralPath (Join-Path $g 'ULTRAKILL.exe'))){ Write-Output $g; break } }"
for /f "usebackq delims=" %%P in (`call "%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression $env:UKTAS_PS_DETECT" 2^>nul`) do if not defined DETECTED_GAME_DIR set "DETECTED_GAME_DIR=%%P"
set "UKTAS_PS_DETECT="
if defined DETECTED_GAME_DIR call :valid_game "%DETECTED_GAME_DIR%"
if errorlevel 1 set "DETECTED_GAME_DIR="
if defined DETECTED_GAME_DIR exit /b 0
REM 2) Common folders on every drive (GOG / manual copies / odd setups).
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if exist "%%D:\" call :check_drive %%D
exit /b 0

:check_drive
call :check_game_dir "%~1:\SteamLibrary\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\Steam\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\Games\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\Games\SteamLibrary\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\Games\Steam\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\SteamGames\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\Program Files\Steam\steamapps\common\ULTRAKILL"
call :check_game_dir "%~1:\Games\ULTRAKILL"
call :check_game_dir "%~1:\GOG Games\ULTRAKILL"
call :check_game_dir "%~1:\ULTRAKILL"
exit /b 0

:check_game_dir
if defined DETECTED_GAME_DIR exit /b 0
if exist "%~1\ULTRAKILL.exe" set "DETECTED_GAME_DIR=%~1"
exit /b 0

REM ---------------------------------------------------------------------------
REM  Game readiness
REM ---------------------------------------------------------------------------
:game_ready
call :valid_game "%GAME_DIR%"
if errorlevel 1 goto bad_game

echo.
echo   %C_OK%[OK]%C_RESET% ULTRAKILL: %GAME_DIR%
echo.
>> "%LOG_FILE%" echo GameDir: %GAME_DIR%
>> "%LOG_FILE%" echo Config: %CONFIG_FILE%

set "MANAGED=%GAME_DIR%\ULTRAKILL_Data\Managed"
if not exist "%MANAGED%\UnityEngine.CoreModule.dll" goto err_managed
if not exist "%MANAGED%\Assembly-CSharp.dll" goto err_managed
if not exist "%MANAGED%\Unity.InputSystem.dll" goto err_managed
if not exist "%MANAGED%\UnityEngine.UI.dll" goto err_managed

echo [1/6] Checking for BepInEx 5...
call :ensure_bepinex
if not exist "%GAME_DIR%\BepInEx\core\BepInEx.dll" goto err_bepinex
if not exist "%GAME_DIR%\BepInEx\core\0Harmony.dll" goto err_bepinex
echo   %C_OK%[OK]%C_RESET% BepInEx 5 is installed.
if not exist "%GAME_DIR%\BepInEx\plugins\" mkdir "%GAME_DIR%\BepInEx\plugins" >nul 2>&1

if "%DEPLOY_ONLY%"=="1" goto deploy_only

REM ---------------------------------------------------------------------------
REM  Toolchain: dotnet -> MSBuild -> portable dotnet -> winget
REM ---------------------------------------------------------------------------
echo [2/6] Checking for the .NET SDK / MSBuild...
call :try_dotnet_sdk
if defined BUILD_TOOL goto toolchain_ready
call :try_msbuild
if defined BUILD_TOOL goto toolchain_ready
call :bootstrap_dotnet
if defined BUILD_TOOL goto toolchain_ready
call :try_winget
if defined BUILD_TOOL goto toolchain_ready
goto no_tool

:try_dotnet_sdk
for /f "delims=" %%D in ('where dotnet 2^>nul') do if not defined BUILD_TOOL call :probe_dotnet "%%D"
if defined BUILD_TOOL exit /b 0
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" call :probe_dotnet "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if defined BUILD_TOOL exit /b 0
if exist "%USERPROFILE%\.dotnet\dotnet.exe" call :probe_dotnet "%USERPROFILE%\.dotnet\dotnet.exe"
if defined BUILD_TOOL exit /b 0
if exist "%ProgramFiles%\dotnet\dotnet.exe" call :probe_dotnet "%ProgramFiles%\dotnet\dotnet.exe"
exit /b 0

REM A dotnet.exe only counts if it has at least one SDK (not just a runtime).
:probe_dotnet
if not exist "%~1" exit /b 0
"%~1" --list-sdks 2>nul | findstr /r "^[0-9][0-9]*\.[0-9]" >nul
if errorlevel 1 exit /b 0
set "DOTNET_EXE=%~f1"
set "DOTNET_ROOT=%~dp1"
set "PATH=%~dp1;%PATH%"
set "BUILD_TOOL=dotnet"
exit /b 0

:try_msbuild
if not exist "%PF86%\Microsoft Visual Studio\Installer\vswhere.exe" exit /b 0
for /f "usebackq delims=" %%M in (`call "%PF86%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do if not defined MSBUILD_EXE set "MSBUILD_EXE=%%M"
if not defined MSBUILD_EXE exit /b 0
if not exist "%MSBUILD_EXE%" set "MSBUILD_EXE="
if not defined MSBUILD_EXE exit /b 0
set "BUILD_TOOL=msbuild"
exit /b 0

:bootstrap_dotnet
echo   %C_WARN%[INFO]%C_RESET% No .NET SDK found. Installing a portable .NET 8 SDK (about 200 MB)...
set "DOTNET_DIR=%LOCALAPPDATA%\Microsoft\dotnet"
if not defined LOCALAPPDATA set "DOTNET_DIR=%USERPROFILE%\.dotnet"
set "INSTALL_PS1=%TEMP%\dotnet-install-uktas.ps1"
>> "%LOG_FILE%" echo ===== .NET SDK bootstrap into %DOTNET_DIR% =====

echo   Downloading %DOTNET_INSTALL_URL% ...
call :download "%DOTNET_INSTALL_URL%" "%INSTALL_PS1%"
if errorlevel 1 goto bootstrap_failed

echo   Installing SDK into "%DOTNET_DIR%" (this can take a few minutes)...
set "UKTAS_PS1=%INSTALL_PS1%"
set "UKTAS_DOTNET_DIR=%DOTNET_DIR%"
"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072; & $env:UKTAS_PS1 -Channel 8.0 -Quality GA -InstallDir $env:UKTAS_DOTNET_DIR -NoPath; exit $LASTEXITCODE" >> "%LOG_FILE%" 2>&1
if exist "%INSTALL_PS1%" del /f /q "%INSTALL_PS1%" >nul 2>&1
call :probe_dotnet "%DOTNET_DIR%\dotnet.exe"
if not defined BUILD_TOOL goto bootstrap_failed
echo   %C_OK%[OK]%C_RESET% Portable SDK installed. No restart needed.
exit /b 0

:bootstrap_failed
echo   %C_WARN%[WARN]%C_RESET% The portable .NET installer failed (details in the log); trying winget...
exit /b 0

:try_winget
where winget >nul 2>nul
if errorlevel 1 exit /b 0
echo   Installing Microsoft.DotNet.SDK.8 with winget (a UAC prompt may appear)...
winget install --id Microsoft.DotNet.SDK.8 -e --source winget --silent --accept-package-agreements --accept-source-agreements >> "%LOG_FILE%" 2>&1
call :probe_dotnet "%ProgramFiles%\dotnet\dotnet.exe"
if defined BUILD_TOOL exit /b 0
call :probe_dotnet "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
exit /b 0

:toolchain_ready
if /i "%BUILD_TOOL%"=="dotnet" echo   %C_OK%[OK]%C_RESET% Using dotnet: %DOTNET_EXE%
if /i "%BUILD_TOOL%"=="dotnet" "%DOTNET_EXE%" --version >> "%LOG_FILE%" 2>&1
if /i "%BUILD_TOOL%"=="msbuild" echo   %C_OK%[OK]%C_RESET% Using MSBuild: %MSBUILD_EXE%
echo.

REM ---------------------------------------------------------------------------
REM  Restore + build
REM ---------------------------------------------------------------------------
echo [3/6] Restoring NuGet packages...
if /i "%BUILD_TOOL%"=="dotnet" "%DOTNET_EXE%" restore "%PROJECT_FILE%" --source "%NUGET_SOURCE%" "-p:UltrakillPath=%GAME_DIR%" "-p:GameDir=%GAME_DIR%" >> "%LOG_FILE%" 2>&1
if /i "%BUILD_TOOL%"=="msbuild" "%MSBUILD_EXE%" "%PROJECT_FILE%" /t:Restore "/p:RestoreSources=%NUGET_SOURCE%" /p:Configuration=%CONFIGURATION% "/p:UltrakillPath=%GAME_DIR%" "/p:GameDir=%GAME_DIR%" /v:minimal /nologo >> "%LOG_FILE%" 2>&1
if errorlevel 1 goto err_restore

echo [4/6] Building %CONFIGURATION%...
if /i "%BUILD_TOOL%"=="dotnet" "%DOTNET_EXE%" build "%PROJECT_FILE%" -c %CONFIGURATION% --no-restore "-p:UltrakillPath=%GAME_DIR%" "-p:GameDir=%GAME_DIR%" -v minimal >> "%LOG_FILE%" 2>&1
if /i "%BUILD_TOOL%"=="msbuild" "%MSBUILD_EXE%" "%PROJECT_FILE%" /t:Build /p:Configuration=%CONFIGURATION% "/p:UltrakillPath=%GAME_DIR%" "/p:GameDir=%GAME_DIR%" /v:minimal /nologo >> "%LOG_FILE%" 2>&1
if errorlevel 1 goto err_build

if not exist "%OUTPUT_DLL%" goto err_no_dll
echo [5/6] Build succeeded: %OUTPUT_DLL%
echo.

:deploy_only
if "%BUILD_ONLY%"=="1" goto success_build_only
if not exist "%OUTPUT_DLL%" goto err_no_dll

set "RUNNING=0"
tasklist /FI "IMAGENAME eq ULTRAKILL.exe" /NH 2>nul | findstr /I "ULTRAKILL.exe" >nul 2>&1
if not errorlevel 1 set "RUNNING=1"
if "%RUNNING%"=="1" goto game_running

:copy_plugin
set "PLUGIN_DIR=%GAME_DIR%\BepInEx\plugins"
echo [6/6] Deploying to %PLUGIN_DIR%...
if not exist "%PLUGIN_DIR%\" mkdir "%PLUGIN_DIR%" >nul 2>&1
REM Remove copies left by the old -p:DeployOnBuild layout so BepInEx does not
REM load the plugin twice.
if exist "%PLUGIN_DIR%\UltrakillTAS\UltrakillTAS.dll" del /f /q "%PLUGIN_DIR%\UltrakillTAS\UltrakillTAS.dll" >nul 2>&1
if exist "%PLUGIN_DIR%\UltrakillTAS\UltrakillTAS.pdb" del /f /q "%PLUGIN_DIR%\UltrakillTAS\UltrakillTAS.pdb" >nul 2>&1
copy /Y "%OUTPUT_DLL%" "%PLUGIN_DIR%\UltrakillTAS.dll" >nul 2>&1
if errorlevel 1 goto err_copy
if not exist "%PLUGIN_DIR%\UltrakillTAS.dll" goto err_copy
if exist "%OUTPUT_PDB%" copy /Y "%OUTPUT_PDB%" "%PLUGIN_DIR%\UltrakillTAS.pdb" >nul 2>&1

echo.
echo ===========================================================================
echo  %C_OK%SUCCESS%C_RESET%
echo ===========================================================================
echo  Installed : %PLUGIN_DIR%\UltrakillTAS.dll
echo  Game      : %GAME_DIR%
echo  Config    : %CONFIG_FILE%
echo  BepInEx   : %GAME_DIR%\BepInEx\config\com.tas.ultrakill.cfg
echo  Scripts   : %GAME_DIR%\BepInEx\config\ULTRAKILL-TAS
echo.
echo  Launch ULTRAKILL and press Insert to open the TAS window.
echo  Build log : %LOG_FILE%
popd
exit /b 0

:game_running
echo.
echo %C_WARN%[WARN]%C_RESET% ULTRAKILL.exe is running. The DLL may be locked and the
echo        game will not load the new build until it is restarted.
if "%NOPAUSE%"=="1" goto err_running
choice /C YN /M "Deploy anyway (Y) or abort (N)"
if errorlevel 2 goto err_running
goto copy_plugin

:success_build_only
echo Build only requested; the plugin was not copied.
echo Output: %~dp0%OUTPUT_DLL%
popd
exit /b 0

REM ---------------------------------------------------------------------------
REM  Helpers
REM ---------------------------------------------------------------------------
:init_colors
if defined NO_COLOR exit /b 0
for /f %%E in ('echo prompt $E ^| cmd /d /q') do set "ESC=%%E"
if not defined ESC exit /b 0
REM ANSI colors need Windows 10 build 10586+; older consoles stay plain.
for /f "tokens=4-6 delims=. " %%A in ('ver') do if %%A LSS 10 set "ESC="
if not defined ESC exit /b 0
set "C_INFO=%ESC%[96m"
set "C_OK=%ESC%[92m"
set "C_WARN=%ESC%[93m"
set "C_ERR=%ESC%[91m"
set "C_DIM=%ESC%[90m"
set "C_RESET=%ESC%[0m"
exit /b 0

:find_powershell
set "PSEXE="
if exist "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" set "PSEXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if defined PSEXE exit /b 0
for /f "delims=" %%P in ('where powershell.exe 2^>nul') do if not defined PSEXE set "PSEXE=%%P"
if defined PSEXE exit /b 0
for /f "delims=" %%P in ('where pwsh.exe 2^>nul') do if not defined PSEXE set "PSEXE=%%P"
exit /b 0

REM :download <url> <outfile>  -- PowerShell first, then curl.exe. Returns 0/1.
:download
set "DL_URL=%~1"
set "DL_OUT=%~2"
if exist "%DL_OUT%" del /f /q "%DL_OUT%" >nul 2>&1
>> "%LOG_FILE%" echo Downloading %DL_URL%
"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; try { [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072; Invoke-WebRequest -UseBasicParsing -Uri $env:DL_URL -OutFile $env:DL_OUT -TimeoutSec 600; exit 0 } catch { Write-Host ('PowerShell download failed: ' + $_.Exception.Message); exit 1 }" >> "%LOG_FILE%" 2>&1
if not errorlevel 1 if exist "%DL_OUT%" exit /b 0
where curl.exe >nul 2>nul
if errorlevel 1 exit /b 1
>> "%LOG_FILE%" echo Retrying with curl.exe
curl.exe -L --fail --silent --show-error --retry 3 -o "%DL_OUT%" "%DL_URL%" >> "%LOG_FILE%" 2>&1
if errorlevel 1 exit /b 1
if not exist "%DL_OUT%" exit /b 1
exit /b 0

REM ---------------------------------------------------------------------------
REM  Failures
REM ---------------------------------------------------------------------------
:err_pushd
echo [ERROR] Could not enter the script folder: %~dp0
exit /b 1

:err_no_project
set "FAIL_MSG=%PROJECT_FILE% was not found next to this script. If you opened it from inside a .zip, extract the whole folder first, then run build_and_deploy.bat from there."
goto fail

:err_no_powershell
set "FAIL_MSG=Windows PowerShell was not found. It ships with every Windows 10/11 install; check that %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe exists."
goto fail

:err_log
set "FAIL_MSG=Cannot write %LOG_FILE%. Move the project folder somewhere you can write to (for example Documents or Desktop) and run it again."
goto fail

:no_game
set "FAIL_MSG=ULTRAKILL was not found. Run the script again and paste the game folder, or pass it as the first argument: build_and_deploy.bat "D:\path\to\ULTRAKILL""
goto fail

:bad_game
set "BAD_PATH=%GAME_DIR%"
if not defined BAD_PATH set "BAD_PATH=%GAME_INPUT%"
if not defined BAD_PATH set "BAD_PATH=(no path selected)"
set "FAIL_MSG=ULTRAKILL.exe / ULTRAKILL_Data was not found in '%BAD_PATH%'. Check the path."
goto fail

:err_managed
set "FAIL_MSG=ULTRAKILL_Data\Managed is incomplete. In Steam, right-click ULTRAKILL, open Properties, Installed Files, and click Verify integrity of game files."
goto fail

:err_bepinex
set "FAIL_MSG=BepInEx 5 could not be installed automatically in '%GAME_DIR%'. Check the log above. Manual fix: download %BEPINEX_URL% and extract it into the game folder (next to ULTRAKILL.exe)."
goto fail

:no_tool
set "FAIL_MSG=No .NET SDK or MSBuild was found and automatic installation failed. Install the .NET 8 SDK (x64) from https://dotnet.microsoft.com/download/dotnet/8.0 and run this script again."
goto fail

:err_restore
set "FAIL_MSG=NuGet restore failed. Check your internet connection (nuget.org must be reachable) and the log."
goto fail

:err_build
set "FAIL_MSG=The C# build failed. The compiler errors are listed above and in the log."
goto fail

:err_no_dll
set "FAIL_MSG=The build output '%OUTPUT_DLL%' was not found. Run without --deploy-only to build it first."
goto fail

:err_copy
set "FAIL_MSG=Could not copy the DLL into '%GAME_DIR%\BepInEx\plugins'. Close ULTRAKILL. If the game is under Program Files, right-click this script and choose Run as administrator."
goto fail

:err_running
set "FAIL_MSG=Deployment aborted because ULTRAKILL.exe is running."
goto fail

:fail
echo.
echo %C_ERR%[ERROR]%C_RESET% %FAIL_MSG%
if not exist "%LOG_FILE%" goto fail_end
if not defined PSEXE goto fail_end
echo.
echo %C_DIM%----- errors found in the log -----%C_RESET%
"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "$e = Select-String -LiteralPath $env:LOG_FILE -Pattern ': error |error MSB|error NU|failed|exception' -ErrorAction SilentlyContinue | Select-Object -First 25; if ($e) { $e | ForEach-Object { '  ' + $_.Line.Trim() } } else { '  (none)' }" 2>nul
echo %C_DIM%----- last 30 log lines -----%C_RESET%
"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:LOG_FILE -Tail 30" 2>nul
echo %C_DIM%-----------------------------------%C_RESET%
echo Full log: %LOG_FILE%
:fail_end
popd
exit /b 1

:usage_fail
call :usage_text
popd
exit /b 1

:usage
call :usage_text
popd
exit /b 0

:usage_text
echo.
echo ULTRAKILL TAS Tool - build and deploy
echo.
echo   build_and_deploy.bat [game folder] [--build-only] [--deploy-only]
echo                        [--use-saved] [--nopause]
echo.
echo   game folder    folder containing ULTRAKILL.exe (saved in build_config.ini)
echo   --build-only   compile without copying to BepInEx\plugins
echo   --deploy-only  copy the existing bin\Release\UltrakillTAS.dll
echo   --use-saved    skip the path prompt and use build_config.ini
echo   --nopause      never wait for a key (also implies --use-saved)
echo.
echo   Missing BepInEx 5 and .NET SDK are installed automatically, per user,
echo   without admin rights.
exit /b 0

REM ===========================================================================
REM  BepInEx 5 bootstrap
REM
REM  Downloads the official BepInEx 5 x64 release, verifies its SHA-256 and
REM  extracts it into the game folder. Always returns 0; the caller checks
REM  that BepInEx\core\BepInEx.dll exists.
REM  No ( ) blocks here on purpose: paths like "Program Files (x86)" would
REM  break them.
REM ===========================================================================
:ensure_bepinex
if not exist "%GAME_DIR%\BepInEx\core\BepInEx.dll" goto bepinex_install
if not exist "%GAME_DIR%\BepInEx\core\0Harmony.dll" goto bepinex_install
exit /b 0

:bepinex_install
echo   %C_WARN%[INFO]%C_RESET% BepInEx 5 was not found. Installing BepInEx %BEPINEX_VERSION% (x64)...
>> "%LOG_FILE%" echo ===== BepInEx bootstrap %DATE% %TIME% =====
>> "%LOG_FILE%" echo Installing BepInEx %BEPINEX_VERSION% x64 into %GAME_DIR%

set "BEPINEX_ZIP=%TEMP%\ULTRAKILL-TAS-BepInEx-%BEPINEX_VERSION%.zip"
set "BEPINEX_STAGE=%TEMP%\ULTRAKILL-TAS-BepInEx-%RANDOM%%RANDOM%"
if exist "%BEPINEX_STAGE%\" rmdir /s /q "%BEPINEX_STAGE%" >nul 2>&1
mkdir "%BEPINEX_STAGE%" >nul 2>&1

echo   Downloading %BEPINEX_URL%
call :download "%BEPINEX_URL%" "%BEPINEX_ZIP%"
if errorlevel 1 goto bepinex_dl_failed

"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "$hash=(Get-FileHash -Algorithm SHA256 -LiteralPath $env:BEPINEX_ZIP).Hash.ToLowerInvariant(); if ($hash -ne $env:BEPINEX_SHA256) { Write-Host ('BepInEx checksum mismatch: expected ' + $env:BEPINEX_SHA256 + ', got ' + $hash); exit 1 }; exit 0" >> "%LOG_FILE%" 2>&1
if errorlevel 1 goto bepinex_hash_failed

"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "try { Expand-Archive -LiteralPath $env:BEPINEX_ZIP -DestinationPath $env:BEPINEX_STAGE -Force -ErrorAction Stop; exit 0 } catch { Write-Host $_.Exception.Message; exit 1 }" >> "%LOG_FILE%" 2>&1
if not exist "%BEPINEX_STAGE%\BepInEx\core\BepInEx.dll" tar -xf "%BEPINEX_ZIP%" -C "%BEPINEX_STAGE%" >> "%LOG_FILE%" 2>&1
if not exist "%BEPINEX_STAGE%\BepInEx\core\BepInEx.dll" goto bepinex_extract_failed

xcopy "%BEPINEX_STAGE%\*" "%GAME_DIR%\" /E /I /Y /Q >> "%LOG_FILE%" 2>&1
REM Files from a downloaded zip carry the Mark-of-the-Web; unblock them so
REM the Mono runtime loads them.
"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -Command "$root=$env:GAME_DIR; Get-ChildItem -LiteralPath (Join-Path $root 'BepInEx') -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue; foreach ($f in @('winhttp.dll','doorstop_config.ini','changelog.txt','.doorstop_version')) { $p=Join-Path $root $f; if (Test-Path -LiteralPath $p) { Unblock-File -LiteralPath $p -ErrorAction SilentlyContinue } }" >> "%LOG_FILE%" 2>&1
if not exist "%GAME_DIR%\BepInEx\core\BepInEx.dll" goto bepinex_copy_failed
if not exist "%GAME_DIR%\BepInEx\core\0Harmony.dll" goto bepinex_copy_failed

call :bepinex_cleanup
echo   %C_OK%[OK]%C_RESET% BepInEx %BEPINEX_VERSION% installed into the game folder.
>> "%LOG_FILE%" echo BepInEx %BEPINEX_VERSION% installed successfully.
exit /b 0

:bepinex_dl_failed
echo   %C_ERR%[ERROR]%C_RESET% BepInEx download failed. Check your internet connection.
>> "%LOG_FILE%" echo BepInEx download failed.
goto bepinex_failed
:bepinex_hash_failed
echo   %C_ERR%[ERROR]%C_RESET% BepInEx download is corrupted (checksum mismatch).
>> "%LOG_FILE%" echo BepInEx integrity check failed.
goto bepinex_failed
:bepinex_extract_failed
echo   %C_ERR%[ERROR]%C_RESET% Could not extract the BepInEx archive.
>> "%LOG_FILE%" echo BepInEx archive extraction failed.
goto bepinex_failed
:bepinex_copy_failed
echo   %C_ERR%[ERROR]%C_RESET% Could not copy BepInEx into the game folder (no write access?).
>> "%LOG_FILE%" echo BepInEx core missing after copy; the game folder may be read-only.
goto bepinex_failed
:bepinex_failed
call :bepinex_cleanup
exit /b 0

:bepinex_cleanup
if exist "%BEPINEX_ZIP%" del /f /q "%BEPINEX_ZIP%" >nul 2>&1
if exist "%BEPINEX_STAGE%\" rmdir /s /q "%BEPINEX_STAGE%" >nul 2>&1
exit /b 0
