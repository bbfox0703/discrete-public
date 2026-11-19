<#
.SYNOPSIS
    Phase 6 segment C — heap scan for instances of a class (+ Phase 32 filters).

.DESCRIPTION
    Scans the host's committed RW pages for the class header pointer.
    For IL2CPP, that's Il2CppClass*; for Mono, MonoVTable* of the class
    in the root domain.

    Phase 32 adds a backend-agnostic post-scan filter and a Mono live-object
    enumeration mode to cut false positives:
      -Validate        (default on)  re-read +0 == class header; drops transient
                                      stack/register-spill slots.
      -AliveOnly                      m_CachedPtr != 0; drops destroyed Unity shells.
      -LiveOnly        (Mono)         enumerate via mono_gc_walk_heap (live only).
      -FieldFilter <json>             AND-combined {offset,type,op,value} predicates;
                                      the generic targeting engine (caller supplies
                                      the per-class predicate, e.g. playerIndex==1).

.PARAMETER ClassAssembly / Namespace / Name
    Class to scan for. Default: UnityEngine.Transform.

.PARAMETER Limit
    Max RAW matches. Default 100. Filtering happens AFTER, so raise this if a
    filtered target can sit past the first `limit` raw hits.

.PARAMETER FieldFilter
    Raw JSON array of predicates, spliced verbatim into the request. Example
    (Core Keeper local player, playerIndex==1 at +0x5FC):
      -FieldFilter '[{"offset":1532,"type":"System.Int32","op":"==","value":1}]'

.EXAMPLE
    .\tools\Test-FindInstances.ps1
    .\tools\Test-FindInstances.ps1 -ClassAssembly Pug.Other -Namespace '' -Name PlayerController -Limit 256 -AliveOnly
    .\tools\Test-FindInstances.ps1 -ClassAssembly Pug.Other -Namespace '' -Name PlayerController -Limit 256 `
        -FieldFilter '[{"offset":1532,"type":"System.Int32","op":"==","value":1}]'
    .\tools\Test-FindInstances.ps1 -ClassAssembly Pug.Other -Namespace '' -Name PlayerController -Limit 256 -LiveOnly
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly   = 'UnityEngine.CoreModule',
    [string] $Namespace       = 'UnityEngine',
    [string] $Name            = 'Transform',
    [int]    $Limit           = 100,
    [bool]   $Validate        = $true,
    [switch] $AliveOnly,
    [switch] $Sane,
    [switch] $LiveOnly,
    [int]    $CachedPtrOffset = 0,        # 0 = use server default (object header size)
    [string] $FieldFilter     = '',       # raw JSON array of predicates
    [int]    $TimeoutMs       = 8000
)

$ErrorActionPreference = 'Stop'

# Validate FieldFilter is well-formed JSON before we splice it raw.
if ($FieldFilter) {
    try { [void]($FieldFilter | ConvertFrom-Json) }
    catch { throw "FieldFilter is not valid JSON: $($_.Exception.Message)" }
}

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

try {
    Write-Host ''
    Write-Host "==[ findInstances $Namespace.$Name (assembly=$ClassAssembly, limit=$Limit) ]==" -ForegroundColor Green

    # Build the request JSON explicitly. ConvertTo-Json unwraps single-element
    # arrays (the fieldFilter trap), so we splice the raw JSON array ourselves.
    $argParts = @(
        ('"assembly":'  + ($ClassAssembly | ConvertTo-Json)),
        ('"namespace":' + ($Namespace     | ConvertTo-Json)),
        ('"name":'      + ($Name          | ConvertTo-Json)),
        ('"limit":'     + $Limit),
        ('"validate":'  + $Validate.ToString().ToLowerInvariant())
    )
    if ($AliveOnly)            { $argParts += '"aliveOnly":true' }
    if ($Sane)                 { $argParts += '"sane":true' }
    if ($LiveOnly)             { $argParts += '"liveOnly":true' }
    if ($CachedPtrOffset -gt 0){ $argParts += ('"cachedPtrOffset":' + $CachedPtrOffset) }
    if ($FieldFilter)          { $argParts += ('"fieldFilter":' + $FieldFilter) }
    $req = '{"id":1,"cmd":"findInstances","args":{' + ($argParts -join ',') + '}}'
    $writer.WriteLine($req)

    $start = Get-Date
    $line = $reader.ReadLine()
    $elapsed = ((Get-Date) - $start).TotalMilliseconds

    if (-not $line) { throw 'Empty response' }
    $r = ($line | ConvertFrom-Json).result
    if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }

    $headerLabel = if ($r.class_ptr) { 'class_ptr ' } else { 'vtable    ' }
    $headerVal   = if ($r.class_ptr) { $r.class_ptr } else { $r.vtable }
    Write-Host ("$headerLabel : {0}" -f $headerVal) -ForegroundColor Cyan
    Write-Host ("count       : {0}  (limit {1}, scan took {2:N0} ms)" -f `
        $r.count, $r.limit, $elapsed)

    # Phase 32 — filter telemetry. Shows how many raw hits each layer dropped.
    if ($r.filter) {
        $f = $r.filter
        $lw = if ($null -ne $f.live_walk) { "  liveWalk=$($f.live_walk)" } else { '' }
        $sn = if ($null -ne $f.sane_dropped) { " sane=$($f.sane_dropped)(refFields=$($f.sane_ref_fields))" } else { '' }
        Write-Host ('filter      : raw={0} kept={1}  dropped[validate={2} alive={3}{4} field={5}]{6}' -f `
            $f.raw_count, $f.kept, $f.validate_dropped, $f.alive_dropped, $sn, $f.field_dropped, $lw) -ForegroundColor Magenta
        if ($f.live_walk_unavailable) {
            Write-Host '              (liveOnly requested but mono_gc_walk_heap not exported — fell back to raw scan)' -ForegroundColor Yellow
        }
    }

    if ($r.scan_stats) {
        $s = $r.scan_stats
        Write-Host ('scan_stats  : regions total={0} scanned={1} skipImage={2} skipStack={3} bytes={4:N0}' -f `
            $s.regions_total, $s.regions_scanned, $s.regions_skip_image, `
            $s.regions_skip_stack, $s.bytes_scanned) -ForegroundColor DarkGray
    }
    Write-Host ''

    $shown = [Math]::Min($r.count, 20)
    for ($i = 0; $i -lt $shown; ++$i) {
        '{0,4}: {1}' -f $i, $r.matches[$i] | Write-Host
    }
    if ($r.count -gt $shown) {
        Write-Host ("... ({0} more)" -f ($r.count - $shown)) -ForegroundColor DarkGray
    }
}
finally {
    $client.Close()
}
