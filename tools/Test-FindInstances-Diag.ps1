<#
.SYNOPSIS
    Offline diagnostic dump for findInstances "fake instances" — by FIELD CONTENT.

.DESCRIPTION
    findInstances on the Mono backend is a raw qword scan: it returns every
    8-byte-aligned slot in committed RW memory whose value equals the target
    MonoVTable*. For Core Keeper's PlayerController this returns ~82 hits that
    are ALL structurally valid object headers — header validation (and the
    reverse `whatIsAt` walker) cannot tell them apart, because both use the very
    same "first qword == vtable" signal. So the "fakes" the user sees are not
    invalid objects; they are inactive / pooled / template / dead-but-not-yet-
    collected PlayerController objects. The ONE that matters (the live, locally
    controlled player) differs only by its FIELD CONTENT.

    Likewise, IL2CPP-style metadata-arena address exclusion does NOT work on
    Mono: the class-metadata range overlaps the whole managed heap (measured:
    klass range spans every match address). Address range cannot filter them.

    Therefore this script dumps each instance's decoded field values via
    `walkInstance` and characterises them so the live player can be told apart
    offline:

      1. getClassRangeStats  -> region context (logged, NOT used to filter)
      2. findInstances       -> all matches
      3. per match: walkInstance -> every managed field + live value

    For each instance it computes a "populated" profile:
      nFields      total decoded fields
      nNonNull     fields with a non-zero value / non-empty string / non-null ptr
      nStrings     non-empty decoded strings (often the giveaway — player name)
      sampleStr    up to a few of those strings

    Template / pooled / dead objects tend to be mostly zero (low nNonNull);
    the live player is densely populated. The full per-field dump is written to
    JSON for offline comparison (diff the field values across instances to find
    the discriminating field, then build a content-based filter / "find live
    player" RPC).

.PARAMETER ClassAssembly / Namespace / Name
    Class spec. Defaults target Core Keeper's PlayerController. If resolution
    fails, swap assembly/namespace (e.g. -ClassAssembly Assembly-CSharp
    -Namespace Pug.Other).

.PARAMETER Limit
    Max matches to scan. Default 256 (DLL caps at 4096).

.PARAMETER OutFile
    Output JSON path. Default: tools\diag\find-instances-fields-<class>-<stamp>.json

.EXAMPLE
    .\tools\Test-FindInstances-Diag.ps1
    .\tools\Test-FindInstances-Diag.ps1 -ClassAssembly Pug.Other -Namespace '' -Name PlayerController -Limit 256
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly = 'Pug.Other',
    [string] $Namespace     = '',
    [string] $Name          = 'PlayerController',
    [int]    $Limit         = 256,
    [int]    $TimeoutMs     = 8000,
    [string] $OutFile       = ''
)

$ErrorActionPreference = 'Stop'

# --- pipe plumbing (matches Test-WhatIsAt.ps1 convention) -------------------
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first (Pick & inject / unity_dump.lua / Test-Handshake.ps1).' }

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

function To-U64($v) {
    if ($null -eq $v) { return $null }
    $s = "$v"
    if ($s -eq '') { return $null }
    if ($s -like '0x*') { return [Convert]::ToUInt64($s.Substring(2), 16) }
    return [Convert]::ToUInt64($s)
}

# Guard against ConvertTo-Json unwrapping a single-element array (known repo trap).
# NB: pipe-enumerate rather than @($items) — @() on a List[object] throws
# "Argument types do not match" under PowerShell 7.
function ConvertTo-JsonArray($items, [int]$Depth = 10) {
    $arr = @($items | ForEach-Object { $_ })
    if ($arr.Count -eq 0) { return '[]' }
    if ($arr.Count -eq 1) { return '[' + ($arr[0] | ConvertTo-Json -Depth $Depth) + ']' }
    return ($arr | ConvertTo-Json -Depth $Depth)
}

# Is a decoded walkInstance field "populated" (non-default)?
function Field-IsPopulated($f) {
    if ($null -ne $f.string -and "$($f.string)" -ne '') { return $true }
    if ($null -ne $f.value) {
        if ($f.value -is [bool]) { return [bool]$f.value }
        return ([double]$f.value -ne 0)
    }
    if ($null -ne $f.ptr) {
        $p = To-U64 $f.ptr
        return ($null -ne $p -and $p -ne 0)
    }
    return $false
}

try {
    $fqn = if ($Namespace) { "$Namespace.$Name" } else { $Name }

    # --- 1. region context (logged only) ----------------------------------
    Write-Host ''
    Write-Host '==[ getClassRangeStats — region context (not used to filter) ]==' -ForegroundColor Green
    $stats = Send-Cmd 'getClassRangeStats' @{ rebuild = $false } 1
    if ($stats.error) { Write-Host "ERROR: $($stats.error)" -ForegroundColor Red; return }
    Write-Host ("backend     : {0}" -f $stats.backend)
    Write-Host ("class_count : {0}" -f $stats.class_count)
    Write-Host ("klass range : {0} .. {1}" -f $stats.min_klass, $stats.max_klass)
    if ($stats.min_vtable) { Write-Host ("vtable range: {0} .. {1}" -f $stats.min_vtable, $stats.max_vtable) }

    # --- 2. findInstances --------------------------------------------------
    Write-Host ''
    Write-Host "==[ findInstances $fqn (assembly=$ClassAssembly, limit=$Limit) ]==" -ForegroundColor Green
    $fi = Send-Cmd 'findInstances' @{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name; limit = $Limit } 2
    if ($fi.error) { Write-Host "ERROR: $($fi.error)" -ForegroundColor Red; return }
    if ($fi.count -lt 1) { Write-Host 'No matches. Check the class spec (try assembly/namespace swap).' -ForegroundColor Yellow; return }
    Write-Host ("class_ptr   : {0}" -f $fi.class_ptr)
    Write-Host ("vtable      : {0}" -f $fi.vtable)
    Write-Host ("count       : {0}  (limit {1})" -f $fi.count, $fi.limit)

    # --- 3. walkInstance per match -----------------------------------------
    Write-Host ''
    Write-Host ("==[ dumping fields for {0} instances (walkInstance) ]==" -f $fi.count) -ForegroundColor Green

    $records = New-Object System.Collections.Generic.List[object]
    $prevU64 = $null
    $id = 100

    for ($i = 0; $i -lt $fi.matches.Count; ++$i) {
        $cand    = $fi.matches[$i]
        $candU64 = To-U64 $cand
        $gap = if ($null -ne $prevU64) { [int64]($candU64 - $prevU64) } else { 0 }
        $prevU64 = $candU64
        $pageAligned = (($candU64 -band 0xFFF) -eq 0)

        $w = Send-Cmd 'walkInstance' @{ address = $cand; assembly = $ClassAssembly; namespace = $Namespace; name = $Name } $id
        $id++

        $nFields = 0; $nNonNull = 0; $strings = New-Object System.Collections.Generic.List[string]
        $walkError = $null
        if ($w.error) {
            $walkError = $w.error
        } elseif ($w.fields) {
            foreach ($f in $w.fields) {
                $nFields++
                if (Field-IsPopulated $f) { $nNonNull++ }
                if ($null -ne $f.string -and "$($f.string)" -ne '') { [void]$strings.Add("$($f.name)=$($f.string)") }
            }
        }
        $sampleStr = @($strings | Select-Object -First 6)

        $rec = [ordered]@{
            index       = $i
            address     = $cand
            pageAligned = $pageAligned
            gapFromPrev = $gap
            nFields     = $nFields
            nNonNull    = $nNonNull
            nStrings    = $strings.Count
            sampleStr   = $sampleStr
            walkError   = $walkError
            depth       = $w.depth
            fields      = $w.fields
        }
        $records.Add([pscustomobject]$rec)

        if (($i % 16) -eq 0) { Write-Host -NoNewline '.' }
    }
    Write-Host ''

    # --- summary: populated profile ---------------------------------------
    Write-Host ''
    Write-Host '==[ per-instance populated profile (sorted by nNonNull desc) ]==' -ForegroundColor Green
    Write-Host ('  {0,-4} {1,-20} {2,-5} {3,-7} {4,-8} {5}' -f 'idx','address','algn','nFields','nNonNull','strings') -ForegroundColor DarkGray
    $sorted = $records | Sort-Object @{ Expression = 'nNonNull'; Descending = $true }, index
    foreach ($r in $sorted) {
        $algn = if ($r.pageAligned) { 'page' } else { '.' }
        $color = if ($r.walkError) { 'Red' } elseif ($r.nNonNull -ge 1) { 'Cyan' } else { 'DarkGray' }
        $strJoin = if ($r.sampleStr.Count) { ($r.sampleStr -join ' | ') } elseif ($r.walkError) { "ERR:$($r.walkError)" } else { '' }
        Write-Host ('  #{0,-3} {1} {2,-5} {3,-7} {4,-8} {5}' -f `
            $r.index, $r.address, $algn, $r.nFields, $r.nNonNull, $strJoin) -ForegroundColor $color
    }

    # quick histogram of nNonNull to spot the outlier(s)
    Write-Host ''
    Write-Host '==[ nNonNull histogram (how many instances have N populated fields) ]==' -ForegroundColor Green
    $records | Group-Object nNonNull | Sort-Object { [int]$_.Name } | ForEach-Object {
        Write-Host ('  nNonNull={0,-4} : {1} instance(s)' -f $_.Name, $_.Count) -ForegroundColor DarkGray
    }

    # --- write JSON --------------------------------------------------------
    if (-not $OutFile) {
        $stampUtc = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
        $safeName = ($fqn -replace '[^A-Za-z0-9_.-]', '_')
        $diagDir  = Join-Path $PSScriptRoot 'diag'
        if (-not (Test-Path $diagDir)) { New-Item -ItemType Directory -Path $diagDir | Out-Null }
        $OutFile = Join-Path $diagDir ("find-instances-fields-{0}-{1}.json" -f $safeName, $stampUtc)
    }

    $meta = [ordered]@{
        class       = [ordered]@{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name; fqn = $fqn }
        backend     = "$($stats.backend)"
        class_ptr   = "$($fi.class_ptr)"
        vtable      = "$($fi.vtable)"
        klass_range = [ordered]@{ min = "$($stats.min_klass)";  max = "$($stats.max_klass)" }
        match_count = [int]$fi.count
        limit       = [int]$fi.limit
    }

    $metaJson    = $meta | ConvertTo-Json -Depth 5
    $matchesJson = ConvertTo-JsonArray $records 10
    $full = "{`n  ""meta"": $metaJson,`n  ""matches"": $matchesJson`n}"
    Set-Content -Path $OutFile -Value $full -Encoding UTF8

    Write-Host ''
    Write-Host ("==[ wrote {0} instances -> {1} ]==" -f $records.Count, $OutFile) -ForegroundColor Green
    Write-Host 'Hand this JSON back: I will diff field values across instances to find the live player + the discriminating field.' -ForegroundColor DarkGray
}
finally {
    $client.Close()
}
