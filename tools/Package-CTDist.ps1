<#
.SYNOPSIS
    Assemble a Cheat Engine .CT distribution folder — copy the backend
    DLLs (renaming the 32-bit Mono build) next to a .CT so the Discrete
    bridge's M.dllPath can resolve them at runtime.

.DESCRIPTION
    The "Inject Discrete Bridge into CE Table" flow does NOT embed the
    backend DLLs in the .CT — they ship beside it. The 32-bit Mono build
    is named Discrete-Mono.dll in dist\x86\, which collides with the
    64-bit name, so it must be renamed to Discrete-Mono-x86.dll when both
    sit in one folder. This script does that renaming copy for you.

    Sources (under -DistDir, default <repo>\dist):
        Discrete-Mono.dll       (x64)  -> <dest>\Discrete-Mono.dll
        Discrete-IL2CPP.dll     (x64)  -> <dest>\Discrete-IL2CPP.dll
        x86\Discrete-Mono.dll   (x86)  -> <dest>\Discrete-Mono-x86.dll

    Each source is best-effort: a missing one (e.g. you only built Mono,
    or skipped the x86 arch) is reported and skipped, not fatal — as long
    as at least one DLL lands. Run build.ps1 -Target DLL -Backend Both
    -Arch Both first to produce all three.

.PARAMETER Destination
    Target folder for the distribution. Created if missing. The DLLs
    (and -CtFile, if given) are copied here.

.PARAMETER CtFile
    Optional .CT to copy alongside the DLLs, so -Destination becomes a
    ready-to-zip distribution folder.

.PARAMETER DistDir
    Source dist folder holding the built DLLs. Default: <repo>\dist.

.EXAMPLE
    .\tools\Package-CTDist.ps1 -Destination .\release\MyGameTrainer `
        -CtFile .\MyGameTrainer.CT

    Produces release\MyGameTrainer\ containing MyGameTrainer.CT plus the
    three correctly-named DLLs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Destination,
    [string] $CtFile,
    [string] $DistDir
)

$ErrorActionPreference = 'Stop'

# Repo root = parent of this tools\ folder. Default DistDir = <repo>\dist.
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DistDir) { $DistDir = Join-Path $repoRoot 'dist' }

if (-not (Test-Path -LiteralPath $DistDir -PathType Container)) {
    throw "DistDir not found: $DistDir  (run build.ps1 -Target DLL first)"
}

# source (relative to DistDir) -> destination file name.
$map = @(
    @{ Src = 'Discrete-Mono.dll';      Dst = 'Discrete-Mono.dll';     Label = 'Mono x64'   }
    @{ Src = 'Discrete-IL2CPP.dll';    Dst = 'Discrete-IL2CPP.dll';   Label = 'IL2CPP x64' }
    @{ Src = 'x86\Discrete-Mono.dll';  Dst = 'Discrete-Mono-x86.dll'; Label = 'Mono x86'   }
)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$destFull = (Resolve-Path -LiteralPath $Destination).Path

$copied  = 0
$skipped = @()
foreach ($e in $map) {
    $src = Join-Path $DistDir $e.Src
    if (Test-Path -LiteralPath $src -PathType Leaf) {
        $dst = Join-Path $destFull $e.Dst
        Copy-Item -LiteralPath $src -Destination $dst -Force
        Write-Host ("  [+] {0,-12} {1} -> {2}" -f $e.Label, $e.Src, $e.Dst)
        $copied++
    } else {
        $skipped += $e.Label
        Write-Warning ("missing {0}: {1} (skipped)" -f $e.Label, $src)
    }
}

if ($copied -eq 0) {
    throw "No DLLs copied — none of the expected builds exist under $DistDir. " +
          "Run: .\build.ps1 -Target DLL -Backend Both -Arch Both"
}

# Optional: copy the .CT alongside the DLLs.
if ($CtFile) {
    if (-not (Test-Path -LiteralPath $CtFile -PathType Leaf)) {
        throw "CtFile not found: $CtFile"
    }
    $ctDst = Join-Path $destFull (Split-Path -Leaf $CtFile)
    Copy-Item -LiteralPath $CtFile -Destination $ctDst -Force
    Write-Host ("  [+] {0,-12} {1}" -f 'CT table', (Split-Path -Leaf $CtFile))
}

Write-Host ""
Write-Host ("Packaged {0} DLL(s) into {1}" -f $copied, $destFull) -ForegroundColor Green
if ($skipped.Count -gt 0) {
    Write-Host ("Skipped (not built): {0}" -f ($skipped -join ', ')) -ForegroundColor Yellow
}
