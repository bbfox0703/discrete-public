<#
.SYNOPSIS
    Phase 20a-2 verification — replay the Object.FindObjectsOfType(Type, bool)
    recipe end-to-end via raw pipe RPCs (no CE / Lua needed).

.DESCRIPTION
    `unity_scene.lua` pivoted in Phase 20a to use Unity's authoritative
    enumeration API for GameObjects because the findInstances heap-scan
    heuristic was returning IL2CPP RGCTX metadata records as false positives.

    The 2-arg overload `UnityEngine.Object.FindObjectsOfType(Type type,
    bool includeInactive)` was the only one that survived IL2CPP code stripping
    on SaGa F2R (Unity 2021, 2026-05-08); the recipe was wired into the Lua
    bridge but the session ended before end-to-end verification.

    This smoke replays the 4-step recipe over the DLL pipe:

      1. System.Type.GetType("UnityEngine.GameObject, UnityEngine.CoreModule")
         → typeAddr (managed Type instance)
      2. UnityEngine.Object.FindObjectsOfType(typeAddr, true)
         sig=(System.Type, System.Boolean)  → managed GameObject[] address
      3. walkArray(arrAddr, limit=$Limit)   → element pointers (the actual
         GameObject instances)
      4. For first $SampleNames elements: Object.get_name(addr) → human name

    PASS criteria (all must hold):
      • Step 1 returns ok=true, returnType=System.Type, non-null returnValue
      • Step 2 returns ok=true, returnType="UnityEngine.Object[]"-ish,
        non-null + non-zero returnValue
      • Step 3 returns count>0 with non-zero ptr per element
      • Step 4 returns plausible names ("Main Camera", "Canvas", etc.) — at
        least 1 of the sampled returns a non-empty string with no
        native_crash_in_runtime_invoke

    Recommended target order:
      • BongoCat (Mono)         — fast mechanical check (Mono never strips)
      • SaGa F2R (IL2CPP / Unity 2021) — the canonical stripping-survival test

.PARAMETER Limit
    Max GameObjects to walk from the returned array. Default 32 (small, fast).
    Bump to 256+ to stress the recipe.

.PARAMETER SampleNames
    Number of leading elements to fetch names for. Default 5. Set to 0 to skip
    step 4 entirely (useful if you just want to confirm the array is reachable
    without paying per-element callMethod cost).

.PARAMETER TimeoutMs
    Per-RPC pipe timeout in ms. Default 10000. Bump for slow targets.

.EXAMPLE
    .\tools\Test-FindObjectsOfType.ps1
    .\tools\Test-FindObjectsOfType.ps1 -Limit 256 -SampleNames 10
    .\tools\Test-FindObjectsOfType.ps1 -SampleNames 0   # array reachability only
#>
[CmdletBinding()]
param(
    [int] $Limit       = 32,
    [int] $SampleNames = 5,
    [int] $TimeoutMs   = 10000
)

$ErrorActionPreference = 'Stop'

# ---------- Helpers ----------------------------------------------------------

# `callMethod` over the pipe. PowerShell's ConvertTo-Json eats single-element
# array properties, so the args field is spliced in as raw text — same pattern
# Test-CallMethod.ps1 uses.
function Invoke-CallMethod {
    param(
        [Parameter(Mandatory)] [System.IO.StreamWriter] $Writer,
        [Parameter(Mandatory)] [System.IO.StreamReader] $Reader,
        [Parameter(Mandatory)] [string] $ClassAssembly,
        [Parameter(Mandatory)] [string] $Namespace,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Method,
        [string] $SignatureMatch  = '',
        [string] $InstanceAddress = '',
        [Parameter(Mandatory)] [string] $ArgsJson,
        [int]    $Id = 1
    )
    $body = @{
        assembly  = $ClassAssembly
        namespace = $Namespace
        name      = $Name
        method    = $Method
    }
    if ($SignatureMatch)  { $body.signatureMatch = $SignatureMatch }
    if ($InstanceAddress) { $body.instance       = $InstanceAddress }
    $bodyJson = ($body | ConvertTo-Json -Compress)
    $bodyJson = $bodyJson.Substring(0, $bodyJson.Length - 1) + ',"args":' + $ArgsJson + '}'
    $req = '{"id":' + $Id + ',"cmd":"callMethod","args":' + $bodyJson + '}'
    $Writer.WriteLine($req)
    $line = $Reader.ReadLine()
    if (-not $line) { throw 'Empty response from callMethod' }
    return ($line | ConvertFrom-Json).result
}

function Invoke-FindMethodInstances {
    param(
        [Parameter(Mandatory)] [System.IO.StreamWriter] $Writer,
        [Parameter(Mandatory)] [System.IO.StreamReader] $Reader,
        [Parameter(Mandatory)] [string] $ClassAssembly,
        [Parameter(Mandatory)] [string] $Namespace,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Method,
        [int] $Id = 1
    )
    $req = @{
        id   = $Id
        cmd  = 'findMethodInstances'
        args = @{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name; method = $Method }
    } | ConvertTo-Json -Compress
    $Writer.WriteLine($req)
    $line = $Reader.ReadLine()
    if (-not $line) { throw 'Empty response from findMethodInstances' }
    return ($line | ConvertFrom-Json).result
}

function Invoke-WalkArray {
    param(
        [Parameter(Mandatory)] [System.IO.StreamWriter] $Writer,
        [Parameter(Mandatory)] [System.IO.StreamReader] $Reader,
        [Parameter(Mandatory)] [string] $Address,
        [int] $Limit = 256,
        [int] $Id    = 1
    )
    $req = @{
        id   = $Id
        cmd  = 'walkArray'
        args = @{ address = $Address; limit = $Limit }
    } | ConvertTo-Json -Compress
    $Writer.WriteLine($req)
    $line = $Reader.ReadLine()
    if (-not $line) { throw 'Empty response from walkArray' }
    return ($line | ConvertFrom-Json).result
}

function Format-Hex64 { param([uint64] $v) return ('0x{0:X16}' -f $v) }
function Parse-Hex    {
    param([string] $s)
    if ([string]::IsNullOrWhiteSpace($s)) { return 0UL }
    if ($s -match '^0[xX]') { $s = $s.Substring(2) }
    return [System.Convert]::ToUInt64($s, 16)
}

function Assert-Pass { param([bool] $cond, [string] $label)
    if ($cond) {
        Write-Host ('  PASS  ' + $label) -ForegroundColor Green
    } else {
        Write-Host ('  FAIL  ' + $label) -ForegroundColor Red
        $script:Failed = $true
    }
}

# ---------- Connect ----------------------------------------------------------

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Test-Handshake.ps1.' }
# NamedPipeClientStream doesn't support ReadTimeout/WriteTimeout — Connect()
# handles connect-side timeout; per-call reads are blocking. If the DLL hangs,
# Ctrl-C the host.

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

$script:Failed = $false

try {
    Write-Host ''
    Write-Host '==[ Phase 20a-2 — Object.FindObjectsOfType(Type, bool) recipe ]==' -ForegroundColor Green
    Write-Host ('  Limit={0}  SampleNames={1}  TimeoutMs={2}' -f $Limit, $SampleNames, $TimeoutMs) -ForegroundColor DarkGray
    Write-Host ''

    # ---------- Step 1: typeAddr = System.Type.GetType("UnityEngine.GameObject, UnityEngine.CoreModule")
    Write-Host '[Step 1] System.Type.GetType("UnityEngine.GameObject, UnityEngine.CoreModule")' -ForegroundColor Cyan
    $r1 = Invoke-CallMethod -Writer $writer -Reader $reader `
        -ClassAssembly mscorlib -Namespace System -Name Type -Method GetType `
        -SignatureMatch '(System.String)' `
        -ArgsJson '[{"type":"string","value":"UnityEngine.GameObject, UnityEngine.CoreModule"}]' `
        -Id 1
    if ($r1.error)     { Write-Host ('  ERROR: ' + $r1.error) -ForegroundColor Red; throw 'Step 1 failed' }
    if ($r1.exception) { Write-Host ('  EXCEPTION: ' + $r1.exception) -ForegroundColor Magenta; throw 'Step 1 threw managed exception' }
    $typeAddrHex = $r1.returnValue
    $typeAddr    = Parse-Hex $typeAddrHex
    Write-Host ('  returnType : {0}' -f $r1.returnType) -ForegroundColor DarkGray
    Write-Host ('  returnValue: {0}' -f $typeAddrHex)   -ForegroundColor Yellow
    Assert-Pass ($r1.ok -eq $true)            'ok=true'
    Assert-Pass ($typeAddr -ne 0)             'typeAddr != 0'
    Write-Host ''

    if ($typeAddr -eq 0) { throw 'Cannot continue without typeAddr' }

    # ---------- Step 2a: probe overloads first so the smoke is target-aware
    # Unity API surface changes per version:
    #   2018-2021 : FindObjectsOfType(Type) + FindObjectsOfType(Type, bool)
    #   2022+     : adds FindObjectsByType<T>(FindObjectsInactive, FindObjectsSortMode)
    #               + FindObjectsByType<T>(FindObjectsSortMode); the old
    #               FindObjectsOfType overloads stay but may be marked obsolete
    #               or have signature variants we can't anticipate.
    # By enumerating real overloads first, the smoke adapts to whatever the
    # injected runtime actually exposes and prints a clear menu when nothing
    # matches.
    Write-Host '[Step 2a] findMethodInstances UnityEngine.Object.FindObjectsOfType' -ForegroundColor Cyan
    $probe = Invoke-FindMethodInstances -Writer $writer -Reader $reader `
        -ClassAssembly UnityEngine.CoreModule -Namespace UnityEngine -Name Object `
        -Method FindObjectsOfType -Id 20
    $overloads = @()
    if ($probe.error)      { Write-Host ('  probe error: ' + $probe.error) -ForegroundColor DarkYellow }
    elseif ($probe.matches){ $overloads = @($probe.matches) }
    Write-Host ('  overload count: {0}' -f $overloads.Count) -ForegroundColor DarkGray
    foreach ($ov in $overloads) {
        Write-Host ('    {0,-40} static={1}  @ {2}' -f $ov.signature, $ov.isStatic, $ov.address) -ForegroundColor DarkGray
    }
    # Bonus probe — newer-Unity replacement so the user sees it if it's the
    # only surviving path.
    Write-Host '[Step 2a] findMethodInstances UnityEngine.Object.FindObjectsByType (Unity 2022+ replacement)' -ForegroundColor Cyan
    $probe2 = Invoke-FindMethodInstances -Writer $writer -Reader $reader `
        -ClassAssembly UnityEngine.CoreModule -Namespace UnityEngine -Name Object `
        -Method FindObjectsByType -Id 21
    if ($probe2.matches -and @($probe2.matches).Count -gt 0) {
        foreach ($ov in @($probe2.matches)) {
            Write-Host ('    {0,-60} static={1}  @ {2}' -f $ov.signature, $ov.isStatic, $ov.address) -ForegroundColor DarkGray
        }
    } else {
        Write-Host ('    (none — target is pre-2022 or this method was stripped)') -ForegroundColor DarkGray
    }
    Write-Host ''

    # ---------- Step 2b: pick the best surviving overload and call it
    # Priority order (matches expected Unity API surface across versions):
    #   1. (System.Type, System.Boolean)            — Unity 2018–2021 sweet spot
    #   2. (System.Type)                             — older / 1-arg fallback
    #   3. FindObjectsByType (System.Type, FindObjectsInactive, FindObjectsSortMode)
    #                                                — Unity 2022+ replacement.
    #                                                  Enum args marshaled as
    #                                                  int32 (ABI-identical
    #                                                  underlying type).
    #                                                  Include=1, None=0.
    $picked       = $null
    $pickedMethod = 'FindObjectsOfType'
    $pickedArgs   = ''
    $pickedNote   = ''
    foreach ($ov in $overloads) {
        if ($ov.signature -eq '(System.Type, System.Boolean)') {
            $picked       = $ov
            $pickedArgs   = '[{"type":"address","value":"' + $typeAddrHex + '"},{"type":"bool","value":true}]'
            $pickedNote   = '2-arg (Type, bool) — Unity 2018-2021 path'
            break
        }
    }
    if (-not $picked) {
        foreach ($ov in $overloads) {
            if ($ov.signature -eq '(System.Type)') {
                $picked       = $ov
                $pickedArgs   = '[{"type":"address","value":"' + $typeAddrHex + '"}]'
                $pickedNote   = '1-arg (Type) — older fallback; misses inactive'
                break
            }
        }
    }
    if (-not $picked) {
        # Last resort: Unity 2022+ replacement API. We need the 3-arg compiled
        # overload (the others are generic and have address=0x0). Pass Include=1
        # and None=0 as int32 — enum ABI is its underlying integer type.
        $byType = $null
        if ($probe2.matches) {
            foreach ($ov in @($probe2.matches)) {
                if ($ov.signature -eq '(System.Type, UnityEngine.FindObjectsInactive, UnityEngine.FindObjectsSortMode)' -and
                    (Parse-Hex $ov.address) -ne 0) {
                    $byType = $ov; break
                }
            }
        }
        if ($byType) {
            $picked       = $byType
            $pickedMethod = 'FindObjectsByType'
            $pickedArgs   = '[{"type":"address","value":"' + $typeAddrHex + '"},' +
                            '{"type":"int32","value":1},' +
                            '{"type":"int32","value":0}]'
            $pickedNote   = 'Unity 2022+ FindObjectsByType (Type, Include=1, None=0) — enum args as int32'
        }
    }

    Write-Host '[Step 2b] Call selected overload' -ForegroundColor Cyan
    if (-not $picked) {
        Write-Host '  No usable enumeration overload found.' -ForegroundColor Red
        Write-Host '  → Phase 20a-2 recipe does NOT work on this target as-shipped.' -ForegroundColor Red
        Write-Host '  Both FindObjectsOfType and FindObjectsByType are unreachable.' -ForegroundColor DarkYellow
        Write-Host '  Fall back to Phase 20-HEAP (DLL-side libgc walker).' -ForegroundColor DarkYellow
        $script:Failed = $true
        Write-Host ''
        Write-Host '==[ VERDICT: FAIL ]==' -ForegroundColor Red
        exit 1
    }

    Write-Host ('  method : UnityEngine.Object.' + $pickedMethod) -ForegroundColor Yellow
    Write-Host ('  picked : ' + $picked.signature) -ForegroundColor Yellow
    Write-Host ('  note   : ' + $pickedNote) -ForegroundColor DarkGray
    $r2 = Invoke-CallMethod -Writer $writer -Reader $reader `
        -ClassAssembly UnityEngine.CoreModule -Namespace UnityEngine -Name Object -Method $pickedMethod `
        -SignatureMatch $picked.signature `
        -ArgsJson $pickedArgs `
        -Id 22
    if ($r2.error)     { Write-Host ('  ERROR: ' + $r2.error) -ForegroundColor Red; throw 'Step 2b failed' }
    if ($r2.exception) { Write-Host ('  EXCEPTION: ' + $r2.exception) -ForegroundColor Magenta; throw 'Step 2b threw managed exception' }
    $arrAddrHex = $r2.returnValue
    $arrAddr    = Parse-Hex $arrAddrHex
    Write-Host ('  returnType : {0}' -f $r2.returnType) -ForegroundColor DarkGray
    Write-Host ('  returnValue: {0}' -f $arrAddrHex)    -ForegroundColor Yellow
    Assert-Pass ($r2.ok -eq $true)  'ok=true'
    Assert-Pass ($arrAddr -ne 0)    'arrAddr != 0  (overload returned a real array)'
    Write-Host ''

    if ($arrAddr -eq 0) { throw 'Cannot continue without arrAddr' }

    # ---------- Step 3: walkArray(arrAddr, limit=$Limit)
    Write-Host ('[Step 3] walkArray(' + $arrAddrHex + ', limit=' + $Limit + ')') -ForegroundColor Cyan
    $r3 = Invoke-WalkArray -Writer $writer -Reader $reader -Address $arrAddrHex -Limit $Limit -Id 3
    if ($r3.error) { Write-Host ('  ERROR: ' + $r3.error) -ForegroundColor Red; throw 'Step 3 failed' }
    $elements = @($r3.elements)
    Write-Host ('  length      : {0}' -f $r3.length)       -ForegroundColor DarkGray
    Write-Host ('  emitted     : {0}' -f $r3.count)        -ForegroundColor Yellow
    Write-Host ('  element_size: {0}  is_pointer={1}' -f $r3.element_size, $r3.is_pointer) -ForegroundColor DarkGray
    Assert-Pass ($elements.Count -gt 0)                                  'walkArray returned at least 1 element'
    $nonZeroPtrs = @($elements | Where-Object { (Parse-Hex $_.ptr) -ne 0 }).Count
    Assert-Pass ($nonZeroPtrs -gt 0)                                     'at least one element has non-zero ptr'
    Write-Host ''

    # ---------- Step 4: name decode for first $SampleNames elements
    if ($SampleNames -gt 0 -and $elements.Count -gt 0) {
        Write-Host ('[Step 4] Object.get_name() on first {0} elements' -f $SampleNames) -ForegroundColor Cyan
        $namedOk    = 0
        $crashedOn  = 0
        $sampleN    = [Math]::Min($SampleNames, $elements.Count)
        for ($i = 0; $i -lt $sampleN; ++$i) {
            $e = $elements[$i]
            $instAddr = $e.ptr
            if (-not $instAddr -or (Parse-Hex $instAddr) -eq 0) {
                Write-Host ('  {0,3}: <zero ptr> (skipping)' -f $i) -ForegroundColor DarkGray
                continue
            }
            $rN = Invoke-CallMethod -Writer $writer -Reader $reader `
                -ClassAssembly UnityEngine.CoreModule -Namespace UnityEngine -Name Object -Method get_name `
                -SignatureMatch '()' `
                -InstanceAddress $instAddr `
                -ArgsJson '[]' `
                -Id (100 + $i)
            $tag   = ''
            $color = 'White'
            if ($rN.error -and $rN.error -match 'native_crash_in_runtime_invoke') {
                $tag = '<native_crash>'; $color = 'Magenta'; $crashedOn++
            } elseif ($rN.error) {
                $tag = '<err: ' + $rN.error + '>'; $color = 'Red'
            } elseif ($rN.exception) {
                $tag = '<exc: ' + $rN.exception + '>'; $color = 'Magenta'
            } elseif ($rN.returnValue) {
                $tag = '"' + $rN.returnValue + '"'; $color = 'Yellow'; $namedOk++
            } else {
                $tag = '<null>'; $color = 'DarkGray'
            }
            Write-Host ('  {0,3}: {1}  {2}' -f $i, $instAddr, $tag) -ForegroundColor $color
        }
        Write-Host ''
        Assert-Pass ($namedOk -gt 0)                                  ('{0}/{1} elements got plausible names' -f $namedOk, $sampleN)
        Assert-Pass ($crashedOn -eq 0)                                ('{0}/{1} elements native_crashed (want 0)' -f $crashedOn, $sampleN)
        Write-Host ''
    } else {
        Write-Host '[Step 4] skipped (SampleNames=0 or no elements)' -ForegroundColor DarkGray
        Write-Host ''
    }

    # ---------- Verdict
    if ($script:Failed) {
        Write-Host '==[ VERDICT: FAIL ]==' -ForegroundColor Red
        Write-Host 'One or more assertions failed. If Step 2 returned a zero arrAddr, the 2-arg' -ForegroundColor Red
        Write-Host 'overload is stripped on this target — fall back to Phase 20-HEAP.' -ForegroundColor Red
        exit 1
    } else {
        Write-Host '==[ VERDICT: PASS ]==' -ForegroundColor Green
        Write-Host 'Phase 20a-2 recipe is mechanically sound on this target.' -ForegroundColor Green
        Write-Host 'If running on Mono, also verify on an IL2CPP target (e.g. SaGa F2R) before' -ForegroundColor DarkGray
        Write-Host 'closing Phase 20a-2 — Mono never strips, so this only validates wire shape.' -ForegroundColor DarkGray
    }
}
finally {
    $client.Close()
}
