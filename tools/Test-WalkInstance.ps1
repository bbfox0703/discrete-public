<#
.SYNOPSIS
    Phase 6 segment B — walk an instance and decode each field.

.DESCRIPTION
    Given an instance address and a class spec (assembly / namespace / name),
    asks the DLL to walk the inheritance chain and decode every instance field.
    Pair with Test-FindInstances.ps1 to obtain candidate addresses.

.PARAMETER Address
    Instance start address. Hex string ("0x...") or integer.

.PARAMETER ClassAssembly / Namespace / Name
    The runtime class the address points at. Defaults match the typical
    scene-Transform shape so you can smoke-test with one of the addresses
    Test-FindInstances returned.

.EXAMPLE
    .\tools\Test-WalkInstance.ps1 -Address 0x1234ABCD0
    .\tools\Test-WalkInstance.ps1 -Address 0x1234ABCD0 `
        -ClassAssembly Assembly-CSharp -Namespace '' -Name ItemMenu
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string] $Address,
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

try {
    Write-Host ''
    Write-Host "==[ walkInstance $Namespace.$Name @ $Address ]==" -ForegroundColor Green
    $req = @{
        id   = 1
        cmd  = 'walkInstance'
        args = @{
            address   = $Address
            assembly  = $ClassAssembly
            namespace = $Namespace
            name      = $Name
        }
    } | ConvertTo-Json -Compress
    $writer.WriteLine($req)

    $line = $reader.ReadLine()
    if (-not $line) { throw 'Empty response' }
    $r = ($line | ConvertFrom-Json).result
    if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }

    Write-Host ("addr   : {0}" -f $r.address) -ForegroundColor Cyan
    Write-Host ("class  : {0}.{1}" -f $r.namespace, $r.name)
    Write-Host ("depth  : {0}  fields: {1}" -f $r.depth, $r.count)
    Write-Host ''

    foreach ($f in $r.fields) {
        $line1 = '{0,4}  {1,-30}  {2}' -f $f.offset, $f.name, $f.type
        Write-Host $line1 -ForegroundColor White
        $detail = ''
        if ($null -ne $f.value) {
            $detail = "= $($f.value)"
        } elseif ($null -ne $f.string) {
            $detail = "= `"$($f.string)`" (len $($f.string_length))"
        } elseif ($f.string_error) {
            $detail = "string: $($f.string_error) ptr=$($f.ptr)"
        } elseif ($null -ne $f.ptr) {
            $detail = "ptr=$($f.ptr)"
        } elseif ($f.error) {
            $detail = "ERROR: $($f.error)"
        }
        if ($detail) {
            Write-Host ("      from={0}  {1}" -f $f.from, $detail) -ForegroundColor DarkGray
        } else {
            Write-Host ("      from={0}" -f $f.from) -ForegroundColor DarkGray
        }
    }
}
finally {
    $client.Close()
}
