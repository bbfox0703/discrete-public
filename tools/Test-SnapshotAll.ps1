<#
.SYNOPSIS
    Phase 29a — snapshotBegin / snapshotChunk / snapshotEnd smoke test.

.DESCRIPTION
    Exercises the new "全抓" snapshot pipe RPCs end-to-end. The Phase
    29a-v2 redesign moves all decode work into snapshotBegin (under one
    outer ThreadAttachGuard) so snapshotChunk is pure slicing — see
    docs/plan.md §Phase 29a for the legacy-32-bit Mono freeze that
    forced this redesign.

    Each step prints HH:MM:SS.fff + elapsed-since-start + delta-from-last
    so you can pinpoint where time goes without relying on subjective
    feel. Chunk loop emits a per-chunk row plus running RPC latency
    stats.

    Bundles Phase 27c live smoke for array element capture. Real-array
    captures (typeName has '.' or '/') are shown under "sample array
    fields"; open-generic placeholder arrays (T[]) are now filtered at
    the DLL side and reported as a stat. Array-decode error path
    (implausible_length / header_unreadable) is reported separately as
    "array fields with errors".

.PARAMETER Assemblies
    Optional list of assembly names. Default: 全抓.

.PARAMETER InstanceCap
    Max instances per class. Default 100.

.PARAMETER ArrayCap
    Max elements per T[] field. Default 50.

.PARAMETER ChunkLimit
    Classes per snapshotChunk RPC. Default 32.

.PARAMETER MaxChunks
    Safety cap. Default 5000.

.PARAMETER SampleClasses
    Class entries to pretty-print at the end.

.PARAMETER SampleArrayFields
    Array fields to pretty-print as Phase 27c smoke evidence.

.PARAMETER TimeoutMs
    Pipe connect timeout. Default 60s.

.EXAMPLE
    .\tools\Test-SnapshotAll.ps1
    .\tools\Test-SnapshotAll.ps1 -Assemblies Assembly-CSharp
#>
[CmdletBinding()]
param(
    [string[]] $Assemblies         = @(),
    [int]      $InstanceCap        = 100,
    [int]      $ArrayCap           = 50,
    [int]      $ChunkLimit         = 32,
    [int]      $MaxChunks          = 5000,
    [int]      $SampleClasses      = 5,
    [int]      $SampleArrayFields  = 5,
    [int]      $TimeoutMs          = 60000
)

$ErrorActionPreference = 'Stop'
$script:StartTime = Get-Date
$script:LastStep  = $script:StartTime

function Stamp($label) {
    $now    = Get-Date
    $sinceStart = ($now - $script:StartTime).TotalMilliseconds
    $sinceLast  = ($now - $script:LastStep).TotalMilliseconds
    $script:LastStep = $now
    Write-Host ("[{0:HH:mm:ss.fff}  +{1,9:N1}ms  Δ{2,9:N1}ms]  {3}" -f $now, $sinceStart, $sinceLast, $label) -ForegroundColor DarkGray
}

Stamp 'script start'

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Pick & inject or unity_dump.lua.' }
Stamp 'pipe connected'

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Send-Cmd([string]$Cmd, [hashtable]$Body, [int]$Id) {
    $req = @{ id = $Id; cmd = $Cmd; args = $Body } | ConvertTo-Json -Compress -Depth 12
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    $resp = $line | ConvertFrom-Json
    if ($null -ne $resp.error) {
        return [pscustomobject]@{ error = "$($resp.error.message) (code $($resp.error.code))" }
    }
    return $resp.result
}

function Fmt-Num($n) {
    if ($null -eq $n) { return '?' }
    return ('{0:N0}' -f [long]$n)
}

try {
    Write-Host ''
    Write-Host '==[ handshake ]==' -ForegroundColor Green
    $hs = Send-Cmd 'handshake' @{} 1
    Stamp 'handshake done'
    if ($hs.error) { Write-Host "ERROR: $($hs.error)" -ForegroundColor Red; return }
    Write-Host ("  backend={0} unity={1} assemblies={2} build={3}" -f `
        $hs.backend, $hs.unity_version, $hs.assemblies_count, $hs.build)

    # ===================================================================
    Write-Host ''
    Write-Host '==[ snapshotBegin ]==' -ForegroundColor Green
    $beginBody = @{
        instanceCap = $InstanceCap
        arrayCap    = $ArrayCap
    }
    if ($Assemblies.Count -gt 0) {
        $beginBody['assemblies'] = @($Assemblies)
        Write-Host ("  scope: narrow ({0})" -f ($Assemblies -join ', '))
    } else {
        Write-Host '  scope: 全抓 (all assemblies — default)'
    }
    Write-Host ("  instanceCap={0} arrayCap={1}" -f $InstanceCap, $ArrayCap)
    Stamp 'snapshotBegin: sending request'

    $beginStart = Get-Date
    $begin = Send-Cmd 'snapshotBegin' $beginBody 2
    $beginElapsed = ((Get-Date) - $beginStart).TotalMilliseconds
    Stamp 'snapshotBegin: response received'
    if ($begin.error) { Write-Host "  ERROR: $($begin.error)" -ForegroundColor Red; return }

    Write-Host ("  snapshotId    = {0}" -f $begin.snapshotId)
    Write-Host ("  capturedAt    = {0}" -f $begin.capturedAt)
    Write-Host ("  gameBuildId   = {0}" -f $begin.gameBuildId)
    Write-Host ("  gameSessionId = {0}" -f $begin.gameSessionId)
    Write-Host ("  totalClasses  = {0}" -f (Fmt-Num $begin.totalClasses))
    Write-Host ("  totalInstances= {0}" -f (Fmt-Num $begin.totalInstances))
    Write-Host ("  Begin RPC elapsed = {0:N1} ms ({1:F2} s)" -f $beginElapsed, ($beginElapsed/1000.0)) -ForegroundColor Yellow
    if ($begin.scanStats) {
        Write-Host ("  scanStats: regionsTotal={0} regionsScanned={1} skipImage={2} skipStack={3} bytesScanned={4}" -f `
            $begin.scanStats.regionsTotal, $begin.scanStats.regionsScanned,
            $begin.scanStats.regionsSkipImage, $begin.scanStats.regionsSkipStack,
            (Fmt-Num $begin.scanStats.bytesScanned))
        if ($null -ne $begin.scanStats.vtablesResolved) {
            Write-Host ("            vtablesResolved={0}" -f $begin.scanStats.vtablesResolved)
        }
    }
    if ($begin.decodeStats) {
        Write-Host ("  decodeStats: decodedClasses={0} decodedInstances={1} decodedArrays={2} skippedOpenGenericArrays={3}" -f `
            $begin.decodeStats.decodedClasses, $begin.decodeStats.decodedInstances,
            $begin.decodeStats.decodedArrays, $begin.decodeStats.skippedOpenGenericArrays)
    }

    if ($begin.plan -and $begin.plan.Count -gt 0) {
        Write-Host ''
        Write-Host '  per-assembly plan (top 10 by instance count):'
        $top = $begin.plan |
            Sort-Object -Property instanceCount -Descending |
            Select-Object -First 10
        foreach ($p in $top) {
            Write-Host ("    {0,-32} classCount={1,5} instanceCount={2,6}" -f `
                $p.assembly, $p.classCount, $p.instanceCount)
        }
    }

    if ([int64]$begin.totalClasses -eq 0) {
        Write-Host '  (no classes captured)' -ForegroundColor Yellow
        return
    }

    # ===================================================================
    Write-Host ''
    Write-Host '==[ snapshotChunk loop ]==' -ForegroundColor Green
    Stamp 'chunk loop: start'
    $cursor       = 0
    $chunkCount   = 0
    $classesSeen  = 0
    $instSeen     = 0
    $staticsSeen  = 0
    $fieldsSeen   = 0
    $arrayFields  = 0   # successful captures
    $arrayErrors  = 0   # captured-then-rejected (implausible_length / header_unreadable)
    $errorsSeen   = 0
    $sampleArrays = New-Object System.Collections.Generic.List[object]
    $sampleArrayErrors = New-Object System.Collections.Generic.List[object]
    $sampleClsArr = New-Object System.Collections.Generic.List[object]

    $chunkLatencies = New-Object System.Collections.Generic.List[double]
    $chunkLoopStart = Get-Date

    while ($true) {
        $rpcStart = Get-Date
        $chunk = Send-Cmd 'snapshotChunk' @{
            snapshotId = $begin.snapshotId
            cursor     = $cursor
            limit      = $ChunkLimit
        } (10 + $chunkCount)
        $rpcMs = ((Get-Date) - $rpcStart).TotalMilliseconds
        $chunkLatencies.Add($rpcMs)
        $chunkCount++

        if ($chunk.error) {
            Write-Host ("  ERROR mid-loop: {0}" -f $chunk.error) -ForegroundColor Red
            break
        }

        foreach ($c in $chunk.classes) {
            $classesSeen++
            if ($c.error) { $errorsSeen++; continue }
            if ($c.statics)   { $staticsSeen += @($c.statics).Count }
            if ($c.instances) {
                $instSeen += @($c.instances).Count
                foreach ($i in $c.instances) {
                    if ($i.fields) {
                        $fieldsSeen += @($i.fields).Count
                        foreach ($f in $i.fields) {
                            if ($null -ne $f.arrayElements -and
                                $null -ne $f.arrayElements.length) {
                                $arrayFields++
                                if ($sampleArrays.Count -lt $SampleArrayFields) {
                                    $sampleArrays.Add([pscustomobject]@{
                                        Class    = "$($c.namespace).$($c.name)".TrimStart('.')
                                        Instance = $i.address
                                        Field    = $f.name
                                        Type     = $f.type
                                        Length   = $f.arrayElements.length
                                        Count    = $f.arrayElements.count
                                        Elements = $f.arrayElements.elements
                                    })
                                }
                            } elseif ($null -ne $f.arrayError) {
                                $arrayErrors++
                                if ($sampleArrayErrors.Count -lt 5) {
                                    $sampleArrayErrors.Add([pscustomobject]@{
                                        Class = "$($c.namespace).$($c.name)".TrimStart('.')
                                        Field = $f.name
                                        Type  = $f.type
                                        Error = $f.arrayError
                                        Len   = $f.arrayErrorLength
                                    })
                                }
                            }
                        }
                    }
                }
            }
            if ($sampleClsArr.Count -lt $SampleClasses -and
                (($c.statics -and @($c.statics).Count -gt 0) -or
                 ($c.instances -and @($c.instances).Count -gt 0))) {
                $sampleClsArr.Add($c)
            }
        }

        # Per-chunk progress with timestamps (every chunk for first 3, then every 20).
        if ($chunkCount -le 3 -or ($chunkCount % 20) -eq 0) {
            $avg = ($chunkLatencies | Measure-Object -Average).Average
            $max = ($chunkLatencies | Measure-Object -Maximum).Maximum
            Stamp ("chunk #{0,-3}  cursor={1,-5} -> next={2,-5} rpc={3,5:N0}ms avg={4,5:N0}ms max={5,5:N0}ms  cls={6} inst={7} arr={8} arrErr={9} (done={10})" -f `
                $chunkCount, $chunk.cursor, $chunk.nextCursor, $rpcMs, $avg, $max,
                $classesSeen, $instSeen, $arrayFields, $arrayErrors, $chunk.done)
        }

        if ($chunk.done) { break }
        if ($chunkCount -ge $MaxChunks) {
            Write-Host ("  hit MaxChunks={0} before done — aborting" -f $MaxChunks) -ForegroundColor Yellow
            break
        }
        $cursor = [int]$chunk.nextCursor
    }
    $chunkElapsed = ((Get-Date) - $chunkLoopStart).TotalMilliseconds
    Stamp 'chunk loop: done'

    # ===================================================================
    Write-Host ''
    Write-Host '==[ summary ]==' -ForegroundColor Green
    $avgLat = ($chunkLatencies | Measure-Object -Average).Average
    $maxLat = ($chunkLatencies | Measure-Object -Maximum).Maximum
    $minLat = ($chunkLatencies | Measure-Object -Minimum).Minimum
    Write-Host ("  chunks         = {0}" -f $chunkCount)
    Write-Host ("  chunk loop     = {0:N1} ms ({1:F2} s)" -f $chunkElapsed, ($chunkElapsed / 1000.0)) -ForegroundColor Yellow
    Write-Host ("  per-chunk RPC  = avg {0:N1} ms / min {1:N1} ms / max {2:N1} ms" -f $avgLat, $minLat, $maxLat)
    Write-Host ("  Begin + chunks = {0:F2} s" -f (($beginElapsed + $chunkElapsed) / 1000.0)) -ForegroundColor Yellow
    Write-Host ("  classes seen   = {0}" -f (Fmt-Num $classesSeen))
    Write-Host ("  with errors    = {0}" -f $errorsSeen)
    Write-Host ("  total statics  = {0}" -f (Fmt-Num $staticsSeen))
    Write-Host ("  total instances= {0}" -f (Fmt-Num $instSeen))
    Write-Host ("  total fields   = {0}" -f (Fmt-Num $fieldsSeen))
    Write-Host ("  array fields captured OK (Phase 27c smoke)  = {0}" -f $arrayFields)
    Write-Host ("  array fields with arrayError                 = {0}" -f $arrayErrors)

    if ($sampleClsArr.Count -gt 0) {
        Write-Host ''
        Write-Host '  --- sample classes ---'
        foreach ($c in $sampleClsArr) {
            $fqn = "$($c.namespace).$($c.name)".TrimStart('.')
            Write-Host ("    [{0}] {1}" -f $c.assembly, $fqn) -ForegroundColor Cyan
            if ($c.statics) {
                Write-Host ("      statics ({0}):" -f @($c.statics).Count)
                foreach ($s in @($c.statics) | Select-Object -First 3) {
                    $valStr = if ($null -ne $s.value)    { $s.value.ToString() }
                              elseif ($null -ne $s.ptr)  { "ptr=$($s.ptr)" }
                              elseif ($null -ne $s.string) { "`"$($s.string)`"" }
                              elseif ($null -ne $s.error) { "(err: $($s.error))" }
                              else                       { '(?)' }
                    Write-Host ("        {0,-32} {1,-22} +0x{2,-4:X} = {3}" -f $s.name, $s.type, [int]$s.offset, $valStr)
                }
                if (@($c.statics).Count -gt 3) {
                    Write-Host ("        ... and $((@($c.statics).Count) - 3) more")
                }
            }
            if ($c.instances) {
                $i0 = @($c.instances)[0]
                Write-Host ("      instance[0] @ {0} ({1} fields)" -f $i0.address, @($i0.fields).Count)
                foreach ($f in @($i0.fields) | Select-Object -First 4) {
                    $valStr = if ($null -ne $f.value)    { $f.value.ToString() }
                              elseif ($null -ne $f.ptr)  { "ptr=$($f.ptr)" }
                              elseif ($null -ne $f.string) { "`"$($f.string)`"" }
                              else                       { '(?)' }
                    Write-Host ("        {0,-28} {1,-22} +0x{2,-4:X} = {3}" -f $f.name, $f.type, [int]$f.offset, $valStr)
                }
            }
        }
    }

    if ($sampleArrays.Count -gt 0) {
        Write-Host ''
        Write-Host '  --- sample array fields OK (Phase 27c smoke) ---'
        foreach ($a in $sampleArrays) {
            Write-Host ("    {0}.{1}  type={2}  length={3} captured={4}" -f $a.Class, $a.Field, $a.Type, $a.Length, $a.Count) -ForegroundColor Cyan
            foreach ($e in @($a.Elements) | Select-Object -First 5) {
                $valStr = if ($null -ne $e.value)    { $e.value.ToString() }
                          elseif ($null -ne $e.ptr)  { $e.ptr }
                          elseif ($null -ne $e.string) { "`"$($e.string)`"" }
                          else                       { '(?)' }
                Write-Host ("      [{0,3}] {1}" -f $e.index, $valStr)
            }
            $elemCount = if ($a.Elements) { @($a.Elements).Count } else { 0 }
            if ($elemCount -gt 5) {
                Write-Host ("      ... and $($elemCount - 5) more")
            }
        }
    } elseif ($arrayErrors -eq 0) {
        Write-Host ''
        Write-Host '  (no array fields captured — no T[] hit in scope)' -ForegroundColor Yellow
    }

    if ($sampleArrayErrors.Count -gt 0) {
        Write-Host ''
        Write-Host '  --- sample array decode errors (likely false-positive instances) ---'
        foreach ($e in $sampleArrayErrors) {
            Write-Host ("    {0}.{1}  type={2}  err={3} reportedLength={4}" -f $e.Class, $e.Field, $e.Type, $e.Error, $e.Len) -ForegroundColor DarkYellow
        }
    }

    # ===================================================================
    Write-Host ''
    Write-Host '==[ snapshotEnd ]==' -ForegroundColor Green
    $end = Send-Cmd 'snapshotEnd' @{ snapshotId = $begin.snapshotId } 999999
    Stamp 'snapshotEnd done'
    if ($end.error) {
        Write-Host ("  ERROR: {0}" -f $end.error) -ForegroundColor Red
    } else {
        Write-Host ("  dropped={0}" -f $end.dropped)
    }
    Write-Host ''
    Write-Host ("TOTAL elapsed = {0:F2} s" -f (((Get-Date) - $script:StartTime).TotalSeconds)) -ForegroundColor Yellow
}
finally {
    $client.Dispose()
}
