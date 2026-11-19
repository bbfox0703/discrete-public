<#
.SYNOPSIS
    Exercise Phase 3 segment 2 Mono pipe commands: getClasses / getClassInfo /
    getFieldInfo / getMethodInfo. Assumes the DLL is already injected and the
    pipe is up.

.DESCRIPTION
    Connects to \\.\pipe\DiscreteBfx and runs four queries, printing results.
    No injection — run .\tools\Test-Handshake.ps1 first to get the DLL into
    the target.

.PARAMETER Assembly
    Assembly to enumerate / look up. Default: Assembly-CSharp.

.PARAMETER Namespace
    Namespace of the class to drill into. Default: UnityEngine.

.PARAMETER Name
    Class name to drill into. Default: GameObject.

.PARAMETER ClassAssembly
    Assembly that owns the drill-down class (separate from -Assembly so you can
    list Assembly-CSharp classes but inspect a UnityEngine class).
    Default: UnityEngine.CoreModule.

.PARAMETER Field
    Optional field name to look up on the chosen class.

.PARAMETER Method
    Optional method name to look up on the chosen class.

.PARAMETER ListLimit
    How many rows of getClasses output to display. Default: 15.

.EXAMPLE
    .\tools\Test-MonoWalk.ps1
    .\tools\Test-MonoWalk.ps1 -Assembly Assembly-CSharp -ClassAssembly Assembly-CSharp -Namespace '' -Name PlayerController -Field health
#>
[CmdletBinding()]
param(
    [string] $Assembly      = 'Assembly-CSharp',
    [string] $ClassAssembly = 'UnityEngine.CoreModule',
    [string] $Namespace     = 'UnityEngine',
    [string] $Name          = 'GameObject',
    [string] $Field         = '',
    [string] $Method        = '',
    [int]    $ListLimit     = 15,
    [int]    $TimeoutMs     = 3000
)

$ErrorActionPreference = 'Stop'

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw "Pipe connect failed. Inject the DLL first via .\tools\Test-Handshake.ps1." }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Invoke-Cmd {
    param([int]$Id, [string]$Cmd, [hashtable]$CmdArgs)
    $payload = @{ id = $Id; cmd = $Cmd }
    if ($CmdArgs) { $payload.args = $CmdArgs }
    $writer.WriteLine(($payload | ConvertTo-Json -Compress))
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    return ($line | ConvertFrom-Json)
}

try {
    Write-Host ''
    Write-Host "==[ getClasses assembly=$Assembly ]==" -ForegroundColor Green
    $r = Invoke-Cmd 1 'getClasses' @{ assembly = $Assembly }
    if ($r.result.error) { Write-Host "  ERROR: $($r.result.error)" -ForegroundColor Red }
    else {
        Write-Host "  count: $($r.result.count)"
        $r.result.classes | Select-Object -First $ListLimit |
            Format-Table @{n='namespace';e={$_.namespace}}, name, @{n='token';e={'0x{0:X8}' -f $_.token}} -AutoSize | Out-Host
    }

    Write-Host ''
    Write-Host "==[ getClassInfo $ClassAssembly :: $Namespace.$Name ]==" -ForegroundColor Green
    $gi = Invoke-Cmd 2 'getClassInfo' @{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name }
    if ($gi.result.error) { Write-Host "  ERROR: $($gi.result.error)" -ForegroundColor Red }
    else {
        $parent = if ($gi.result.parent) { "$($gi.result.parent.namespace).$($gi.result.parent.name)" } else { '<none>' }
        Write-Host "  parent: $parent"
        Write-Host "  fields: $($gi.result.fields.Count), methods: $($gi.result.methods.Count)"
        Write-Host '  -- first fields --'
        $gi.result.fields | Select-Object -First 8 |
            Format-Table name, type, @{n='offset';e={'0x{0:X}' -f $_.offset}}, static -AutoSize | Out-Host
        Write-Host '  -- first methods --'
        $gi.result.methods | Select-Object -First 8 |
            Format-Table name, @{n='token';e={'0x{0:X8}' -f $_.token}}, static -AutoSize | Out-Host
    }

    if ($Field) {
        Write-Host ''
        Write-Host "==[ getFieldInfo $Namespace.$Name.$Field ]==" -ForegroundColor Green
        $fi = Invoke-Cmd 3 'getFieldInfo' @{
            assembly = $ClassAssembly; namespace = $Namespace; name = $Name; field = $Field
        }
        if ($fi.result.error) { Write-Host "  ERROR: $($fi.result.error)" -ForegroundColor Red }
        else { $fi.result | Format-List | Out-Host }
    }

    if ($Method) {
        Write-Host ''
        Write-Host "==[ getMethodInfo $Namespace.$Name.$Method ]==" -ForegroundColor Green
        $mi = Invoke-Cmd 4 'getMethodInfo' @{
            assembly = $ClassAssembly; namespace = $Namespace; name = $Name; method = $Method
        }
        if ($mi.result.error) { Write-Host "  ERROR: $($mi.result.error)" -ForegroundColor Red }
        else {
            $mi.result.matches | Format-Table name, @{n='token';e={'0x{0:X8}' -f $_.token}}, static -AutoSize | Out-Host
        }
    }
}
finally {
    $client.Close()
}
