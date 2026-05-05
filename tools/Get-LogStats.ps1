# Get-LogStats.ps1 — show per-PID log directory sizes for UnityCEDumper.
#
# Phase 14e: visual verification that the 4-file × 8 MB rotation cap is
# being honoured. Run after a heavy session to confirm:
#   - current.log <= 8 MB
#   - at most 4 files per category (current.log + current.{1,2,3}.log)
#   - per-category total <= ~32 MB
#
# Usage:
#   .\tools\Get-LogStats.ps1                      # all PIDs under %LOCALAPPDATA%\UnityCEDumper\Logs
#   .\tools\Get-LogStats.ps1 -Pid 57040           # one PID

[CmdletBinding()]
param(
    [int]$Pid = 0
)

$ErrorActionPreference = 'Stop'

$root = Join-Path $env:LOCALAPPDATA 'UnityCEDumper\Logs'
if (-not (Test-Path $root)) {
    Write-Host "No log root: $root" -ForegroundColor Yellow
    exit 0
}

# 4 × 8 MB rotation per category — see dll/src/shared/MagicStrings.h.
$kMaxFileBytes  = 8MB
$kMaxFileCount  = 4
$kCategoryCap   = $kMaxFileBytes * $kMaxFileCount

$pidDirs = if ($Pid -gt 0) {
    @(Join-Path $root $Pid)
} else {
    Get-ChildItem -Path $root -Directory
}

foreach ($pidDir in $pidDirs) {
    if (-not (Test-Path $pidDir)) { continue }
    $pidName = Split-Path -Leaf $pidDir
    Write-Host ""
    Write-Host "PID $pidName" -ForegroundColor Cyan
    foreach ($catDir in Get-ChildItem -Path $pidDir -Directory) {
        $files = Get-ChildItem -Path $catDir.FullName -File | Sort-Object Name
        $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
        $overCount  = if ($files.Count -gt $kMaxFileCount) { ' OVER FILE CAP' } else { '' }
        $overBytes  = if ($totalBytes -gt $kCategoryCap)   { ' OVER BYTE CAP' } else { '' }
        Write-Host ("  {0,-10} {1,3} file(s)  {2,8:N0} KB total{3}{4}" -f
                    $catDir.Name, $files.Count, ($totalBytes / 1KB),
                    $overCount, $overBytes)
        foreach ($f in $files) {
            $rotMark = if ($f.Length -gt $kMaxFileBytes) { ' OVER FILE CAP' } else { '' }
            Write-Host ("      {0,-18} {1,8:N0} KB{2}" -f $f.Name, ($f.Length / 1KB), $rotMark)
        }
    }
}

Write-Host ""
Write-Host "Caps: $($kMaxFileBytes / 1MB) MB/file, $kMaxFileCount files/category, $($kCategoryCap / 1MB) MB/category total." -ForegroundColor Gray
