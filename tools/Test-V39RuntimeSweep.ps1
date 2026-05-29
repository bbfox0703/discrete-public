<#
.SYNOPSIS
    One-shot v32+ runtime sweep — proves what works on a v32+ IL2CPP target
    even when MetadataParser hard-rejects the on-disk metadata.dat.

.DESCRIPTION
    Phase 5 segment 2d cross-version follow-up. The structural-shape
    Il2CppMetadataRegistration scan (`il2cppGetMetadataRegistration`)
    landed verified on v39 in the 2026-05-07 sweep. This script extends
    that to the broader runtime API surface — does class enumeration,
    field iteration, instance scanning, walkInstance decoding,
    findMethodInstances, and callMethod all work on v32+ where the
    metadata.dat parser refuses the file?

    Each step is independent — failures land as ERROR lines but the
    sweep continues so you see the full picture in one shot.

    Pre-flight: inject Discrete-IL2CPP.dll into a v32+ target via
    Test-Handshake.ps1 first.

.EXAMPLE
    .\tools\Test-V39RuntimeSweep.ps1
#>
[CmdletBinding()]
param([int] $TimeoutMs = 5000)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject the DLL first via Test-Handshake.ps1.' }
$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Send-Cmd {
    param([string]$Cmd, [hashtable]$CmdArgs = @{})
    $writer.WriteLine((@{ id = 1; cmd = $Cmd; args = $CmdArgs } | ConvertTo-Json -Compress -Depth 6))
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response from $Cmd" }
    return ($line | ConvertFrom-Json).result
}

function Section { param([string]$Title) Write-Host ''; Write-Host "==[ $Title ]==" -ForegroundColor Cyan }

try {
    # ---- 1. Handshake (sanity) -----------------------------------------
    Section 'handshake'
    $hs = Send-Cmd 'handshake'
    Write-Host ("  backend={0}  unity={1}  metadata=v{2}  build={3}" -f `
        $hs.backend, $hs.unity_version, $hs.metadata_version, $hs.build)
    if ($hs.metadata_version -lt 32) {
        Write-Host "  WARN: this script targets v32+ — metadata=$($hs.metadata_version) is already supported." -ForegroundColor Yellow
    }

    # ---- 2. Registration scan (proven on v39 already) ------------------
    Section 'il2cppGetMetadataRegistration (Phase 5 segment 2d)'
    $reg = Send-Cmd 'il2cppGetMetadataRegistration'
    if ($reg.found) {
        Write-Host ("  found=YES  address={0}  types_count={1}" -f $reg.address, $reg.types_count) -ForegroundColor Green
    } else {
        Write-Host '  found=NO — structural scan failed' -ForegroundColor Red
    }

    # ---- 3. getAssemblies (domain walk) --------------------------------
    Section 'getAssemblies (runtime domain walk)'
    $asm = Send-Cmd 'getAssemblies'
    if ($asm.error) {
        Write-Host "  ERROR: $($asm.error)" -ForegroundColor Red
    } else {
        $count = $asm.assemblies.Count
        Write-Host ("  OK  assemblies={0}  first 3: {1}" -f $count,
            (($asm.assemblies | Select-Object -First 3 | ForEach-Object { $_.name }) -join ', ')) -ForegroundColor Green
    }

    # ---- 4. getClasses on UnityEngine.CoreModule ----------------------
    Section 'getClasses (UnityEngine.CoreModule)'
    $cls = Send-Cmd 'getClasses' @{ assembly = 'UnityEngine.CoreModule' }
    if ($cls.error) {
        Write-Host "  ERROR: $($cls.error)" -ForegroundColor Red
    } else {
        Write-Host ("  OK  classes={0}" -f $cls.classes.Count) -ForegroundColor Green
        $hasGameObject = $cls.classes | Where-Object { $_.name -eq 'GameObject' }
        Write-Host ("  GameObject present: {0}" -f ([bool]$hasGameObject))
    }

    # ---- 5. getClassInfo for GameObject (runtime field iteration) -----
    Section 'getClassInfo UnityEngine.GameObject'
    $info = Send-Cmd 'getClassInfo' @{ assembly = 'UnityEngine.CoreModule'; namespace = 'UnityEngine'; name = 'GameObject' }
    if ($info.error) {
        Write-Host "  ERROR: $($info.error)" -ForegroundColor Red
    } else {
        $instFields = ($info.fields | Where-Object { -not $_.is_static }).Count
        $methods = $info.methods.Count
        Write-Host ("  OK  fields={0} ({1} instance)  methods={2}  parent={3}" -f `
            $info.fields.Count, $instFields, $methods,
            ($info.parent ? "$($info.parent.namespace).$($info.parent.name)" : '(none)')) -ForegroundColor Green
    }

    # ---- 6. findInstances of GameObject -------------------------------
    Section 'findInstances UnityEngine.GameObject (limit=8)'
    $find = Send-Cmd 'findInstances' @{ assembly = 'UnityEngine.CoreModule'; namespace = 'UnityEngine'; name = 'GameObject'; limit = 8 }
    if ($find.error) {
        Write-Host "  ERROR: $($find.error)" -ForegroundColor Red
        $firstInstance = $null
    } else {
        Write-Host ("  OK  matches={0}/{1}  classPtr={2}" -f $find.count, $find.limit, ($find.classPtr ?? $find.vtable)) -ForegroundColor Green
        $firstInstance = $find.matches | Select-Object -First 1
        if ($firstInstance) { Write-Host "  first: $firstInstance" }
    }

    # ---- 7. walkInstance on the first GameObject ----------------------
    if ($firstInstance) {
        Section "walkInstance $firstInstance"
        $walk = Send-Cmd 'walkInstance' @{ address = $firstInstance; assembly = 'UnityEngine.CoreModule'; namespace = 'UnityEngine'; name = 'GameObject' }
        if ($walk.error) {
            Write-Host "  ERROR: $($walk.error)" -ForegroundColor Red
        } else {
            Write-Host ("  OK  fields_decoded={0}" -f $walk.fields.Count) -ForegroundColor Green
            $walk.fields | Select-Object -First 5 | ForEach-Object {
                $val = $_.value ?? $_.hex ?? '<no value>'
                if ($val.Length -gt 60) { $val = $val.Substring(0, 57) + '...' }
                Write-Host ("    +0x{0:X4}  {1,-22}  {2,-30}  {3}" -f $_.offset, $_.name, $_.type, $val)
            }
            if ($walk.fields.Count -gt 5) { Write-Host "    ... ($($walk.fields.Count - 5) more)" -ForegroundColor DarkGray }
        }
    }

    # ---- 8. findMethodInstances Mathf.Abs (Phase 18) ------------------
    Section 'findMethodInstances UnityEngine.Mathf.Abs'
    $meth = Send-Cmd 'findMethodInstances' @{ assembly = 'UnityEngine.CoreModule'; namespace = 'UnityEngine'; name = 'Mathf'; method = 'Abs' }
    if ($meth.error) {
        Write-Host "  ERROR: $($meth.error)" -ForegroundColor Red
    } else {
        Write-Host ("  OK  overloads={0}" -f $meth.count) -ForegroundColor Green
        $meth.matches | Select-Object -First 4 | ForEach-Object {
            Write-Host ("    {0}{1}  ->  {2}" -f $_.name, ($_.signature ?? ''), ($_.address ?? '(no addr)'))
        }
    }

    # ---- 9. callMethod Mathf.Abs(int:-7) (Phase 19) -------------------
    Section 'callMethod UnityEngine.Mathf.Abs(System.Int32)'
    $call = Send-Cmd 'callMethod' @{
        assembly       = 'UnityEngine.CoreModule'
        namespace      = 'UnityEngine'
        name           = 'Mathf'
        method         = 'Abs'
        signatureMatch = '(System.Int32)'
        args           = @( @{ type = 'int32'; value = -7 } )
    }
    if ($call.error) {
        Write-Host "  ERROR: $($call.error)" -ForegroundColor Red
    } elseif ($call.exception) {
        Write-Host ("  EXCEPTION: $($call.exception)") -ForegroundColor Red
    } else {
        $expect = ($call.returnValue -eq 7) ? 'PASS' : 'WRONG VALUE'
        $col = ($expect -eq 'PASS') ? 'Green' : 'Red'
        Write-Host ("  OK  returnType={0}  returnValue={1}  ({2})" -f $call.returnType, $call.returnValue, $expect) -ForegroundColor $col
    }

    # ---- summary -------------------------------------------------------
    Section 'sweep complete'
    Write-Host '  Each line above is one independent runtime path. Anything in red is a real v32+ assumption baked somewhere; everything in green confirms that path is metadata.dat-independent and works on v39.'
}
finally {
    $client.Close()
}
