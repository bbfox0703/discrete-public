# inject-hash.ps1 — post-publish: write SHA256 of DiscreteUI.exe into the
# integrity-guardian slot. Reads the file, finds the 16-byte sentinel
# followed by 32 0xCC bytes, zeroes the slot, computes SHA256 of the
# zeroed buffer, then writes the hash back into the slot. The runtime
# IntegrityGuardian repeats the same zero-then-hash dance and compares.
#
# Sentinel bytes MUST stay in sync with
#   ui/UnityDumpUI.Core/Services/IntegrityCore.cs
#   ui/UnityDumpUI.Core/Services/IntegrityGuardian.cs
#
# Called from build.ps1 only on `-Mode Publish` (AOT). Debug / Release
# builds keep the 0xCC placeholder, which IntegrityCore.IsDevMode treats
# as "skip all checks" so day-to-day iteration is unaffected.

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

$sentinel = [byte[]]@(
    0x9D, 0x4F, 0xC2, 0x18, 0x73, 0xAE, 0x36, 0xB1,
    0xED, 0x5A, 0x82, 0x07, 0xD4, 0x69, 0xF5, 0x2C
)

$sentinelLen = 16
$hashLen     = 32
$slotLen     = $sentinelLen + $hashLen   # 48
$devFill     = 0xCC

function Find-DevSlot {
    param([byte[]]$Binary, [byte[]]$Sentinel)

    for ($i = 0; $i -le ($Binary.Length - $slotLen); $i++) {
        $match = $true
        for ($j = 0; $j -lt $sentinelLen; $j++) {
            if ($Binary[$i + $j] -ne $Sentinel[$j]) { $match = $false; break }
        }
        if (-not $match) { continue }

        # Slot must still be in dev-fill (0xCC * 32) — anything else means
        # the slot was already written, or we matched a stray sentinel-like
        # byte pattern elsewhere in the binary.
        $isDevSlot = $true
        for ($k = 0; $k -lt $hashLen; $k++) {
            if ($Binary[$i + $sentinelLen + $k] -ne $devFill) {
                $isDevSlot = $false; break
            }
        }
        if ($isDevSlot) { return $i }
    }
    return -1
}

Write-Host "[INFO] Reading $ExePath" -ForegroundColor Cyan
[byte[]]$binary = [System.IO.File]::ReadAllBytes($ExePath)
Write-Host "[INFO] Binary size: $($binary.Length) bytes" -ForegroundColor Cyan

$slotPos = Find-DevSlot -Binary $binary -Sentinel $sentinel
if ($slotPos -lt 0) {
    Write-Error "Integrity slot not found. Make sure IntegrityGuardian.HashSlot compiled in and still has 0xCC placeholder."
    exit 1
}
Write-Host "[INFO] Slot at offset 0x$($slotPos.ToString('X8'))" -ForegroundColor Green

# Zero the hash region so the runtime guardian's computation matches
# (it does the same zero-then-hash before comparing).
$hashOffset = $slotPos + $sentinelLen
for ($i = 0; $i -lt $hashLen; $i++) { $binary[$hashOffset + $i] = 0x00 }

$sha256 = [System.Security.Cryptography.SHA256]::Create()
[byte[]]$hash = $sha256.ComputeHash($binary)
$sha256.Dispose()

$hashHex = ($hash | ForEach-Object { $_.ToString("X2") }) -join ""
Write-Host "[INFO] SHA256: $hashHex" -ForegroundColor Cyan

for ($i = 0; $i -lt $hashLen; $i++) { $binary[$hashOffset + $i] = $hash[$i] }

[System.IO.File]::WriteAllBytes($ExePath, $binary)
Write-Host "[OK]   Hash injected at 0x$($hashOffset.ToString('X8'))" -ForegroundColor Green
