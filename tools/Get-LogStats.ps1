# Get-LogStats.ps1 — show per-PID log directory sizes for Discrete.
#
# Visual check of the rotation + retention policy (dll/src/shared/MagicStrings.h,
# ui/UnityDumpUI.Core/Constants.cs). Run after a heavy session to confirm:
#   - current.log <= 8 MB (larger chunks are archived as YYYYMMDD-HHMMSS.log)
#   - nothing older than the 21-day retention window survives a DLL attach /
#     UI start (flagged "PAST RETENTION")
# There is no file-count cap: retention is by age.
#
# Usage:
#   .\tools\Get-LogStats.ps1                      # all folders under %LOCALAPPDATA%\discrete\Logs
#   .\tools\Get-LogStats.ps1 -Pid 57040           # one PID

[CmdletBinding()]
param(
    # Not "$Pid": that name collides with PowerShell's read-only $PID.
    [Alias('Pid')]
    [int]$ProcessId = 0
)

$ErrorActionPreference = 'Stop'

$root = Join-Path $env:LOCALAPPDATA 'discrete\Logs'
if (-not (Test-Path $root)) {
    Write-Host "No log root: $root" -ForegroundColor Yellow
    exit 0
}

$kMaxFileBytes    = 8MB
$kRetentionDays   = 21
$cutoff           = (Get-Date).AddDays(-$kRetentionDays)

$dirs = if ($ProcessId -gt 0) {
    @(Get-Item (Join-Path $root $ProcessId))
} else {
    Get-ChildItem -Path $root -Directory
}

foreach ($dir in $dirs) {
    Write-Host ""
    Write-Host "$($dir.Name)" -ForegroundColor Cyan
    foreach ($catDir in Get-ChildItem -Path $dir.FullName -Directory) {
        $files = @(Get-ChildItem -Path $catDir.FullName -File | Sort-Object Name)
        $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
        Write-Host ("  {0,-10} {1,3} file(s)  {2,8:N0} KB total" -f
                    $catDir.Name, $files.Count, ($totalBytes / 1KB))
        foreach ($f in $files) {
            $mark = ''
            if ($f.Name -eq 'current.log' -and $f.Length -gt $kMaxFileBytes) { $mark += ' OVER FILE CAP' }
            if ($f.Name -ne 'current.log' -and $f.LastWriteTime -lt $cutoff) { $mark += ' PAST RETENTION' }
            Write-Host ("      {0,-22} {1,8:N0} KB  {2:yyyy-MM-dd HH:mm}{3}" -f
                        $f.Name, ($f.Length / 1KB), $f.LastWriteTime, $mark)
        }
    }
}

Write-Host ""
Write-Host "Policy: $($kMaxFileBytes / 1MB) MB/file then archive; archives + PID folders kept $kRetentionDays days (no count cap)." -ForegroundColor Gray
