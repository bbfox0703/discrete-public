<#
.SYNOPSIS
    Phase 18a-3 — generate an AOB pattern from a method address.

.DESCRIPTION
    Given an address (typically obtained from Test-FindMethodInstances.ps1),
    sends `getMethodAobPattern` to the running DLL pipe. The handler reads
    `Length` bytes, walks them with a minimal x64 length disassembler, and
    returns a Cheat Engine-style pattern with rel8 / rel32 immediates and
    RIP-relative ModRM disp32 fields wildcarded.

    Output also includes the containing module name + offset, ready to drop
    into an `aobscanmodule(name, MODULE, AOB)` AA script line.

.PARAMETER Address
    Method native address (hex, with or without 0x prefix). Required.

.PARAMETER Length
    Bytes to read. Default 32, capped at 128 by the DLL.

.EXAMPLE
    # Round-trip with Test-FindMethodInstances:
    #   .\tools\Test-FindMethodInstances.ps1
    #   → grab the `address` from the output, then:
    .\tools\Test-AobPattern.ps1 -Address 0x00007FFEA7773740
    .\tools\Test-AobPattern.ps1 -Address 0x7FFEA7773740 -Length 64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Address,
    [int]    $Length    = 32,
    [int]    $TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

try {
    Write-Host ''
    Write-Host "==[ getMethodAobPattern $Address (length=$Length) ]==" -ForegroundColor Green

    $req = @{
        id   = 1
        cmd  = 'getMethodAobPattern'
        args = @{ address = $Address; length = $Length }
    } | ConvertTo-Json -Compress
    $writer.WriteLine($req)

    $start = Get-Date
    $line  = $reader.ReadLine()
    $ms    = ((Get-Date) - $start).TotalMilliseconds
    if (-not $line) { throw 'Empty response' }
    $resp = $line | ConvertFrom-Json
    if ($resp.error) { Write-Host "PROTOCOL ERROR: $($resp.error.message)" -ForegroundColor Red; return }
    $r = $resp.result
    if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }

    Write-Host ('module     : {0}  ({1} bytes)' -f $r.moduleName, $r.moduleSize)
    Write-Host ('module base: {0}' -f $r.moduleBase)
    Write-Host ('  + offset : {0}' -f $r.moduleOffset) -ForegroundColor Yellow
    Write-Host ('bytes read : {0}  (took {1:N0} ms)' -f $r.length, $ms)
    Write-Host ('decoded    : {0} instr / {1} wildcards ({2} relBranches, {3} ripRel)' -f `
        $r.stats.instructions, $r.stats.wildcards, $r.stats.relBranches, $r.stats.ripRel)
    if ($r.decoderBailed) {
        Write-Host ('decoder bailed at byte +{0}' -f $r.bailedAt) -ForegroundColor DarkYellow
    }
    Write-Host ''
    Write-Host 'pattern (paste into aobscanmodule):' -ForegroundColor Cyan
    Write-Host ('  ' + $r.pattern) -ForegroundColor White
    Write-Host ''
    Write-Host 'raw bytes:' -ForegroundColor DarkGray
    # Re-format raw bytes hex string with spaces every 2 chars for readability.
    $hex = $r.bytes
    $pretty = ($hex -split '(.{2})' | Where-Object { $_ } ) -join ' '
    Write-Host ('  ' + $pretty) -ForegroundColor DarkGray
}
finally {
    $client.Close()
}
