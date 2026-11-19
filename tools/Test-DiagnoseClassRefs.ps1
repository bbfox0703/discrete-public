<#
.SYNOPSIS
    Phase 28b-v3 investigation — dump every storage slot holding a class's
    Il2CppClass* and static_fields pointer.

.DESCRIPTION
    Designed to determine empirically how compiler-generated `<>c`
    lambda-cache classes are referenced from AOT IL2CPP code. The Phase
    28b-v2 ClassPointerScanner returns 0 module slots for these — the
    diagnostic shows whether the class pointer lives anywhere readable
    (module .data/.bss/.rdata, runtime-heap metadata-usage tables, etc.)
    along with 64 bytes of surrounding context so we can tell what kind
    of table the slot is part of.

    Typical usage: run against a working class first (e.g. SteamManager,
    which Phase 28b-v2 chain-detected successfully) to see the "expected"
    layout, then against `<>c` to see what's different.

.PARAMETER Assembly / Namespace / Class
    Target class to diagnose. Defaults to the SaGa F2R `<>c` lambda-cache
    class the Phase 28b-v2 investigation surfaced.

.PARAMETER HeapScan
    Toggle the committed-RW heap pass (default $true). False skips heap
    and reports module-only — much faster but misses runtime tables.

.PARAMETER HeapLimit
    Per-pointer cap on heap slots (default 64). Stops the diagnostic from
    running away on a process with thousands of GC roots holding the
    pointer.

.EXAMPLE
    # SaGa F2R `<>c` lambda cache (the case Phase 28b-v2 returns 0 for):
    .\tools\Test-DiagnoseClassRefs.ps1 -Class 'MainGame/StrReader2UShortAry/<>c'

    # Comparison baseline — a class Phase 28b-v2 chain-detected fine:
    .\tools\Test-DiagnoseClassRefs.ps1 -Class 'SteamManager'

.EXAMPLE
    # Module-only fast scan:
    .\tools\Test-DiagnoseClassRefs.ps1 -Class 'SteamManager' -HeapScan:$false
#>
[CmdletBinding()]
param(
    [string] $Assembly  = 'Assembly-CSharp',
    [string] $Namespace = '',
    [Parameter(Mandatory = $true)]
    [string] $Class,
    [string] $FieldName = '',          # optional — also resolve + scan for FieldInfo*
    [bool]   $HeapScan  = $true,
    [int]    $HeapLimit = 64,
    [int]    $TimeoutMs = 30000
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

# Pretty-print 128 hex chars as 8 qwords of LE-decoded pointers.
function Show-Context([string] $hex, [int] $slotByteOffset, [string] $indent = '         ') {
    if (-not $hex) { return }
    $total = [int]($hex.Length / 2)
    $qwords = [int]($total / 8)
    Write-Host ("$indent context (slot at byte +0x{0:X2}, total {1} bytes):" -f $slotByteOffset, $total) -ForegroundColor DarkGray
    for ($qi = 0; $qi -lt $qwords; ++$qi) {
        $byteOff = $qi * 8
        # Extract 8 bytes in little-endian, reassemble as a u64.
        $bytes = New-Object byte[] 8
        for ($bi = 0; $bi -lt 8; ++$bi) {
            $bytes[$bi] = [Convert]::ToByte($hex.Substring(($byteOff + $bi) * 2, 2), 16)
        }
        $v = [BitConverter]::ToUInt64($bytes, 0)
        $marker = if ($byteOff -eq $slotByteOffset) { '>>' } else { '  ' }
        $color = if ($byteOff -eq $slotByteOffset) { 'Cyan' } else { 'DarkGray' }
        Write-Host ("$indent $marker +0x{0:X2}  0x{1:X16}" -f $byteOff, $v) -ForegroundColor $color
    }
}

try {
    $fqn = if ($Namespace) { "$Namespace.$Class" } else { $Class }
    Write-Host ''
    Write-Host "==[ diagnoseClassReferences  $fqn ]==" -ForegroundColor Green

    $start = Get-Date
    $r = Send-Cmd 'diagnoseClassReferences' @{
        assembly  = $Assembly
        ns        = $Namespace
        class     = $Class
        fieldName = $FieldName
        heapScan  = $HeapScan
        heapLimit = $HeapLimit
    } 1
    $elapsed = ((Get-Date) - $start).TotalMilliseconds

    if ($r.error) {
        Write-Host "ERROR: $($r.error)" -ForegroundColor Red
        return
    }

    Write-Host ("elapsed                   : {0:N0} ms" -f $elapsed)
    Write-Host ("classPtr                  : {0}"       -f $r.classPtr)
    Write-Host ("staticFieldsPtr           : {0}"       -f $r.staticFieldsPtr)
    Write-Host ("staticFieldsOffsetInClass : 0x{0:X}"   -f $r.staticFieldsOffsetInClass)
    if ($r.targetFieldName) {
        Write-Host ("targetFieldName           : {0}"   -f $r.targetFieldName)
        Write-Host ("targetFieldInfoPtr        : {0}"   -f $r.targetFieldInfoPtr)
        Write-Host ("targetFieldOffset         : 0x{0:X}" -f $r.targetFieldOffset)
    }
    Write-Host ("module base / size        : {0} / {1:N0}" -f $r.module.base, $r.module.size)
    Write-Host ""
    Write-Host ("sections ({0}):" -f $r.module.sections.Count) -ForegroundColor DarkCyan
    foreach ($s in $r.module.sections) {
        $flags = if ($s.isExecutable) { 'X' } else { '-' }
        $flags += if ($s.isWritable)   { 'W' } else { '-' }
        Write-Host ("  {0,-10} {1}  size={2,12:N0}  {3}" -f $s.name, $s.va, $s.size, $flags) -ForegroundColor DarkGray
    }

    Write-Host ""
    Write-Host ("moduleClassPtrSlots     : {0}" -f $r.moduleClassPtrSlots.Count)     -ForegroundColor Cyan
    foreach ($s in $r.moduleClassPtrSlots) {
        Write-Host ("  {0}  section={1}" -f $s.address, $s.section) -ForegroundColor White
        Show-Context $s.contextHex $s.contextSlotOffset
    }

    Write-Host ""
    Write-Host ("moduleStaticFieldsSlots : {0}" -f $r.moduleStaticFieldsSlots.Count) -ForegroundColor Cyan
    foreach ($s in $r.moduleStaticFieldsSlots) {
        Write-Host ("  {0}  section={1}" -f $s.address, $s.section) -ForegroundColor White
        Show-Context $s.contextHex $s.contextSlotOffset
    }

    if ($r.targetFieldName) {
        Write-Host ""
        Write-Host ("moduleFieldInfoSlots    : {0}  (FieldInfo* for {1}.{2})" -f `
            $r.moduleFieldInfoSlots.Count, $r.classFqn, $r.targetFieldName) `
            -ForegroundColor Magenta
        foreach ($s in $r.moduleFieldInfoSlots) {
            Write-Host ("  {0}  section={1}" -f $s.address, $s.section) -ForegroundColor White
            Show-Context $s.contextHex $s.contextSlotOffset
        }
    }

    Write-Host ""
    Write-Host ("heapClassPtrSlots       : {0}{1}" -f $r.heapClassPtrSlots.Count, `
                $(if ($r.stats.heapCapHit) { ' (cap hit — more exist)' } else { '' })) -ForegroundColor Cyan
    foreach ($s in $r.heapClassPtrSlots) {
        $prot = '0x{0:X4}' -f [uint32]$s.regionProtect
        $typeStr = switch ($s.regionType) {
            0x1000000 { 'IMAGE'   }
            0x40000   { 'MAPPED'  }
            0x20000   { 'PRIVATE' }
            default   { '0x{0:X}' -f $s.regionType }
        }
        Write-Host ("  {0}  region={1}..+{2:X}  prot={3}  type={4}" -f `
            $s.address, $s.regionBase, $s.regionSize, $prot, $typeStr) -ForegroundColor White
        Show-Context $s.contextHex $s.contextSlotOffset
    }

    Write-Host ""
    Write-Host ("heapStaticFieldsSlots   : {0}{1}" -f $r.heapStaticFieldsSlots.Count, `
                $(if ($r.stats.heapCapHit) { ' (cap hit — more exist)' } else { '' })) -ForegroundColor Cyan
    foreach ($s in $r.heapStaticFieldsSlots) {
        $prot = '0x{0:X4}' -f [uint32]$s.regionProtect
        $typeStr = switch ($s.regionType) {
            0x1000000 { 'IMAGE'   }
            0x40000   { 'MAPPED'  }
            0x20000   { 'PRIVATE' }
            default   { '0x{0:X}' -f $s.regionType }
        }
        Write-Host ("  {0}  region={1}..+{2:X}  prot={3}  type={4}" -f `
            $s.address, $s.regionBase, $s.regionSize, $prot, $typeStr) -ForegroundColor White
        Show-Context $s.contextHex $s.contextSlotOffset
    }

    if ($r.targetFieldName) {
        Write-Host ""
        Write-Host ("heapFieldInfoSlots      : {0}  (FieldInfo* for {1}.{2})" -f `
            $r.heapFieldInfoSlots.Count, $r.classFqn, $r.targetFieldName) -ForegroundColor Magenta
        foreach ($s in $r.heapFieldInfoSlots) {
            $prot = '0x{0:X4}' -f [uint32]$s.regionProtect
            $typeStr = switch ($s.regionType) {
                0x1000000 { 'IMAGE'   }
                0x40000   { 'MAPPED'  }
                0x20000   { 'PRIVATE' }
                default   { '0x{0:X}' -f $s.regionType }
            }
            Write-Host ("  {0}  region={1}..+{2:X}  prot={3}  type={4}" -f `
                $s.address, $s.regionBase, $s.regionSize, $prot, $typeStr) -ForegroundColor White
            Show-Context $s.contextHex $s.contextSlotOffset
        }
    }

    Write-Host ""
    Write-Host "==[ stats ]==" -ForegroundColor Green
    Write-Host ("module bytes scanned : {0:N0}" -f $r.stats.moduleBytesScanned)
    Write-Host ("heap regions scanned : {0}"    -f $r.stats.heapRegionsScanned)
    Write-Host ("heap regions skipped : {0}"    -f $r.stats.heapRegionsSkipped)
    Write-Host ("heap bytes scanned   : {0:N0}" -f $r.stats.heapBytesScanned)
    Write-Host ("heap cap hit         : {0}"    -f $r.stats.heapCapHit)

    Write-Host ""
    Write-Host '==[ classStruct first 256 bytes (qwords) ]==' -ForegroundColor Green
    if ($r.classStructHex) {
        Show-Context $r.classStructHex -1 '  '
    } else {
        Write-Host '  (unreadable)' -ForegroundColor DarkGray
    }

    # Phase 28b-v3 — Navigation candidates. These are AOT method
    # pointers extracted from heap class-ptr slots that look like
    # MethodInfo entries. Each is an address inside GameAssembly.dll's
    # executable sections — exactly what we need to disasm to figure
    # out how `<>c`-style classes are referenced at the .text level.
    #
    # Pulls 64 bytes of raw machine code via the existing Phase 18
    # `getMethodAobPattern` RPC so you don't have to alt-tab to CE
    # just to grab the disasm bytes. Paste the hex into any x64
    # disassembler (or share with me) to identify the access pattern.
    Write-Host ""
    Write-Host '==[ navigation candidates (AOT methods that reference this class) ]==' -ForegroundColor Green
    if (-not $r.navigationCandidates -or $r.navigationCandidates.Count -eq 0) {
        Write-Host '  (none — no heap MethodInfo entries found pointing into the module exec sections)' -ForegroundColor DarkGray
    } else {
        Write-Host ("  Found {0} candidate AOT method(s). Pulling 64 bytes of code from each:" -f `
            $r.navigationCandidates.Count) -ForegroundColor DarkCyan
        $idCnt = 1000
        foreach ($cand in $r.navigationCandidates) {
            Write-Host ""
            Write-Host ("  methodPointer = {0}    (surfaced from heap slot {1})" -f `
                $cand.methodPointer, $cand.slotAddress) -ForegroundColor Cyan
            $aob = Send-Cmd 'getMethodAobPattern' @{
                address = $cand.methodPointer
                length  = 128
            } $idCnt
            $idCnt++
            if ($aob.error) {
                Write-Host ("    (getMethodAobPattern error: {0})" -f $aob.error) -ForegroundColor Yellow
                continue
            }
            # RPC response fields: `bytes` (raw hex), `pattern` (CE AOB
            # with `??` wildcards), `moduleName`, `moduleOffset`.
            if ($aob.moduleName) {
                Write-Host ("    {0} + {1}" -f $aob.moduleName, $aob.moduleOffset) -ForegroundColor DarkGray
            }
            if ($aob.bytes) {
                Write-Host '    raw bytes (paste into a disassembler):' -ForegroundColor DarkGray
                # Pretty-print 16 bytes per line.
                for ($off = 0; $off -lt $aob.bytes.Length; $off += 32) {
                    $take = [Math]::Min(32, $aob.bytes.Length - $off)
                    $line = $aob.bytes.Substring($off, $take)
                    # Space every byte.
                    $spaced = ($line -split '(.{2})' | Where-Object { $_ -ne '' }) -join ' '
                    Write-Host ("      +0x{0:X2}  {1}" -f ($off / 2), $spaced) -ForegroundColor White
                }
            }
            if ($aob.pattern) {
                Write-Host ("    AOB pattern: {0}" -f $aob.pattern) -ForegroundColor DarkGray
            }
        }
        Write-Host ""
        Write-Host '  > Send me the raw-bytes hex from any 2-3 of those candidates.' -ForegroundColor Green
        Write-Host '  > I will decode them to identify the v3 access-chain pattern.' -ForegroundColor Green
    }
}
finally {
    $client.Close()
}
