<#
.SYNOPSIS
    Probe Il2CppMetadataRegistration discovery in isolation.

.DESCRIPTION
    Exercises the structural-shape AOB scan (Phase 5 segment 2d follow-up)
    independently of MetadataParser. Useful when:

    * metadata.dat parsing is rejected (e.g. v32+ targets where MetadataParser
      hard-rejects with "unsupported metadata version" — the registration
      scan is independent and may still work).
    * Diagnosing whether the scan failed (no hit) or the encoding was wrong
      (hit + types_count=0 / nonsense address).
    * Confirming registration discovery on a new metadata version target
      before doing fuller cross-version validation via Test-MetadataFields.

.EXAMPLE
    .\tools\Test-MetadataRegistration.ps1

    {
      "id": 1,
      "result": {
        "address": "0x00007FFE9C13D830",
        "found": true,
        "types_array": "0x00007FFE9C22A1C0",
        "types_count": 144837
      }
    }
#>
[CmdletBinding()]
param([int] $TimeoutMs = 3000)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject the DLL first via Test-Handshake.ps1.' }
$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)
try {
    $writer.WriteLine('{"id":1,"cmd":"il2cppGetMetadataRegistration","args":{}}')
    $line = $reader.ReadLine()
    if (-not $line) { throw 'Empty response' }
    $line | ConvertFrom-Json | ConvertTo-Json -Depth 5
}
finally {
    $client.Close()
}
