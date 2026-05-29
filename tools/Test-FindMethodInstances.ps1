<#
.SYNOPSIS
    Phase 18 — find every overload of a method on a class, with signatures
    and JIT/AOT native addresses.

.DESCRIPTION
    Sends `findMethodInstances` to the running DLL pipe. The handler walks
    the supplied class's declared methods, returns each overload that
    matches `Method` with its decoded signature (param types, return type),
    a symbol-safe humanSuffix used for AA-script naming, and the native
    code address (Mono: post-JIT; IL2CPP: AOT methodPointer).

    Optional `-SignatureMatch` substring narrows by signature — pass
    "Vector3" to pick `Vector3.Set(Vector3)` over `Vector3.Set(float, float, float)`.

.PARAMETER ClassAssembly / Namespace / Name
    Class to query. Defaults match Phase 18's canonical test case
    (UnityEngine.Vector3.Set on the IL2CPP path).

.PARAMETER Method
    Method name to enumerate overloads for. Default `Set`.

.PARAMETER SignatureMatch
    Optional substring filter against the rendered signature, e.g.
    "Vector3" matches `(UnityEngine.Vector3)` only.

.EXAMPLE
    .\tools\Test-FindMethodInstances.ps1
    .\tools\Test-FindMethodInstances.ps1 -Method Set -SignatureMatch Vector3
    .\tools\Test-FindMethodInstances.ps1 -ClassAssembly Assembly-CSharp -Namespace MainGame -Name PLY_WORK -Method Init
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly = 'UnityEngine.CoreModule',
    [string] $Namespace     = 'UnityEngine',
    [string] $Name          = 'Vector3',
    [string] $Method        = 'Set',
    [string] $SignatureMatch = '',
    [int]    $TimeoutMs     = 10000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

try {
    Write-Host ''
    $hdr = "==[ findMethodInstances $Namespace.$Name.$Method (asm=$ClassAssembly"
    if ($SignatureMatch) { $hdr += "  sigMatch='$SignatureMatch'" }
    Write-Host "$hdr) ]==" -ForegroundColor Green

    $argsObj = @{
        assembly  = $ClassAssembly
        namespace = $Namespace
        name      = $Name
        method    = $Method
    }
    if ($SignatureMatch) { $argsObj.signatureMatch = $SignatureMatch }
    $req = @{ id = 1; cmd = 'findMethodInstances'; args = $argsObj } | ConvertTo-Json -Compress
    $writer.WriteLine($req)

    $start = Get-Date
    $line  = $reader.ReadLine()
    $ms    = ((Get-Date) - $start).TotalMilliseconds

    if (-not $line) { throw 'Empty response' }
    $resp = $line | ConvertFrom-Json
    if ($resp.error) { Write-Host "PROTOCOL ERROR: $($resp.error.message)" -ForegroundColor Red; return }
    $r = $resp.result
    if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }

    Write-Host ("count   : {0}  (took {1:N0} ms)" -f $r.count, $ms) -ForegroundColor Cyan
    Write-Host ''

    if ($r.count -eq 0) {
        Write-Host '(no overloads matched)' -ForegroundColor DarkGray
        return
    }

    for ($i = 0; $i -lt $r.matches.Count; ++$i) {
        $m = $r.matches[$i]
        $modifiers = @()
        if ($m.isStatic)   { $modifiers += 'static'   }
        if ($m.isAbstract) { $modifiers += 'abstract' }
        elseif ($m.isVirtual) { $modifiers += 'virtual' }
        $modStr = if ($modifiers) { '[' + ($modifiers -join ',') + '] ' } else { '' }

        $compileTag = if ($m.compiled) { '' } else { '  (compile failed)' }
        Write-Host ('  #{0} {1}{2}{3}{4}' -f $i, $modStr, $m.name, $m.signature, $compileTag) -ForegroundColor White
        Write-Host ('     return     : {0}' -f $m.returnType)
        Write-Host ('     declared in: {0}.{1}' -f $m.declaringNamespace, $m.declaringClass)
        Write-Host ('     address    : {0}' -f $m.address) -ForegroundColor Yellow
        Write-Host ('     suffix     : {0}' -f $m.humanSuffix) -ForegroundColor DarkCyan
        if ($m.paramNames -and $m.paramNames.Count -gt 0) {
            $pretty = @()
            for ($j = 0; $j -lt $m.paramTypes.Count; ++$j) {
                $tn = $m.paramTypes[$j]
                $nm = if ($j -lt $m.paramNames.Count) { $m.paramNames[$j] } else { '' }
                $pretty += ('{0} {1}' -f $tn, $nm).Trim()
            }
            Write-Host ('     params     : {0}' -f ($pretty -join ', ')) -ForegroundColor DarkGray
        }
        Write-Host ''
    }
}
finally {
    $client.Close()
}
