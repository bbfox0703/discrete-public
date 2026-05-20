<#
.SYNOPSIS
    Phase 27a — Unity-aware value scan (scanFieldValue) smoke test.

.DESCRIPTION
    Exercises the new `scanFieldValue` pipe RPC in both modes:
      * global       — walks committed RW pages and filters non-managed
                       hits via whatIsAt (Phase 17). Slower First Scan
                       but no class hint required.
      * class-scoped — uses findInstances + per-field SafeRead. Requires
                       a class spec but completes in <100ms typical.

    For each mode, runs an exact-value Int32 scan + a range scan, prints
    sample rows, and validates the response envelope shape.

.PARAMETER ClassAssembly / Namespace / Name
    Class spec for the class-scoped probe. Default matches BongoCat's
    MainGame/CALC_WORK (a common Mono target). Override with the real
    class from your live target.

.PARAMETER Value
    Integer value to search for (exact mode). Default: 100.

.PARAMETER DataType
    Wire dataType string. Default: 'int32'. Accepts int8/int16/int32/
    int64/uint8.../float/double or CLR names like System.Int32.

.PARAMETER ScanType
    'exact' (default) or 'range'.

.PARAMETER ValueTo
    Upper bound for range scans. Default: Value + 10.

.PARAMETER Limit
    Cap on results. Default: 200 (keeps console output manageable).

.PARAMETER SkipGlobal
    Skip the global-mode probe (it scans every RW page so it takes a
    few seconds on big targets).

.EXAMPLE
    .\tools\Test-ScanFieldValue.ps1
    .\tools\Test-ScanFieldValue.ps1 -Value 50 -ScanType range -ValueTo 200
    .\tools\Test-ScanFieldValue.ps1 -ClassAssembly Assembly-CSharp `
        -Namespace '' -Name 'MainGame/PLY_WORK' -Value 9999
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly = 'Assembly-CSharp',
    [string] $Namespace     = '',
    [string] $Name          = 'MainGame/CALC_WORK',
    [object] $Value         = 100,
    [string] $DataType      = 'int32',
    [ValidateSet('exact','range')]
    [string] $ScanType      = 'exact',
    [object] $ValueTo       = $null,
    [int]    $Limit         = 200,
    [switch] $SkipGlobal,
    [int]    $TimeoutMs     = 60000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Pick & inject or unity_dump.lua.' }

# NamedPipeClientStream doesn't support ReadTimeout. ReadLine blocks
# until the DLL handler returns — global scans can take a few seconds.

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Send-Cmd([string]$Cmd, [hashtable]$Body, [int]$Id) {
    $req = @{ id = $Id; cmd = $Cmd; args = $Body } | ConvertTo-Json -Compress -Depth 6
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    $resp = $line | ConvertFrom-Json
    if ($null -ne $resp.error) {
        # JSON-RPC-style envelope: { id, error: { code, message } }. Lift the
        # error into a result-shaped object so the caller's Show-Envelope
        # surfaces it as `ERROR: ...` instead of printing empty fields.
        return [pscustomobject]@{ error = "$($resp.error.message) (code $($resp.error.code))" }
    }
    return $resp.result
}

function Show-Row($row, [int]$index) {
    $field = if ($row.field) { $row.field } elseif ($row.nearestField) { "~$($row.nearestField.name)+$($row.nearestField.delta)" } else { '(interior)' }
    $clsFq = if ($row.ns) { "$($row.ns).$($row.class)" } else { $row.class }
    Write-Host ("  [{0,3}] {1,-18} +0x{2,-4:X} {3,-32} = {4,-12} kind={5} ({6})" -f `
        $index,
        $row.addr,
        [int]$row.fieldOffset,
        $clsFq,
        ($row.currentValue),
        $row.fieldKind,
        $field)
}

function Show-Envelope($r, [string]$label) {
    if ($r.error) {
        Write-Host ("  ERROR: {0}" -f $r.error) -ForegroundColor Red
        return $false
    }
    Write-Host ("  {0,-22} ok={1} mode={2} count={3} truncated={4}" -f $label, $r.ok, $r.mode, $r.count, $r.truncated)
    if ($null -ne $r.droppedNonManaged) {
        Write-Host ("  {0,-22} droppedNonManaged={1} scannedBytes={2:N0} readFailureBytes={3:N0}" -f '', $r.droppedNonManaged, $r.scannedBytes, $r.readFailureBytes)
    }
    if ($null -ne $r.instancesScanned) {
        Write-Host ("  {0,-22} instancesScanned={1} fieldsConsidered={2}" -f '', $r.instancesScanned, $r.fieldsConsidered)
    }
    if ($r.scan_stats) {
        Write-Host ("  {0,-22} regions total={1} scanned={2} skipImage={3} skipStack={4} threadStacks={5}" -f `
            '', $r.scan_stats.regions_total, $r.scan_stats.regions_scanned,
            $r.scan_stats.regions_skip_image, $r.scan_stats.regions_skip_stack,
            $r.scan_stats.thread_stacks)
    }
    return $true
}

if ($null -eq $ValueTo) {
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double]) { $ValueTo = $Value + 10 }
    else                                                                 { $ValueTo = $Value }
}

try {
    Write-Host ''
    Write-Host '==[ handshake — verify backend ready ]==' -ForegroundColor Green
    $hs = Send-Cmd 'handshake' @{} 1
    if ($hs.error) { Write-Host "ERROR: $($hs.error)" -ForegroundColor Red; return }
    Write-Host ("backend={0} unity={1} assemblies={2} build={3}" -f $hs.backend, $hs.unity_version, $hs.assemblies_count, $hs.build)

    # ===== Class-scoped mode =====
    $fqn = if ($Namespace) { "$Namespace.$Name" } else { $Name }
    Write-Host ''
    Write-Host ("==[ scanFieldValue (class-scoped) — {0} {1} {2} {3} ]==" -f $fqn, $DataType, $ScanType, $Value) -ForegroundColor Green
    $body = @{
        dataType  = $DataType
        scanType  = $ScanType
        value     = $Value
        assembly  = $ClassAssembly
        namespace = $Namespace
        name      = $Name
        classScope = $true
        limit     = $Limit
    }
    if ($ScanType -eq 'range') { $body['valueTo'] = $ValueTo }
    $start = Get-Date
    $r1 = Send-Cmd 'scanFieldValue' $body 2
    $elapsed = ((Get-Date) - $start).TotalMilliseconds
    if (Show-Envelope $r1 'class-scoped') {
        Write-Host ("  elapsed                {0:F1} ms" -f $elapsed)
        $shown = 0
        foreach ($row in $r1.candidates) {
            Show-Row $row $shown
            $shown++
            if ($shown -ge 10) { Write-Host ("  ... and {0} more" -f ($r1.count - 10)); break }
        }
        if ($shown -eq 0) { Write-Host '  (no class-scoped matches — try a different value or class)' -ForegroundColor Yellow }
    }

    # ===== Global mode =====
    if (-not $SkipGlobal) {
        Write-Host ''
        Write-Host ("==[ scanFieldValue (global) — {0} {1} {2} ]==" -f $DataType, $ScanType, $Value) -ForegroundColor Green
        $body2 = @{
            dataType = $DataType
            scanType = $ScanType
            value    = $Value
            limit    = $Limit
            maxBackwardScan = 16384
        }
        if ($ScanType -eq 'range') { $body2['valueTo'] = $ValueTo }
        $start = Get-Date
        $r2 = Send-Cmd 'scanFieldValue' $body2 3
        $elapsed = ((Get-Date) - $start).TotalMilliseconds
        if (Show-Envelope $r2 'global') {
            Write-Host ("  elapsed                {0:F1} ms" -f $elapsed)
            $shown = 0
            foreach ($row in $r2.candidates) {
                Show-Row $row $shown
                $shown++
                if ($shown -ge 10) { Write-Host ("  ... and {0} more" -f ($r2.count - 10)); break }
            }
            if ($shown -eq 0) {
                Write-Host '  (no managed hits — value might be too common (got dropped via whatIsAt))' -ForegroundColor Yellow
            }
        }
    }

    Write-Host ''
    Write-Host '==[ scanFieldValue smoke complete ]==' -ForegroundColor Green
}
finally {
    # StreamReader/Writer.Dispose() closes the underlying stream by default,
    # so disposing all three cascades — only dispose the outermost ($client)
    # and let the inner readers/writers drop via GC. Suppresses the spurious
    # "Cannot access a closed pipe" noise on script exit.
    if ($client) { try { $client.Dispose() } catch { } }
}
