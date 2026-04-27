<#
.SYNOPSIS
    Dumps a window of string literals from global-metadata.dat (Phase 5 / IL2CPP).

.DESCRIPTION
    Calls the il2cppGetMetadataStrings pipe command. Assumes the IL2CPP DLL is
    already injected (Test-Handshake.ps1 first).

.PARAMETER Limit
    Max number of strings to fetch. 0 = all (use with care -- a Unity game can
    have tens of thousands).

.PARAMETER Offset
    Starting index into the string-literal table.

.EXAMPLE
    .\tools\Test-MetadataStrings.ps1
    .\tools\Test-MetadataStrings.ps1 -Limit 50 -Offset 100
    .\tools\Test-MetadataStrings.ps1 -Limit 0   # dump everything (large!)
#>
[CmdletBinding()]
param(
    [int] $Limit     = 25,
    [int] $Offset    = 0,
    [int] $TimeoutMs = 3000
)

$ErrorActionPreference = 'Stop'

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw "Pipe connect failed. Inject the DLL first via .\tools\Test-Handshake.ps1." }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

try {
    $req = @{
        id   = 1
        cmd  = 'il2cppGetMetadataStrings'
        args = @{ limit = $Limit; offset = $Offset }
    } | ConvertTo-Json -Compress
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw 'Empty response' }

    $resp = $line | ConvertFrom-Json
    if ($resp.result.error) {
        Write-Host "ERROR: $($resp.result.error)" -ForegroundColor Red
        return
    }

    Write-Host ''
    Write-Host "metadata version : $($resp.result.version)" -ForegroundColor Cyan
    Write-Host "total strings    : $($resp.result.count)"
    Write-Host "offset           : $($resp.result.offset)"
    Write-Host "returned         : $($resp.result.returned)"
    Write-Host ''

    $i = $resp.result.offset
    foreach ($s in $resp.result.strings) {
        $display = if ($null -eq $s) { '<null>' } else { $s }
        # Truncate ridiculously long strings for readability
        if ($display.Length -gt 80) { $display = $display.Substring(0, 77) + '...' }
        '{0,6}: {1}' -f $i, $display | Write-Host
        $i++
    }
}
finally {
    $client.Close()
}
