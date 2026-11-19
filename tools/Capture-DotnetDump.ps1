<#
.SYNOPSIS
  Phase 29g — capture a full managed-heap dump of the running DiscreteUI with
  dotnet-dump and print a `dumpheap -stat` type histogram (NO object cap, unlike
  dotnet-gcdump which truncates at 10,000,000 objects). Resolves the FieldIndex
  weight question the truncated gcdump left open.

.DESCRIPTION
  dotnet-gcdump caps collection at 10M objects, so on a multi-GB heap it only
  shows the first slice. dotnet-dump collects the whole process and
  `dumpheap -stat` (SOS) then reports every type's true Count + TotalSize.

  Same JIT-build requirement as gcdump: SOS managed-heap commands work on a
  CoreCLR (JIT) process but NOT on a Native AOT build. Profile the JIT target
  (`build.cmd ui`, PublishAot=false). The managed heap layout is identical
  AOT-vs-JIT, so the numbers are valid.

  WARNING — a Heap/Full dump file is roughly the size of the process's private
  memory (your baseline was ~21.9 GB). This script checks free disk space on
  the output drive first and aborts if there isn't room. Collecting briefly
  pauses (freezes) the target process. Analyzing a 20 GB dump is slow and
  RAM-hungry.

  Requires the dotnet-dump global tool:
      dotnet tool install -g dotnet-dump
  (or pass -InstallTool).

.EXAMPLE
  .\tools\Capture-DotnetDump.ps1 -Label baseline -InstallTool

.EXAMPLE
  # Just collect now, analyze later (e.g. on another machine):
  .\tools\Capture-DotnetDump.ps1 -Label baseline -NoAnalyze
  .\tools\Capture-DotnetDump.ps1 -AnalyzeOnly -DumpPath C:\path\to\file.dmp
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'DiscreteUI',
    [int]$TargetPid = 0,
    [string]$Label = '',
    [string]$OutDir = "$env:LOCALAPPDATA\discrete\dotnet-dump",
    [ValidateSet('Heap', 'Full', 'Mini', 'Triage')]
    [string]$Type = 'Heap',
    [int]$Top = 60,
    [switch]$NoAnalyze,
    [switch]$AnalyzeOnly,
    [string]$DumpPath = '',
    [switch]$InstallTool
)

$ErrorActionPreference = 'Stop'
function Write-Section($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

# Phase 29g suspect types — the regex used to highlight rows in dumpheap -stat.
$suspectPattern = @(
    'System\.String\b',
    'System\.Char\[\]',
    'System\.Byte\[\]',
    'System\.Text\.Json\.JsonDocument',
    'MetadataDb',
    'SnapshotFieldValue',
    'SnapshotFieldEntry',
    'SnapshotClassEntry',
    'SnapshotInstanceEntry',
    'SnapshotArrayElement',
    'Dictionary`2'
) -join '|'

# -------------- [1] Ensure dotnet-dump --------------
$dd = Get-Command dotnet-dump -ErrorAction SilentlyContinue
if (-not $dd) {
    if ($InstallTool) {
        Write-Section 'Installing dotnet-dump (global tool)'
        & dotnet tool install -g dotnet-dump
        if ($LASTEXITCODE -ne 0) { throw 'dotnet tool install -g dotnet-dump failed.' }
        $dd = Get-Command dotnet-dump -ErrorAction SilentlyContinue
    }
    if (-not $dd) {
        throw "dotnet-dump not found on PATH. Install it with:`n    dotnet tool install -g dotnet-dump`n(or re-run with -InstallTool)."
    }
}

# -------------- [Analyze-only fast path] --------------
function Invoke-Analyze([string]$file, [int]$top, [string]$pattern) {
    if (-not (Test-Path $file)) { throw "Dump file not found: $file" }
    $statPath = [System.IO.Path]::ChangeExtension($file, '.dumpheap-stat.txt')
    Write-Section "Analyzing (dumpheap -stat) — full stat saved to $statPath"
    $out = & dotnet-dump analyze $file -c 'dumpheap -stat' -c 'exit' 2>&1
    $out | Out-File -FilePath $statPath -Encoding UTF8
    $lines = @($out | ForEach-Object { $_.ToString() })

    # dumpheap -stat prints "MT Count TotalSize ClassName" sorted by TotalSize
    # ASCENDING, so the biggest types are at the BOTTOM. Show the tail.
    Write-Host "`n-- Largest $top types by TotalSize (tail of the histogram) --" -ForegroundColor White
    $statRows = $lines | Where-Object { $_ -match '^\s*[0-9a-fA-F`]{6,}\s+\d+\s+\d+' }
    if ($statRows.Count -gt 0) {
        $statRows | Select-Object -Last $top | ForEach-Object { Write-Host $_ }
    }
    else {
        # Fallback: show the last chunk verbatim if the row regex didn't match.
        $lines | Select-Object -Last ($top + 10) | ForEach-Object { Write-Host $_ }
    }

    Write-Section "Phase 29g suspects"
    $hits = $lines | Where-Object { $_ -match $pattern }
    if ($hits.Count -gt 0) { $hits | ForEach-Object { Write-Host $_ -ForegroundColor Yellow } }
    else { Write-Host "  (no suspect rows matched — open the full stat at $statPath)" -ForegroundColor DarkGray }
    Write-Host "`nFull stat: $statPath" -ForegroundColor Cyan
}

if ($AnalyzeOnly) {
    if ([string]::IsNullOrWhiteSpace($DumpPath)) { throw '-AnalyzeOnly requires -DumpPath <file>.' }
    Invoke-Analyze -file $DumpPath -top $Top -pattern $suspectPattern
    return
}

# -------------- [2] Resolve target PID --------------
if ($TargetPid -gt 0) {
    $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
    if (-not $proc) { throw "No process with PID $TargetPid." }
}
else {
    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { throw "No '$ProcessName' process running. Launch the JIT build (build.cmd ui) first, or pass -TargetPid." }
    if ($procs.Count -gt 1) {
        $procs | Select-Object Id, @{n='WS (MB)';e={[math]::Round($_.WorkingSet64/1MB,1)}} | Format-Table -AutoSize
        throw "Multiple '$ProcessName' processes — pass -TargetPid to pick one."
    }
    $proc = $procs[0]
}
$TargetPid = $proc.Id
$privBytes = $proc.PrivateMemorySize64
Write-Section 'Target'
Write-Host ("  Process : {0} (PID {1})" -f $proc.Name, $TargetPid)
Write-Host ("  WS      : {0:N1} MB" -f ($proc.WorkingSet64 / 1MB))
Write-Host ("  Private : {0:N1} MB" -f ($privBytes / 1MB))

# -------------- [3] Disk-space guard --------------
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$drive = (Get-Item $OutDir).PSDrive
$free = (Get-PSDrive -Name $drive.Name).Free
# Heap dumps are smaller than Full but still large; require private + 2 GB slack.
$needed = $privBytes + 2GB
Write-Host ("  Out dir : {0} (drive {1}: {2:N1} GB free)" -f $OutDir, $drive.Name, ($free / 1GB))
if ($free -lt $needed) {
    throw ("Not enough free space on {0}: need ~{1:N1} GB (process private + 2 GB slack), have {2:N1} GB. Point -OutDir at a bigger drive, or use -Type Mini (no managed heap, won't help here)." -f $drive.Name, ($needed / 1GB), ($free / 1GB))
}
if ($Type -in @('Mini', 'Triage')) {
    Write-Host "  NOTE: -Type $Type does not include the full managed heap; dumpheap -stat will be incomplete. Use Heap or Full for Phase 29g." -ForegroundColor Yellow
}

# -------------- [4] Collect --------------
$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$slug = if ([string]::IsNullOrWhiteSpace($Label)) { '' } else { ($Label -replace '[^A-Za-z0-9._-]', '-') + '_' }
$dumpPath = Join-Path $OutDir ("discrete_{0}{1}.dmp" -f $slug, $ts)

Write-Section "Collecting $Type dump (briefly pauses the target — be patient on a 20 GB heap)"
& dotnet-dump collect -p $TargetPid --type $Type -o $dumpPath
$collectOk = ($LASTEXITCODE -eq 0) -and (Test-Path $dumpPath)
if (-not $collectOk -and $Type -eq 'Heap') {
    # Known dotnet-dump issue: --type Heap throws "Value does not fall within
    # the expected range." on very large heaps (heap-subset computation
    # overflow). --type Full uses the simpler OS dump writer and succeeds.
    Write-Host "  Heap collect failed (exit $LASTEXITCODE) — retrying with --type Full (simpler OS writer)…" -ForegroundColor Yellow
    if (Test-Path $dumpPath) { Remove-Item $dumpPath -Force -ErrorAction SilentlyContinue }
    & dotnet-dump collect -p $TargetPid --type Full -o $dumpPath
    $collectOk = ($LASTEXITCODE -eq 0) -and (Test-Path $dumpPath)
}
if (-not $collectOk) {
    throw "dotnet-dump collect failed (exit $LASTEXITCODE). Fallback: create a dump via Task Manager (right-click DiscreteUI -> Create dump file), then run:  .\tools\Capture-DotnetDump.ps1 -AnalyzeOnly -DumpPath <that .DMP>"
}
$dumpGb = (Get-Item $dumpPath).Length / 1GB
Write-Host ("  Saved   : {0} ({1:N2} GB)" -f $dumpPath, $dumpGb) -ForegroundColor Green

# -------------- [5] Analyze --------------
if ($NoAnalyze) {
    Write-Host "`n-NoAnalyze set. Analyze later with:" -ForegroundColor DarkGray
    Write-Host ("  .\tools\Capture-DotnetDump.ps1 -AnalyzeOnly -DumpPath `"{0}`"" -f $dumpPath) -ForegroundColor DarkGray
    return
}
Invoke-Analyze -file $dumpPath -top $Top -pattern $suspectPattern

Write-Host "`nKey question this answers: is FieldIndex (SnapshotFieldValue + its keys/" -ForegroundColor Cyan
Write-Host "ValueDisplay strings) actually a big live retainer? gcdump hid it beyond the"
Write-Host "10M cap; dumpheap -stat shows its true Count + TotalSize here."
