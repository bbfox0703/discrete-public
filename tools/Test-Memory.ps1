<#
.SYNOPSIS
    Phase 6 segment A — exercise readMemory / writeMemory / resolvePointer.
    Backend-agnostic: works on Mono and IL2CPP variants.

.DESCRIPTION
    Walks two scenarios end-to-end:
      1. Locate a known string default's runtime address via getStaticFieldData
         (IL2CPP) or via the Mono equivalent, then readMemory the bytes back.
      2. Set up a small pointer chain by allocating a stack-style probe.

    Lighter mode: pass -Address / -Size / -Hex to drive a single op.

.PARAMETER Address
    Target address (hex string like "0x123ABC" or integer). For readMemory and
    writeMemory.

.PARAMETER Size
    Number of bytes to read.

.PARAMETER Hex
    Hex bytes to write. Spaces / commas accepted. Pairs only ("DEADBEEF" not
    "DEAD BEEF AB" with odd char count).

.PARAMETER Mode
    'read' | 'write' | 'resolve'. Default 'read'.

.PARAMETER Base / Offsets
    For -Mode resolve. Base is hex-string or int; Offsets is a comma-separated
    list of decimal/hex offsets (each may be negative).

.EXAMPLE
    .\tools\Test-Memory.ps1 -Mode read -Address 0x7FF600000000 -Size 16
    .\tools\Test-Memory.ps1 -Mode write -Address 0x7FF600001000 -Hex "90 90 90 90"
    .\tools\Test-Memory.ps1 -Mode resolve -Base 0x7FF600000000 -Offsets "0x10,0,0x40" -Size 8
#>
[CmdletBinding()]
param(
    [ValidateSet('read', 'write', 'resolve')]
    [string] $Mode      = 'read',
    [string] $Address,
    [int]    $Size      = 16,
    [string] $Hex,
    [string] $Base,
    [string] $Offsets,
    [int]    $TimeoutMs = 3000
)

$ErrorActionPreference = 'Stop'

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject the DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Invoke-Cmd {
    param([string]$Cmd, [hashtable]$CmdArgs)
    $writer.WriteLine((@{ id = 1; cmd = $Cmd; args = $CmdArgs } | ConvertTo-Json -Compress))
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    return ($line | ConvertFrom-Json).result
}

try {
    switch ($Mode) {
        'read' {
            if (-not $Address) { throw 'readMemory requires -Address' }
            $r = Invoke-Cmd 'readMemory' @{ address = $Address; size = $Size }
            if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }
            Write-Host ("addr  : {0}" -f $r.address)
            Write-Host ("size  : {0}" -f $r.size)
            Write-Host ("hex   : {0}" -f $r.hex)
            # Try ASCII rendering for readability
            $bytes = for ($i = 0; $i -lt $r.hex.Length; $i += 2) {
                [Convert]::ToByte($r.hex.Substring($i, 2), 16)
            }
            $ascii = -join ($bytes | ForEach-Object {
                if ($_ -ge 0x20 -and $_ -lt 0x7F) { [char]$_ } else { '.' }
            })
            Write-Host ("ascii : {0}" -f $ascii)
        }
        'write' {
            if (-not $Address) { throw 'writeMemory requires -Address' }
            if (-not $Hex)     { throw 'writeMemory requires -Hex' }
            $r = Invoke-Cmd 'writeMemory' @{ address = $Address; hex = $Hex }
            if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }
            Write-Host ("OK wrote {0} bytes at {1}" -f $r.written, $r.address) -ForegroundColor Green
        }
        'resolve' {
            if (-not $Base) { throw 'resolvePointer requires -Base' }
            $offArr = @()
            if ($Offsets) {
                foreach ($t in ($Offsets -split ',')) {
                    $t = $t.Trim()
                    if ($t.StartsWith('0x') -or $t.StartsWith('0X')) {
                        $offArr += [Convert]::ToInt64($t.Substring(2), 16)
                    } else {
                        $offArr += [Convert]::ToInt64($t)
                    }
                }
            }
            $argHash = @{ base = $Base; offsets = $offArr }
            if ($Size -gt 0) { $argHash.size = $Size }
            $r = Invoke-Cmd 'resolvePointer' $argHash
            if ($r.error) {
                Write-Host "ERROR: $($r.error) (step=$($r.step), at $($r.address))" -ForegroundColor Red
                Write-Host "trace: $($r.trace -join ' -> ')"
                return
            }
            Write-Host ("final : {0}" -f $r.final_address) -ForegroundColor Green
            Write-Host ("trace : {0}" -f ($r.trace -join ' -> '))
            if ($r.hex) {
                Write-Host ("hex   : {0}" -f $r.hex)
            }
        }
    }
}
finally {
    $client.Close()
}
