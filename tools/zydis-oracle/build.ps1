<#
.SYNOPSIS
    Build and run the Zydis differential oracle (see README.md).

.DESCRIPTION
    1. fetch_zydis.ps1 - pinned Zydis into vendor/zydis (no-op when present).
    2. Loads the MSVC x64 environment (vswhere -> VS DevShell).
    3. Compiles Zydis + native/*.cpp into build/zydis_oracle.lib (only what is
       out of date; -Rebuild forces it).
    4. Runs the crate's tests through cargo with the settings of -Mode.

    Nothing outside tools/zydis-oracle is written. The cargo target dir is
    tools/zydis-oracle/target; generated tables land in target/oracle-tables.

.PARAMETER Mode
    quick  (default) ~4 M random cases (chain + xref), golden vectors, 3k
           generated modules end to end, msvc sort, both table regenerations.
    full   the original proofs: chain fuzz 160 M, e2e 60k modules, xref fuzz
           99 M (two seeds), every exhaustive / structured sweep for BOTH
           projections (293 M each), tables.
    tables only regenerate apx_map4.rs + apx_mem.rs and diff against the repo.
    probe  both projections of one byte string (-Hex "48 8B 00").
    real   end-to-end chain scan of real GameAssembly.dll files (-Dlls "a;b").
    build  only fetch + compile, run nothing.

.PARAMETER Filter
    Extra cargo test-name filter (e.g. 'oracle_sweep::xref_legacy'); runs the
    matching tests including #[ignore]d ones, with the -Mode environment.

.EXAMPLE
    .\tools\zydis-oracle\build.ps1
    .\tools\zydis-oracle\build.ps1 -Mode full
    .\tools\zydis-oracle\build.ps1 -Mode probe -Hex "62 F4 7C 08 01 43 11"
    .\tools\zydis-oracle\build.ps1 -Mode full -Filter oracle_sweep::chain_vex2
#>
[CmdletBinding()]
param(
    [ValidateSet('quick', 'full', 'tables', 'probe', 'real', 'build')]
    [string]$Mode = 'quick',
    [string]$Hex,
    [string]$Dlls,
    [string]$Filter,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

# Run in a child process: the MSVC DevShell, the cargo settings and the -Mode
# knobs below are all environment variables, and a script dot-run from the
# user's shell would leave them behind (a later 'quick' would silently run
# with 'full' sizes, RUSTFLAGS would stay deleted, ...).
if (-not $env:ZYDIS_ORACLE_CHILD) {
    $env:ZYDIS_ORACLE_CHILD = '1'
    try {
        $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Mode', $Mode)
        if ($Hex)     { $childArgs += @('-Hex', $Hex) }
        if ($Dlls)    { $childArgs += @('-Dlls', $Dlls) }
        if ($Filter)  { $childArgs += @('-Filter', $Filter) }
        if ($Rebuild) { $childArgs += '-Rebuild' }
        & (Get-Process -Id $PID).Path @childArgs
        exit $LASTEXITCODE
    } finally {
        Remove-Item Env:ZYDIS_ORACLE_CHILD -ErrorAction SilentlyContinue
    }
}

$Root     = $PSScriptRoot
$Zydis    = Join-Path $Root 'vendor\zydis'
$Zycore   = Join-Path $Zydis 'dependencies\zycore'
$BuildDir = Join-Path $Root 'build'
$Lib      = Join-Path $BuildDir 'zydis_oracle.lib'
$ZydisSha = '17bc6fb1584bb31e1d595819aab7be57115750f7'

function Write-Step([string]$t) { Write-Host "== $t" -ForegroundColor Cyan }
function Fail([string]$t) { Write-Host "zydis-oracle: $t" -ForegroundColor Red; exit 1 }

if ($Mode -eq 'probe' -and -not $Hex)  { Fail '-Mode probe needs -Hex "<bytes>"' }
if ($Mode -eq 'real'  -and -not $Dlls) { Fail '-Mode real needs -Dlls "<GameAssembly.dll>[;...]"' }

# --- 1. Zydis ------------------------------------------------------------------
Write-Step 'fetch Zydis'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'fetch_zydis.ps1')
if ($LASTEXITCODE -ne 0) { Fail 'fetch_zydis.ps1 failed' }

# --- 2. MSVC environment -------------------------------------------------------
function Enter-Msvc {
    if ($env:VSCMD_ARG_TGT_ARCH -eq 'x64' -and (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        Write-Host '   MSVC x64 environment already loaded'
        return
    }
    $candidates = @()
    $inPath = Get-Command vswhere -ErrorAction SilentlyContinue
    if ($inPath) { $candidates += $inPath.Source }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') }
    if ($env:ProgramFiles)        { $candidates += (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe') }
    $vswhere = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $vswhere) { Fail 'vswhere.exe not found - install Visual Studio with the C++ desktop workload' }
    $vs = & $vswhere -latest -prerelease -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { Fail 'no Visual Studio with the x64 C++ tools found' }
    $dll = Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    if (-not (Test-Path $dll)) { Fail "Microsoft.VisualStudio.DevShell.dll not found under $vs" }
    Import-Module $dll
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
    Write-Host "   MSVC x64 environment loaded from $vs"
}

# --- 3. native library ---------------------------------------------------------
$Defs = @('/DZYDIS_STATIC_BUILD', '/DZYCORE_STATIC_BUILD', '/DZYDIS_DISABLE_ENCODER', '/DZYDIS_DISABLE_FORMATTER', '/DNDEBUG')
$Inc  = @("/I$Zydis\include", "/I$Zydis\src", "/I$Zycore\include")
# The legacy DLL built Zydis decoder-only with AVX-512 and KNC tables ON and
# minimal mode OFF (= no ZYDIS_DISABLE_* for those), exactly as here.
$ZydisSrc  = 'Decoder', 'DecoderData', 'MetaInfo', 'Mnemonic', 'Register', 'Segment', 'SharedData', 'String', 'Utils', 'Zydis' |
             ForEach-Object { Join-Path $Zydis "src\$_.c" }
$ZycoreSrc = 'Format', 'String', 'Vector', 'Bitset', 'Zycore', 'API\Memory', 'API\Process', 'API\Synchronization', 'API\Terminal', 'API\Thread' |
             ForEach-Object { Join-Path $Zycore "src\$_.c" }
$Native    = Get-ChildItem (Join-Path $Root 'native\*.cpp') | ForEach-Object { $_.FullName }

function Invoke-Cl([string]$OutDir, [string[]]$Sources, [string[]]$Extra) {
    # Windows PowerShell 5.1 turns native stderr into errors under 'Stop';
    # cl's success is judged by its exit code.
    $ErrorActionPreference = 'Continue'
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $log = Join-Path $OutDir 'cl.log'
    # /Fo<dir>/ keeps one object per source; zydis and zycore both have
    # String.c, hence separate directories. The trailing separator is '/', not
    # '\': Windows PowerShell 5.1 quotes a path with spaces as "...\", and
    # cl's argv parser would read \" as an escaped quote.
    & cl.exe /nologo /c /O2 /MD @Extra @Defs @Inc "/Fo$OutDir/" @Sources > $log 2>&1
    if ($LASTEXITCODE -ne 0) { Get-Content $log | Write-Host; Fail "cl failed ($OutDir)" }
}

function Build-Native {
    $stamp = Join-Path $BuildDir 'zydis.stamp'
    $zydisObj = Join-Path $BuildDir 'obj\zydis'
    $zycoreObj = Join-Path $BuildDir 'obj\zycore'
    $nativeObj = Join-Path $BuildDir 'obj\native'
    $haveZydis = (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $ZydisSha)
    $libTime = if (Test-Path $Lib) { (Get-Item $Lib).LastWriteTimeUtc } else { [datetime]::MinValue }
    $nativeNewer = @($Native | Where-Object { (Get-Item $_).LastWriteTimeUtc -gt $libTime }).Count -gt 0
    if (-not $Rebuild -and $haveZydis -and -not $nativeNewer -and (Test-Path $Lib)) {
        Write-Host "   $Lib is up to date"
        return
    }
    Enter-Msvc
    if ($Rebuild -or -not $haveZydis) {
        Remove-Item -Recurse -Force (Join-Path $BuildDir 'obj') -ErrorAction SilentlyContinue
        Write-Host '   compiling Zydis + Zycore'
        # Vendor code: its own warnings are not ours to fix.
        Invoke-Cl $zydisObj $ZydisSrc @('/W0')
        Invoke-Cl $zycoreObj $ZycoreSrc @('/W0')
        Set-Content -Path $stamp -Value $ZydisSha
    }
    Write-Host '   compiling native/*.cpp'
    Invoke-Cl $nativeObj $Native @('/W3', '/EHsc', '/std:c++20')
    $objs = @(Get-ChildItem $zydisObj, $zycoreObj, $nativeObj -Filter *.obj | ForEach-Object { $_.FullName })
    $ErrorActionPreference = 'Continue'
    & lib.exe /nologo "/OUT:$Lib" @objs
    if ($LASTEXITCODE -ne 0) { Fail 'lib failed' }
    $ErrorActionPreference = 'Stop'
    Write-Host "   built $Lib ($($objs.Count) objects)"
}

Write-Step 'native library'
New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null
Build-Native
if ($Mode -eq 'build') { exit 0 }

# --- 4. cargo ------------------------------------------------------------------
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { Fail 'cargo not found - install Rust via rustup (the repo-root rust-toolchain.toml pins the version)' }

$env:ZYDIS_ORACLE_LIB_DIR = $BuildDir
$env:CARGO_TARGET_DIR     = Join-Path $Root 'target'
$env:ORACLE_OUT_DIR       = Join-Path $Root 'target\oracle-tables'
# A developer's own flags must not change what is being proven.
Remove-Item Env:RUSTFLAGS -ErrorAction SilentlyContinue
Remove-Item Env:CARGO_ENCODED_RUSTFLAGS -ErrorAction SilentlyContinue

$filterArgs = @('oracle_')   # only the oracle tests, not the repo modules' own
$extra = @()
switch ($Mode) {
    'quick'  { }
    'full'   {
        $env:CHAIN_FUZZ  = '20000000'   # x 8 modes = 160 M
        $env:E2E_ITERS   = '60000'
        $env:XF_ITERS    = '5500000'    # x 9 modes x 2 seeds = 99 M
        $env:XF_SEEDS    = '1,2'
        $extra = @('--include-ignored')
    }
    'tables' { $filterArgs = @('oracle_tables') }
    'probe'  { $env:ORACLE_HEX = $Hex;  $filterArgs = @('oracle_probe');  $extra = @('--ignored') }
    'real'   { $env:REAL_DLLS  = $Dlls; $filterArgs = @('oracle_chain::real_game_assembly_images'); $extra = @('--ignored') }
}
if ($Filter) { $filterArgs = @($Filter); $extra = @('--include-ignored') }

Write-Step "cargo test ($Mode)"
Push-Location $Root
$t0 = Get-Date
try {
    # cargo writes progress to stderr; judge by exit code only.
    $ErrorActionPreference = 'Continue'
    & cargo test --locked --lib -- @filterArgs @extra --nocapture
    $rc = $LASTEXITCODE
} finally {
    Pop-Location
    $ErrorActionPreference = 'Stop'
}
$dt = (Get-Date) - $t0
if ($rc -ne 0) {
    Write-Host ''
    Write-Host "zydis-oracle: FAILED after $([int]$dt.TotalSeconds) s (see the mismatch classes above; README 'Reading a mismatch')" -ForegroundColor Red
    if (Test-Path $env:ORACLE_OUT_DIR) { Write-Host "   regenerated tables: $env:ORACLE_OUT_DIR" }
    exit 1
}
Write-Host ''
Write-Host "zydis-oracle: $Mode passed in $([int]$dt.TotalSeconds) s" -ForegroundColor Green
exit 0
