<#
.SYNOPSIS
    Unified build script for Discrete (DLL + UI + tests).

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
        DLL        — C++ injected DLL (uses -Backend) plus the version.dll / winmm.dll proxies
        UI         — Avalonia UI app (publish)
        Test       — xUnit v3 test suite + Rust core tests
        ProxyDLL   — Same as DLL (the proxies are part of the DLL build)
        MetadataCli— Offline metadata CLI (x64)
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
    [ValidateSet('DLL','UI','Test','CppTest','ProxyDLL','MetadataCli','Deploy','DeployScripts','All')]
    [string]$Target = 'All',

    [ValidateSet('Mono','IL2CPP','Both')]
    [string]$Backend = 'Both',

    [ValidateSet('Debug','Release','Publish')]
    [string]$Mode = 'Release',

    # Phase 12d: target architecture for the DLL + proxy build. Default
    # is 'Both' so a no-arg `build.cmd` rebuilds x64 + x86 together —
    # otherwise the x86 DLL silently goes stale and 32-bit Mono targets
    # (Legend of Mortal / LEAVES / 活俠傳) load whatever was in
    # `dist/x86/` from the last time someone passed `-Arch x86`
    # explicitly. The 'Both' path spawns a child process for the x86 leg
    # (DevShell pins PATH to its first arch); the extra ~10 s on top of
    # the x64 build is worth not shipping stale 32-bit binaries.
    # IL2CPP is never built x86 (no shipping IL2CPP target is 32-bit) —
    # the dispatch below skips IL2CPP when -Arch x86 is selected.
    [ValidateSet('x64','x86','Both')]
    [string]$Arch = 'Both',

    [switch]$Clean,

    # Skip the identity guard (account / machine name in tracked files). For
    # debugging the guard itself; do NOT use it to push past a real finding.
    [switch]$NoIdentityCheck,

    # Keep build_number.txt as-is instead of incrementing it. The release
    # workflow pins it to the vNNN tag first, then builds with this flag so
    # the embedded build number equals the tag.
    [switch]$NoBumpBuildNumber,

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
$SLN       = Join-Path $UI_DIR   'Discrete.sln'

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

# Every vcvarsall prompt (x64, x86, arm64) sets Platform to its target
# architecture, and some OEM Windows images set a machine-wide one. MSBuild
# reads every environment variable as a property: Platform=x64 moves the C#
# outputs into bin\x64\..., and x86 / arm64 conflict with the projects'
# win-x64 RuntimeIdentifier. Nothing this script runs needs it (CMake uses
# Ninja), so it goes before anything runs.
if ($env:Platform) {
    Write-Info "Platform=$($env:Platform) cleared: MSBuild would read it and move or break the C# builds"
    Remove-Item Env:Platform
}

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

$script:DevShellLoadedFor = $null   # 'x64' or 'x86' — Enter-VsDevEnvironment caches
$script:VsInstallPath     = $null

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

    # Ask for the C++ toolset first: the newest Visual Studio is not always the
    # one that has it (a fresh VS major can sit beside an older one that does).
    # '-products *' also admits Build Tools, which vswhere's default product
    # filter (Community / Professional / Enterprise) hides; Build Tools ship
    # Microsoft.VisualStudio.DevShell.dll too. tools\zydis-oracle\build.ps1
    # uses the same filter, so both pick the same toolset.
    $vsFilter = @('-latest', '-products', '*', '-prerelease')
    $vsPath = & $vswhere @vsFilter -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
    if (-not $vsPath) {
        # Tell "no Visual Studio" apart from "Visual Studio without the C++ toolset".
        $anyVs = & $vswhere @vsFilter -property installationPath | Select-Object -First 1
        if ($anyVs) {
            throw "Visual Studio found at '$anyVs' but it has no C++ toolset. Add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 (Desktop development with C++) in the Visual Studio Installer."
        }
        throw 'No Visual Studio installation found (need the Desktop development with C++ workload).'
    }

    $script:VsInstallPath = $vsPath
    return $vsPath
}

# The loaded toolset's cl.exe / link.exe folder (VCToolsInstallDir,
# bin\Host<host>\<target>), or $null when the environment names none that
# holds both (no dev env, or one whose toolset a Visual Studio update removed).
function Get-MsvcBinDir {
    if (-not $env:VCToolsInstallDir -or -not $env:VSCMD_ARG_HOST_ARCH -or -not $env:VSCMD_ARG_TGT_ARCH) { return $null }
    $bin = (Join-Path $env:VCToolsInstallDir "bin\Host$($env:VSCMD_ARG_HOST_ARCH)\$($env:VSCMD_ARG_TGT_ARCH)").TrimEnd('\')
    if ((Test-Path -LiteralPath (Join-Path $bin 'cl.exe')) -and (Test-Path -LiteralPath (Join-Path $bin 'link.exe'))) { return $bin }
    return $null
}

# Whether PATH lists $Dir (case-insensitive, trailing backslash ignored).
function Test-OnPath([string]$Dir) {
    return [bool]($env:PATH -split ';' | Where-Object { $_.TrimEnd('\') -eq $Dir } | Select-Object -First 1)
}

# Put the toolset's folder first on PATH when another cl.exe or link.exe
# resolves before it. A shell started from a developer prompt (Git Bash,
# MSYS2) may put its own folders in front, and their usr\bin holds a
# coreutils link.exe that CMake, rustc and the AOT link would otherwise run.
# CMake and Ninja are left where they are.
function Repair-MsvcToolOrder([string]$Bin) {
    $moved = $false
    foreach ($tool in 'cl.exe', 'link.exe') {
        $first = Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $first -or (Split-Path $first.Source -Parent).TrimEnd('\') -ne $Bin) {
            $found = if ($first) { $first.Source } else { 'nothing' }
            Write-Info "$tool resolved to $found; putting $Bin first on PATH"
            $moved = $true
        }
    }
    if ($moved) { $env:PATH = "$Bin;$env:PATH" }
}

$script:MsvcBin = $null   # set by Enter-VsDevEnvironment: the loaded toolset's cl / link folder

function Enter-VsDevEnvironment {
    param(
        [ValidateSet('x64','x86')]
        [string]$Arch = 'x64'
    )
    if ($script:DevShellLoadedFor -eq $Arch) { return }
    if ($script:DevShellLoadedFor -and $script:DevShellLoadedFor -ne $Arch) {
        # Switching arches within one process is unsupported by DevShell
        # (PATH / INCLUDE / LIB stay pinned to the first arch loaded). Bail
        # loud so the caller can rerun the build per-arch.
        throw "DevShell already loaded for $($script:DevShellLoadedFor); cannot switch to $Arch in the same process. Re-run build.ps1 per arch, or use -Arch Both which spawns a child process per arch."
    }

    # Reuse a matching dev env already present in this shell (e.g. inherited from a
    # parent Developer PowerShell, or a sibling repo's vcvars/DevShell publish run in
    # this same session) instead of stacking another layer onto PATH/INCLUDE/LIB.
    # Enter-VsDevShell PREPENDS the VC paths every time; re-entering overflows the
    # linker command line -> AOT link "input line too long", exit 123.
    # Reuse it only while its toolset still exists and PATH still lists it;
    # what a later shell put in front of PATH is repaired. A prompt left open
    # across a Visual Studio update, or a shell that replaced PATH (MSYS2), is
    # entered afresh instead.
    if ($env:VSCMD_ARG_TGT_ARCH -eq $Arch) {
        $bin = Get-MsvcBinDir
        if ($bin -and (Test-OnPath $bin)) {
            Repair-MsvcToolOrder $bin
            $script:MsvcBin = $bin
            $script:DevShellLoadedFor = $Arch
            Write-Ok "MSVC $Arch environment already loaded (reusing inherited env)"
            return
        }
        Write-Info "The inherited $Arch environment names no installed MSVC toolset on PATH; entering the DevShell"
    }

    $vsPath = Resolve-VsInstallPath

    # VsDevCmd honours an inherited VCToolsVersion. A full three-part version
    # must name an installed folder or it refuses to load ("Version ... is not
    # valid"), e.g. in a prompt opened before an update removed that toolset;
    # without it, VsDevCmd picks the installed toolset. Shorter (14.38) and
    # side-by-side (14.38.17.8) pins it resolves itself, so they are kept.
    if ($env:VCToolsVersion -match '^\d+\.\d+\.\d+$' -and -not (Test-Path -LiteralPath (Join-Path $vsPath "VC\Tools\MSVC\$($env:VCToolsVersion)"))) {
        Write-Info "VCToolsVersion=$($env:VCToolsVersion) is not installed under $vsPath; the DevShell picks the installed toolset"
        Remove-Item Env:VCToolsVersion
    }
    $devShellDll = Join-Path $vsPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    if (-not (Test-Path $devShellDll)) {
        throw "Microsoft.VisualStudio.DevShell.dll not found at $devShellDll."
    }

    Write-Info "Loading VS DevShell from $vsPath ($Arch)"
    Import-Module $devShellDll
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments "-arch=$Arch -host_arch=x64" | Out-Null

    $bin = Get-MsvcBinDir
    if (-not $bin) { throw "The VS DevShell ($Arch) named no MSVC toolset folder holding cl.exe and link.exe." }
    Repair-MsvcToolOrder $bin
    $script:MsvcBin = $bin
    $script:DevShellLoadedFor = $Arch
    Write-Ok "MSVC $Arch environment loaded"
}

# CMake arguments that name the loaded toolset's cl.exe for C and C++. A fresh
# configure takes CC / CXX before any PATH search, and under Ninja it tries
# c++ / g++ before cl, so a user's CXX or MSYS2's mingw64\bin ahead on PATH
# would otherwise pick the compiler. Passed as -D, not as environment
# variables, so the -Arch Both x86 child does not inherit the x64 cl.exe.
function Get-MsvcCompilerArgs {
    $cl = Join-Path $script:MsvcBin 'cl.exe'
    foreach ($name in 'CC', 'CXX') {
        $v = [Environment]::GetEnvironmentVariable($name)
        if ($v -and $v -ne $cl) { Write-Info "$name names $v; this configure uses $cl" }
    }
    $fwd = $cl -replace '\\', '/'
    return @("-DCMAKE_C_COMPILER=$fwd", "-DCMAKE_CXX_COMPILER=$fwd")
}

# The gitignored C++ dependencies under dll\vendor are restored by
# sync_vendor.ps1; a fresh clone or a new worktree has none, and CMake would
# then fail inside add_subdirectory with a message that reads like a
# toolchain fault.
function Assert-VendorDeps {
    $missing = @('dll\vendor\minihook\CMakeLists.txt',
                 'dll\vendor\nlohmann\single_include\nlohmann\json.hpp') |
        Where-Object { -not (Test-Path (Join-Path $RepoRoot $_)) }
    if ($missing) {
        Write-Fail "Pinned C++ dependencies missing: $($missing -join ', ')"
        Write-Info 'Restore them with: .\sync_vendor.ps1   (clones the pins in tools\vendor_pins.json)'
        return $false
    }
    return $true
}

# Phase R0: fail fast with an actionable message instead of a CMake error
# halfway through configure. Both triples are needed: x64 for every target,
# i686 for the x86 Mono DLL and the x86 test run.
function Assert-RustToolchain {
    # rustup (not just cargo) is required: rust-toolchain.toml pins the exact
    # toolchain + targets, and only rustup honours it.
    if (-not (Get-Command rustup -ErrorAction SilentlyContinue)) {
        Write-Fail 'rustup not found in PATH — install Rust via https://rustup.rs (a standalone Rust install cannot honour rust-toolchain.toml)'
        return $false
    }
    # build.cmd runs Windows PowerShell 5.1, where any stderr line of a native
    # command becomes a terminating error under EAP=Stop — and rustup reports
    # progress ("info: syncing channel updates ...") on stderr. Relax EAP for
    # these calls and judge success by exit code only.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # No-arg install = whatever rust-toolchain.toml pins, incl. its targets.
        # A no-op when already present; on a fresh clone / after a pin bump it
        # downloads here, in a step that tolerates stderr.
        & rustup toolchain install 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Fail 'rustup toolchain install (from rust-toolchain.toml) failed — run it by hand to see why'
            return $false
        }
        $installed = & rustup target list --installed 2>$null
        $rustc = & rustc --version 2>$null
    } finally {
        $ErrorActionPreference = $prevEap
    }
    $missing = @('x86_64-pc-windows-msvc', 'i686-pc-windows-msvc') | Where-Object { $installed -notcontains $_ }
    if ($missing) {
        Write-Fail "Rust target(s) missing: $($missing -join ', ') — run: rustup target add $($missing -join ' ')"
        return $false
    }
    Write-Info "rust:   $rustc"
    return $true
}

# ---------------------------------------------------------------------------
# Preamble
# ---------------------------------------------------------------------------
Write-Banner "Discrete Build  |  Mode: $Mode  |  Target: $Target  |  Backend: $Backend  |  Arch: $Arch"
Write-Info "Root:   $RepoRoot"
Write-Info "Clean:  $Clean"

# ---------------------------------------------------------------------------
# Identity guard - runs before anything compiles. A commit always precedes a
# push, so the build is the last cheap moment to catch this machine's account
# or computer name (or a C:\Users\<name> path) in a tracked file or an
# unpushed commit message. tools\check_no_local_paths.py reads the names from
# %USERNAME% / %COMPUTERNAME% at run time, stores nothing, and redacts them in
# its own output. Ported from UE5CEDumper's build.ps1.
# ---------------------------------------------------------------------------
function Resolve-PythonExe {
    return (@('py','python','python3') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1)
}

function Resolve-MetadataFixtures {
    $localFixtures = Join-Path $RepoRoot 'tools\verify\fixtures.local.txt'
    if (-not $env:DISCRETE_METADATA_FIXTURES -and (Test-Path $localFixtures)) {
        $list = @(Get-Content $localFixtures | ForEach-Object { ($_ -split '#', 2)[0].Trim() } | Where-Object { $_ })
        if ($list.Count -gt 0) {
            $env:DISCRETE_METADATA_FIXTURES = ($list -join ';')
        }
    }
    if (-not $env:DISCRETE_METADATA_FIXTURES) { return @() }
    return @($env:DISCRETE_METADATA_FIXTURES -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$script:FixturesSummary = $null
$script:GoldenSummary   = $null

if (-not $NoIdentityCheck) {
    Write-Step 'Identity guard (account / machine name in tracked files)'
    $py = Resolve-PythonExe
    if (-not $py) {
        Write-Fail 'no Python interpreter on PATH -- cannot run the identity guard (re-run with -NoIdentityCheck to skip deliberately)'
        exit 1
    }
    Push-Location $RepoRoot
    try     { & $py -I (Join-Path $RepoRoot 'tools\check_no_local_paths.py'); $identityExit = $LASTEXITCODE }
    finally { Pop-Location }
    # 1 is the checker's only "a name leaked" code. Anything else non-zero
    # (its CANNOT RUN code 2, or a py launcher with no interpreter) means the
    # tree was never checked, which is not a leak and not a pass.
    if ($identityExit -eq 1) {
        Write-Fail 'a tracked file or unpushed commit message carries a machine-local path or this machine''s account/computer name (redacted above). Fix it, or pass -NoIdentityCheck for a deliberate placeholder.'
        exit 1
    }
    if ($identityExit -ne 0) {
        Write-Fail "the identity guard could not check the tree (exit $identityExit, reason above). Fix that, or pass -NoIdentityCheck to skip the guard deliberately."
        exit 1
    }
    Write-Ok 'no machine-local paths or identifiers in tracked files'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Fail 'dotnet SDK not found in PATH'
    exit 1
}
# Check dotnet --version from $UI_DIR so ui/global.json pins the reported SDK (D-M7).
Push-Location $UI_DIR
try     { Write-Info "dotnet: $(dotnet --version)" }
finally { Pop-Location }

if ($Target -in @('DLL','Test','ProxyDLL','MetadataCli','All')) {
    if (-not (Assert-RustToolchain)) { exit 1 }
}
if ($Target -in @('DLL','ProxyDLL','MetadataCli','All')) {
    if (-not (Assert-VendorDeps)) { exit 1 }
}

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

if ($NoBumpBuildNumber) {
    $bnPath   = Join-Path $RepoRoot 'build_number.txt'
    $buildNum = if (Test-Path $bnPath) { [int](Get-Content -Path $bnPath -TotalCount 1) } else { 0 }
    Write-Info "Build#: $buildNum (not bumped)"
} else {
    $buildNum = Bump-BuildNumber
    Write-Info "Build#: $buildNum"
}

$exitCode = 0

# ---------------------------------------------------------------------------
# DLL build (CMake + Ninja, MSVC)
# ---------------------------------------------------------------------------
function Build-DllForArch {
    param(
        [ValidateSet('x64','x86')]
        [string]$Arch,
        [ValidateSet('Mono','IL2CPP','Both')]
        [string]$BackendForArch
    )
    Enter-VsDevEnvironment -Arch $Arch

    $cfg = if ($Mode -eq 'Debug') { 'Debug' } else { 'Release' }
    $dllBuildDir = Join-Path $BUILD_DIR "dll-$($BackendForArch.ToLower())-$($cfg.ToLower())-$Arch"
    # x64 outputs ship to dist/ (production); x86 outputs go to dist/x86/
    # to keep the same-name DLLs from clobbering each other.
    $archDistDir = if ($Arch -eq 'x86') { Join-Path $DIST_DIR 'x86' } else { $DIST_DIR }

    Write-Banner "C++ DLL  |  Backend=$BackendForArch  Mode=$cfg  Arch=$Arch"

    # Always do a clean configure — same rule as UE5CEDumper. CMake's cache
    # is fragile across BACKEND swaps, so wipe before reconfigure.
    if (Test-Path $dllBuildDir) {
        Write-Step "Removing CMake cache for clean configure ($dllBuildDir)"
        Remove-Item $dllBuildDir -Recurse -Force
    }

    if (-not (Test-Path $archDistDir)) {
        New-Item -ItemType Directory -Path $archDistDir -Force | Out-Null
    }

    # Publish mode statically links the MSVC CRT so the shipped DLLs don't
    # require VC++ Redistributable on the target machine. Debug / Release
    # keep the default dynamic CRT (cheap iteration, smaller .obj cache).
    $staticCrt = if ($Mode -eq 'Publish') { 'ON' } else { 'OFF' }

    Write-Step "Configuring CMake (Ninja + MSVC $Arch, static-CRT=$staticCrt)"
    $compilerArgs = Get-MsvcCompilerArgs
    & cmake -S $RepoRoot -B $dllBuildDir -G Ninja @compilerArgs `
        "-DCMAKE_BUILD_TYPE=$cfg" `
        "-DBACKEND=$BackendForArch" `
        "-DSTATIC_CRT=$staticCrt" | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Fail 'cmake configure failed'; return $false }
    Write-Ok 'CMake configured'

    Write-Step "Building DLL ($cfg / $Arch)"
    & cmake --build $dllBuildDir --parallel | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Fail 'cmake build failed'; return $false }

    $outDir = Join-Path $dllBuildDir 'bin'
    if (-not (Test-Path $outDir)) {
        Write-Fail "DLL output dir not found: $outDir"
        return $false
    }

    $produced = 0
    # .exe added Phase 12d-5 — inject-x86.exe is bundled into the x86
    # build so DiscreteUI can shell out to it for Pick & Inject on
    # 32-bit targets. Always-empty list under x64.
    # .pdb only ships in Debug — Release/Publish dist stays pdb-free
    # (matches the distribution build; debug symbols aren't redistributed).
    $distExts = if ($Mode -eq 'Debug') { @('.dll', '.pdb', '.exe') } else { @('.dll', '.exe') }
    Get-ChildItem $outDir -File | Where-Object { $_.Extension -in $distExts } | ForEach-Object {
        Copy-Item $_.FullName -Destination $archDistDir -Force
        if ($_.Extension -eq '.dll' -or $_.Extension -eq '.exe') {
            $sz = Get-FileSize (Join-Path $archDistDir $_.Name)
            $relDir = if ($Arch -eq 'x86') { 'dist\x86\' } else { 'dist\' }
            Write-Ok "$($_.Name) ($sz) -> $relDir"
            $produced++
        }
    }
    if ($produced -eq 0) {
        Write-Fail 'No DLLs found in build output'
        return $false
    }
    return $true
}

function Build-Dll {
    # x86 IL2CPP isn't a real-world target (no shipping IL2CPP game is
    # 32-bit), so when -Arch x86|Both we silently restrict the x86 leg
    # to Mono. The user's selected Backend still drives the x64 leg.
    if ($Arch -eq 'Both') {
        # DevShell pins PATH / INCLUDE / LIB to the first arch it loads;
        # there's no clean way to swap mid-process. Spawn a child PS for
        # the second arch so it gets a fresh DevShell.
        $x64Ok = Build-DllForArch -Arch x64 -BackendForArch $Backend
        Write-Banner 'Re-launching child process for x86 build'
        # The child runs in the console PowerShell of this script's own
        # edition (build.cmd starts Windows PowerShell 5.1; pwsh need not be
        # installed), not the host process, which may be an editor such as
        # the ISE. The parent already ran the identity guard and bumped (or
        # pinned) build_number.txt, so every x64 / x86 binary carries the
        # same build number.
        $childArgs = @('-NoProfile','-ExecutionPolicy','Bypass',
                       '-File',$PSCommandPath,
                       '-Target','DLL','-Mode',$Mode,'-Arch','x86',
                       '-NoIdentityCheck','-NoBumpBuildNumber')
        # Out-Host: without it the child's output would become part of this
        # function's return value, and a non-empty array reads as success
        # even when the x86 leg failed.
        $psExe = if ($PSVersionTable.PSEdition -eq 'Core') { Join-Path $PSHOME 'pwsh.exe' } else { Join-Path $PSHOME 'powershell.exe' }
        & $psExe @childArgs | Out-Host
        $x86Ok = ($LASTEXITCODE -eq 0)
        return ($x64Ok -and $x86Ok)
    }

    $backendForArch = if ($Arch -eq 'x86') {
        if ($Backend -eq 'IL2CPP') {
            Write-Info 'Skipping x86 build: IL2CPP has no 32-bit targets in the wild.'
            return $true
        }
        'Mono'
    } else {
        $Backend
    }
    return (Build-DllForArch -Arch $Arch -BackendForArch $backendForArch)
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
        # Native AOT links with MSVC: `build ui publish` never ran the DLL
        # build, so load the x64 environment here. An x86 environment loaded
        # by `-Arch x86` cannot be swapped in this process.
        if ($script:DevShellLoadedFor -eq 'x86') {
            Write-Fail 'The Native AOT publish is win-x64, but this run loaded the x86 MSVC environment; publish the UI with -Arch x64 or Both.'
            return $false
        }
        try { Enter-VsDevEnvironment -Arch x64 }
        catch { Write-Fail "Native AOT needs the MSVC x64 linker; the VS DevShell could not be loaded: $_"; return $false }
        $msvcLink = Join-Path $script:MsvcBin 'link.exe'

        # IlcUseEnvironmentalTools makes ILCompiler take LIB from this
        # environment instead of its own vcvars discovery, and run a bare
        # `link`, which a coreutils link.exe ahead on PATH (Git's or MSYS2's
        # usr\bin) would answer; CppLinker names MSVC's by full path.
        # Run from $UI_DIR so ui/global.json governs the .NET SDK version (D-M7).
        Write-Step 'Publishing UnityDumpUI (Native AOT, Release)'
        Push-Location $UI_DIR
        try {
            & dotnet publish $UI_PROJ `
                -c Release `
                -r win-x64 `
                -p:PublishAot=true `
                -p:IlcUseEnvironmentalTools=true `
                "-p:CppLinker=$msvcLink" `
                -o $publishDir `
                --nologo | Out-Host
        } finally {
            Pop-Location
        }
    }
    else {
        # Self-contained single-file for Debug/Release iteration. The .NET
        # runtime is bundled (~96 MB) but no AOT compile step, so iteration
        # stays fast.
        Write-Step "Publishing UnityDumpUI ($csCfg, self-contained single-file)"
        Push-Location $UI_DIR
        try {
            & dotnet publish $UI_PROJ `
                -c $csCfg `
                -r win-x64 `
                --self-contained `
                -p:PublishSingleFile=true `
                -p:PublishAot=false `
                -p:IncludeNativeLibrariesForSelfExtract=true `
                -p:IncludeAllContentForSelfExtract=true `
                -o $publishDir `
                --nologo | Out-Host
        } finally {
            Pop-Location
        }
    }
    if ($LASTEXITCODE -ne 0) { Write-Fail 'UI publish failed'; return $false }

    $exeFile = Get-ChildItem -Path $publishDir -Filter 'DiscreteUI.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $exeFile) {
        Write-Fail 'No UI build output found'
        return $false
    }

    if ($Mode -eq 'Publish') {
        # AOT publish drops a few native side-DLLs (Skia/HarfBuzz/ANGLE) — copy
        # everything that isn't a build artifact reference. No .pdb: debug
        # symbols are not part of the redistributable.
        Get-ChildItem -Path $publishDir -File |
            Where-Object { $_.Extension -in '.exe', '.dll' } |
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
    Write-Banner 'Unit Tests  |  xUnit v3 (Microsoft.Testing.Platform)'
    if (-not (Test-Path $TEST_PROJ)) {
        Write-Fail "C# test project not found at $TEST_PROJ - no C# tests ran (a failure, not a skip)"
        $null = Run-RustTests
        return $false
    }
    Write-Step 'Building + running tests'
    # `dotnet test` runs in MTP mode — enabled by the `test.runner` section in
    # ui/global.json. That runner config is read from the global.json found by
    # walking UP from the working directory, so we must invoke from $UI_DIR
    # (RepoRoot has no global.json, which would silently fall back to the
    # VSTest target — removed in MTP v2 on the .NET 10 SDK). MTP mode also
    # rejects VSTest-only flags such as --nologo, and takes --project instead
    # of a positional project path.
    Push-Location $UI_DIR
    try {
        & dotnet test --project $TEST_PROJ -c Release -v minimal | Out-Host
    } finally {
        Pop-Location
    }
    $csOk = ($LASTEXITCODE -eq 0)
    if ($csOk) { Write-Ok 'All tests passed' } else { Write-Fail 'Tests failed' }
    $rustOk   = Run-RustTests
    $cppOk    = Run-CppTests
    $goldenOk = Run-GoldenTests
    return ($csOk -and $rustOk -and $cppOk -and $goldenOk)
}

# Offline C++ DLL self-tests (dll/tests/dll_core_test.cpp), built and run on
# BOTH x64 and x86 (WOW64) so arch-sensitive fixes (B-H1, C-H1, C-H2, A-H2)
# are verified on real 32-bit and 64-bit builds. Never copied to dist\.
function Run-CppTestsForArch {
    param([string]$TestArch)
    Enter-VsDevEnvironment -Arch $TestArch

    $cfg = if ($Mode -eq 'Debug') { 'Debug' } else { 'Release' }
    $testBuildDir = Join-Path $BUILD_DIR "cpp-tests-$($cfg.ToLower())-$TestArch"
    if (Test-Path $testBuildDir) {
        Remove-Item $testBuildDir -Recurse -Force
    }

    $staticCrt = if ($Mode -eq 'Publish') { 'ON' } else { 'OFF' }
    Write-Step "Configuring C++ tests ($cfg / $TestArch, static-CRT=$staticCrt)"
    $compilerArgs = Get-MsvcCompilerArgs
    $backendArg   = if ($TestArch -eq 'x86') { 'Mono' } else { 'Both' }
    & cmake -S $RepoRoot -B $testBuildDir -G Ninja @compilerArgs `
        "-DCMAKE_BUILD_TYPE=$cfg" `
        "-DBACKEND=$backendArg" `
        "-DBUILD_TESTS=ON" `
        "-DSTATIC_CRT=$staticCrt" | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Fail "C++ test cmake configure failed ($TestArch)"; return $false }

    Write-Step "Building + running discrete_dll_test ($TestArch)"
    & cmake --build $testBuildDir --target discrete_dll_test --parallel | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Fail "C++ test build failed ($TestArch)"; return $false }

    $exe = Join-Path $testBuildDir 'bin\discrete_dll_test.exe'
    if (-not (Test-Path $exe)) { Write-Fail "discrete_dll_test.exe not found at $exe"; return $false }
    & $exe | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Fail "discrete_dll_test failed ($TestArch)"; return $false }
    Write-Ok "C++ tests passed ($TestArch)"
    return $true
}

function Run-CppTests {
    if ($Arch -eq 'x86') {
        Write-Banner 'C++ DLL self-tests  |  discrete_dll_test (x86)'
        return (Run-CppTestsForArch -TestArch 'x86')
    }
    if ($Arch -eq 'x64') {
        Write-Banner 'C++ DLL self-tests  |  discrete_dll_test (x64)'
        return (Run-CppTestsForArch -TestArch 'x64')
    }
    Write-Banner 'C++ DLL self-tests  |  discrete_dll_test (x64 + x86)'
    $x64Ok = Run-CppTestsForArch -TestArch 'x64'
    $childArgs = @('-NoProfile','-ExecutionPolicy','Bypass',
                   '-File',$PSCommandPath,
                   '-Target','CppTest','-Mode',$Mode,'-Arch','x86',
                   '-NoIdentityCheck','-NoBumpBuildNumber')
    $psExe = if ($PSVersionTable.PSEdition -eq 'Core') { Join-Path $PSHOME 'pwsh.exe' } else { Join-Path $PSHOME 'powershell.exe' }
    & $psExe @childArgs | Out-Host
    $x86Ok = ($LASTEXITCODE -eq 0)
    return ($x64Ok -and $x86Ok)
}

# Phase R0: the Rust core's unit tests, for both shipping architectures. The
# i686 test exe runs natively under WOW64, so this exercises 32-bit codegen and
# panic unwinding without a 32-bit game. DISCRETE_METADATA_FIXTURES
# (semicolon-separated global-metadata.dat paths) -- or, when it is unset, the
# git-ignored tools\verify\fixtures.local.txt (one path per line) -- also runs
# the real-file parser test; it is a no-op without either.
function Run-RustTests {
    Write-Banner 'Rust core tests  |  cargo test (x64 + x86)'
    $manifest = Join-Path $RepoRoot 'dll\rust\Cargo.toml'
    $fixtures = @(Resolve-MetadataFixtures)
    if ($fixtures.Count -gt 0) {
        Write-Info "Metadata fixtures configured: $($fixtures.Count)"
        $script:FixturesSummary = "$($fixtures.Count) configured"
    } else {
        Write-Info 'real_metadata_fixtures: SKIPPED (no DISCRETE_METADATA_FIXTURES or tools\verify\fixtures.local.txt)'
        $script:FixturesSummary = 'SKIPPED (no DISCRETE_METADATA_FIXTURES or fixtures.local.txt)'
    }
    foreach ($triple in @('x86_64-pc-windows-msvc', 'i686-pc-windows-msvc')) {
        Write-Step "cargo test --target $triple"
        & cargo test --locked --manifest-path $manifest --target $triple --quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Fail "Rust tests failed ($triple)"; return $false }
    }
    Write-Ok 'Rust tests passed (x64 + x86)'

    # D-M4: In Publish mode, also verify the exact shipping profile + CRT
    # combination (release + LTO + codegen-units=1 + static CRT /MT), which
    # proves panic unwinding and ABI layout under the real release codegen.
    if ($Mode -eq 'Publish') {
        $hadEncoded  = Test-Path Env:CARGO_ENCODED_RUSTFLAGS
        $prevEncoded = if ($hadEncoded) { $env:CARGO_ENCODED_RUSTFLAGS } else { $null }
        $hadFlags    = Test-Path Env:RUSTFLAGS
        $prevFlags   = if ($hadFlags) { $env:RUSTFLAGS } else { $null }
        try {
            if ($hadFlags) { Remove-Item Env:RUSTFLAGS }
            $env:CARGO_ENCODED_RUSTFLAGS = '-Ctarget-feature=+crt-static'
            foreach ($triple in @('x86_64-pc-windows-msvc', 'i686-pc-windows-msvc')) {
                Write-Step "cargo test --release (+crt-static) --target $triple"
                & cargo test --release --locked --manifest-path $manifest --target $triple --quiet | Out-Host
                if ($LASTEXITCODE -ne 0) { Write-Fail "Rust release (+crt-static) tests failed ($triple)"; return $false }
            }
        } finally {
            if ($hadEncoded) { $env:CARGO_ENCODED_RUSTFLAGS = $prevEncoded }
            elseif (Test-Path Env:CARGO_ENCODED_RUSTFLAGS) { Remove-Item Env:CARGO_ENCODED_RUSTFLAGS }
            if ($hadFlags) { $env:RUSTFLAGS = $prevFlags }
        }
        Write-Ok 'Rust release (+crt-static) tests passed (x64 + x86)'
    }
    return $true
}

# D-M4: Golden-hash regression gate (metadata_golden.py + r2_golden.py).
# Exit 0 = PASS, exit 1 = FAIL (fails build), exit 2 = WARN (no matching fixtures).
# When no game fixtures are configured (e.g. CI), metadata_golden is skipped with a
# warning and r2_golden still runs its deterministic --no-images synthetic suite.
function Run-GoldenTests {
    Write-Banner 'Golden-hash regression gate  |  metadata_golden + r2_golden'
    $py = Resolve-PythonExe
    if (-not $py) {
        Write-Warning 'Golden scripts skipped: no Python interpreter on PATH'
        $script:GoldenSummary = 'WARN (no Python on PATH)'
        return $true
    }

    $cfg = if ($Mode -eq 'Debug') { 'Debug' } else { 'Release' }
    $cliBinDir  = Join-Path $BUILD_DIR "cli-$($cfg.ToLower())\bin"
    $hasMetaCli = Test-Path (Join-Path $cliBinDir 'metadata-cli.exe')
    $hasR2Tools = (Test-Path (Join-Path $cliBinDir 'r2-dump.exe')) -and (Test-Path (Join-Path $cliBinDir 'xref-dump.exe'))
    if (-not $hasMetaCli -and -not $hasR2Tools) {
        Write-Info "Golden scripts skipped: harness binaries not built in $cliBinDir (run -Target MetadataCli or -Target All)"
        $script:GoldenSummary = 'SKIPPED (harness binaries not built)'
        return $true
    }

    $fixtures = @(Resolve-MetadataFixtures)
    $notes = [System.Collections.Generic.List[string]]::new()
    $allOk = $true

    $runOne = {
        param([string]$Label, [string]$ScriptRel, [string[]]$ExtraArgs)
        $scriptPath = Join-Path $RepoRoot $ScriptRel
        Write-Step "$Label ($ScriptRel)"
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $py -I $scriptPath @ExtraArgs | Out-Host
            $code = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $prevEap
        }
        if ($code -eq 0) {
            Write-Ok "$Label passed"
            $notes.Add("${Label}: PASS")
            return $true
        }
        if ($code -eq 2) {
            Write-Warning "$Label returned exit 2 (no matching fixtures / setup warning)"
            $notes.Add("${Label}: WARN (exit 2)")
            return $true
        }
        Write-Fail "$Label failed (exit $code)"
        $notes.Add("${Label}: FAIL (exit $code)")
        return $false
    }

    if ($fixtures.Count -gt 0) {
        if ($hasMetaCli) {
            if (-not (& $runOne 'metadata_golden' 'tools\verify\metadata_golden.py' @('--cli-dir', $cliBinDir))) { $allOk = $false }
        } else {
            $notes.Add('metadata_golden: SKIPPED (metadata-cli.exe missing)')
        }
        if ($hasR2Tools) {
            if (-not (& $runOne 'r2_golden' 'tools\verify\r2_golden.py' @('--cli-dir', $cliBinDir))) { $allOk = $false }
        } else {
            $notes.Add('r2_golden: SKIPPED (r2-dump/xref-dump missing)')
        }
    } else {
        Write-Warning 'metadata_golden: SKIPPED (no DISCRETE_METADATA_FIXTURES or tools\verify\fixtures.local.txt)'
        $notes.Add('metadata_golden: SKIPPED (no fixtures)')
        if ($hasR2Tools) {
            if (-not (& $runOne 'r2_golden (synthetic)' 'tools\verify\r2_golden.py' @('--no-images', '--cli-dir', $cliBinDir))) { $allOk = $false }
        } else {
            $notes.Add('r2_golden: SKIPPED (r2-dump/xref-dump missing)')
        }
    }

    $script:GoldenSummary = $notes -join '; '
    return $allOk
}


# ---------------------------------------------------------------------------
# Proxy DLLs + metadata-cli
# ---------------------------------------------------------------------------
function Build-ProxyDll {
    Write-Info 'The version.dll / winmm.dll proxies are built by the DLL build (BUILD_PROXIES=ON); running it.'
    return (Build-Dll)
}
function Build-MetadataCli {
    # metadata-cli is x64-only (only consumer is the offline-analysis flow;
    # parses IL2CPP metadata which has no 32-bit shipping target).
    Enter-VsDevEnvironment -Arch x64

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

    $staticCrt = if ($Mode -eq 'Publish') { 'ON' } else { 'OFF' }
    Write-Step "Configuring CMake (Ninja + MSVC, BUILD_CLI=ON, static-CRT=$staticCrt)"
    $compilerArgs = Get-MsvcCompilerArgs
    & cmake -S $RepoRoot -B $cliBuildDir -G Ninja @compilerArgs `
        "-DCMAKE_BUILD_TYPE=$cfg" `
        "-DBUILD_CLI=ON" `
        "-DBACKEND=IL2CPP" `
        "-DSTATIC_CRT=$staticCrt" | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Fail 'cmake configure failed'; return $false }

    # metadata-cli ships (copied to dist\) and feeds
    # tools\verify\metadata_golden.py. Phase R2: r2-dump and xref-dump feed
    # tools\verify\r2_golden.py
    # (build-dir only, never shipped).
    Write-Step "Building metadata-cli + harness binaries ($cfg)"
    & cmake --build $cliBuildDir --target metadata-cli r2-dump xref-dump --parallel | Out-Host
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

    # CE Lua bridge (Phase 11) — ship every .lua + .CT so users only
    # need the dist/ folder, not a checked-out repo. README ships as HTML
    # (see below) so end users can double-click without a Markdown reader.
    $scriptsDir = Join-Path $RepoRoot 'scripts'
    if (Test-Path $scriptsDir) {
        foreach ($pattern in @('*.lua', '*.CT')) {
            Get-ChildItem $scriptsDir -File -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
                Copy-Item $_.FullName -Destination $DIST_DIR -Force
            }
        }
    }

    # Render scripts/README.md → dist/README.html with embedded CSS so the
    # end-user package looks polished without needing a Markdown reader.
    # The source of truth stays scripts/README.md (GitHub renders it for
    # repo readers). Conversion runs under pwsh (PS 7) for the built-in
    # ConvertFrom-Markdown (Markdig-backed) — falls back to copying the
    # raw .md if pwsh isn't on PATH.
    $readmeMd     = Join-Path $scriptsDir 'README.md'
    $readmeHtml   = Join-Path $DIST_DIR   'README.html'
    $converter    = Join-Path $RepoRoot   'tools\Convert-ReadmeToHtml.ps1'
    if ((Test-Path $readmeMd) -and (Test-Path $converter)) {
        $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
        if ($pwsh) {
            try {
                & $pwsh.Source -NoProfile -ExecutionPolicy Bypass `
                    -File $converter `
                    -InputPath $readmeMd `
                    -OutputPath $readmeHtml
            } catch {
                Write-Info "README → HTML conversion failed: $_"
                Copy-Item $readmeMd -Destination (Join-Path $DIST_DIR 'README.md') -Force
            }
        } else {
            Write-Info 'pwsh (PowerShell 7) not on PATH — shipping raw README.md instead of HTML'
            Copy-Item $readmeMd -Destination (Join-Path $DIST_DIR 'README.md') -Force
        }
    }
}

# ---------------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------------
function Invoke-Deploy {
    param([string]$DeployMode)
    $script = Join-Path $RepoRoot 'tools\Install-CEScripts.ps1'
    if (-not (Test-Path $script)) {
        Write-Fail "Install-CEScripts.ps1 not found at $script"
        return $false
    }
    # Refresh dist\ from scripts\ + build_number.txt before deploying so the
    # release-mode copy (dist\ -> CE) picks up live edits without requiring
    # a full build. Cheap; touches only Lua + companion files.
    Copy-CompanionArtifacts
    Write-Banner "Deploying to Cheat Engine ($DeployMode)"
    & $script -Mode $DeployMode | Out-Host
    return ($LASTEXITCODE -eq 0)
}

switch ($Target) {
    'DLL'           { if (-not (Build-Dll))         { $exitCode = 1 } }
    'UI'            { if (-not (Build-Ui))          { $exitCode = 1 } }
    'Test'          { if (-not (Run-Tests))         { $exitCode = 1 } }
    'CppTest'       { if (-not (Run-CppTests))      { $exitCode = 1 } }
    'ProxyDLL'      { if (-not (Build-ProxyDll))    { $exitCode = 1 } }
    'MetadataCli'   { if (-not (Build-MetadataCli)) { $exitCode = 1 } }
    'Deploy'        { if (-not (Invoke-Deploy 'release')) { $exitCode = 1 } }
    'DeployScripts' { if (-not (Invoke-Deploy 'scripts')) { $exitCode = 1 } }
    'All' {
        if (-not (Build-Dll))         { $exitCode = 1 }
        if (-not (Build-Ui))          { $exitCode = 1 }
        if (-not (Build-MetadataCli)) { $exitCode = 1 }
        if (-not (Run-Tests))         { $exitCode = 1 }
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
Write-Host "  Arch:    $Arch"     -ForegroundColor White
Write-Host "  Build#:  $buildNum" -ForegroundColor White
Write-Host "  Time:    $($sw.Elapsed.ToString('mm\:ss\.ff'))" -ForegroundColor White
if ($script:FixturesSummary) {
    $fixColor = if ($script:FixturesSummary -like 'SKIPPED*') { 'Yellow' } else { 'Gray' }
    Write-Host "  Fixtures: $($script:FixturesSummary)" -ForegroundColor $fixColor
}
if ($script:GoldenSummary) {
    $gColor = if ($script:GoldenSummary -like '*FAIL*') { 'Red' } elseif ($script:GoldenSummary -like '*WARN*' -or $script:GoldenSummary -like '*SKIPPED*') { 'Yellow' } else { 'Green' }
    Write-Host "  Golden:  $($script:GoldenSummary)" -ForegroundColor $gColor
}

$statusColor = if ($exitCode -eq 0) { 'Green' } else { 'Red' }
$statusText  = if ($exitCode -eq 0) { 'SUCCESS' } else { 'FAILED' }
Write-Host "  Status:  $statusText" -ForegroundColor $statusColor
Write-Host ''

# Prune stray .pdb from dist for distribution builds (Release / Publish).
# Debug builds keep symbols for local debugging. Catches both freshly-copied
# and stale .pdb left from a prior Debug build under dist\ and dist\x86\.
if ($Mode -ne 'Debug' -and (Test-Path $DIST_DIR)) {
    Get-ChildItem $DIST_DIR -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

if (Test-Path $DIST_DIR) {
    Write-Host "  Output: $DIST_DIR" -ForegroundColor White
    Get-ChildItem $DIST_DIR -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
        $sz = Get-FileSize $_.FullName
        Write-Host ("    {0,-32}  {1}" -f $_.Name, $sz) -ForegroundColor Gray
    }
    # x86 outputs (Phase 12d) get their own dist subdir so x64 and x86
    # same-name DLLs don't clobber each other.
    $x86Dir = Join-Path $DIST_DIR 'x86'
    if (Test-Path $x86Dir) {
        Write-Host "  Output: $x86Dir (x86)" -ForegroundColor White
        Get-ChildItem $x86Dir -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
            $sz = Get-FileSize $_.FullName
            Write-Host ("    {0,-32}  {1}" -f $_.Name, $sz) -ForegroundColor Gray
        }
    }
}

Write-Host ''

if ($script:transcriptActive) { try { Stop-Transcript | Out-Null } catch { } }
exit $exitCode
