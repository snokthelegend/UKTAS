<#
.SYNOPSIS
  Builds the ULTRAKILL TAS Tool in Release and packages it as a ready-to-share zip.

.DESCRIPTION
  1. dotnet build -c Release --no-incremental (the game folder is only used for references).
  2. Creates dist\UltrakillTAS-<version>\ laid out as a BepInEx folder:
       BepInEx\plugins\UltrakillTAS\UltrakillTAS.dll
       BepInEx\plugins\UltrakillTAS\examples\*.uktas
       UKTAS_DOCS.html (from docs\), README.md, install.bat, install.ps1
  3. Zips it to dist\UltrakillTAS-<version>.zip.

  Nothing is copied into the game folder.

.PARAMETER GameDir
  The folder that contains ULTRAKILL.exe (needed to resolve references while building).
.PARAMETER NoPause
  Do not wait for a key press at the end (for unattended runs).
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'D:\games\steamapps\common\ULTRAKILL',
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
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

# A dotnet.exe only counts if it has at least one SDK (not just a runtime).
function Test-DotnetSdk([string]$exe) {
    if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { return $false }
    try {
        $sdks = @(& $exe --list-sdks)
        return ($sdks.Count -gt 0)
    }
    catch { return $false }
}

function Find-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and (Test-DotnetSdk $cmd.Source)) { return $cmd.Source }
    foreach ($p in @((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
                     (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
                     (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))) {
        if (Test-DotnetSdk $p) { return $p }
    }
    return ''
}

Write-Host ''
Say '==============================================' 'DarkYellow'
Say '  ULTRAKILL TAS Tool - release packager' 'Yellow'
Say '==============================================' 'DarkYellow'
Write-Host ''

try {
    # ---- 0. inputs -------------------------------------------------------
    $csproj = Join-Path $Here 'UltrakillTAS.csproj'
    if (-not (Test-Path -LiteralPath $csproj)) { throw 'UltrakillTAS.csproj was not found next to package.ps1.' }

    $game = $GameDir.Trim().Trim('"').TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $game 'ULTRAKILL.exe'))) {
        throw "No ULTRAKILL.exe in '$game'. Pass the game folder: package.bat ""D:\Games\ULTRAKILL"""
    }
    Ok "Game folder (references only): $game"

    $dotnet = Find-Dotnet
    if (-not $dotnet) { throw 'No .NET SDK found (checked PATH and %LOCALAPPDATA%\Microsoft\dotnet).' }
    Ok "dotnet: $dotnet"
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

    # ---- 1. version ------------------------------------------------------
    $version = ''
    [xml]$xml = Get-Content -LiteralPath $csproj -Raw
    foreach ($pg in @($xml.Project.PropertyGroup)) {
        foreach ($name in @('Version', 'AssemblyVersion')) {
            $node = $pg.$name
            if (-not $version -and $node -and -not [string]::IsNullOrWhiteSpace([string]$node)) { $version = ([string]$node).Trim() }
        }
    }
    if (-not $version) {
        $version = (Get-Date).ToString('yyyyMMdd')
        Warn "No <Version> in the csproj; using the date stamp $version."
    }
    Ok "Version: $version"

    # ---- 2. build --------------------------------------------------------
    Info 'Building Release (this recompiles everything)...'
    & $dotnet build $csproj -c Release --no-incremental "-p:GameDir=$game"
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE). See the compiler output above." }

    $dll = Join-Path $Here 'bin\Release\UltrakillTAS.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw "Build reported success but $dll does not exist." }
    if (((Get-Date) - (Get-Item -LiteralPath $dll).LastWriteTime).TotalMinutes -gt 10) {
        throw "$dll was not produced by this build (it is more than 10 minutes old)."
    }
    Ok 'Build succeeded.'

    # ---- 3. stage --------------------------------------------------------
    $name = "UltrakillTAS-$version"
    $dist = Join-Path $Here 'dist'
    $stage = Join-Path $dist $name
    $zip = Join-Path $dist "$name.zip"

    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

    $pluginDir = Join-Path $stage 'BepInEx\plugins\UltrakillTAS'
    $exDir = Join-Path $pluginDir 'examples'
    New-Item -ItemType Directory -Force -Path $exDir | Out-Null

    Copy-Item -LiteralPath $dll -Destination (Join-Path $pluginDir 'UltrakillTAS.dll') -Force

    $examples = @(Get-ChildItem -LiteralPath (Join-Path $Here 'examples') -Filter '*.uktas' -File -ErrorAction SilentlyContinue)
    if ($examples.Count -eq 0) { Warn 'No examples\*.uktas found.' }
    foreach ($f in $examples) { Copy-Item -LiteralPath $f.FullName -Destination $exDir -Force }

    $single = @(
        @{ From = 'README.md';             To = 'README.md' },
        @{ From = 'docs\UKTAS_DOCS.html';  To = 'UKTAS_DOCS.html' },
        @{ From = 'install.bat';           To = 'install.bat' },
        @{ From = 'install.ps1';           To = 'install.ps1' }
    )
    foreach ($s in $single) {
        $src = Join-Path $Here $s.From
        if (-not (Test-Path -LiteralPath $src)) { throw "Missing file: $($s.From)" }
        Copy-Item -LiteralPath $src -Destination (Join-Path $stage $s.To) -Force
    }
    Ok "Staged: $stage"

    # ---- 4. zip ----------------------------------------------------------
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $zip)) { throw 'Compress-Archive did not create the zip.' }
    $kb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1KB, 1)
    Ok "Zip created ($kb KB)."
}
catch {
    Fail $_.Exception.Message
    Finish 1
}

Write-Host ''
Say 'Done. Release package:' 'Green'
Say "  $zip" 'White'
Finish 0
