<#
.SYNOPSIS
    Phase 17a + 17b — reverse address lookup smoke test.

.DESCRIPTION
    Strategy A backward scan: given any address, walk backwards in 8-byte
    strides looking for a managed object header (first qword == valid
    runtime class pointer / Mono vtable). Reports the containing instance
    + class + raw offset, plus (Phase 17b) the declared field whose
    [offset, offset+size) range contains the queried offset.

    The script first calls findInstances on a target class to obtain a
    real instance address, then probes a series of offsets within that
    instance (the base, +0x8, +0x10, ...) to verify the lookup correctly
    walks back to the same instance and resolves the right field. Also
    probes a deliberately-bogus address to confirm the negative path
    returns `found:false` cleanly.

.PARAMETER ClassAssembly / Namespace / Name
    Target class for the round-trip test. Defaults to
    `UnityEngine.Transform`, present in nearly any Unity scene.

.PARAMETER FieldOffsets
    Byte offsets into the instance to probe. Default: 0, 8, 16, 32, 64.

.PARAMETER InstanceAddress
    Bypass `findInstances` and probe this address directly. Use a known-good
    heap-resident instance address (e.g. one shown in the UI Inspector tab).
    `findInstances`-derived addresses can fall on thread-stack slots whose
    contents change between the scan and the probe; passing an explicit
    address sidesteps that whole class of false positives.

.EXAMPLE
    .\tools\Test-WhatIsAt.ps1
    .\tools\Test-WhatIsAt.ps1 -ClassAssembly Assembly-CSharp -Namespace '' -Name 'MainGame/PLY_WORK'
    .\tools\Test-WhatIsAt.ps1 -ClassAssembly Assembly-CSharp -Namespace '' -Name 'MainGame/PLY_WORK' `
                              -InstanceAddress 0x000001F1A8B40180 -FieldOffsets 0,16,17,24,28
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly   = 'UnityEngine.CoreModule',
    [string] $Namespace       = 'UnityEngine',
    [string] $Name            = 'Transform',
    [int[]]  $FieldOffsets    = @(0, 8, 16, 32, 64),
    [string] $InstanceAddress = '',           # optional override; '0x...' or decimal
    [int]    $InstanceCandidates = 16,         # how many findInstances matches to try
    [int]    $TimeoutMs       = 5000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Pick & inject or unity_dump.lua.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Send-Cmd([string]$Cmd, [hashtable]$Body, [int]$Id) {
    # NOTE: $Args is a PowerShell automatic — never name a param that.
    $req = @{ id = $Id; cmd = $Cmd; args = $Body } | ConvertTo-Json -Compress -Depth 6
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    return ($line | ConvertFrom-Json).result
}

try {
    Write-Host ''
    Write-Host '==[ getClassRangeStats — pre-warm cache ]==' -ForegroundColor Green
    $start = Get-Date
    $stats = Send-Cmd 'getClassRangeStats' @{ rebuild = $false } 1
    $elapsed = ((Get-Date) - $start).TotalMilliseconds
    if ($stats.error) { Write-Host "ERROR: $($stats.error)" -ForegroundColor Red; return }
    Write-Host ("backend       : {0}" -f $stats.backend)
    Write-Host ("class_count   : {0}" -f $stats.class_count)
    if ($stats.vtable_count -ne $null) {
        Write-Host ("vtable_count  : {0}" -f $stats.vtable_count)
    }
    Write-Host ("klass range   : {0} .. {1}" -f $stats.min_klass, $stats.max_klass)
    if ($stats.min_vtable) {
        Write-Host ("vtable range  : {0} .. {1}" -f $stats.min_vtable, $stats.max_vtable)
    }
    Write-Host ("build (ms)    : {0:N0}" -f $elapsed)

    $fqn = if ($Namespace) { "$Namespace.$Name" } else { $Name }
    $instance = $null

    if ($InstanceAddress) {
        # Manual override path — caller already has a known-good heap address.
        $instance = if ($InstanceAddress -like '0x*') {
            '0x{0:X16}' -f [Convert]::ToUInt64($InstanceAddress.Substring(2), 16)
        } else {
            '0x{0:X16}' -f [Convert]::ToUInt64($InstanceAddress)
        }
        Write-Host ''
        Write-Host "==[ Using -InstanceAddress override ]==" -ForegroundColor Green
        Write-Host ("instance      : {0}" -f $instance)
    }
    else {
        Write-Host ''
        Write-Host "==[ findInstances $fqn + whatIsAt validation ]==" -ForegroundColor Green
        $r = Send-Cmd 'findInstances' @{
            assembly  = $ClassAssembly
            namespace = $Namespace
            name      = $Name
            limit     = $InstanceCandidates
        } 2
        if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }
        if ($r.count -lt 1) { Write-Host 'No findInstances matches — pick a different class or pass -InstanceAddress.' -ForegroundColor Yellow; return }
        Write-Host ("candidates    : {0} (probing each until one round-trips)" -f $r.count)

        # findInstances returns every aligned qword in committed RW memory whose
        # value equals the class pointer. That includes:
        #   * stack slots holding transient references (read back as garbage by
        #     the time we whatIsAt them — `*addr` no longer equals the class ptr).
        #   * IL2CPP class-metadata arrays (slots are 8-byte klass pointers; the
        #     queried slot looks like an instance start because *addr == klass,
        #     but +0x10 jumps to the NEXT array slot's class — not the same
        #     instance).
        # Two-stage validation rejects both: round-trip at offset 0, AND probe
        # at +0x10 must return the SAME instance address with the SAME class.
        # Real PLY_WORK instances have at least one declared field inside the
        # first 16-byte header window so +0x10 stays inside; metadata arrays
        # don't.
        $simpleName = ($Name -split '/')[-1]   # strip nested-class prefix for compare
        $idCheck = 50
        for ($i = 0; $i -lt $r.matches.Count; ++$i) {
            $cand = $r.matches[$i]
            $verify0 = Send-Cmd 'whatIsAt' @{ address = $cand } $idCheck
            $idCheck++
            if (-not ($verify0.found -and $verify0.fieldOffset -eq 0 -and $verify0.class -eq $simpleName)) {
                continue
            }
            # Disambiguate against class-metadata arrays.
            $candU64 = [Convert]::ToUInt64($cand.Substring(2), 16)
            $candPlus = '0x{0:X16}' -f ($candU64 + 0x10)
            $verify1 = Send-Cmd 'whatIsAt' @{ address = $candPlus } $idCheck
            $idCheck++
            if ($verify1.found -and $verify1.instance -eq $cand -and $verify1.class -eq $simpleName) {
                $instance = $cand
                Write-Host ("instance      : {0}  (candidate #{1} — round-trip OK)" -f $cand, ($i + 1)) -ForegroundColor Green
                break
            }
        }
        if (-not $instance) {
            Write-Host ("All {0} candidates failed whatIsAt round-trip — likely all stack-slot refs" -f $r.count) -ForegroundColor Yellow
            Write-Host 'Use the UI Inspector to find a real heap-resident instance and re-run with -InstanceAddress.' -ForegroundColor Yellow
            return
        }
    }

    Write-Host ''
    Write-Host '==[ whatIsAt — round-trip at the instance base + a few offsets ]==' -ForegroundColor Green
    $instU64 = [Convert]::ToUInt64($instance, 16)
    $id = 100
    foreach ($off in $FieldOffsets) {
        $probeU64 = $instU64 + [UInt64]$off
        $probeHex = '0x{0:X16}' -f $probeU64
        $start = Get-Date
        $hit = Send-Cmd 'whatIsAt' @{ address = $probeHex } $id
        $elapsed = ((Get-Date) - $start).TotalMilliseconds
        $id++
        if ($hit.found) {
            $instMatch = if ($hit.instance -eq $instance) { 'OK' } else { 'MISMATCH' }
            $cls = if ($hit.namespace) { "$($hit.namespace).$($hit.class)" } else { $hit.class }
            $line = '+0x{0,-4:X} -> inst={1} ({2})  class={3}  off={4}  steps={5}  ({6:N1} ms)' -f `
                $off, $hit.instance, $instMatch, $cls, $hit.fieldOffset, $hit.scannedSteps, $elapsed
            $color = if ($instMatch -eq 'OK') { 'Cyan' } else { 'Yellow' }
            Write-Host $line -ForegroundColor $color
            # Phase 17b — surface the resolved field (or nearest) on a follow-up line.
            if ($hit.field) {
                $f = $hit.field
                $extra = ''
                if ($f.kind -eq 'interior') { $extra = " interior+0x{0:X}" -f $f.interiorOffset }
                if ($f.isEnum -and $f.underlying_type) { $extra += " <$($f.underlying_type)>" }
                Write-Host ('         field: {0,-24} {1,-24} @+0x{2:X} size={3} kind={4} declaredIn={5}{6}' -f `
                    $f.name, $f.type, $f.offset, $f.size, $f.kind, $f.declaredIn, $extra) -ForegroundColor DarkCyan
            } elseif ($hit.nearestField) {
                $nf = $hit.nearestField
                Write-Host ('         nearestField: {0,-20} {1,-24} @+0x{2:X} size={3} delta={4}' -f `
                    $nf.name, $nf.type, $nf.offset, $nf.size, $hit.nearestFieldDelta) -ForegroundColor DarkGray
            } else {
                # No declared field at-or-below the queried offset. Two
                # legitimate causes:
                #   * Offset 0..15 → object header (klass ptr at +0, sync at +8).
                #     Every IL2CPP / Mono managed object lays out the same way.
                #   * Class genuinely has no declared instance fields (rare —
                #     marker types, empty struct).
                if ($hit.fieldOffset -lt 16) {
                    Write-Host '         (object header — klass ptr at +0..7, sync block at +8..15)' -ForegroundColor DarkGray
                } else {
                    Write-Host '         (no declared field at this offset)' -ForegroundColor DarkGray
                }
            }
        } else {
            $line = '+0x{0,-4:X} -> NOT FOUND  reason={1}  steps={2}  ({3:N1} ms)' -f `
                $off, $hit.reason, $hit.scannedSteps, $elapsed
            Write-Host $line -ForegroundColor Yellow
        }
    }

    Write-Host ''
    Write-Host '==[ whatIsAt — bogus address negative path ]==' -ForegroundColor Green
    $hit = Send-Cmd 'whatIsAt' @{ address = '0xDEADBEEF' } 200
    if ($hit.error) {
        Write-Host "ERROR: $($hit.error)" -ForegroundColor Red
    } else {
        $line = '0xDEADBEEF -> found={0}  reason={1}  steps={2}' -f `
            $hit.found, $hit.reason, $hit.scannedSteps
        $color = if ($hit.found) { 'Yellow' } else { 'DarkGray' }
        Write-Host $line -ForegroundColor $color
    }
}
finally {
    $client.Close()
}
