<#
.SYNOPSIS
    Phase 17a — reverse address lookup smoke test.

.DESCRIPTION
    Strategy A backward scan: given any address, walk backwards in 8-byte
    strides looking for a managed object header (first qword == valid
    runtime class pointer / Mono vtable). Reports the containing instance
    + class + offset.

    The script first calls findInstances on a target class to obtain a
    real instance address, then probes a series of offsets within that
    instance (the base, +0x8, +0x10, ...) to verify the lookup correctly
    walks back to the same instance. Also probes a deliberately-bogus
    address to confirm the negative path returns `found:false` cleanly.

.PARAMETER ClassAssembly / Namespace / Name
    Target class for the round-trip test. Defaults to
    `UnityEngine.Transform`, present in nearly any Unity scene.

.PARAMETER FieldOffsets
    Byte offsets into the instance to probe. Default: 0, 8, 16, 32, 64.

.EXAMPLE
    .\tools\Test-WhatIsAt.ps1
    .\tools\Test-WhatIsAt.ps1 -ClassAssembly Assembly-CSharp -Namespace MainGame -Name PLY_WORK
    .\tools\Test-WhatIsAt.ps1 -ClassAssembly Assembly-CSharp -Namespace '' -Name GameManager -FieldOffsets 0,16,128
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly = 'UnityEngine.CoreModule',
    [string] $Namespace     = 'UnityEngine',
    [string] $Name          = 'Transform',
    [int[]]  $FieldOffsets  = @(0, 8, 16, 32, 64),
    [int]    $TimeoutMs     = 5000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
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

    Write-Host ''
    Write-Host "==[ findInstances $Namespace.$Name — find an instance to probe ]==" -ForegroundColor Green
    $r = Send-Cmd 'findInstances' @{
        assembly  = $ClassAssembly
        namespace = $Namespace
        name      = $Name
        limit     = 4
    } 2
    if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }
    if ($r.count -lt 1) { Write-Host 'No instances found — pick a different target class.' -ForegroundColor Yellow; return }
    $instance = $r.matches[0]
    Write-Host ("instance      : {0}" -f $instance)

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
            $line = '+0x{0,-4:X} -> instance={1} ({2})  class={3}  off={4}  steps={5}  ({6:N1} ms)' -f `
                $off, $hit.instance, $instMatch, $cls, $hit.fieldOffset, $hit.scannedSteps, $elapsed
            $color = if ($instMatch -eq 'OK') { 'Cyan' } else { 'Yellow' }
            Write-Host $line -ForegroundColor $color
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
