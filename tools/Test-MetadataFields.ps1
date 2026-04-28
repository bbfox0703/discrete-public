<#
.SYNOPSIS
    Dump every field of an IL2CPP type from global-metadata.dat (Phase 5
    segment 2). Includes const literals that the IL2CPP runtime strips from
    its field iterator.

.PARAMETER Namespace / Name
    Type to look up. Default: UnityEngine.Object.

.EXAMPLE
    .\tools\Test-MetadataFields.ps1
    .\tools\Test-MetadataFields.ps1 -Namespace UnityEngine -Name Vector3
    .\tools\Test-MetadataFields.ps1 -Namespace System -Name Int32
#>
[CmdletBinding()]
param(
    [string] $Namespace = 'UnityEngine',
    [string] $Name      = 'Object',
    [int]    $TimeoutMs = 3000
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
        cmd  = 'il2cppGetMetadataTypeFields'
        args = @{ namespace = $Namespace; name = $Name }
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
    Write-Host "$($resp.result.namespace).$($resp.result.name)" -ForegroundColor Cyan
    Write-Host ("  type_index : {0}" -f $resp.result.type_index)
    Write-Host ("  token      : 0x{0:X8}" -f $resp.result.token)
    Write-Host ("  flags      : 0x{0:X}" -f $resp.result.flags)
    Write-Host ("  field range: [{0} .. {1}) -- {2} fields" -f `
        $resp.result.field_start,
        ($resp.result.field_start + $resp.result.field_count),
        $resp.result.field_count)
    Write-Host ''

    foreach ($f in $resp.result.fields) {
        $hasDefault = if ($f.default) { 'CONST' } else { '     ' }
        '{0,4}  {1}  {2,-40}  type#{3}' -f $f.index, $hasDefault, $f.name, $f.type_index | Write-Host
        if ($f.default) {
            ('         hex16 = {0}   (raw_size={1}, data#{2})' -f `
                $f.default.raw_hex_first16, $f.default.raw_size, $f.default.data_index) | Write-Host
            if ($f.default.decoded_string) {
                $s = $f.default.decoded_string
                if ($s.Length -gt 100) { $s = $s.Substring(0, 97) + '...' }
                '         string = "{0}"' -f $s | Write-Host -ForegroundColor Green
            }
        }
    }
}
finally {
    $client.Close()
}
