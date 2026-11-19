<#
.SYNOPSIS
    Phase 27b — Begin / Refine / End session workflow smoke test.

.DESCRIPTION
    Exercises the CE-style First Scan + Next Scan loop:
      1. beginValueScan  — runs initial scan, returns sessionId
      2. refineValueScan — applies a filter (changed / unchanged / increased
                           / decreased / exact / range / newValue) against
                           the session's stored previous values, prunes the
                           candidate list in place
      3. (repeat refine as many times as you like)
      4. endValueScan    — drops the session

    The session lets the user converge on a target field by alternating
    "press a button in-game that changes the value" + "scan changed" + "do
    nothing" + "scan unchanged" — same workflow as CE.

.PARAMETER Value
    Initial value to search for. Default: 100.

.PARAMETER DataType
    Wire dataType. Default: int32.

.PARAMETER Refines
    Array of refine modes to chain. Each entry is either a bare mode name
    (changed / unchanged / increased / decreased) or a "mode=value" pair
    for exact / newValue (e.g. "exact=42" / "newValue=99").
    Default: @("unchanged", "unchanged")  — illustrates progressive
    narrowing across two no-op refines.

.PARAMETER PauseSeconds
    Sleep this many seconds between refines so the user can change the
    in-game value before the next refine. Default: 3.

.PARAMETER Limit
    Initial scan candidate cap. Default: 200.

.EXAMPLE
    # Find a player stat:
    #   1. First scan for 100 (HP value)
    #   2. Take damage in game so HP drops to 95 — pause 3s
    #   3. Refine "decreased" — should narrow to 1-2 candidates
    #   4. Pause 3s, no in-game change
    #   5. Refine "unchanged" — confirms persistence
    .\tools\Test-ValueScanSession.ps1 -Value 100 -Refines @("decreased","unchanged")
#>
[CmdletBinding()]
param(
    [object]   $Value        = 100,
    [string]   $DataType     = 'int32',
    [string[]] $Refines      = @('unchanged','unchanged'),
    [int]      $PauseSeconds = 3,
    [int]      $Limit        = 200,
    [int]      $TimeoutMs    = 60000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Send-Cmd([string]$Cmd, [hashtable]$Body, [int]$Id) {
    $req = @{ id = $Id; cmd = $Cmd; args = $Body } | ConvertTo-Json -Compress -Depth 6
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    $resp = $line | ConvertFrom-Json
    if ($null -ne $resp.error) {
        return [pscustomobject]@{ error = "$($resp.error.message) (code $($resp.error.code))" }
    }
    return $resp.result
}

function Show-Row($row, [int]$index) {
    $field = if ($row.field) { $row.field } elseif ($row.nearestField) { "~$($row.nearestField.name)+$($row.nearestField.delta)" } else { '(interior)' }
    $clsFq = if ($row.ns) { "$($row.ns).$($row.class)" } else { $row.class }
    Write-Host ("  [{0,3}] {1,-18} +0x{2,-4:X} {3,-32} = {4,-12} kind={5} ({6})" -f `
        $index, $row.addr, [int]$row.fieldOffset, $clsFq, $row.currentValue, $row.fieldKind, $field)
}

function Show-Envelope($r, [string]$label, [double]$elapsed) {
    if ($r.error) { Write-Host ("  ERROR: {0}" -f $r.error) -ForegroundColor Red; return $false }
    Write-Host ("  {0,-22} count={1} total={2} removed={3} truncated={4} ({5:F0}ms)" -f `
        $label, $r.count, $r.totalCandidates, $r.removed, $r.truncated, $elapsed)
    $shown = 0
    foreach ($row in $r.candidates) {
        Show-Row $row $shown
        $shown++
        if ($shown -ge 8) { Write-Host ("  ... {0} more" -f ($r.count - 8)); break }
    }
    return $true
}

try {
    Write-Host ''
    Write-Host '==[ handshake ]==' -ForegroundColor Green
    $hs = Send-Cmd 'handshake' @{} 1
    Write-Host ("backend={0} unity={1} build={2}" -f $hs.backend, $hs.unity_version, $hs.build)

    Write-Host ''
    Write-Host ("==[ beginValueScan — {0} exact {1}, limit={2} ]==" -f $DataType, $Value, $Limit) -ForegroundColor Green
    $start = Get-Date
    $first = Send-Cmd 'beginValueScan' @{
        dataType        = $DataType
        scanType        = 'exact'
        value           = $Value
        limit           = $Limit
        maxBackwardScan = 16384
    } 2
    $ms = ((Get-Date) - $start).TotalMilliseconds
    if (-not (Show-Envelope $first 'begin' $ms)) { return }
    $sessionId = $first.sessionId
    Write-Host ("  sessionId             {0}" -f $sessionId)

    $idCounter = 3
    foreach ($refine in $Refines) {
        if ($PauseSeconds -gt 0) {
            Write-Host ''
            Write-Host ("==[ pausing {0}s — change the in-game value now if needed ]==" -f $PauseSeconds) -ForegroundColor Yellow
            Start-Sleep -Seconds $PauseSeconds
        }
        # Parse "mode=value" or bare "mode"
        $mode = $refine; $val = $null
        if ($refine -match '^(\w+)=(.+)$') {
            $mode = $Matches[1]; $val = $Matches[2]
        }
        Write-Host ''
        $label = if ($val) { "refineValueScan — $mode value=$val" } else { "refineValueScan — $mode" }
        Write-Host ("==[ {0} ]==" -f $label) -ForegroundColor Green
        $body = @{ sessionId = $sessionId; scanType = $mode; limit = 50 }
        if ($val -ne $null) {
            # Try numeric first, fall back to string
            if ($val -as [int])    { $body['value'] = [int]$val    }
            elseif ($val -as [double]) { $body['value'] = [double]$val }
            else                   { $body['value'] = $val          }
        }
        $start = Get-Date
        $r = Send-Cmd 'refineValueScan' $body $idCounter
        $ms = ((Get-Date) - $start).TotalMilliseconds
        Show-Envelope $r $mode $ms | Out-Null
        $idCounter++
    }

    Write-Host ''
    Write-Host '==[ endValueScan ]==' -ForegroundColor Green
    $end = Send-Cmd 'endValueScan' @{ sessionId = $sessionId } $idCounter
    if ($end.error) { Write-Host ("  ERROR: {0}" -f $end.error) -ForegroundColor Red }
    else            { Write-Host ("  dropped={0}" -f $end.dropped) }

    Write-Host ''
    Write-Host '==[ session workflow complete ]==' -ForegroundColor Green
}
finally {
    if ($client) { try { $client.Dispose() } catch { } }
}
