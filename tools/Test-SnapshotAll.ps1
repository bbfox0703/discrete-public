<#
.SYNOPSIS
    Phase 29a — snapshotBegin / snapshotChunk / snapshotEnd smoke test.

.DESCRIPTION
    Exercises the new "全抓" snapshot pipe RPCs end-to-end:

      1. handshake     — confirm backend is ready.
      2. snapshotBegin — runs ONE wide heap walk on the active backend,
                         groups every live managed object by its class,
                         caches the plan under a session id.
      3. snapshotChunk — repeated calls stream class data (statics +
                         instances + array elements) until done=true.
      4. snapshotEnd   — drops the session.

    Default scope = every assembly the runtime exposes (no class hint
    required — that's the whole point of Phase 29). Pass -Assemblies to
    narrow.

    Bundles Phase 27c live smoke for array element capture. The walker
    drills into T[] fields and captures `length + elements[]`. The
    summary at the end reports how many array fields were exercised
    + a sample of element values per backend.

.PARAMETER Assemblies
    Optional list of assembly names to snapshot (e.g. 'Assembly-CSharp').
    Default: 全抓 (every assembly the runtime reports).

.PARAMETER InstanceCap
    Max instances captured per class. Default 100 — matches DLL default.

.PARAMETER ArrayCap
    Max array elements captured per T[] field. Default 50 — matches DLL.

.PARAMETER ChunkLimit
    Max classes returned per snapshotChunk RPC. Default 32 — keeps any
    one RPC's latency below ~1-2s on big targets.

.PARAMETER MaxChunks
    Safety cap on chunk iterations (defends against runaway loops if
    nextCursor ever stalls). Default 5000.

.PARAMETER SampleClasses
    How many class entries to pretty-print in the summary section.

.PARAMETER SampleArrayFields
    How many array fields to pretty-print as Phase 27c smoke evidence.

.PARAMETER TimeoutMs
    Pipe connect timeout. Default 60s.

.EXAMPLE
    # Snapshot every assembly (default scope)
    .\tools\Test-SnapshotAll.ps1

    # Narrow to one user assembly for quicker iteration
    .\tools\Test-SnapshotAll.ps1 -Assemblies Assembly-CSharp -ChunkLimit 50

    # Tight cap for very large games (Songs of Silence, Heroes)
    .\tools\Test-SnapshotAll.ps1 -InstanceCap 20 -ArrayCap 10
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

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Pick & inject or unity_dump.lua.' }

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

    $beginStart = Get-Date
    $begin = Send-Cmd 'snapshotBegin' $beginBody 2
    $beginElapsed = ((Get-Date) - $beginStart).TotalMilliseconds
    if ($begin.error) { Write-Host "  ERROR: $($begin.error)" -ForegroundColor Red; return }

    Write-Host ("  snapshotId    = {0}" -f $begin.snapshotId)
    Write-Host ("  capturedAt    = {0}" -f $begin.capturedAt)
    Write-Host ("  gameBuildId   = {0}" -f $begin.gameBuildId)
    Write-Host ("  gameSessionId = {0}" -f $begin.gameSessionId)
    Write-Host ("  totalClasses  = {0}" -f (Fmt-Num $begin.totalClasses))
    Write-Host ("  totalInstances= {0}" -f (Fmt-Num $begin.totalInstances))
    Write-Host ("  elapsed       = {0:F1} ms" -f $beginElapsed)
    if ($begin.scanStats) {
        Write-Host ("  scanStats: regionsTotal={0} regionsScanned={1} skipImage={2} skipStack={3} bytesScanned={4}" -f `
            $begin.scanStats.regionsTotal, $begin.scanStats.regionsScanned,
            $begin.scanStats.regionsSkipImage, $begin.scanStats.regionsSkipStack,
            (Fmt-Num $begin.scanStats.bytesScanned))
        if ($null -ne $begin.scanStats.vtablesResolved) {
            Write-Host ("            vtablesResolved={0}" -f $begin.scanStats.vtablesResolved)
        }
    }

    # Per-assembly plan summary
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
        Write-Host '  (no classes captured — backend mismatch or empty scope)' -ForegroundColor Yellow
        return
    }

    # ===================================================================
    Write-Host ''
    Write-Host '==[ snapshotChunk loop ]==' -ForegroundColor Green
    $cursor       = 0
    $chunkCount   = 0
    $classesSeen  = 0
    $instSeen     = 0
    $staticsSeen  = 0
    $fieldsSeen   = 0
    $arrayFields  = 0
    $errorsSeen   = 0
    $sampleArrays = New-Object System.Collections.Generic.List[object]
    $sampleClsArr = New-Object System.Collections.Generic.List[object]

    $chunkStart = Get-Date
    while ($true) {
        $chunk = Send-Cmd 'snapshotChunk' @{
            snapshotId = $begin.snapshotId
            cursor     = $cursor
            limit      = $ChunkLimit
        } (10 + $chunkCount)
        $chunkCount++

        if ($chunk.error) {
            Write-Host ("  ERROR mid-loop: {0}" -f $chunk.error) -ForegroundColor Red
            break
        }

        foreach ($c in $chunk.classes) {
            $classesSeen++
            if ($c.error) {
                $errorsSeen++
                continue
            }
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
                            }
                        }
                    }
                }
            }
            # Capture a few sample class entries for pretty-print at the end
            if ($sampleClsArr.Count -lt $SampleClasses -and
                (($c.statics -and @($c.statics).Count -gt 0) -or
                 ($c.instances -and @($c.instances).Count -gt 0))) {
                $sampleClsArr.Add($c)
            }
        }

        if ($chunkCount -le 3 -or ($chunkCount % 20) -eq 0) {
            Write-Host ("  chunk #{0,-3}  cursor={1,-5} -> nextCursor={2,-5} classes={3,-4} cumulative cls={4} inst={5} arr={6} (done={7})" -f `
                $chunkCount, $chunk.cursor, $chunk.nextCursor,
                @($chunk.classes).Count, $classesSeen, $instSeen, $arrayFields, $chunk.done)
        }

        if ($chunk.done) { break }
        if ($chunkCount -ge $MaxChunks) {
            Write-Host ("  hit MaxChunks={0} before done — aborting" -f $MaxChunks) -ForegroundColor Yellow
            break
        }
        $cursor = [int]$chunk.nextCursor
    }
    $chunkElapsed = ((Get-Date) - $chunkStart).TotalMilliseconds

    # ===================================================================
    Write-Host ''
    Write-Host '==[ summary ]==' -ForegroundColor Green
    Write-Host ("  chunks         = {0}" -f $chunkCount)
    Write-Host ("  chunk elapsed  = {0:F1} ms ({1:F1} s)" -f $chunkElapsed, ($chunkElapsed / 1000.0))
    Write-Host ("  total time     = {0:F1} s (begin + chunks)" -f (($beginElapsed + $chunkElapsed) / 1000.0))
    Write-Host ("  classes seen   = {0}" -f (Fmt-Num $classesSeen))
    Write-Host ("  with errors    = {0}" -f $errorsSeen)
    Write-Host ("  total statics  = {0}" -f (Fmt-Num $staticsSeen))
    Write-Host ("  total instances= {0}" -f (Fmt-Num $instSeen))
    Write-Host ("  total fields   = {0}" -f (Fmt-Num $fieldsSeen))
    Write-Host ("  array fields captured (Phase 27c smoke) = {0}" -f $arrayFields)

    # Sample classes
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

    # Phase 27c smoke evidence
    if ($sampleArrays.Count -gt 0) {
        Write-Host ''
        Write-Host '  --- sample array fields (Phase 27c smoke) ---'
        foreach ($a in $sampleArrays) {
            Write-Host ("    {0}.{1}  type={2}  length={3} count={4}" -f $a.Class, $a.Field, $a.Type, $a.Length, $a.Count) -ForegroundColor Cyan
            foreach ($e in @($a.Elements) | Select-Object -First 5) {
                $valStr = if ($null -ne $e.value)    { $e.value.ToString() }
                          elseif ($null -ne $e.ptr)  { $e.ptr }
                          elseif ($null -ne $e.string) { "`"$($e.string)`"" }
                          else                       { '(?)' }
                Write-Host ("      [{0,3}] {1}" -f $e.index, $valStr)
            }
            if (@($a.Elements).Count -gt 5) {
                Write-Host ("      ... and $((@($a.Elements).Count) - 5) more")
            }
        }
    } else {
        Write-Host ''
        Write-Host '  (no array fields captured — either no T[] fields hit in scope, or array decode path needs investigation)' -ForegroundColor Yellow
    }

    # ===================================================================
    Write-Host ''
    Write-Host '==[ snapshotEnd ]==' -ForegroundColor Green
    $end = Send-Cmd 'snapshotEnd' @{ snapshotId = $begin.snapshotId } 999999
    if ($end.error) {
        Write-Host ("  ERROR: {0}" -f $end.error) -ForegroundColor Red
    } else {
        Write-Host ("  dropped={0}" -f $end.dropped)
    }
}
finally {
    $client.Dispose()
}
