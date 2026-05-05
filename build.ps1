<#
.SYNOPSIS
    Unified build script for UnityCEDumper (DLL + UI + tests).

.DESCRIPTION
    Builds the C++ injected DLL (Mono / IL2CPP / both), the Avalonia UI
    (Native AOT publish), and the xUnit v3 test runner. All shippable
    artifacts land in dist\ regardless of target — same convention as
    UE5CEDumper, so the output folder is the only thing you need to copy
    onto the target machine.

    Always invoke through this script (or build.cmd) — bare cmake/dotnet
    calls won't see the Visual Studio Developer Shell environment.

.PARAMETER Target
    What to build. Default: All.
        DLL        — C++ injected DLL (uses -Backend)
        UI         — Avalonia UI app (publish)
        Test       — xUnit v3 test suite
        ProxyDLL   — Proxy DLLs (Phase 12 placeholder)
        MetadataCli— Offline metadata CLI (Phase 13 placeholder)
        All        — Everything

.PARAMETER Backend
    For -Target DLL: which DLL backend to build. Default: Both.
        Mono | IL2CPP | Both

.PARAMETER Mode
    Build configuration. Default: Release.
        Debug | Release | Publish
    Publish runs `dotnet publish` (Native AOT) for the UI.
    Debug / Release run a self-contained single-file publish.

.PARAMETER Clean
    Wipe build\ and dist\ before configuring.

.PARAMETER LogFile
    Optional transcript path. build.cmd uses this to capture the full
    log without piping through cmd.exe (which mangles CJK output).

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Target DLL -Backend Mono
    .\build.ps1 -Mode Publish -Clean
#>

[CmdletBinding()]
param(
    [ValidateSet('DLL','UI','Test','ProxyDLL','MetadataCli','All')]
    [string]$Target = 'All',

    [ValidateSet('Mono','IL2CPP','Both')]
    [string]$Backend = 'Both',

    [ValidateSet('Debug','Release','Publish')]
    [string]$Mode = 'Release',

    [switch]$Clean,

    [string]$LogFile = ''
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# Force UTF-8 for console + .NET subprocesses (avoids CJK garbling).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Optional transcript — replaces an out-of-process Tee-Object pipe so
# cmd.exe's codepage can't corrupt the log.
$script:transcriptActive = $false
if ($LogFile) {
    try {
        Start-Transcript -Path $LogFile -Force | Out-Null
        $script:transcriptActive = $true
    } catch {
        Write-Warning "Could not start transcript to $LogFile : $_"
    }
}

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $RepoRoot

$BUILD_DIR = Join-Path $RepoRoot 'build'
$DIST_DIR  = Join-Path $RepoRoot 'dist'
$UI_DIR    = Join-Path $RepoRoot 'ui'
$UI_PROJ   = Join-Path $UI_DIR   'UnityDumpUI\UnityDumpUI.csproj'
$TEST_PROJ = Join-Path $UI_DIR   'UnityDumpUI.Tests\UnityDumpUI.Tests.csproj'
$SLN       = Join-Path $UI_DIR   'UnityCEDumper.sln'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Banner([string]$Text) {
    $line = '=' * 60
    Write-Host ''
    Write-Host $line -ForegroundColor Cyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host $line -ForegroundColor Cyan
}
function Write-Step([string]$Text) { Write-Host ">> $Text" -ForegroundColor Yellow }
function Write-Ok  ([string]$Text) { Write-Host "   [OK] $Text" -ForegroundColor Green }
function Write-Fail([string]$Text) { Write-Host "   [FAIL] $Text" -ForegroundColor Red }
function Write-Info([string]$Text) { Write-Host "   $Text" -ForegroundColor Gray }

function Get-FileSize([string]$Path) {
    if (Test-Path $Path) {
        $size = (Get-Item $Path).Length
        if ($size -gt 1MB) { return '{0:N1} MB' -f ($size / 1MB) }
        if ($size -gt 1KB) { return '{0:N1} KB' -f ($size / 1KB) }
        return "$size B"
    }
    return 'N/A'
}

function Bump-BuildNumber {
    $path = Join-Path $RepoRoot 'build_number.txt'
    if (-not (Test-Path $path)) { Set-Content -Path $path -Value '1'; return 1 }
    $n = [int](Get-Content -Path $path -TotalCount 1)
    $n++
    # LF line ending so git's autocrlf doesn't whine.
    [System.IO.File]::WriteAllText($path, "$n`n")
    return $n
}

$script:DevShellLoaded = $false
$script:VsInstallPath  = $null

function Resolve-VsInstallPath {
    if ($script:VsInstallPath) { return $script:VsInstallPath }

    $candidates = [System.Collections.Generic.List[string]]::new()
    $inPath = Get-Command vswhere -ErrorAction SilentlyContinue
    if ($inPath) { $candidates.Add($inPath.Source) }
    if (${env:ProgramFiles(x86)}) { $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')) }
    if ($env:ProgramFiles)        { $candidates.Add((Join-Path $env:ProgramFiles        'Microsoft Visual Studio\Installer\vswhere.exe')) }
    if ($env:ChocolateyInstall)   { $candidates.Add((Join-Path $env:ChocolateyInstall   'bin\vswhere.exe')) }
    if ($env:LOCALAPPDATA)        { $candidates.Add((Join-Path $env:LOCALAPPDATA        'Microsoft\VisualStudio\Installer\vswhere.exe')) }

    $vswhere = $candidates | Where-Object { $_ -and (Test-Path $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1
    if (-not $vswhere) { throw 'vswhere.exe not found. Install Visual Studio or Microsoft.VisualStudio.Locator.' }

    $vsPath = & $vswhere -latest -prerelease -property installationPath
    if (-not $vsPath) {
        $vsPath = & $vswhere -latest -prerelease -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    }
    if (-not $vsPath) { throw 'No Visual Studio installation found (need C++ Desktop workload).' }

    $script:VsInstallPath = $vsPath
    return $vsPath
}

function Enter-VsDevEnvironment {
    if ($script:DevShellLoaded) { return }

    $vsPath = Resolve-VsInstallPath
    $devShellDll = Join-Path $vsPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    if (-not (Test-Path $devShellDll)) {
        throw "Microsoft.VisualStudio.DevShell.dll not found at $devShellDll."
    }

    Write-Info "Loading VS DevShell from $vsPath"
    Import-Module $devShellDll
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

    $script:DevShellLoaded = $true
    Write-Ok 'MSVC x64 environment loaded'
}

# ---------------------------------------------------------------------------
# Preamble
# ---------------------------------------------------------------------------
Write-Banner "UnityCEDumper Build  |  Mode: $Mode  |  Target: $Target  |  Backend: $Backend"
Write-Info "Root:   $RepoRoot"
Write-Info "Clean:  $Clean"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Fail 'dotnet SDK not found in PATH'
    exit 1
}
Write-Info "dotnet: $(dotnet --version)"

# ---------------------------------------------------------------------------
# Clean
# ---------------------------------------------------------------------------
if ($Clean) {
    Write-Step 'Cleaning build/ and dist/ ...'
    if (Test-Path $BUILD_DIR) { Remove-Item $BUILD_DIR -Recurse -Force }
    if (Test-Path $DIST_DIR)  { Remove-Item $DIST_DIR  -Recurse -Force }
    Write-Ok 'Clean complete'
}

New-Item -ItemType Directory -Path $DIST_DIR  -Force | Out-Null
New-Item -ItemType Directory -Path $BUILD_DIR -Force | Out-Null

$buildNum = Bump-BuildNumber
Write-Info "Build#: $buildNum"

$exitCode = 0

# ---------------------------------------------------------------------------
# DLL build (CMake + Ninja, MSVC)
# ---------------------------------------------------------------------------
function Build-Dll {
    Enter-VsDevEnvironment

    $cfg = if ($Mode -eq 'Debug') { 'Debug' } else { 'Release' }
    $dllBuildDir = Join-Path $BUILD_DIR "dll-$($Backend.ToLower())-$($cfg.ToLower())"

    Write-Banner "C++ DLL  |  Backend=$Backend  Mode=$cfg"

    # Always do a clean configure — same rule as UE5CEDumper. CMake's cache
    # is fragile across BACKEND swaps, so wipe before reconfigure.
    if (Test-Path $dllBuildDir) {
        Write-Step "Removing CMake cache for clean configure ($dllBuildDir)"
        Remove-Item $dllBuildDir -Recurse -Force
    }

    Write-Step 'Configuring CMake (Ninja + MSVC)'
    & cmake -S $RepoRoot -B $dllBuildDir -G Ninja `
        "-DCMAKE_BUILD_TYPE=$cfg" `
        "-DBACKEND=$Backend"
    if ($LASTEXITCODE -ne 0) { Write-Fail 'cmake configure failed'; return $false }
    Write-Ok 'CMake configured'

    Write-Step "Building DLL ($cfg)"
    & cmake --build $dllBuildDir --parallel
    if ($LASTEXITCODE -ne 0) { Write-Fail 'cmake build failed'; return $false }

    # Copy every produced .dll + .pdb into dist\.
    $outDir = Join-Path $dllBuildDir 'bin'
    if (-not (Test-Path $outDir)) {
        Write-Fail "DLL output dir not found: $outDir"
        return $false
    }

    $produced = 0
    Get-ChildItem $outDir -File | Where-Object { $_.Extension -in '.dll', '.pdb' } | ForEach-Object {
        Copy-Item $_.FullName -Destination $DIST_DIR -Force
        if ($_.Extension -eq '.dll') {
            $sz = Get-FileSize (Join-Path $DIST_DIR $_.Name)
            Write-Ok "$($_.Name) ($sz) -> dist\"
            $produced++
        }
    }
    if ($produced -eq 0) {
        Write-Fail 'No DLLs found in build output'
        return $false
    }
    return $true
}

# ---------------------------------------------------------------------------
# UI build (.NET 10 + Avalonia 12) — always publishes to dist\
# ---------------------------------------------------------------------------
function Build-Ui {
    $csCfg = if ($Mode -eq 'Debug') { 'Debug' } else { 'Release' }
    Write-Banner "C# Avalonia UI  |  $csCfg ($Mode)"

    # UE5CEDumper rule: always wipe bin/obj for the UI csproj before publish.
    $uiProjDir = Split-Path $UI_PROJ -Parent
    foreach ($subdir in @('bin', 'obj')) {
        $p = Join-Path $uiProjDir $subdir
        if (Test-Path $p) {
            Write-Step "Removing $subdir for clean build"
            Remove-Item $p -Recurse -Force
        }
    }

    $publishDir = Join-Path $BUILD_DIR 'ui-publish'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    if ($Mode -eq 'Publish') {
        Write-Step 'Publishing UnityDumpUI (Native AOT, Release)'
        & dotnet publish $UI_PROJ `
            -c Release `
            -r win-x64 `
            -p:PublishAot=true `
            -o $publishDir `
            --nologo
    }
    else {
        # Self-contained single-file for Debug/Release iteration. The .NET
        # runtime is bundled (~96 MB) but no AOT compile step, so iteration
        # stays fast.
        Write-Step "Publishing UnityDumpUI ($csCfg, self-contained single-file)"
        & dotnet publish $UI_PROJ `
            -c $csCfg `
            -r win-x64 `
            --self-contained `
            -p:PublishSingleFile=true `
            -p:PublishAot=false `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:IncludeAllContentForSelfExtract=true `
            -o $publishDir `
            --nologo
    }
    if ($LASTEXITCODE -ne 0) { Write-Fail 'UI publish failed'; return $false }

    $exeFile = Get-ChildItem -Path $publishDir -Filter 'DiscreteUI.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $exeFile) {
        Write-Fail 'No UI build output found'
        return $false
    }

    if ($Mode -eq 'Publish') {
        # AOT publish drops a few native side-DLLs (Skia/HarfBuzz/ANGLE) — copy
        # everything that isn't a build artifact reference.
        Get-ChildItem -Path $publishDir -File |
            Where-Object { $_.Extension -in '.exe', '.dll', '.pdb' } |
            ForEach-Object { Copy-Item $_.FullName -Destination $DIST_DIR -Force }
    }
    else {
        # Self-contained single-file: just the exe + (debug only) pdb.
        Copy-Item $exeFile.FullName -Destination $DIST_DIR -Force
        if ($Mode -eq 'Debug') {
            $pdb = Join-Path $exeFile.DirectoryName 'DiscreteUI.pdb'
            if (Test-Path $pdb) { Copy-Item $pdb -Destination $DIST_DIR -Force }
        }
    }

    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

    $exeSize = Get-FileSize (Join-Path $DIST_DIR 'DiscreteUI.exe')
    Write-Ok "DiscreteUI.exe ($exeSize) -> dist\"
    return $true
}

# ---------------------------------------------------------------------------
# Tests (xUnit v3 standalone exe runner)
# ---------------------------------------------------------------------------
function Run-Tests {
    Write-Banner 'Unit Tests  |  xUnit v3'
    if (-not (Test-Path $TEST_PROJ)) {
        Write-Info 'Test project not found, skipping'
        return $true
    }
    Write-Step 'Building + running tests'
    & dotnet test $TEST_PROJ -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Tests failed'; return $false }
    Write-Ok 'All tests passed'
    return $true
}

# ---------------------------------------------------------------------------
# Phase 12 / 13 placeholders
# ---------------------------------------------------------------------------
function Build-ProxyDll {
    Write-Banner 'Proxy DLL (Phase 12 — not yet implemented)'
    Write-Info 'Skipping: proxy targets land in Phase 12.'
    return $true
}
function Build-MetadataCli {
    Enter-VsDevEnvironment

    $cfg = if ($Mode -eq 'Debug') { 'Debug' } else { 'Release' }
    $cliBuildDir = Join-Path $BUILD_DIR "cli-$($cfg.ToLower())"

    Write-Banner "Metadata CLI (Phase 13)  |  Mode=$cfg"

    # Same clean-configure rule as Build-Dll: cmake's cache is fragile
    # across reconfigures, so wipe before each pass. Cheap (~1s) since
    # the CLI compiles three .cpp files.
    if (Test-Path $cliBuildDir) {
        Write-Step "Removing CMake cache for clean configure ($cliBuildDir)"
        Remove-Item $cliBuildDir -Recurse -Force
    }

    Write-Step 'Configuring CMake (Ninja + MSVC, BUILD_CLI=ON)'
    & cmake -S $RepoRoot -B $cliBuildDir -G Ninja `
        "-DCMAKE_BUILD_TYPE=$cfg" `
        "-DBUILD_CLI=ON" `
        "-DBACKEND=IL2CPP"
    if ($LASTEXITCODE -ne 0) { Write-Fail 'cmake configure failed'; return $false }

    Write-Step "Building metadata-cli ($cfg)"
    & cmake --build $cliBuildDir --target metadata-cli --parallel
    if ($LASTEXITCODE -ne 0) { Write-Fail 'metadata-cli build failed'; return $false }

    $exe = Join-Path $cliBuildDir 'bin\metadata-cli.exe'
    if (-not (Test-Path $exe)) {
        Write-Fail "metadata-cli.exe not produced at: $exe"
        return $false
    }
    Copy-Item $exe -Destination $DIST_DIR -Force
    $sz = Get-FileSize (Join-Path $DIST_DIR 'metadata-cli.exe')
    Write-Ok "metadata-cli.exe ($sz) -> dist\"
    return $true
}

# ---------------------------------------------------------------------------
# Companion artifacts (build_number.txt, scripts/ when present)
# ---------------------------------------------------------------------------
function Copy-CompanionArtifacts {
    $bn = Join-Path $RepoRoot 'build_number.txt'
    if (Test-Path $bn) { Copy-Item $bn -Destination $DIST_DIR -Force }

    # CE Lua bridge (Phase 11) — ship every .lua + .CT + the README so
    # users only need the dist/ folder, not a checked-out repo.
    $scriptsDir = Join-Path $RepoRoot 'scripts'
    if (Test-Path $scriptsDir) {
        foreach ($pattern in @('*.lua', '*.CT', 'README.md')) {
            Get-ChildItem $scriptsDir -File -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
                # README.md keeps its name; the user reads it next to the scripts.
                Copy-Item $_.FullName -Destination $DIST_DIR -Force
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------------
switch ($Target) {
    'DLL'         { if (-not (Build-Dll))         { $exitCode = 1 } }
    'UI'          { if (-not (Build-Ui))          { $exitCode = 1 } }
    'Test'        { if (-not (Run-Tests))         { $exitCode = 1 } }
    'ProxyDLL'    { if (-not (Build-ProxyDll))    { $exitCode = 1 } }
    'MetadataCli' { if (-not (Build-MetadataCli)) { $exitCode = 1 } }
    'All' {
        if (-not (Build-Dll))         { $exitCode = 1 }
        if (-not (Build-Ui))          { $exitCode = 1 }
        if (-not (Run-Tests))         { $exitCode = 1 }
        if (-not (Build-ProxyDll))    { $exitCode = 1 }
        if (-not (Build-MetadataCli)) { $exitCode = 1 }
    }
}

Copy-CompanionArtifacts

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
$sw.Stop()
Write-Banner 'Build Summary'
Write-Host ''
Write-Host "  Mode:    $Mode"     -ForegroundColor White
Write-Host "  Target:  $Target"   -ForegroundColor White
Write-Host "  Backend: $Backend"  -ForegroundColor White
Write-Host "  Build#:  $buildNum" -ForegroundColor White
Write-Host "  Time:    $($sw.Elapsed.ToString('mm\:ss\.ff'))" -ForegroundColor White

$statusColor = if ($exitCode -eq 0) { 'Green' } else { 'Red' }
$statusText  = if ($exitCode -eq 0) { 'SUCCESS' } else { 'FAILED' }
Write-Host "  Status:  $statusText" -ForegroundColor $statusColor
Write-Host ''

if (Test-Path $DIST_DIR) {
    Write-Host "  Output: $DIST_DIR" -ForegroundColor White
    Get-ChildItem $DIST_DIR -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
        $sz = Get-FileSize $_.FullName
        Write-Host ("    {0,-32}  {1}" -f $_.Name, $sz) -ForegroundColor Gray
    }
}

Write-Host ''

if ($script:transcriptActive) { try { Stop-Transcript | Out-Null } catch { } }
exit $exitCode
