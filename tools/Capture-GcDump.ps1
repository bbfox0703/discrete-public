<#
.SYNOPSIS
  Phase 29g — capture a managed-heap gcdump of the running DiscreteUI and
  print a type-level retained-size breakdown of the memory suspects
  (System.String, Char[]/Byte[], Dictionary, SnapshotFieldValue,
  SnapshotFieldEntry, JsonDocument, ...).

.DESCRIPTION
  Confirms the Phase 29g root causes (string duplication / FieldIndex
  doubling / JsonElement retention) BEFORE optimizing, and gives
  before/after numbers for the acceptance target (1 snap < ~2 GB,
  3 snaps < ~6 GB on SaGa Frontier 2 Remastered).

  IMPORTANT — profile a JIT build, NOT the shipping Native AOT build.
  dotnet-gcdump uses EventPipe heap-dump events, which CoreCLR (JIT)
  emits but Native AOT does not. Build the JIT target with:

      .\build.cmd ui          # PublishAot=false, self-contained single-file

  then run that DiscreteUI.exe. The managed heap object layout is
  identical between AOT and JIT (AOT only changes codegen, not the GC
  heap representation), so the type-level numbers are fully valid for
  driving the optimization. If gcdump returns an empty report, you are
  almost certainly attached to the AOT build — rebuild with `build.cmd ui`.

  Requires the dotnet-gcdump global tool:
      dotnet tool install -g dotnet-gcdump
  (or pass -InstallTool to let this script install it for you).

.EXAMPLE
  # Baseline: take 3 snapshots on SaGa F2R, then:
  .\tools\Capture-GcDump.ps1 -Label baseline

.EXAMPLE
  # After landing 29g-1 interning:
  .\tools\Capture-GcDump.ps1 -Label after-interning
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'DiscreteUI',
    [int]$TargetPid = 0,
    [string]$Label = '',
    [string]$OutDir = "$env:LOCALAPPDATA\Discrete_UEDumper\gcdump",
    [int]$Top = 40,
    [switch]$NoReport,
    [switch]$InstallTool
)

$ErrorActionPreference = 'Stop'

function Write-Section($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

# -------------- [1] Ensure dotnet-gcdump --------------
$gc = Get-Command dotnet-gcdump -ErrorAction SilentlyContinue
if (-not $gc) {
    if ($InstallTool) {
        Write-Section 'Installing dotnet-gcdump (global tool)'
        & dotnet tool install -g dotnet-gcdump
        if ($LASTEXITCODE -ne 0) { throw 'dotnet tool install -g dotnet-gcdump failed.' }
        $gc = Get-Command dotnet-gcdump -ErrorAction SilentlyContinue
    }
    if (-not $gc) {
        throw "dotnet-gcdump not found on PATH. Install it with:`n    dotnet tool install -g dotnet-gcdump`n(or re-run this script with -InstallTool)."
    }
}

# -------------- [2] Resolve target PID --------------
if ($TargetPid -gt 0) {
    $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
    if (-not $proc) { throw "No process with PID $TargetPid." }
}
else {
    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) {
        throw "No '$ProcessName' process running. Launch the JIT build (build.cmd ui) first, or pass -TargetPid."
    }
    if ($procs.Count -gt 1) {
        Write-Host "Multiple '$ProcessName' processes found:" -ForegroundColor Yellow
        $procs | Select-Object Id, @{n='WS (MB)';e={[math]::Round($_.WorkingSet64/1MB,1)}} | Format-Table -AutoSize
        throw "Pass -TargetPid to pick one (the app is single-instance, so this is unexpected)."
    }
    $proc = $procs[0]
}
$TargetPid = $proc.Id
$wsMb = [math]::Round($proc.WorkingSet64 / 1MB, 1)
$priMb = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
Write-Section "Target"
Write-Host ("  Process : {0} (PID {1})" -f $proc.Name, $TargetPid)
Write-Host ("  WS      : {0:N1} MB" -f $wsMb)
Write-Host ("  Private : {0:N1} MB" -f $priMb)

# -------------- [3] Collect the gcdump --------------
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$slug = if ([string]::IsNullOrWhiteSpace($Label)) { '' } else { ($Label -replace '[^A-Za-z0-9._-]', '-') + '_' }
$dumpPath = Join-Path $OutDir ("discrete_{0}{1}.gcdump" -f $slug, $ts)

Write-Section "Collecting gcdump (this triggers a GC + heap walk; a few seconds for a big heap)"
& dotnet-gcdump collect -p $TargetPid -o $dumpPath
if ($LASTEXITCODE -ne 0) { throw "dotnet-gcdump collect failed (exit $LASTEXITCODE)." }
if (-not (Test-Path $dumpPath)) { throw "gcdump file was not created: $dumpPath" }
$dumpMb = [math]::Round((Get-Item $dumpPath).Length / 1MB, 2)
Write-Host ("  Saved   : {0} ({1:N2} MB)" -f $dumpPath, $dumpMb) -ForegroundColor Green

# -------------- [4] Report: type-level breakdown --------------
if ($NoReport) {
    Write-Host "`n-NoReport set; skipping 'dotnet-gcdump report'. Open the .gcdump in PerfView or Visual Studio." -ForegroundColor DarkGray
    return
}

$reportPath = [System.IO.Path]::ChangeExtension($dumpPath, '.report.txt')
Write-Section "Type breakdown (full report saved to $reportPath)"
$report = & dotnet-gcdump report $dumpPath 2>&1
$report | Out-File -FilePath $reportPath -Encoding UTF8

$lines = @($report | ForEach-Object { $_.ToString() })
if ($lines.Count -le 2) {
    Write-Host "report is empty — you are almost certainly attached to the Native AOT build." -ForegroundColor Red
    Write-Host "Rebuild a JIT profiling target with:  .\build.cmd ui   then re-run this script." -ForegroundColor Yellow
    return
}

# Top N lines verbatim (report is already size-sorted).
Write-Host "`n-- Top $Top types (by retained size) --" -ForegroundColor White
$lines | Select-Object -First $Top | ForEach-Object { Write-Host $_ }

# Highlight the Phase 29g suspects wherever they appear in the full report.
$suspects = @(
    'System\.String\b',
    'System\.Char\[\]',
    'System\.Byte\[\]',
    'Dictionary',
    'SnapshotFieldValue',
    'SnapshotFieldEntry',
    'SnapshotClassEntry',
    'SnapshotInstanceEntry',
    'SnapshotArrayElement',
    'JsonDocument',
    'MetadataDb'
)
$pattern = ($suspects -join '|')
$hits = $lines | Where-Object { $_ -match $pattern }
Write-Section "Phase 29g suspects (string dup / FieldIndex doubling / JsonElement)"
if ($hits.Count -gt 0) {
    $hits | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}
else {
    Write-Host "  (no suspect types matched — check the full report at $reportPath)" -ForegroundColor DarkGray
}

Write-Host "`nDone. Compare baseline vs post-fix:" -ForegroundColor Cyan
Write-Host "  - System.String + Char[] should collapse after 29g-1 (interning)."
Write-Host "  - Dictionary + SnapshotFieldValue should drop for non-A/B snapshots after 29g-2 (lazy/released FieldIndex)."
Write-Host "  - JsonDocument/Byte[] retention is the 29g-3 (profiler-gated) signal."
