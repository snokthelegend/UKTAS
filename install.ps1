<#
.SYNOPSIS
  Installs the ULTRAKILL TAS Tool into your game. No compiler needed.

.DESCRIPTION
  1. Finds your ULTRAKILL folder (Steam is detected automatically; you are asked only if it is not found).
  2. Installs BepInEx 5 into it if it is missing.
  3. Copies UltrakillTAS.dll into BepInEx\plugins\UltrakillTAS\.
  4. Copies the example scripts into BepInEx\config\ULTRAKILL-TAS\examples\.

  Run it by double-clicking install.bat, or from PowerShell:
    .\install.ps1
    .\install.ps1 -GameDir "D:\Games\ULTRAKILL"
    .\install.ps1 -Dll "C:\Downloads\UltrakillTAS.dll"

.PARAMETER GameDir
  The folder that contains ULTRAKILL.exe. Optional: it is detected when omitted.
.PARAMETER Dll
  Path of UltrakillTAS.dll. Optional: it is looked for next to this script and in bin\Release.
.PARAMETER NoBepInEx
  Do not download or install BepInEx (use this if you installed it yourself).
.PARAMETER NoPause
  Do not wait for a key press at the end (for unattended runs).
#>
[CmdletBinding()]
param(
    [string]$GameDir = '',
    [string]$Dll = '',
    [switch]$NoBepInEx,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$AppId = '1229490'
$BepInExVersion = '5.4.23.6'
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
$BepInExSha256 = '6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Ok([string]$text) { Write-Host '  [OK]    ' -ForegroundColor Green -NoNewline; Write-Host $text }
function Info([string]$text) { Write-Host '  [INFO]  ' -ForegroundColor Cyan -NoNewline; Write-Host $text }
function Warn([string]$text) { Write-Host '  [WARN]  ' -ForegroundColor Yellow -NoNewline; Write-Host $text }
function Fail([string]$text) { Write-Host '  [ERROR] ' -ForegroundColor Red -NoNewline; Write-Host $text }

function Finish([int]$code) {
    if (-not $NoPause) {
        Write-Host ''
        Read-Host 'Press Enter to close this window' | Out-Null
    }
    exit $code
}

function Test-GameFolder([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    try { return (Test-Path -LiteralPath (Join-Path $path 'ULTRAKILL.exe')) } catch { return $false }
}

# Accepts the folder, or the path of ULTRAKILL.exe itself, with or without quotes.
function Normalize-GamePath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return '' }
    $p = $path.Trim().Trim('"')
    if ($p -match 'ULTRAKILL\.exe$') { $p = Split-Path -Parent $p }
    return $p.TrimEnd('\')
}

function Find-GameFolder {
    $candidates = New-Object System.Collections.ArrayList

    if ($env:ULTRAKILL_DIR) { [void]$candidates.Add($env:ULTRAKILL_DIR) }

    # The folder the developer build script remembered.
    $ini = Join-Path $Here 'build_config.ini'
    if (Test-Path -LiteralPath $ini) {
        foreach ($line in Get-Content -LiteralPath $ini) {
            if ($line -match '^\s*GAME_DIR\s*=\s*(.+)$') { [void]$candidates.Add($Matches[1].Trim()) }
        }
    }

    # Steam's own uninstall entry for ULTRAKILL.
    foreach ($key in @("HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App $AppId",
                       "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App $AppId")) {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item -and $item.InstallLocation) { [void]$candidates.Add([string]$item.InstallLocation) }
    }

    # Every Steam library on every drive.
    $steamRoots = New-Object System.Collections.ArrayList
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item) {
            if ($item.SteamPath) { [void]$steamRoots.Add(([string]$item.SteamPath).Replace('/', '\')) }
            if ($item.InstallPath) { [void]$steamRoots.Add([string]$item.InstallPath) }
        }
    }
    foreach ($root in $steamRoots) {
        [void]$candidates.Add((Join-Path $root 'steamapps\common\ULTRAKILL'))
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) {
                [void]$candidates.Add((Join-Path $m.Groups[1].Value.Replace('\\', '\') 'steamapps\common\ULTRAKILL'))
            }
        }
    }

    # Usual places, on every fixed drive.
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        if ($drive.DriveType -ne 'Fixed') { continue }
        $r = $drive.RootDirectory.FullName
        foreach ($sub in @('Program Files (x86)\Steam', 'Program Files\Steam', 'Steam', 'SteamLibrary', 'Games\Steam', 'games')) {
            [void]$candidates.Add((Join-Path $r (Join-Path $sub 'steamapps\common\ULTRAKILL')))
        }
    }

    foreach ($c in $candidates) {
        $norm = Normalize-GamePath $c
        if (Test-GameFolder $norm) { return $norm }
    }
    return ''
}

function Find-Dll {
    if ($Dll) {
        $p = $Dll.Trim().Trim('"')
        if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
        return ''
    }
    foreach ($rel in @('UltrakillTAS.dll', 'plugins\UltrakillTAS.dll', 'BepInEx\plugins\UltrakillTAS\UltrakillTAS.dll', 'bin\Release\UltrakillTAS.dll')) {
        $p = Join-Path $Here $rel
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return ''
}

Write-Host ''
Say '==============================================' 'DarkYellow'
Say '  ULTRAKILL TAS Tool - installer' 'Yellow'
Say '==============================================' 'DarkYellow'
Write-Host ''

# ---- 1. the plugin file ---------------------------------------------------
$dllPath = Find-Dll
if (-not $dllPath) {
    Fail 'UltrakillTAS.dll was not found next to this installer.'
    Say '         Download the release zip again (it contains UltrakillTAS.dll), or build it yourself with build_and_deploy.bat.'
    Finish 1
}
Ok "Plugin file: $dllPath"

# ---- 2. the game ----------------------------------------------------------
$game = Normalize-GamePath $GameDir
if ($game -and -not (Test-GameFolder $game)) {
    Warn "'$game' does not contain ULTRAKILL.exe; looking for the game instead."
    $game = ''
}
if (-not $game) { $game = Find-GameFolder }
while (-not $game) {
    Warn 'Could not find ULTRAKILL automatically.'
    Say '         In Steam: right-click ULTRAKILL > Manage > Browse local files, then copy the folder path.'
    $typed = Read-Host '  Paste the folder that contains ULTRAKILL.exe (or press Enter to quit)'
    if ([string]::IsNullOrWhiteSpace($typed)) { Fail 'Cancelled.'; Finish 1 }
    $game = Normalize-GamePath $typed
    if (-not (Test-GameFolder $game)) {
        Fail "No ULTRAKILL.exe in '$game'."
        $game = ''
    }
}
Ok "Game folder: $game"

if (Get-Process -Name 'ULTRAKILL' -ErrorAction SilentlyContinue) {
    Warn 'ULTRAKILL is running. Close it first, or the plugin file may be locked.'
    if (-not $NoPause) { Read-Host '         Press Enter once the game is closed' | Out-Null }
}

# ---- 3. BepInEx -----------------------------------------------------------
$core = Join-Path $game 'BepInEx\core\BepInEx.dll'
$harmony = Join-Path $game 'BepInEx\core\0Harmony.dll'
if ((Test-Path -LiteralPath $core) -and (Test-Path -LiteralPath $harmony)) {
    Ok 'BepInEx is already installed.'
}
elseif ($NoBepInEx) {
    Warn 'BepInEx is not installed and -NoBepInEx was given. The plugin will not load until you install BepInEx 5 (x64).'
}
else {
    Info "BepInEx $BepInExVersion is missing. Downloading it from GitHub (about 3 MB)..."
    $zip = Join-Path $env:TEMP "ULTRAKILL-TAS-BepInEx-$BepInExVersion.zip"
    $stage = Join-Path $env:TEMP ("ULTRAKILL-TAS-BepInEx-" + [Guid]::NewGuid().ToString('N'))
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
        if ($hash -ne $BepInExSha256) { throw "Checksum mismatch (expected $BepInExSha256, got $hash)." }
        Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
        Copy-Item -Path (Join-Path $stage '*') -Destination $game -Recurse -Force
        Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx') -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
        foreach ($f in @('winhttp.dll', 'doorstop_config.ini')) {
            $p = Join-Path $game $f
            if (Test-Path -LiteralPath $p) { Unblock-File -LiteralPath $p -ErrorAction SilentlyContinue }
        }
        if (-not (Test-Path -LiteralPath $core)) { throw 'BepInEx.dll is missing after extracting the archive.' }
        Ok "BepInEx $BepInExVersion installed."
    }
    catch {
        Fail "Could not install BepInEx automatically: $($_.Exception.Message)"
        Say "         Manual fix: download $BepInExUrl"
        Say '         and extract it into the game folder (next to ULTRAKILL.exe), then run this installer again.'
        Finish 1
    }
    finally {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---- 4. the plugin --------------------------------------------------------
$pluginDir = Join-Path $game 'BepInEx\plugins\UltrakillTAS'
try {
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    Copy-Item -LiteralPath $dllPath -Destination (Join-Path $pluginDir 'UltrakillTAS.dll') -Force
    $pdb = [IO.Path]::ChangeExtension($dllPath, '.pdb')
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination (Join-Path $pluginDir 'UltrakillTAS.pdb') -Force }
    Unblock-File -LiteralPath (Join-Path $pluginDir 'UltrakillTAS.dll') -ErrorAction SilentlyContinue
    Ok "Plugin installed: $pluginDir"
}
catch {
    Fail "Could not copy the plugin: $($_.Exception.Message)"
    Say '         Is the game still running, or is the folder read-only?'
    Finish 1
}

# A second copy anywhere else under plugins\ loads twice and BepInEx complains.
$dupes = @(Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx\plugins') -Recurse -Filter 'UltrakillTAS.dll' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName.TrimEnd('\') -ne $pluginDir.TrimEnd('\') })
foreach ($d in $dupes) {
    Warn "Another copy exists: $($d.FullName)"
    $answer = 'n'
    if (-not $NoPause) { $answer = Read-Host '         Rename it to UltrakillTAS.dll.old so only one copy loads? [Y/n]' }
    if ($answer -eq '' -or $answer -match '^[yY]') {
        try { Move-Item -LiteralPath $d.FullName -Destination ($d.FullName + '.old') -Force; Ok 'Old copy disabled.' }
        catch { Warn "Could not rename it: $($_.Exception.Message)" }
    }
}

# ---- 5. examples ----------------------------------------------------------
$examples = Join-Path $Here 'examples'
if (-not (Test-Path -LiteralPath $examples)) { $examples = Join-Path $Here 'BepInEx\plugins\UltrakillTAS\examples' }
if (Test-Path -LiteralPath $examples) {
    $target = Join-Path $game 'BepInEx\config\ULTRAKILL-TAS\examples'
    try {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($f in Get-ChildItem -LiteralPath $examples -File) {
            $dest = Join-Path $target $f.Name
            if (-not (Test-Path -LiteralPath $dest)) { Copy-Item -LiteralPath $f.FullName -Destination $dest }
        }
        Ok "Example scripts: $target"
    }
    catch { Warn "Could not copy the examples: $($_.Exception.Message)" }
}

Write-Host ''
Say 'Done. Next steps:' 'Green'
Say '  1. Start ULTRAKILL. A "ULTRAKILL TAS Tool loaded" message appears in the top right.'
Say '  2. Press Insert to open the TAS window. Hover any button for a short explanation.'
Say '  3. The Script Editor tab is a text editor for .uktas files (Ctrl+S saves, Ctrl+K comments).'
Say '  Scripts live in: BepInEx\config\ULTRAKILL-TAS'
Finish 0
