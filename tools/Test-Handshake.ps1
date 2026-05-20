<#
.SYNOPSIS
    Inject UnityDumper DLL into a target Unity process and run a handshake / scan
    smoke test through the named pipe.

.DESCRIPTION
    End-to-end Phase 2 acceptance helper. Steps:
        1. Validate the PID exists (and the user passed -Backend or -Auto)
        2. Auto-detect backend from loaded modules if -Auto is set (default)
        3. Call .\tools\Inject-Dll.ps1 with the chosen backend
        4. Connect to \\.\pipe\UnityDumpBfx
        5. Send: ping, handshake, scan
        6. Pretty-print each response

    If you launch with no arguments, prints help.

.PARAMETER ProcessId
    Target process PID. Use .\tools\List-UnityProcs.ps1 to find one.

.PARAMETER Backend
    Force a specific DLL variant: Mono | IL2CPP | Auto.
    Default: Auto -- inspect the target's loaded modules and pick.

.PARAMETER SkipInject
    Skip injection step (use when the DLL is already loaded in the target).

.PARAMETER PipeTimeoutMs
    How long to wait for the pipe to come up (default 5000).

.EXAMPLE
    .\tools\Test-Handshake.ps1 -ProcessId 12345
    .\tools\Test-Handshake.ps1 -ProcessId 12345 -Backend IL2CPP
    .\tools\Test-Handshake.ps1 -ProcessId 12345 -SkipInject
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [int] $ProcessId,

    [ValidateSet('Auto', 'Mono', 'IL2CPP')]
    [string] $Backend = 'Auto',

    [switch] $SkipInject,

    [int] $PipeTimeoutMs = 5000
)

function Show-Help { Get-Help $PSCommandPath -Detailed | Out-Host }

if (-not $ProcessId) {
    Show-Help
    Write-Host ''
    Write-Host 'Tip: run .\tools\List-UnityProcs.ps1 first to find a target PID.' -ForegroundColor Yellow
    return
}

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$injectScript = Join-Path $PSScriptRoot 'Inject-Dll.ps1'
if (-not (Test-Path $injectScript)) { throw "Inject-Dll.ps1 not found at $injectScript" }

# --- Resolve target ----------------------------------------------------------
$proc = Get-Process -Id $ProcessId -ErrorAction Stop

# --- Backend auto-detect -----------------------------------------------------
if ($Backend -eq 'Auto') {
    $detected = $null
    try {
        foreach ($m in $proc.Modules) {
            switch -Wildcard ($m.ModuleName) {
                'GameAssembly.dll'   { $detected = 'IL2CPP'; break }
                'mono-2.0-bdwgc.dll' { $detected = 'Mono';   break }
                'mono.dll'           { if (-not $detected) { $detected = 'Mono' } }
            }
            if ($detected) { break }
        }
    } catch {
        throw "Cannot read modules of PID $ProcessId (access denied?). Pass -Backend explicitly."
    }
    if (-not $detected) {
        throw "Could not auto-detect backend for PID $ProcessId. Pass -Backend Mono or -Backend IL2CPP."
    }
    $Backend = $detected
    Write-Host "Auto-detected backend: $Backend" -ForegroundColor Cyan
}

# --- Inject ------------------------------------------------------------------
if (-not $SkipInject) {
    Write-Host ''
    Write-Host "==[ Injecting UnityDumper-$Backend.dll into PID $ProcessId ]==" -ForegroundColor Green
    & $injectScript -Target $ProcessId -Backend $Backend
    # Don't trust $LASTEXITCODE: it tracks native EXE exit codes, not .ps1
    # return values, so it can be stale from a previous command. The pipe
    # connect below is the real success check.
} else {
    Write-Host '(SkipInject set -- assuming DLL is already loaded)' -ForegroundColor DarkGray
}

# --- Connect pipe ------------------------------------------------------------
Write-Host ''
Write-Host '==[ Pipe handshake ]==' -ForegroundColor Green
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try {
    $client.Connect($PipeTimeoutMs)
} catch {
    throw "Pipe connect timed out after $PipeTimeoutMs ms. Is the DLL loaded? Check %LOCALAPPDATA%\Discrete_UEDumper\Logs\$ProcessId\init\current.log"
}

$writer = New-Object System.IO.StreamWriter($client)
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Invoke-Cmd {
    param([int] $Id, [string] $Cmd)
    $req = @{ id = $Id; cmd = $Cmd } | ConvertTo-Json -Compress
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for cmd '$Cmd'" }
    $resp = $line | ConvertFrom-Json
    Write-Host "  $Cmd ->" -ForegroundColor Cyan
    $resp.result | Format-List | Out-String | ForEach-Object { $_.TrimEnd() } | Write-Host
    return $resp
}

try {
    [void] (Invoke-Cmd -Id 1 -Cmd 'ping')
    $hs = Invoke-Cmd -Id 2 -Cmd 'handshake'
    [void] (Invoke-Cmd -Id 3 -Cmd 'scan')
} finally {
    $client.Close()
}

# --- Summary -----------------------------------------------------------------
Write-Host ''
Write-Host '==[ Acceptance check ]==' -ForegroundColor Green
$ok = $true
if ($hs.result.backend -ne $Backend) {
    Write-Host "  FAIL: backend=$($hs.result.backend), expected $Backend" -ForegroundColor Red
    $ok = $false
} else {
    Write-Host "  OK   backend=$Backend"
}
if (-not $hs.result.unity_version) {
    Write-Host '  WARN unity_version=null (could not read globalgamemanagers/data.unity3d)' -ForegroundColor Yellow
} else {
    Write-Host "  OK   unity_version=$($hs.result.unity_version)"
}
if ($Backend -eq 'IL2CPP') {
    if (-not $hs.result.metadata_version) {
        Write-Host '  WARN metadata_version=null (global-metadata.dat unreadable or encrypted)' -ForegroundColor Yellow
    } else {
        Write-Host "  OK   metadata_version=$($hs.result.metadata_version)"
    }
}
if ($ok) {
    Write-Host ''
    Write-Host 'PASS' -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host 'FAIL' -ForegroundColor Red
    exit 1
}
