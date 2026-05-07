<#
.SYNOPSIS
    Phase 19a — invoke a managed (IL2CPP) method via the DLL pipe and print
    the typed return value or the captured managed exception.

.DESCRIPTION
    Sends `callMethod` to the running DLL pipe. 19a is IL2CPP + static-only
    + primitive / null / address args + primitive / address returns. String
    args and instance dispatch land in 19c and will fail here with a
    distinct error code.

    Three pre-canned scenarios cover the acceptance gates:

      1. Positive  : Mathf.Abs(int32:-7) -> 7
      2. Exception : Convert.ToByte(int32:-1) -> OverflowException
      3. Disambig  : Mathf.Abs (overload picked via -SignatureMatch Single)

    Anything beyond that, supply -ClassAssembly / -Namespace / -Name /
    -Method / -ArgsJson manually.

.PARAMETER Scenario
    `Abs` (default), `Throw`, `Float`, or `Custom`. `Custom` requires the
    Class* / Method / ArgsJson params.

.PARAMETER ArgsJson
    JSON array of {type, value} pairs. Wire `type` tags: bool, byte, sbyte,
    int16, uint16, int32, uint32, int64, uint64, float, double, char,
    address, null. (string is reserved for 19c and will fail.)

.EXAMPLE
    .\tools\Test-CallMethod.ps1                          # Mathf.Abs(-7)
    .\tools\Test-CallMethod.ps1 -Scenario Throw          # Convert.ToInt32 throws
    .\tools\Test-CallMethod.ps1 -Scenario Float          # Mathf.Abs (Single overload)
    .\tools\Test-CallMethod.ps1 -Scenario Custom `
        -ClassAssembly mscorlib -Namespace System -Name Math `
        -Method Abs -ArgsJson '[{"type":"int32","value":-12345}]'
#>
[CmdletBinding()]
param(
    [ValidateSet('Abs','Throw','Float','Custom')]
    [string] $Scenario = 'Abs',

    [string] $ClassAssembly,
    [string] $Namespace,
    [string] $Name,
    [string] $Method,
    [string] $ArgsJson,
    [string] $SignatureMatch = '',

    [int] $TimeoutMs = 10000
)

$ErrorActionPreference = 'Stop'

switch ($Scenario) {
    'Abs' {
        if (-not $ClassAssembly) { $ClassAssembly = 'UnityEngine.CoreModule' }
        if (-not $Namespace)     { $Namespace     = 'UnityEngine' }
        if (-not $Name)          { $Name          = 'Mathf' }
        if (-not $Method)        { $Method        = 'Abs' }
        if (-not $ArgsJson)      { $ArgsJson      = '[{"type":"int32","value":-7}]' }
        if (-not $SignatureMatch){ $SignatureMatch = 'Int32' }
    }
    'Float' {
        if (-not $ClassAssembly) { $ClassAssembly = 'UnityEngine.CoreModule' }
        if (-not $Namespace)     { $Namespace     = 'UnityEngine' }
        if (-not $Name)          { $Name          = 'Mathf' }
        if (-not $Method)        { $Method        = 'Abs' }
        if (-not $ArgsJson)      { $ArgsJson      = '[{"type":"float","value":-3.5}]' }
        if (-not $SignatureMatch){ $SignatureMatch = 'Single' }
    }
    'Throw' {
        # Convert.ToByte(int32) throws OverflowException for negative values.
        # Picked over Convert.ToInt32(string) because:
        #   (a) string args aren't supported until 19c
        #   (b) Convert.ToInt32(null) RETURNS 0 — null doesn't throw per spec
        #   (c) "String" as a signatureMatch is ambiguous (matches the
        #       (string), (string, IFormatProvider), (string, int) overloads)
        # SignatureMatch uses the parens-bounded form "(System.Int32)" so it
        # uniquely matches the single-int overload — bare "Int32" matches
        # "UInt32" (substring!) AND "(Int32, IFormatProvider)" too.
        if (-not $ClassAssembly) { $ClassAssembly = 'mscorlib' }
        if (-not $Namespace)     { $Namespace     = 'System' }
        if (-not $Name)          { $Name          = 'Convert' }
        if (-not $Method)        { $Method        = 'ToByte' }
        if (-not $ArgsJson)      { $ArgsJson      = '[{"type":"int32","value":-1}]' }
        if (-not $SignatureMatch){ $SignatureMatch = '(System.Int32)' }
    }
    'Custom' {
        if (-not $ClassAssembly -or -not $Name -or -not $Method -or -not $ArgsJson) {
            throw 'Custom scenario requires -ClassAssembly, -Name, -Method, -ArgsJson'
        }
    }
}

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

try {
    $hdr = "==[ callMethod  $Namespace.$Name.$Method"
    if ($SignatureMatch) { $hdr += "  sigMatch='$SignatureMatch'" }
    $hdr += "  asm=$ClassAssembly ]=="
    Write-Host ''
    Write-Host $hdr -ForegroundColor Green
    Write-Host ('args  : {0}' -f $ArgsJson) -ForegroundColor DarkGray

    # Build the wire request body. PowerShell's ConvertTo-Json silently
    # unwraps a single-element array stored as an object property — so a
    # naive `args = ConvertFrom-Json '[{...}]'` round-trip emits the args
    # field as a bare object, not a 1-element array, and the DLL handler
    # reports `arg_count_mismatch` (formal=1, wire=0). Workaround: splice
    # $ArgsJson in as raw text after letting ConvertTo-Json handle the
    # scalar fields.
    $bodyHash = @{
        assembly  = $ClassAssembly
        namespace = $Namespace
        name      = $Name
        method    = $Method
    }
    if ($SignatureMatch) { $bodyHash.signatureMatch = $SignatureMatch }
    $bodyJson = ($bodyHash | ConvertTo-Json -Compress)
    # Drop the trailing `}`, append `,"args":<raw json array>}`.
    $bodyJson = $bodyJson.Substring(0, $bodyJson.Length - 1) + ',"args":' + $ArgsJson + '}'
    $req = '{"id":1,"cmd":"callMethod","args":' + $bodyJson + '}'
    $writer.WriteLine($req)

    $start = Get-Date
    $line  = $reader.ReadLine()
    $ms    = ((Get-Date) - $start).TotalMilliseconds

    if (-not $line) { throw 'Empty response' }
    $resp = $line | ConvertFrom-Json
    if ($resp.error) {
        Write-Host ("PROTOCOL ERROR: {0}" -f $resp.error.message) -ForegroundColor Red
        return
    }
    $r = $resp.result
    if ($r.error) {
        Write-Host ("ERROR  : {0}" -f $r.error) -ForegroundColor Red
        return
    }

    Write-Host ("ok       : {0}  (took {1:N0} ms; server elapsedMs={2})" -f $r.ok, $ms, $r.elapsedMs) -ForegroundColor Cyan
    if ($r.method) {
        Write-Host ('method   : {0}{1}  @ {2}' -f $Method, $r.method.signature, $r.method.address) -ForegroundColor DarkGray
        Write-Host ('suffix   : {0}' -f $r.method.humanSuffix) -ForegroundColor DarkGray
    }
    Write-Host ('returnTy : {0}' -f $r.returnType) -ForegroundColor White
    if ($null -ne $r.returnValue) {
        Write-Host ('returnV  : {0}' -f $r.returnValue) -ForegroundColor Yellow
    } else {
        Write-Host ('returnV  : (null)') -ForegroundColor DarkGray
    }
    if ($r.exception) {
        Write-Host ('exception: {0}' -f $r.exception) -ForegroundColor Magenta
    }
    if ($r.note) {
        Write-Host ('note     : {0}' -f $r.note) -ForegroundColor DarkYellow
    }
    Write-Host ''
}
finally {
    $client.Close()
}
