<#
.SYNOPSIS
    Drive the AOBMaker CE plugin pipe directly to verify Phase 22's
    bridge-inject pipeline. Doesn't require a Unity game — just CE 7.6+
    with the AOBMaker plugin loaded and any cheat table open.

.DESCRIPTION
    Sends one of three test payloads to \\.\pipe\AOBMakerCEBridge:

      Probe (default) — InjectTableFile + CreateAAScript with stub
                        content; no real bridge files. Useful as a
                        "is the pipe wired correctly?" smoke.
      -Full           — Replays the production InjectDiscreteBridge
                        pipeline: 7 inject calls reading scripts/*.lua
                        from the repo + 1 CreateAAScript with the
                        bootstrap. After this completes, CE's
                        Table->Show table files lists all 7 lua files
                        and the address list has the Load Discrete
                        Bridge entry — enable it to load the bridge.
      -PingOnly       — Just verifies the pipe responds (no mutation
                        of the open CT). The lightest probe.

    Per-step result is printed with timing + the AOBMaker plugin's
    error message on failure.

.PARAMETER Full
    Replay the production InjectDiscreteBridge pipeline (7 file
    injects + 1 bootstrap AA). Reads scripts/*.lua from the repo so
    the lua content is identical to what dist/DiscreteUI.exe ships.

.PARAMETER PingOnly
    Probe pipe availability and exit. Doesn't mutate the open CT.

.PARAMETER PipeName
    Override the AOBMaker pipe name. Default: AOBMakerCEBridge.

.PARAMETER TimeoutMs
    Per-call response timeout. Default 5000 (15000 for InjectTableFile
    in -Full mode, matching AobMakerBridge's InjectResponseTimeoutMs).

.EXAMPLE
    # Smallest smoke — does the pipe respond at all?
    .\tools\Test-BridgeInject.ps1 -PingOnly

.EXAMPLE
    # Default: minimal inject (1 stub file + 1 stub AA).
    .\tools\Test-BridgeInject.ps1

.EXAMPLE
    # Full production pipeline replay.
    .\tools\Test-BridgeInject.ps1 -Full
#>
[CmdletBinding()]
param(
    [switch] $Full,
    [switch] $PingOnly,
    [string] $PipeName  = 'AOBMakerCEBridge',
    [int]    $TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Production constants — match AobMakerBridge.cs.
$InjectTimeoutMs = 15000
$ConnectTimeoutMs = 1500

# --- Wire-format helpers -----------------------------------------------------

function Connect-AobMaker {
    param([string]$PipeName)
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect($ConnectTimeoutMs)
        return $pipe
    }
    catch {
        $pipe.Dispose()
        return $null
    }
}

function Send-PipeMessage {
    param(
        [System.IO.Pipes.NamedPipeClientStream]$Pipe,
        [string]$JsonPayload,
        [int]$TimeoutMs
    )
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($JsonPayload)
    $lenBuf = [System.BitConverter]::GetBytes([uint32]$bytes.Length)
    $Pipe.Write($lenBuf,  0, 4)
    $Pipe.Write($bytes,   0, $bytes.Length)
    $Pipe.Flush()

    # Read response (4-byte length + JSON payload). NamedPipeClientStream
    # supports ReadTimeout via the underlying handle — async would be
    # cleaner but adds a runspace dance we don't need for a smoke script.
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    $respLen = New-Object byte[] 4
    $read = 0
    while ($read -lt 4) {
        if ((Get-Date) -gt $deadline) { throw "timed out waiting for response length" }
        $n = $Pipe.Read($respLen, $read, 4 - $read)
        if ($n -le 0) { throw "pipe closed mid-length-read ($read/4 bytes)" }
        $read += $n
    }
    $payloadLen = [System.BitConverter]::ToUInt32($respLen, 0)
    if ($payloadLen -eq 0 -or $payloadLen -gt 10MB) { throw "bad response length: $payloadLen" }

    $payload = New-Object byte[] $payloadLen
    $read = 0
    while ($read -lt $payloadLen) {
        if ((Get-Date) -gt $deadline) { throw "timed out waiting for response payload" }
        $n = $Pipe.Read($payload, $read, $payloadLen - $read)
        if ($n -le 0) { throw "pipe closed mid-payload-read ($read/$payloadLen bytes)" }
        $read += $n
    }
    $json = [System.Text.Encoding]::UTF8.GetString($payload)
    return $json | ConvertFrom-Json
}

function Invoke-AobMakerCommand {
    param(
        [string]$PipeName,
        [string]$JsonPayload,
        [int]$TimeoutMs,
        [string]$Label
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $pipe = Connect-AobMaker -PipeName $PipeName
    if ($null -eq $pipe) {
        Write-Host ("  [{0,5}ms] {1}: pipe unreachable (CE not running, or AOBMaker plugin not loaded)" `
            -f $sw.ElapsedMilliseconds, $Label) -ForegroundColor Red
        return $false
    }
    try {
        $resp = Send-PipeMessage -Pipe $pipe -JsonPayload $JsonPayload -TimeoutMs $TimeoutMs
        $sw.Stop()
        if ($resp.success) {
            Write-Host ("  [{0,5}ms] {1}: ok" -f $sw.ElapsedMilliseconds, $Label) -ForegroundColor Green
            return $true
        }
        else {
            Write-Host ("  [{0,5}ms] {1}: failure — {2}" -f $sw.ElapsedMilliseconds, $Label, $resp.message) -ForegroundColor Red
            return $false
        }
    }
    catch {
        $sw.Stop()
        Write-Host ("  [{0,5}ms] {1}: exception — {2}" -f $sw.ElapsedMilliseconds, $Label, $_.Exception.Message) -ForegroundColor Red
        return $false
    }
    finally {
        $pipe.Dispose()
    }
}

# --- Payload builders --------------------------------------------------------

function New-InjectTableFilePayload {
    param([string]$FileName, [string]$Content)
    [pscustomobject]@{
        type     = 'InjectTableFile'
        fileName = $FileName
        content  = $Content
    } | ConvertTo-Json -Compress
}

function New-CreateAAScriptPayload {
    param([string]$Description, [string]$Script, [bool]$AutoActivate = $false)
    [pscustomobject]@{
        type         = 'CreateAAScript'
        description  = $Description
        script       = $Script
        autoActivate = $AutoActivate
    } | ConvertTo-Json -Compress
}

# --- Main --------------------------------------------------------------------

Write-Host ''
Write-Host "== AOBMaker bridge smoke ==  pipe: \\.\pipe\$PipeName" -ForegroundColor Cyan
Write-Host ''

# Step 0: probe.
Write-Host "Step 0/N: pipe availability probe…" -ForegroundColor Gray
$pipe = Connect-AobMaker -PipeName $PipeName
if ($null -eq $pipe) {
    Write-Host "  pipe unreachable. Open Cheat Engine 7.6+ with the AOBMaker plugin loaded, then re-run." -ForegroundColor Red
    exit 1
}
$pipe.Dispose()
Write-Host "  pipe reachable." -ForegroundColor Green
Write-Host ''

if ($PingOnly) {
    Write-Host "Done — ping-only mode." -ForegroundColor Cyan
    exit 0
}

# Step list — different in -Full vs default mode.
if ($Full) {
    $scriptsDir = Join-Path $repoRoot 'scripts'
    if (-not (Test-Path $scriptsDir)) { throw "scripts/ directory not found at $scriptsDir" }

    # Match BridgeLuaResources.LoadOrder verbatim.
    $loadOrder = @(
        'utils.lua',
        'unity_dump.lua',
        'unity_dissect.lua',
        'unity_symbols.lua',
        'unity_addrfind.lua',
        'unity_invoke.lua',
        'unity_scene.lua'
    )

    Write-Host "-Full mode: replaying production InjectDiscreteBridge pipeline" -ForegroundColor Cyan
    Write-Host ('  ({0} file injects + 1 bootstrap AA)' -f $loadOrder.Count) -ForegroundColor Gray
    Write-Host ''

    $okCount = 0
    $stepIdx = 0
    foreach ($name in $loadOrder) {
        $stepIdx++
        $path = Join-Path $scriptsDir $name
        if (-not (Test-Path $path)) {
            Write-Host ("  step $stepIdx/8: $name — file not found at $path") -ForegroundColor Red
            continue
        }
        $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        $payload = New-InjectTableFilePayload -FileName $name -Content $content
        $label   = "InjectTableFile $name ($($content.Length) chars)"
        Write-Host ("Step $stepIdx/8: $label…") -ForegroundColor Gray
        if (Invoke-AobMakerCommand -PipeName $PipeName -JsonPayload $payload -TimeoutMs $InjectTimeoutMs -Label $label) {
            $okCount++
        }
    }

    # Bootstrap AA — minimal stub since BootstrapAaScriptGenerator's full
    # output is in C# (we'd need to call into DiscreteUI.dll to get the
    # actual rendered template; for the smoke the user enables Tools→Inject
    # Discrete Bridge from DiscreteUI to get the production AA. Here we
    # ship a hand-rolled functional equivalent so re-saving the table
    # leaves a usable Load Discrete Bridge entry).
    $bootstrap = @'
[ENABLE]
{$lua}
if syntaxcheck then return end
if _G.discrete_bridgeLoaded then
  print('[Discrete] Load Discrete Bridge: already loaded')
  return
end
local function loadEmbedded(name)
  local f = findTableFile(name)
  if not f then error('[Discrete] missing embedded file: ' .. name) end
  local ss = createStringStream()
  ss.copyFrom(f.Stream, f.Stream.Size)
  local code = ss.DataString
  ss.destroy()
  local fn, err = load(code, '@' .. name)
  if not fn then error('[Discrete] parse error in ' .. name .. ': ' .. tostring(err)) end
  return fn
end
package.preload['utils'] = loadEmbedded('utils.lua')
local files = { 'unity_dump.lua','unity_dissect.lua','unity_symbols.lua',
                'unity_addrfind.lua','unity_invoke.lua','unity_scene.lua' }
for _, n in ipairs(files) do loadEmbedded(n)() end
local U = require('utils')
local ok, build = pcall(function() return U.callDLL('UnityDumper_GetBuildNumber','uint32') end)
if ok then print(string.format('[Discrete] Load Discrete Bridge: ready — DLL build %d', build)) end
_G.discrete_bridgeLoaded = true

[DISABLE]
{$lua}
_G.discrete_bridgeLoaded = nil
'@
    $stepIdx++
    $label = "CreateAAScript Load Discrete Bridge"
    Write-Host ("Step $stepIdx/8: $label…") -ForegroundColor Gray
    $payload = New-CreateAAScriptPayload -Description 'Load Discrete Bridge' -Script $bootstrap -AutoActivate $false
    if (Invoke-AobMakerCommand -PipeName $PipeName -JsonPayload $payload -TimeoutMs $TimeoutMs -Label $label) {
        $okCount++
    }

    Write-Host ''
    if ($okCount -eq 8) {
        Write-Host "Done — 8/8 steps OK." -ForegroundColor Green
        Write-Host "Switch to CE: Table -> Show table files should list 7 unity_*.lua files;" -ForegroundColor Gray
        Write-Host "  the address list should have a 'Load Discrete Bridge' entry. Enable it to" -ForegroundColor Gray
        Write-Host "  load the bridge into the CE Lua state." -ForegroundColor Gray
    }
    else {
        Write-Host ("Done — {0}/8 steps OK ({1} failure(s))." -f $okCount, (8 - $okCount)) -ForegroundColor Yellow
    }
    exit ([int](-not ($okCount -eq 8)))
}

# Default (minimal) mode: 1 stub file + 1 stub AA.
Write-Host "Default mode: minimal smoke (1 stub file + 1 stub AA entry)" -ForegroundColor Cyan
Write-Host ''

$smokePayload1 = New-InjectTableFilePayload `
    -FileName '_discrete_smoke.lua' `
    -Content "-- Phase 22 wire smoke`nprint('[Discrete smoke] _discrete_smoke.lua loaded ' .. os.date())`n"
$ok1 = Invoke-AobMakerCommand -PipeName $PipeName -JsonPayload $smokePayload1 -TimeoutMs $InjectTimeoutMs `
    -Label "InjectTableFile _discrete_smoke.lua"

$smokeAa = @'
[ENABLE]
{$lua}
if syntaxcheck then return end
local f = findTableFile('_discrete_smoke.lua')
if f then
  local ss = createStringStream()
  ss.copyFrom(f.Stream, f.Stream.Size)
  local code = ss.DataString
  ss.destroy()
  local fn, err = load(code, '@_discrete_smoke.lua')
  if fn then fn() else print('[Discrete smoke] parse: ' .. tostring(err)) end
else
  print('[Discrete smoke] _discrete_smoke.lua not found in table files')
end

[DISABLE]
{$lua}
print('[Discrete smoke] entry disabled')
'@
$smokePayload2 = New-CreateAAScriptPayload -Description 'Discrete: smoke probe' -Script $smokeAa -AutoActivate $false
$ok2 = Invoke-AobMakerCommand -PipeName $PipeName -JsonPayload $smokePayload2 -TimeoutMs $TimeoutMs `
    -Label "CreateAAScript Discrete: smoke probe"

Write-Host ''
if ($ok1 -and $ok2) {
    Write-Host "Done — both calls OK." -ForegroundColor Green
    Write-Host "Switch to CE:" -ForegroundColor Gray
    Write-Host "  - Table -> Show table files should list _discrete_smoke.lua" -ForegroundColor Gray
    Write-Host "  - Address list should have 'Discrete: smoke probe' entry" -ForegroundColor Gray
    Write-Host "  - Enabling the entry runs _discrete_smoke.lua (prints to CE Lua console)" -ForegroundColor Gray
    exit 0
}
else {
    Write-Host "Done — partial failure." -ForegroundColor Yellow
    exit 1
}
