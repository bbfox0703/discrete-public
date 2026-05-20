<#
.SYNOPSIS
    Phase 20-MT stress smoke — sustained callMethod burst with both
    main_thread=false (legacy worker-thread path) and main_thread=true
    (new dispatcher path). Side-by-side latency + game-freeze comparison.

.DESCRIPTION
    Designed to surface the Phase 20a freeze (~2048 sustained Unity icalls
    locked up SaGa F2R). The user runs this against a live Unity game with
    DiscreteUI / Test-Handshake already injected; the script:

      1. Sends `getDispatcherStats` to verify the hook is installed +
         healthy (last_tick_age_ms within freshness window).
      2. Picks a cheap Unity icall — Mathf.Abs(int) — that's safe from
         either thread but goes through runtime_invoke. (Heavy scene-
         graph methods like Transform.get_parent need to come from a
         findInstances result; we keep the smoke standalone.)
      3. Runs N iterations on the worker-thread path. Times each.
      4. Runs N iterations on the main-thread path. Times each.
      5. Polls getDispatcherStats again — tick_count delta confirms
         the hook fired; drain_count delta confirms our queue was
         drained on the main thread.

    USER OBSERVATION: keep the game window visible. The legacy path
    should not freeze on Mathf.Abs (it's pure managed math). For real
    Unity-engine smoke, swap in a Transform.get_parent call after
    findInstances.

    Wire-shape note: ConvertTo-Json silently unwraps single-element
    arrays stored as object properties (PowerShell quirk recorded in
    MEMORY.md). We splice the args JSON in as raw text so the DLL
    handler sees the array as an array.

.PARAMETER Iterations
    How many calls per backend path. Default 500.

.EXAMPLE
    .\tools\Test-MainThreadBurst.ps1 -Iterations 1000
#>
[CmdletBinding()]
param(
    [int] $Iterations = 500,
    [int] $TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'

function Open-Pipe {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
    $c.Connect(5000)
    return @{
        client = $c
        writer = (New-Object System.IO.StreamWriter($c) -Property @{ AutoFlush = $true })
        reader = (New-Object System.IO.StreamReader($c))
    }
}

# Splices an args-JSON-array tail onto a scalar-only hashtable. ConvertTo-Json
# unwraps single-element arrays held in object properties (a known PowerShell
# bug — see MEMORY.md feedback_powershell_json_array). We let JSON encode
# everything except the args array, then string-splice the array in.
function Build-CallMethodReq($scalarFields, $argsArrayJson, $mainThread, $mainThreadTimeoutMs) {
    $body = ($scalarFields | ConvertTo-Json -Compress)
    # Strip trailing '}' so we can splice more fields in.
    $body = $body.Substring(0, $body.Length - 1)
    $body += ',"args":' + $argsArrayJson
    if ($mainThread) {
        $body += ',"main_thread":true'
        if ($mainThreadTimeoutMs) { $body += ',"main_thread_timeout_ms":' + $mainThreadTimeoutMs }
    }
    $body += '}'
    return '{"id":1,"cmd":"callMethod","args":' + $body + '}'
}

function Send-Raw($pipe, $rawJson) {
    $pipe.writer.WriteLine($rawJson)
    $line = $pipe.reader.ReadLine()
    if (-not $line) { throw "Empty response" }
    return ($line | ConvertFrom-Json)
}

function Send-Cmd($pipe, $cmd, $args) {
    $req = @{ id = 1; cmd = $cmd; args = $args } | ConvertTo-Json -Compress -Depth 8
    $pipe.writer.WriteLine($req)
    $line = $pipe.reader.ReadLine()
    if (-not $line) { throw "Empty response from $cmd" }
    return ($line | ConvertFrom-Json).result
}

Write-Host ''
Write-Host "==[ Phase 20-MT stress smoke (Iterations=$Iterations) ]==" -ForegroundColor Green

$pipe = Open-Pipe
try {
    # 1. Confirm dispatcher health.
    $stats0 = Send-Cmd $pipe 'getDispatcherStats' @{}
    Write-Host ''
    Write-Host 'Pre-burst dispatcher state:' -ForegroundColor Cyan
    $stats0 | Format-Table initialized, hook_installed, main_thread_id, tick_count, drain_count, last_tick_age_ms, is_healthy -AutoSize | Out-Host

    if (-not $stats0.hook_installed) {
        Write-Host '  ! Hook not installed yet — handshake to arm it' -ForegroundColor Yellow
        Send-Cmd $pipe 'handshake' @{} | Out-Null
        Start-Sleep -Milliseconds 100
        $stats0 = Send-Cmd $pipe 'getDispatcherStats' @{}
        Write-Host 'After handshake:' -ForegroundColor Cyan
        $stats0 | Format-Table initialized, hook_installed, main_thread_id, tick_count, drain_count, last_tick_age_ms, is_healthy -AutoSize | Out-Host
    }
    if (-not $stats0.is_healthy) {
        Write-Host '  ! Dispatcher NOT healthy (no recent main-thread tick). Is the game past the loading screen?' -ForegroundColor Red
        Write-Host '    main_thread=true calls will queue and time out. Aborting.' -ForegroundColor Red
        return
    }

    # 2. Build the call's scalar fields + raw args-array JSON ONCE.
    $scalarFields = @{
        assembly       = 'UnityEngine.CoreModule'
        namespace      = 'UnityEngine'
        name           = 'Mathf'
        method         = 'Abs'
        signatureMatch = '(System.Int32)'
    }
    $argsArrayJson = '[{"type":"int32","value":-42}]'

    # 3. Worker-thread burst.
    Write-Host ''
    Write-Host "Worker-thread burst ($Iterations × Mathf.Abs(-42)) — legacy path…" -ForegroundColor Cyan
    $reqWorker = Build-CallMethodReq $scalarFields $argsArrayJson $false $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $errors = 0
    $firstReturnValue = $null
    for ($i = 1; $i -le $Iterations; ++$i) {
        $env = Send-Raw $pipe $reqWorker
        $r = $env.result
        if ($null -eq $r) {
            $errors++
            if ($errors -le 3) { Write-Host ("  envelope-error #{0}: {1}" -f $i, ($env.error | ConvertTo-Json -Compress)) -ForegroundColor Red }
        }
        elseif (-not $r.ok) {
            $errors++
            $msg = if ($r.error) { $r.error } elseif ($r.exception) { 'EXC: ' + $r.exception } else { '(no error string)' }
            if ($errors -le 3) { Write-Host "  err #$i $msg" -ForegroundColor Red }
        }
        elseif ($i -eq 1) { $firstReturnValue = $r.returnValue }
    }
    $sw.Stop()
    $perCallWorker = $sw.Elapsed.TotalMilliseconds / $Iterations
    Write-Host ("  total {0,7:0} ms  | per-call avg {1,6:0.00} ms | errors {2} | first returnValue={3}" -f $sw.Elapsed.TotalMilliseconds, $perCallWorker, $errors, $firstReturnValue)

    # 4. Main-thread burst.
    Write-Host ''
    Write-Host "Main-thread burst ($Iterations × Mathf.Abs(-42)) — dispatcher path…" -ForegroundColor Cyan
    $reqMT = Build-CallMethodReq $scalarFields $argsArrayJson $true $TimeoutMs
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $errors = 0
    $timeouts = 0
    $firstReturnValue = $null
    $firstDispatchPath = $null
    for ($i = 1; $i -le $Iterations; ++$i) {
        $env = Send-Raw $pipe $reqMT
        $r = $env.result
        if ($null -eq $r) {
            $errors++
            if ($errors -le 3) { Write-Host ("  envelope-error #{0}: {1}" -f $i, ($env.error | ConvertTo-Json -Compress)) -ForegroundColor Red }
        }
        elseif (-not $r.ok) {
            $errors++
            if ($r.dispatch -and $r.dispatch.timed_out) { $timeouts++ }
            $msg = if ($r.error) { $r.error } elseif ($r.exception) { 'EXC: ' + $r.exception } else { '(no error string)' }
            if ($errors -le 3) { Write-Host "  err #$i $msg" -ForegroundColor Red }
        }
        elseif ($i -eq 1) {
            $firstReturnValue  = $r.returnValue
            $firstDispatchPath = if ($r.dispatch) { $r.dispatch.path } else { '(no dispatch info)' }
        }
    }
    $sw.Stop()
    $perCallMain = $sw.Elapsed.TotalMilliseconds / $Iterations
    Write-Host ("  total {0,7:0} ms  | per-call avg {1,6:0.00} ms | errors {2} (timeouts {3}) | first returnValue={4} dispatch.path={5}" -f $sw.Elapsed.TotalMilliseconds, $perCallMain, $errors, $timeouts, $firstReturnValue, $firstDispatchPath)

    # 5. Post-burst dispatcher state.
    $stats1 = Send-Cmd $pipe 'getDispatcherStats' @{}
    Write-Host ''
    Write-Host 'Post-burst dispatcher state:' -ForegroundColor Cyan
    $stats1 | Format-Table initialized, hook_installed, tick_count, submit_count, drain_count, timeout_count, last_tick_age_ms, is_healthy -AutoSize | Out-Host

    Write-Host ''
    Write-Host 'Delta:' -ForegroundColor Cyan
    Write-Host ("  ticks observed: {0}  (Unity Update fired this many times during the burst)" -f ($stats1.tick_count   - $stats0.tick_count))
    Write-Host ("  submits:        {0}  (callMethod main_thread=true)" -f ($stats1.submit_count - $stats0.submit_count))
    Write-Host ("  drained:        {0}  (work items the main thread executed)" -f ($stats1.drain_count  - $stats0.drain_count))
    Write-Host ("  timeouts:       {0}" -f ($stats1.timeout_count - $stats0.timeout_count))

    Write-Host ''
    Write-Host '==[ Acceptance ]==' -ForegroundColor Green
    $ok = $true
    if (($stats1.tick_count - $stats0.tick_count) -lt 1) {
        Write-Host '  FAIL: no main-thread ticks observed — hook not firing' -ForegroundColor Red
        $ok = $false
    } else {
        Write-Host '  OK: main-thread ticks observed' -ForegroundColor Green
    }
    if (($stats1.drain_count - $stats0.drain_count) -lt $Iterations) {
        Write-Host ("  FAIL: drained {0} but expected at least {1}" -f ($stats1.drain_count - $stats0.drain_count), $Iterations) -ForegroundColor Red
        $ok = $false
    } else {
        Write-Host '  OK: all main_thread=true work items drained' -ForegroundColor Green
    }
    if ($timeouts -gt 0) {
        Write-Host "  WARN: $timeouts timeout(s) — game may have stuttered" -ForegroundColor Yellow
    }
    if ($ok) { Write-Host '  PASS' -ForegroundColor Green } else { Write-Host '  FAIL' -ForegroundColor Red }
}
finally {
    $pipe.client.Close()
}
