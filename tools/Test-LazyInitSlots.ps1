<#
.SYNOPSIS
    Phase 28b-v3.1 step 1 — sample lazy-init slots to discover the
    IL2CPP MetadataInitializerEntry layout.

.DESCRIPTION
    Phase 28b-v3.0's `IL2CPPLazyInitSlotScanner` finds every
    `lea reg, [rip+SLOT]; call rel32` 2-instruction sequence in
    GameAssembly.dll's `.text`. The slot at SLOT_ADDR ends up holding
    an Il2CppClass* after `il2cpp_codegen_initialize_method` runs.

    v3.0 maps slot → class by reading the slot's current value (only
    works for already-populated slots). v3.1 needs to map slot → class
    STATICALLY so the never-executed-lambda case works too. To design
    v3.1, we need to know the layout of the metadata-initializer entry
    that the runtime function reads when called via this pattern.

    This script asks the DLL for a sample of N slots and dumps:
      * Slot address + leaIP + callIP + callTarget
      * Current value at the slot (null = never inited; non-null = class ptr)
      * 64 bytes of raw context (16 before + 48 starting at the slot)
      * If the slot's current value is a valid class pointer, the class name

    Useful sample strategy:
      * `-OnlyNonZero` to focus on slots that have been populated —
        we can verify the +0 offset really IS Il2CppClass*.
      * Then run without `-OnlyNonZero` to see what nulls look like —
        if `[slot+8]` or `[slot+16]` carries a small uint32 even when
        slot is null, that's likely typeIndex.

.EXAMPLE
    .\tools\Test-LazyInitSlots.ps1 -OnlyNonZero -Limit 8
    .\tools\Test-LazyInitSlots.ps1 -Limit 16
#>
[CmdletBinding()]
param(
    [int]  $Limit       = 16,
    [int]  $BytesBefore = 16,
    [int]  $BytesAfter  = 48,
    [switch] $OnlyNonZero,
    [int]  $TimeoutMs   = 30000
)

$ErrorActionPreference = 'Stop'

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject the DLL first via Pick & inject.' }
$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Send-Cmd([string] $Cmd, [hashtable] $Body, [int] $Id) {
    $req = @{ id = $Id; cmd = $Cmd; args = $Body } | ConvertTo-Json -Compress -Depth 6
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response for $Cmd" }
    return ($line | ConvertFrom-Json).result
}

# Decode N hex bytes as 8 qwords + raw bytes per line.
function Show-Context([string] $hex, [int] $slotByteOffset, [string] $indent = '         ') {
    if (-not $hex) {
        Write-Host "$indent (no context)" -ForegroundColor DarkGray
        return
    }
    $total = [int]($hex.Length / 2)
    $qwords = [int]($total / 8)
    Write-Host ("$indent context ({0} bytes; slot starts at +0x{1:X2}):" -f $total, $slotByteOffset) -ForegroundColor DarkGray
    for ($qi = 0; $qi -lt $qwords; ++$qi) {
        $byteOff = $qi * 8
        $bytes = New-Object byte[] 8
        for ($bi = 0; $bi -lt 8; ++$bi) {
            $bytes[$bi] = [Convert]::ToByte($hex.Substring(($byteOff + $bi) * 2, 2), 16)
        }
        $v   = [BitConverter]::ToUInt64($bytes, 0)
        $lo32 = [BitConverter]::ToUInt32($bytes, 0)
        $hi32 = [BitConverter]::ToUInt32($bytes, 4)
        $marker = if ($byteOff -eq $slotByteOffset) { '>>' } else { '  ' }
        $colour = if ($byteOff -eq $slotByteOffset) { 'Cyan' } elseif ($byteOff -lt $slotByteOffset) { 'DarkGray' } else { 'Gray' }
        Write-Host ("$indent $marker +0x{0:X2}  0x{1:X16}    lo32=0x{2:X8} ({2,10:N0})   hi32=0x{3:X8}" -f `
            $byteOff, $v, $lo32, $hi32) -ForegroundColor $colour
    }
}

try {
    Write-Host ''
    Write-Host "==[ dumpLazyInitSlots  Limit=$Limit  OnlyNonZero=$OnlyNonZero ]==" -ForegroundColor Green

    $start = Get-Date
    $r = Send-Cmd 'dumpLazyInitSlots' @{
        limit       = $Limit
        bytesBefore = $BytesBefore
        bytesAfter  = $BytesAfter
        onlyNonZero = [bool]$OnlyNonZero
    } 1
    $elapsed = ((Get-Date) - $start).TotalMilliseconds

    if ($r.error) {
        Write-Host "ERROR: $($r.error)" -ForegroundColor Red
        return
    }

    Write-Host ("elapsed           : {0:N0} ms"     -f $elapsed)
    Write-Host ("totalSlots        : {0:N0}"        -f $r.totalSlots)
    Write-Host ("nonZeroSlots      : {0:N0}  ({1:N1}%)" -f $r.nonZeroSlots, ($r.nonZeroSlots * 100.0 / [math]::Max(1, $r.totalSlots)))
    Write-Host ("pointerLikeSlots  : {0:N0}  ({1:N1}%)" -f $r.pointerLikeSlots, ($r.pointerLikeSlots * 100.0 / [math]::Max(1, $r.totalSlots)))
    Write-Host ("returned          : {0}"           -f $r.returned)
    Write-Host ""

    $i = 0
    foreach ($s in $r.slots) {
        $i++
        $loc = $s.valueLocation
        $marker = switch ($loc) {
            'heap'             { '✓' }
            'in-module'        { '·' }
            'non-pointer-like' { '?' }
            default            { '·' }
        }
        $colour = switch ($loc) {
            'heap'             { 'Cyan' }
            'in-module'        { 'DarkGray' }
            'non-pointer-like' { 'Yellow' }
            default            { 'DarkGray' }
        }
        Write-Host ("[$i] $marker  slot={0}  cur={1}  ({2})" -f $s.slotAddress, $s.currentValue, $loc) -ForegroundColor $colour
        Write-Host ("    leaIP={0}  callIP={1}  callTarget={2}" -f $s.leaIP, $s.callIP, $s.callTarget) -ForegroundColor DarkGray
        if ($s.token) {
            $t = $s.token
            $resolved = if ($t.resolvedClassPtr) { " -> Il2CppClass*={0}" -f $t.resolvedClassPtr } else { '' }
            Write-Host ("    token: kind={0} ({1})  index={2}{3}" -f `
                $t.kind, $t.kindName, $t.index, $resolved) -ForegroundColor Magenta
        }
        Show-Context $s.contextHex $s.contextSlotOffset
        Write-Host ""
    }

    Write-Host '==[ NOTES ]==' -ForegroundColor Green
    Write-Host '- ">>" marks the slot itself (offset 0 of the candidate metadata-initializer entry).'
    Write-Host '- valueLocation: "heap" = currentValue points outside GameAssembly.dll (likely Il2CppClass*).'
    Write-Host '                "in-module" = points into the module (likely a string literal / metadata constant — NOT a class).'
    Write-Host '                "non-pointer-like" = small integer or misaligned (probably not even a pointer).'
    Write-Host '- Focus on "heap" entries: those are most likely real lazy-init class-pointer cache slots.'
    Write-Host '- Look at +0x8 / +0x10 / +0x18 (qwords after the slot): if any field is a small uint32 that varies'
    Write-Host '  between slots and stays < 100_000, that is likely a typeIndex into Il2CppMetadataRegistration->types[].'
}
finally {
    $client.Close()
}
