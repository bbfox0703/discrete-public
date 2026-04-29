<#
.SYNOPSIS
    Phase 6 segment D — exercise registerSymbol / lookupSymbol / listSymbols /
    monoGetVTable / getEnumValues.

.DESCRIPTION
    Three sub-modes via -Mode:
      symbol  : registerSymbol then lookupSymbol then listSymbols round-trip.
      vtable  : monoGetVTable for a class (Mono only).
      enum    : getEnumValues for a class (IL2CPP best-effort, Mono stub).

.EXAMPLE
    .\tools\Test-SegmentD.ps1 -Mode symbol
    .\tools\Test-SegmentD.ps1 -Mode vtable -ClassAssembly UnityEngine.CoreModule -Namespace UnityEngine -Name Transform
    .\tools\Test-SegmentD.ps1 -Mode enum   -ClassAssembly UnityEngine.CoreModule -Namespace UnityEngine -Name CameraType
#>
[CmdletBinding()]
param(
    [ValidateSet('symbol', 'vtable', 'enum')]
    [string] $Mode = 'symbol',
    [string] $ClassAssembly = 'UnityEngine.CoreModule',
    [string] $Namespace     = 'UnityEngine',
    [string] $Name          = 'Transform',
    [int]    $TimeoutMs     = 5000
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
        'symbol' {
            Write-Host "==[ registerSymbol round-trip ]==" -ForegroundColor Green
            $r1 = Invoke-Cmd 'registerSymbol' @{ name = 'demo_player'; address = '0xDEADBEEF1234' }
            Write-Host ("  set    : {0} -> {1}" -f $r1.name, $r1.address)
            $r2 = Invoke-Cmd 'lookupSymbol' @{ name = 'demo_player' }
            Write-Host ("  lookup : {0} = {1}" -f $r2.name, $r2.address)
            $r3 = Invoke-Cmd 'registerSymbol' @{ name = 'demo_world'; address = '0x1111222233334444' }
            Write-Host ("  set    : {0} -> {1}" -f $r3.name, $r3.address)
            $r4 = Invoke-Cmd 'listSymbols' @{}
            Write-Host ("  list   : count={0}" -f $r4.count)
            foreach ($s in $r4.symbols) {
                Write-Host ("           {0,-20} {1}" -f $s.name, $s.address) -ForegroundColor DarkGray
            }
            $r5 = Invoke-Cmd 'registerSymbol' @{ name = 'demo_player'; action = 'delete' }
            Write-Host ("  delete : demo_player deleted={0}" -f $r5.deleted)
            $r6 = Invoke-Cmd 'listSymbols' @{}
            Write-Host ("  final  : count={0}" -f $r6.count)
        }
        'vtable' {
            Write-Host "==[ monoGetVTable $Namespace.$Name ]==" -ForegroundColor Green
            $r = Invoke-Cmd 'monoGetVTable' @{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name }
            if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }
            Write-Host ("  vtable : {0}" -f $r.vtable) -ForegroundColor Cyan
        }
        'enum' {
            Write-Host "==[ getEnumValues $Namespace.$Name ]==" -ForegroundColor Green
            $r = Invoke-Cmd 'getEnumValues' @{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name }
            if ($r.error) { Write-Host "ERROR: $($r.error)  $($r.hint)" -ForegroundColor Red; return }
            Write-Host ("  type   : {0}.{1}  ({2} members)" -f $r.namespace, $r.name, $r.count) -ForegroundColor Cyan
            foreach ($v in $r.values) {
                $valTxt = if ($null -ne $v.value) { "= $($v.value)" }
                          elseif ($v.decode_error) { "? ($($v.decode_error))" }
                          else { '' }
                $w = if ($v.enc_bytes) { "$($v.enc_bytes)B" } else { '' }
                Write-Host ("    {0,-32}  {1,-12}  enc={2}  raw={3}" -f $v.name, $valTxt, $w, $v.raw_hex)
            }
        }
    }
}
finally {
    $client.Close()
}
