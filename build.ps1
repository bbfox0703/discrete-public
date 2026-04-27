<#
.SYNOPSIS
    Unified build script for UnityCEDumper.

.DESCRIPTION
    Builds the C++ DLL (Mono / IL2CPP / both), the Avalonia UI (with Native AOT),
    the proxy DLLs (version / winmm / dxgi), and the xUnit v3 test runner.

    Always invoke through this script — bare cmake / dotnet calls will fail
    because they need the Visual Studio Developer Shell environment.

.PARAMETER Target
    What to build. Default: All.
        DLL        — C++ injected DLL (uses -Backend)
        UI         — Avalonia UI app
        Test       — Run xUnit v3 test suite
        ProxyDLL   — Proxy DLLs (Phase 12 — placeholder)
        MetadataCli— Offline metadata CLI (Phase 13 — placeholder)
        All        — Everything

.PARAMETER Backend
    For -Target DLL: which DLL backend to build. Default: Both.
        Mono | IL2CPP | Both

.PARAMETER Mode
    Build configuration. Default: Release.
        Debug | Release | Publish
    Publish runs `dotnet publish` (Native AOT) for the UI.

.PARAMETER Clean
    Wipe build/ before configuring.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Target DLL -Backend Mono
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Mode Publish -Clean
#>

[CmdletBinding()]
param(
    [ValidateSet('DLL','UI','Test','ProxyDLL','MetadataCli','All')]
    [string]$Target = 'All',

    [ValidateSet('Mono','IL2CPP','Both')]
    [string]$Backend = 'Both',

    [ValidateSet('Debug','Release','Publish')]
    [string]$Mode = 'Release',

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $RepoRoot

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Section($msg) {
    Write-Host ''
    Write-Host "==[ $msg ]==" -ForegroundColor Cyan
}

function Bump-BuildNumber {
    $path = Join-Path $RepoRoot 'build_number.txt'
    if (-not (Test-Path $path)) { Set-Content -Path $path -Value '1'; return 1 }
    $n = [int](Get-Content -Path $path -TotalCount 1)
    $n++
    Set-Content -Path $path -Value "$n"
    return $n
}

$script:DevShellLoaded = $false
$script:VsInstallPath = $null

function Resolve-VsInstallPath {
    if ($script:VsInstallPath) { return $script:VsInstallPath }

    # Search every known vswhere.exe location (matches UE5CEDumper's resilient probe).
    $candidates = [System.Collections.Generic.List[string]]::new()
    $inPath = Get-Command vswhere -ErrorAction SilentlyContinue
    if ($inPath) { $candidates.Add($inPath.Source) }
    if (${env:ProgramFiles(x86)}) { $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')) }
    if ($env:ProgramFiles)        { $candidates.Add((Join-Path $env:ProgramFiles        'Microsoft Visual Studio\Installer\vswhere.exe')) }
    if ($env:ChocolateyInstall)   { $candidates.Add((Join-Path $env:ChocolateyInstall   'bin\vswhere.exe')) }
    if ($env:LOCALAPPDATA)        { $candidates.Add((Join-Path $env:LOCALAPPDATA        'Microsoft\VisualStudio\Installer\vswhere.exe')) }

    $vswhere = $candidates | Where-Object { $_ -and (Test-Path $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1
    if (-not $vswhere) { throw "vswhere.exe not found. Install Visual Studio or Microsoft.VisualStudio.Locator." }

    $vsPath = & $vswhere -latest -prerelease -property installationPath
    if (-not $vsPath) {
        $vsPath = & $vswhere -latest -prerelease -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    }
    if (-not $vsPath) { throw "No Visual Studio installation found (need C++ Desktop workload)." }

    $script:VsInstallPath = $vsPath
    return $vsPath
}

function Enter-VsDevEnvironment {
    # Idempotent — only loads once per build.ps1 invocation.
    # Mirrors UE5CEDumper's pattern: import the DevShell DLL and call the
    # Enter-VsDevShell cmdlet directly. We deliberately do NOT use
    # Launch-VsDevShell.ps1, which can recursively re-invoke itself in VS 18.
    if ($script:DevShellLoaded) { return }

    $vsPath = Resolve-VsInstallPath
    $devShellDll = Join-Path $vsPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    if (-not (Test-Path $devShellDll)) {
        throw "Microsoft.VisualStudio.DevShell.dll not found at $devShellDll."
    }

    Write-Host "Loading VS DevShell from $vsPath ..." -ForegroundColor DarkGray
    Import-Module $devShellDll
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

    $script:DevShellLoaded = $true
    Write-Host "  VS DevShell ready" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# DLL build (CMake + Ninja)
# ---------------------------------------------------------------------------
function Build-Dll {
    Enter-VsDevEnvironment

    $cfg = if ($Mode -eq 'Debug') { 'Debug' } else { 'Release' }
    $buildDir = Join-Path $RepoRoot "build/dll-$($Backend.ToLower())-$($cfg.ToLower())"

    if ($Clean -and (Test-Path $buildDir)) {
        Write-Host "Cleaning $buildDir ..." -ForegroundColor DarkGray
        Remove-Item -Recurse -Force $buildDir
    }

    Write-Section "Configure DLL  (Backend=$Backend, Mode=$cfg)"
    & cmake -S $RepoRoot -B $buildDir -G Ninja `
        "-DCMAKE_BUILD_TYPE=$cfg" `
        "-DBACKEND=$Backend"
    if ($LASTEXITCODE -ne 0) { throw "cmake configure failed." }

    Write-Section "Build DLL"
    & cmake --build $buildDir --parallel
    if ($LASTEXITCODE -ne 0) { throw "cmake build failed." }

    $outDir = Join-Path $buildDir 'bin'
    if (Test-Path $outDir) {
        Get-ChildItem $outDir -Filter '*.dll' | ForEach-Object {
            Write-Host ("  produced: {0}  ({1:N0} bytes)" -f $_.Name, $_.Length) -ForegroundColor Green
        }
    }
}

# ---------------------------------------------------------------------------
# UI build (.NET 10 + Avalonia 12)
# ---------------------------------------------------------------------------
function Build-Ui {
    Write-Section "Build UI  (Mode=$Mode)"
    $uiDir = Join-Path $RepoRoot 'ui'
    Push-Location $uiDir
    try {
        if ($Mode -eq 'Publish') {
            & dotnet publish 'UnityDumpUI/UnityDumpUI.csproj' -c Release -r win-x64
        } elseif ($Mode -eq 'Debug') {
            & dotnet build 'UnityCEDumper.sln' -c Debug
        } else {
            & dotnet build 'UnityCEDumper.sln' -c Release
        }
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
    } finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------------------
# Tests (xUnit v3 — runs as standalone exe)
# ---------------------------------------------------------------------------
function Run-Tests {
    Write-Section "Run Tests  (xUnit v3)"
    $uiDir = Join-Path $RepoRoot 'ui'
    Push-Location $uiDir
    try {
        & dotnet test 'UnityDumpUI.Tests/UnityDumpUI.Tests.csproj' -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
    } finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------------------
# ProxyDLL / MetadataCli — Phase 12 / 13 placeholders
# ---------------------------------------------------------------------------
function Build-ProxyDll {
    Write-Section "Build ProxyDLL (Phase 12 — not yet implemented)"
    Write-Host "  Skipping: proxy targets land in Phase 12." -ForegroundColor Yellow
}
function Build-MetadataCli {
    Write-Section "Build MetadataCli (Phase 13 — not yet implemented)"
    Write-Host "  Skipping: CLI lands in Phase 13." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------------
$buildNum = Bump-BuildNumber
Write-Host "UnityCEDumper build #$buildNum  (Target=$Target, Backend=$Backend, Mode=$Mode)" -ForegroundColor Magenta

switch ($Target) {
    'DLL'         { Build-Dll }
    'UI'          { Build-Ui }
    'Test'        { Run-Tests }
    'ProxyDLL'    { Build-ProxyDll }
    'MetadataCli' { Build-MetadataCli }
    'All' {
        Build-Dll
        Build-Ui
        Run-Tests
        Build-ProxyDll
        Build-MetadataCli
    }
}

Write-Host ''
Write-Host "Build #$buildNum complete." -ForegroundColor Green
