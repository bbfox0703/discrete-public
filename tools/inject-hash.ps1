# inject-hash.ps1 — post-publish: stamp SHA256 into three token slots
# (BuildArtifactToken / ExportFormatToken / WindowsRuntimeMetadata).
#
# Flow: locate each sentinel + trailing 32x0xCC dev slot, zero ALL three
# hash regions, compute SHA256 of the zeroed buffer ONCE, write that same
# hash into all three slots. The runtime token readers repeat the same
# zero-everything-then-hash dance so each independently verifies the
# whole binary — tampering any slot trips all three checks.
#
# Sentinel bytes MUST stay in sync with
#   ui/UnityDumpUI.Core/Services/SchemaConstants.cs
#   ui/UnityDumpUI.Core/Services/BuildArtifactToken.cs       (slot A)
#   ui/UnityDumpUI.Core/Services/Export/ExportFormatToken.cs (slot B)
#   ui/UnityDumpUI/Platform/WindowsRuntimeMetadata.cs        (slot C)
#
# Called from build.ps1 only on `-Mode Publish` (AOT). Debug / Release
# builds keep the 0xCC placeholders, which IsDevMode treats as "skip all
# checks" so day-to-day iteration is unaffected.

param(
    [Parameter(Mandatory)]
    [string]$ExePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    Write-Error "File not found: $ExePath"
    exit 1
}

$sentinels = @{
    A = [byte[]]@(
        0x9D, 0x4F, 0xC2, 0x18, 0x73, 0xAE, 0x36, 0xB1,
        0xED, 0x5A, 0x82, 0x07, 0xD4, 0x69, 0xF5, 0x2C
    )
    B = [byte[]]@(
        0xF4, 0x8A, 0x21, 0xBE, 0x67, 0xD3, 0x09, 0x55,
        0xA8, 0x1C, 0xEB, 0x4F, 0x73, 0xC2, 0x96, 0x38
    )
    C = [byte[]]@(
        0x52, 0xE7, 0xB9, 0x0D, 0xC4, 0x71, 0x36, 0xAF,
        0x82, 0x5B, 0xF9, 0x14, 0x6E, 0xD8, 0x47, 0xA1
    )
}

$sentinelLen = 16
$hashLen     = 32
$slotLen     = $sentinelLen + $hashLen
$devFill     = 0xCC

function Find-DevSlot {
    param([byte[]]$Binary, [byte[]]$Sentinel, [string]$Name)

    for ($i = 0; $i -le ($Binary.Length - $slotLen); $i++) {
        $match = $true
        for ($j = 0; $j -lt $sentinelLen; $j++) {
            if ($Binary[$i + $j] -ne $Sentinel[$j]) { $match = $false; break }
        }
        if (-not $match) { continue }

        $isDevSlot = $true
        for ($k = 0; $k -lt $hashLen; $k++) {
            if ($Binary[$i + $sentinelLen + $k] -ne $devFill) { $isDevSlot = $false; break }
        }
        if ($isDevSlot) { return $i }
    }
    return -1
}

Write-Host "[INFO] Reading $ExePath" -ForegroundColor Cyan
[byte[]]$binary = [System.IO.File]::ReadAllBytes($ExePath)
Write-Host "[INFO] Binary size: $($binary.Length) bytes" -ForegroundColor Cyan

# Locate all three dev slots first; bail loudly if any is missing.
$slotPositions = @{}
foreach ($name in @('A', 'B', 'C')) {
    $pos = Find-DevSlot -Binary $binary -Sentinel $sentinels[$name] -Name $name
    if ($pos -lt 0) {
        Write-Error "Slot $name not found (sentinel + 32x0xCC). Did the token reader get trimmed away?"
        exit 1
    }
    $slotPositions[$name] = $pos
    Write-Host ("[INFO] Slot {0}: 0x{1:X8}" -f $name, $pos) -ForegroundColor Green
}

# Zero all three hash regions before computing the SHA256, so the hash
# we write matches what the runtime computes (which also zeros all three).
foreach ($name in @('A', 'B', 'C')) {
    $hashOffset = $slotPositions[$name] + $sentinelLen
    for ($i = 0; $i -lt $hashLen; $i++) { $binary[$hashOffset + $i] = 0x00 }
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
[byte[]]$hash = $sha256.ComputeHash($binary)
$sha256.Dispose()

$hashHex = ($hash | ForEach-Object { $_.ToString("X2") }) -join ""
Write-Host "[INFO] SHA256: $hashHex" -ForegroundColor Cyan

# Write the same SHA into all three slots.
foreach ($name in @('A', 'B', 'C')) {
    $hashOffset = $slotPositions[$name] + $sentinelLen
    for ($i = 0; $i -lt $hashLen; $i++) { $binary[$hashOffset + $i] = $hash[$i] }
    Write-Host ("[OK]   Slot {0} stamped at 0x{1:X8}" -f $name, $hashOffset) -ForegroundColor Green
}

[System.IO.File]::WriteAllBytes($ExePath, $binary)
Write-Host "[OK]   All three slots injected" -ForegroundColor Green
