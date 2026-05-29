<#
.SYNOPSIS
    Exercise Phase 3 segment 3 commands: getStaticFieldData + monoCompileMethod.
    Assumes the DLL is already injected (run Test-Handshake.ps1 first).

.PARAMETER ClassAssembly / Namespace / Name
    Class to operate on. Defaults probe UnityEngine.Object.

.PARAMETER Field
    Static field whose runtime address to resolve.
    Default: kInstanceID_None (a known static System.Int32 on UnityEngine.Object).

.PARAMETER Method
    Method to JIT-compile and report native address for.
    Default: GetInstanceID.

.EXAMPLE
    .\tools\Test-MonoOps.ps1
    .\tools\Test-MonoOps.ps1 -ClassAssembly Assembly-CSharp -Namespace '' -Name AdventsDrawer -Method Start
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly = 'UnityEngine.CoreModule',
    [string] $Namespace     = 'UnityEngine',
    [string] $Name          = 'Object',
    [string] $Field         = 'kInstanceID_None',
    [string] $Method        = 'GetInstanceID',
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
    $writer.WriteLine((@{ id = $Id; cmd = $Cmd; args = $CmdArgs } | ConvertTo-Json -Compress))
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    return ($line | ConvertFrom-Json)
}

try {
    $base = @{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name }

    Write-Host ''
    Write-Host "==[ getStaticFieldData $Namespace.$Name.$Field ]==" -ForegroundColor Green
    $sf = Invoke-Cmd 1 'getStaticFieldData' ($base + @{ field = $Field })
    if ($sf.result.error) { Write-Host "  ERROR: $($sf.result.error)" -ForegroundColor Red }
    else { $sf.result | Format-List | Out-Host }

    Write-Host ''
    Write-Host "==[ monoCompileMethod $Namespace.$Name.$Method ]==" -ForegroundColor Green
    $cm = Invoke-Cmd 2 'monoCompileMethod' ($base + @{ method = $Method })
    if ($cm.result.error) { Write-Host "  ERROR: $($cm.result.error)" -ForegroundColor Red }
    else { $cm.result | Format-List | Out-Host }
}
finally {
    $client.Close()
}
