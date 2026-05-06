<#
.SYNOPSIS
    Deploy UnityCEDumper Lua scripts + DLLs into Cheat Engine's discrete folder.

.DESCRIPTION
    Copies the bridge scripts (unity_dump / unity_dissect / unity_symbols / utils)
    and the backend DLLs (UnityDumper-Mono, UnityDumper-IL2CPP) into
    <CE-install>\discrete\ so CE Lua's `dofile` can pick them up.

    Two source modes:
      * release  Copy everything from dist/ — use post-build.cmd. (Default.)
      * scripts  Copy *.lua from scripts/ + DLLs from dist/ — use during live
                 iteration on the Lua bridge without re-running build.cmd.

    CE typically lives in C:\Program Files\Cheat Engine which requires admin.
    The script detects this and self-elevates via Start-Process -Verb RunAs.

.PARAMETER CePath
    Cheat Engine install directory. If omitted, the script tries (in order):
      1. $env:CE_PATH
      2. C:\Program Files\Cheat Engine
      3. C:\Program Files (x86)\Cheat Engine
    The chosen directory must contain cheatengine-x86_64.exe.

.PARAMETER Mode
    'release' (default) or 'scripts'. See description.

.PARAMETER Force
    Reserved — overwrite-without-prompt is already the default. Kept for
    forward-compat.

.EXAMPLE
    .\tools\Install-CEScripts.ps1
    .\tools\Install-CEScripts.ps1 -Mode scripts
    .\tools\Install-CEScripts.ps1 -CePath 'D:\Tools\CE'
#>

[CmdletBinding()]
param(
    [string]$CePath,

    [ValidateSet('release', 'scripts')]
    [string]$Mode = 'release',

    [switch]$Force,

    # Internal — set when the script re-launches itself elevated. Don't pass
    # manually.
    [switch]$IsElevatedRelaunch
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$Text) { Write-Host ">> $Text" -ForegroundColor Yellow }
function Write-Ok  ([string]$Text) { Write-Host "   [OK] $Text" -ForegroundColor Green }
function Write-Fail([string]$Text) { Write-Host "   [FAIL] $Text" -ForegroundColor Red }
function Write-Info([string]$Text) { Write-Host "   $Text" -ForegroundColor Gray }

# ---------------------------------------------------------------------------
# Resolve CE install directory
# ---------------------------------------------------------------------------
function Resolve-CePath {
    param([string]$Override)

    $candidates = @()
    if ($Override)         { $candidates += $Override }
    if ($env:CE_PATH)      { $candidates += $env:CE_PATH }
    $candidates += 'C:\Program Files\Cheat Engine'
    $candidates += 'C:\Program Files (x86)\Cheat Engine'

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        $exe = Join-Path $c 'cheatengine-x86_64.exe'
        if (Test-Path $exe) { return (Resolve-Path $c).Path }
    }

    throw "Could not locate Cheat Engine install. Tried: $($candidates -join ', '). Set CE_PATH env var or pass -CePath."
}

$cePath        = Resolve-CePath -Override $CePath
$discreteDir   = Join-Path $cePath 'discrete'
$distDir       = Join-Path $RepoRoot 'dist'
$scriptsDir    = Join-Path $RepoRoot 'scripts'

Write-Step 'Resolved paths'
Write-Info ("CE install:  {0}" -f $cePath)
Write-Info ("Target dir:  {0}" -f $discreteDir)
Write-Info ("Source mode: {0}" -f $Mode)

# ---------------------------------------------------------------------------
# Build file list per mode
# ---------------------------------------------------------------------------
$sourceFiles = New-Object System.Collections.Generic.List[string]

$luaNames = @('unity_dump.lua', 'unity_dissect.lua', 'unity_symbols.lua', 'utils.lua')
$dllNames = @('UnityDumper-Mono.dll', 'UnityDumper-IL2CPP.dll')

switch ($Mode) {
    'release' {
        if (-not (Test-Path $distDir)) {
            throw "dist\ not found at $distDir. Run build.cmd first, or use -Mode scripts for live-iteration deploys."
        }
        foreach ($n in $luaNames + $dllNames) {
            $f = Join-Path $distDir $n
            if (Test-Path $f) { [void]$sourceFiles.Add($f) }
            else              { Write-Warning "Skipping missing $n in dist\" }
        }
    }
    'scripts' {
        # Lua from scripts/ (live edits), DLLs still come from dist/
        foreach ($n in $luaNames) {
            $f = Join-Path $scriptsDir $n
            if (Test-Path $f) { [void]$sourceFiles.Add($f) }
            else              { Write-Warning "Skipping missing $n in scripts\" }
        }
        foreach ($n in $dllNames) {
            $f = Join-Path $distDir $n
            if (Test-Path $f) { [void]$sourceFiles.Add($f) }
            else              { Write-Warning "Skipping missing $n in dist\ (build the DLL first)" }
        }
    }
}

if ($sourceFiles.Count -eq 0) {
    throw "No source files resolved. Nothing to copy."
}

# ---------------------------------------------------------------------------
# Self-elevate if the target tree isn't writable
# ---------------------------------------------------------------------------
function Test-WriteAccess {
    param([string]$Dir)

    # Walk up until we hit an existing dir, then probe with a temp file.
    $probe = $Dir
    while ($probe -and -not (Test-Path $probe)) { $probe = Split-Path -Parent $probe }
    if (-not $probe) { return $false }

    try {
        $temp = Join-Path $probe (".discrete-write-test-{0}.tmp" -f ([Guid]::NewGuid().ToString('N')))
        New-Item -Path $temp -ItemType File -Force | Out-Null
        Remove-Item -Path $temp -Force
        return $true
    } catch {
        return $false
    }
}

$writable = Test-WriteAccess -Dir $discreteDir
$isAdmin  = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
              [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $writable) {
    if ($isAdmin) {
        # We're already elevated and still can't write — surface that, don't loop.
        throw "Already elevated but cannot write to $discreteDir. Check folder ACLs."
    }
    if ($IsElevatedRelaunch) {
        # Sanity: avoid infinite re-elevation loops.
        throw "Re-elevation didn't yield write access to $discreteDir. Aborting."
    }

    Write-Step 'Need admin to write to Program Files — re-launching elevated...'
    $argList = @(
        '-NoProfile',
        '-NoExit',
        '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath,
        '-CePath', "`"$cePath`"",
        '-Mode',   $Mode,
        '-IsElevatedRelaunch'
    )
    if ($Force) { $argList += '-Force' }

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -Wait
    exit 0
}

# ---------------------------------------------------------------------------
# Copy
# ---------------------------------------------------------------------------
if (-not (Test-Path $discreteDir)) {
    Write-Step "Creating $discreteDir"
    New-Item -Path $discreteDir -ItemType Directory -Force | Out-Null
}

Write-Step ("Deploying {0} file(s) to {1}" -f $sourceFiles.Count, $discreteDir)
$copied = 0
foreach ($src in $sourceFiles) {
    $name = Split-Path -Leaf $src
    $dest = Join-Path $discreteDir $name
    Copy-Item -Path $src -Destination $dest -Force
    $sz = '{0:N1} KB' -f ((Get-Item $dest).Length / 1KB)
    Write-Ok ("{0,-30}  {1}" -f $name, $sz)
    $copied++
}

Write-Host ''
Write-Step ("Done. {0} file(s) deployed." -f $copied)
Write-Host ''
Write-Host '  Next step in CE:' -ForegroundColor Cyan
Write-Host ('    dofile([[{0}\unity_dump.lua]])' -f $discreteDir) -ForegroundColor White
Write-Host ''

# Auto-close the elevated window after a short pause so the user can see results.
if ($IsElevatedRelaunch) {
    Write-Host '  (Elevated window — press Enter to close)' -ForegroundColor DarkGray
    [void](Read-Host)
}
