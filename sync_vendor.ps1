<#
.SYNOPSIS
    Discrete vendor sync - pinned build deps + floating reference clones.

.DESCRIPTION
    Two DIFFERENT kinds of third-party tree live here, and they must not be
    treated alike (lesson carried over from UE5CEDumper's sync_tools.ps1):

      1. Build deps -> dll/vendor/<name>/   (nlohmann/json, MinHook, Zydis)
         COMPILED INTO THE DLL. Each is PINNED to the commit recorded in
         tools/vendor_pins.json, which the release workflow reads too, so a
         local build and a CI release use the same sources. Moving a pin is a
         dependency bump: rebuild and run the tests before committing it.
         By default this script only restores the pinned checkout and REPORTS
         whether upstream has moved. -UpdatePins opts in to moving them.

      2. Reference clones -> vendor/<name>/  (Il2CppDumper, mono, libil2cpp, ...)
         Read-only oracles for cross-checking layouts. Nothing builds against
         them, so they follow upstream tip freely.

    Both subtrees are gitignored (each clone has its own .git/). Submodules were
    avoided on purpose: nlohmann/json's history is 150+ MB.

    Why the old version was changed (2026-11-14): it hard-reset the build deps
    to upstream tip on every run, so a routine sync silently changed compiled
    code, and nothing recorded which commit a build had used. UE5CEDumper hit
    the same class of problem (a Zydis v4 -> v5 jump broke its decoder code).

.PARAMETER SkipReference
    Do not touch the reference clones; only restore / report the build deps.
    The release workflow runs with this.

.PARAMETER UpdatePins
    Move every build dep to its upstream tip and rewrite tools/vendor_pins.json.
    This is a code change. Afterwards:
      .\build.cmd dll
      .\build.cmd test
    and commit tools/vendor_pins.json only if both pass.

.PARAMETER Force
    Let a pinned checkout move even when its working tree has local edits
    (they are discarded). Without it such a dep is reported and left alone.

.EXAMPLE
    .\sync_vendor.ps1                  # restore pins, report upstream, refresh reference clones
    .\sync_vendor.ps1 -SkipReference   # build deps only (fresh clone / CI)
    .\sync_vendor.ps1 -UpdatePins      # bump the pins (then rebuild + test)
#>

[CmdletBinding()]
param(
    [switch]$SkipReference,
    [switch]$UpdatePins,
    [switch]$Force
)

# Native git failures do not throw in Windows PowerShell 5.1, so every git call
# below checks $LASTEXITCODE itself. 'Stop' would only catch cmdlet errors.
$ErrorActionPreference = 'Continue'
Set-Location -Path $PSScriptRoot

$PinsFile = Join-Path $PSScriptRoot 'tools\vendor_pins.json'
$script:Failed = $false

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}
function Write-Bad([string]$Text) {
    Write-Host "  $Text" -ForegroundColor Red
    $script:Failed = $true
}

$referenceClones = [ordered]@{
    'Il2CppDumper'             = 'https://github.com/Perfare/Il2CppDumper'
    'Il2CppDumperV35plus'      = 'https://github.com/roytu/Il2CppDumper'
    'UnityExplorer'            = 'https://github.com/sinai-dev/UnityExplorer'
    'Il2CppAssemblyUnhollower' = 'https://github.com/knah/Il2CppAssemblyUnhollower'
    'BepInEx'                  = 'https://github.com/BepInEx/BepInEx'
    'mono'                     = 'https://github.com/mono/mono'
    'libil2cpp'                = 'https://github.com/MlgmXyysd/libil2cpp'
    'Il2CppInspectorRedux'     = 'https://github.com/LukeFZ/Il2CppInspectorRedux'
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Shallow-fetch one exact commit and check it out detached, then bring nested
# submodules to whatever THAT commit records.
#
# NOT 'submodule update --remote --recursive': UE5CEDumper measured that it
# also walks nested submodules to THEIR upstream tip, which moved Zydis's
# Zycore off the commit Zydis pins and left a build Zydis was never tested
# against. The parent decides its children; we only decide the parent.
function Checkout-Commit([string]$Dir, [string]$Sha, [bool]$Recursive) {
    # nlohmann/json ships benchmark files with ~150-char names; under a deep
    # checkout path that crosses MAX_PATH and the checkout fails half-way.
    git -C $Dir config core.longpaths true
    git -C $Dir fetch -q --depth 1 origin $Sha
    if ($LASTEXITCODE -ne 0) { return $false }
    git -C $Dir checkout -q --force FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { return $false }
    if ($Recursive) {
        git -C $Dir submodule update -q --init --recursive --depth 1 --force
        if ($LASTEXITCODE -ne 0) { return $false }
    }
    return $true
}

# Upstream tip via ls-remote: no fetch into the clone, and a failure is visible.
# Never compare against an unfetched or failed-to-fetch ref - that reports a
# stale "up to date".
function Get-UpstreamTip([string]$Url) {
    # Collect ALL output before picking a line. Piping into 'Select-Object
    # -First 1' stops the pipeline early, which can kill git before it exits,
    # leaving a random non-zero $LASTEXITCODE (seen: 2 of 3 deps reported
    # FAILED on a healthy network).
    $out = @(git ls-remote $Url HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $out.Count -eq 0) { return $null }
    return ($out[0] -split '\s+')[0]
}

function Get-RepoWebUrl([string]$Url) { return ($Url -replace '\.git$', '') }

# ---------------------------------------------------------------------------
# 1. Build deps (pinned, compiled into the DLL)
# ---------------------------------------------------------------------------
Write-Section 'build deps (pinned by tools/vendor_pins.json, compiled into the DLL)'

if (-not (Test-Path $PinsFile)) {
    Write-Bad "tools/vendor_pins.json not found - cannot sync build deps"
    exit 1
}
try   { $pins = Get-Content $PinsFile -Raw | ConvertFrom-Json }
catch { Write-Bad "tools/vendor_pins.json does not parse: $($_.Exception.Message)"; exit 1 }

$depNames = @($pins.PSObject.Properties | Where-Object { $_.Name -notlike '_*' } | ForEach-Object { $_.Name })
New-Item -ItemType Directory -Path 'dll\vendor' -Force | Out-Null
$newPins = [ordered]@{}

foreach ($name in $depNames) {
    $pin       = $pins.$name
    $dir       = "dll\vendor\$name"
    $recursive = [bool]$pin.recursive
    $target    = $pin.sha

    if ($UpdatePins) {
        $tip = Get-UpstreamTip $pin.url
        if (-not $tip) { Write-Bad "${name}: upstream check FAILED - pin not moved"; $newPins[$name] = $pin.sha; continue }
        $target = $tip
    }

    if (-not (Test-Path "$dir\.git")) {
        Write-Host "  ${name}: cloning at $($target.Substring(0,7))..." -ForegroundColor Green
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        git -C $dir init -q
        git -C $dir remote add origin $pin.url
        if (-not (Checkout-Commit $dir $target $recursive)) { Write-Bad "${name}: fetch/checkout of $target FAILED"; $newPins[$name] = $pin.sha; continue }
    } else {
        $head = (git -C $dir rev-parse HEAD 2>$null)
        if ($head -ne $target) {
            $dirty = @(git -C $dir status --porcelain 2>$null | Where-Object { $_ })
            if ($dirty.Count -gt 0 -and -not $Force) {
                Write-Bad "${name}: has local edits and is not on $($target.Substring(0,7)) - left alone (re-run with -Force to discard them)"
                $newPins[$name] = $pin.sha
                continue
            }
            if (-not (Checkout-Commit $dir $target $recursive)) { Write-Bad "${name}: fetch/checkout of $target FAILED"; $newPins[$name] = $pin.sha; continue }
            $from = if ($head) { $head.Substring(0,7) } else { '?' }
            Write-Host "  ${name}: moved $from -> $($target.Substring(0,7))" -ForegroundColor Green
        } elseif ($recursive) {
            # On the right commit, but a nested submodule may have drifted or
            # never been initialised ('+', '-', 'U' prefixes).
            $drift = @(git -C $dir submodule status --recursive 2>$null | Where-Object { $_ -match '^[+\-U]' })
            if ($drift.Count -gt 0) {
                git -C $dir submodule update -q --init --recursive --depth 1 --force
                if ($LASTEXITCODE -ne 0) { Write-Bad "${name}: restoring nested submodules FAILED" }
                else { Write-Host "  ${name}: nested submodules restored to what $name pins" -ForegroundColor Green }
            }
        }
    }
    $newPins[$name] = $target

    $short = $target.Substring(0,7)
    if ($UpdatePins) {
        if ($target -eq $pin.sha) { Write-Host "  ${name}: already at upstream tip $short" -ForegroundColor Green }
        else { Write-Host "  ${name}: pin $($pin.sha.Substring(0,7)) -> $short  ($(Get-RepoWebUrl $pin.url)/compare/$($pin.sha)...$target)" -ForegroundColor Yellow }
        continue
    }

    # Report-only upstream check.
    $tip = Get-UpstreamTip $pin.url
    if (-not $tip) {
        Write-Host "  ${name}: at pin $short; upstream check FAILED (offline?) - drift unknown" -ForegroundColor Yellow
    } elseif ($tip -eq $pin.sha) {
        Write-Host "  ${name}: at pin $short = upstream tip" -ForegroundColor Green
    } else {
        Write-Host "  ${name}: at pin $short; upstream tip is $($tip.Substring(0,7))" -ForegroundColor Yellow
        Write-Host "      review: $(Get-RepoWebUrl $pin.url)/compare/$($pin.sha)...$tip" -ForegroundColor DarkGray
    }
}

# 'git describe' is useless on these shallow clones, and for Zydis it would lie
# anyway (master's ancestry only reaches the v4.0.0 tag while the header says
# 5.x). The headers' own version macros are the fact.
$zydisHeader = 'dll\vendor\zydis\include\Zydis\Zydis.h'
if (Test-Path $zydisHeader) {
    $m = Select-String -Path $zydisHeader -Pattern 'define\s+ZYDIS_VERSION\s+0x([0-9A-Fa-f]+)' | Select-Object -First 1
    if ($m) {
        $v = [Convert]::ToUInt64($m.Matches[0].Groups[1].Value, 16)
        Write-Host ("  zydis version: {0}.{1}.{2} (from Zydis.h)" -f (($v -shr 48) -band 0xFFFF), (($v -shr 32) -band 0xFFFF), (($v -shr 16) -band 0xFFFF)) -ForegroundColor DarkGray
    }
}
$jsonHeader = 'dll\vendor\nlohmann\single_include\nlohmann\json.hpp'
if (Test-Path $jsonHeader) {
    $parts = foreach ($k in 'MAJOR', 'MINOR', 'PATCH') {
        $m = Select-String -Path $jsonHeader -Pattern "define\s+NLOHMANN_JSON_VERSION_$k\s+(\d+)" | Select-Object -First 1
        if ($m) { $m.Matches[0].Groups[1].Value } else { '?' }
    }
    Write-Host "  nlohmann/json version: $($parts -join '.') (from json.hpp)" -ForegroundColor DarkGray
}

# Leftover from the retired nlohmann submodule; CMake no longer looks there.
if (Test-Path 'dll\vendor\nlohmann_json') {
    Write-Host '  dll\vendor\nlohmann_json is an unused leftover of the old submodule - safe to delete by hand' -ForegroundColor DarkGray
}

if ($UpdatePins) {
    # Rewritten by hand rather than ConvertTo-Json: PowerShell 5.1's output
    # formatting is unreadable and would churn the diff on every bump.
    $comment = $pins._comment -replace '"', '\"'
    $lines = @('{', "  `"_comment`": `"$comment`",")
    $i = 0
    foreach ($name in $depNames) {
        $i++
        $sep = if ($i -lt $depNames.Count) { ',' } else { '' }
        $lines += "  `"$name`": {"
        $lines += "    `"url`": `"$($pins.$name.url)`","
        $lines += "    `"sha`": `"$($newPins[$name])`","
        $lines += "    `"recursive`": $(([bool]$pins.$name.recursive).ToString().ToLower())"
        $lines += "  }$sep"
    }
    $lines += '}'
    [System.IO.File]::WriteAllText($PinsFile, (($lines -join "`n") + "`n"))
    Write-Host ''
    Write-Host '  tools/vendor_pins.json rewritten. This is a DEPENDENCY CHANGE - before committing it:' -ForegroundColor Yellow
    Write-Host '    .\build.cmd dll'
    Write-Host '    .\build.cmd test'
    Write-Host '  The release workflow builds from these pins, so a pin you have not built is a release you have not built.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 2. Reference clones (gitignored, follow upstream tip)
# ---------------------------------------------------------------------------
if (-not $SkipReference) {
    Write-Section 'reference clones (vendor/, follow upstream tip)'
    New-Item -ItemType Directory -Path 'vendor' -Force | Out-Null

    foreach ($entry in $referenceClones.GetEnumerator()) {
        $name = $entry.Key
        $dir  = "vendor\$name"
        if (Test-Path "$dir\.git") {
            $before = (git -C $dir rev-parse --short HEAD 2>$null)
            # Shallow clone: 'pull --ff-only' cannot advance it (no merge base),
            # so fetch the tip shallowly and move to it.
            git -C $dir fetch -q --depth 1 origin
            if ($LASTEXITCODE -ne 0) {
                # Without this, the reset below would land on a stale FETCH_HEAD
                # and the line would read like a fresh sync.
                Write-Bad "${name}: FETCH FAILED - left at $before"
                continue
            }
            git -C $dir reset -q --hard FETCH_HEAD
            $after = (git -C $dir rev-parse --short HEAD 2>$null)
            if ($after -ne $before) { Write-Host "  ${name}: $before -> $after" -ForegroundColor Green }
            else                    { Write-Host "  ${name}: $after (unchanged)" -ForegroundColor DarkGray }
        } else {
            Write-Host "  ${name}: cloning..." -ForegroundColor Green
            git clone -q --depth 1 $entry.Value $dir
            if ($LASTEXITCODE -ne 0) { Write-Bad "${name}: clone FAILED"; continue }
            Write-Host "  ${name}: $(git -C $dir rev-parse --short HEAD 2>$null)" -ForegroundColor Green
        }
    }
}

Write-Host ''
if ($script:Failed) {
    Write-Host 'done, WITH FAILURES (see red lines above).' -ForegroundColor Red
    exit 1
}
Write-Host 'done.' -ForegroundColor Cyan
exit 0
