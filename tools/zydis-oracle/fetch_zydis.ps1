<#
.SYNOPSIS
    Fetch the pinned Zydis the oracle compiles against (tools/zydis-oracle/vendor/zydis).

.DESCRIPTION
    The Rust decoder adapter (dll/rust/src/chain/decode.rs) reproduces what
    Zydis 5.0 at commit 17bc6fb said for the legacy C++ chain detector and
    instance-field xref scan. This oracle proves that against THAT Zydis, so the
    commit is pinned here, independent of the DLL build (which no longer uses
    Zydis).

    Idempotent: a clone already on the pin with its nested submodules intact is
    left alone. Otherwise the exact commit is fetched shallowly and checked out
    detached, then nested submodules (Zycore) are brought to whatever THAT
    commit records — the same Checkout-Commit approach as sync_vendor.ps1,
    which deliberately avoids 'submodule update --remote' (that would move
    Zycore to its own upstream tip, a combination Zydis was never tested with).

.PARAMETER Force
    Discard local edits in vendor/zydis when it has to move.

.EXAMPLE
    .\fetch_zydis.ps1
#>
[CmdletBinding()]
param([switch]$Force)

# Native git failures do not throw in Windows PowerShell 5.1; every git call
# checks $LASTEXITCODE itself.
$ErrorActionPreference = 'Continue'

$Url = 'https://github.com/zyantific/zydis.git'
$Sha = '17bc6fb1584bb31e1d595819aab7be57115750f7'
$Dir = Join-Path $PSScriptRoot 'vendor\zydis'

function Fail([string]$Text) {
    Write-Host "fetch_zydis: $Text" -ForegroundColor Red
    exit 1
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git not found in PATH' }

function Checkout-Commit([string]$D) {
    git -C $D config core.longpaths true
    git -C $D fetch -q --depth 1 origin $Sha
    if ($LASTEXITCODE -ne 0) { return $false }
    git -C $D checkout -q --force FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { return $false }
    git -C $D submodule update -q --init --recursive --depth 1 --force
    return ($LASTEXITCODE -eq 0)
}

function Test-SubmodulesClean([string]$D) {
    # '+' = moved, '-' = not initialised, 'U' = conflicted.
    $st = @(git -C $D submodule status --recursive 2>$null)
    if ($LASTEXITCODE -ne 0) { return $false }
    return (@($st | Where-Object { $_ -match '^[+\-U]' }).Count -eq 0)
}

if (-not (Test-Path (Join-Path $Dir '.git'))) {
    Write-Host "fetch_zydis: cloning zydis at $($Sha.Substring(0,7)) into $Dir" -ForegroundColor Green
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    git -C $Dir init -q
    git -C $Dir remote add origin $Url
    if (-not (Checkout-Commit $Dir)) { Fail "fetch/checkout of $Sha FAILED" }
} else {
    $head = (git -C $Dir rev-parse HEAD 2>$null)
    if ($head -ne $Sha) {
        $dirty = @(git -C $Dir status --porcelain 2>$null | Where-Object { $_ })
        if ($dirty.Count -gt 0 -and -not $Force) {
            Fail "vendor/zydis has local edits and is not on $($Sha.Substring(0,7)) - re-run with -Force to discard them"
        }
        Write-Host "fetch_zydis: moving vendor/zydis to $($Sha.Substring(0,7))" -ForegroundColor Green
        if (-not (Checkout-Commit $Dir)) { Fail "fetch/checkout of $Sha FAILED" }
    } elseif (-not (Test-SubmodulesClean $Dir)) {
        Write-Host 'fetch_zydis: restoring nested submodules to what zydis pins' -ForegroundColor Green
        git -C $Dir submodule update -q --init --recursive --depth 1 --force
        if ($LASTEXITCODE -ne 0) { Fail 'restoring nested submodules FAILED' }
    }
}

# Verify: the pin, clean submodules, and the sources build.ps1 compiles.
$head = (git -C $Dir rev-parse HEAD 2>$null)
if ($head -ne $Sha) { Fail "vendor/zydis is at '$head', expected $Sha" }
if (-not (Test-SubmodulesClean $Dir)) { Fail 'nested submodules are not at the commits zydis records' }
foreach ($f in 'src\Decoder.c', 'include\Zydis\Zydis.h', 'dependencies\zycore\src\Zycore.c') {
    if (-not (Test-Path (Join-Path $Dir $f))) { Fail "missing $f after checkout" }
}
$zycore = (git -C (Join-Path $Dir 'dependencies\zycore') rev-parse --short HEAD 2>$null)
Write-Host "fetch_zydis: zydis $($Sha.Substring(0,7)) (zycore $zycore) OK" -ForegroundColor Green
exit 0
