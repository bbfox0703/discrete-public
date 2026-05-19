<#
.SYNOPSIS
    Phase 21a smoke — write a primitive field via setInstanceField and verify
    via readMemory roundtrip.

.DESCRIPTION
    Backend-agnostic smoke for the new setInstanceField pipe RPC. Saves the
    current bytes at instance+offset via readMemory, calls setInstanceField
    with the supplied type+value, reads the field back, then (unless
    -NoRestore) writes the original bytes back so the smoke is non-destructive.

    Combine with Test-FindInstances.ps1 to get an instance address and
    Test-WalkInstance.ps1 to inspect field offsets.

.PARAMETER Address
    Instance start address (hex "0x..." or integer).

.PARAMETER FieldOffset
    Cumulative byte offset from the instance base to the target field. Pass
    0 if Address already points at the field storage.

.PARAMETER Type
    Wire-type tag. One of: bool, sbyte/byte, int16/uint16, int32/uint32,
    int64/uint64, float, double, char, address. CLR names like System.Int32
    also accepted.

.PARAMETER Value
    The value to write. Numeric for numeric types, $true/$false or 0/1 for
    bool, hex string or integer for address.

.PARAMETER NoRestore
    Skip restoring the original bytes after verification. Use when you
    intentionally want the new value persisted.

.EXAMPLE
    # Write 999 into PLY_WORK.HP (Int32 at offset 0x28) and restore afterward
    .\tools\Test-SetInstanceField.ps1 -Address 0x1234ABCD0 -FieldOffset 0x28 `
        -Type int32 -Value 999

    # Persist a float change
    .\tools\Test-SetInstanceField.ps1 -Address 0x1234ABCD0 -FieldOffset 0x10 `
        -Type float -Value 3.14 -NoRestore
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)] [string] $Address,
    [Parameter(Mandatory=$true)] [int]    $FieldOffset,
    [Parameter(Mandatory=$true)] [string] $Type,
    [Parameter(Mandatory=$true)]          $Value,
    [switch] $NoRestore,
    [int]    $TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject the DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

function Send-Rpc {
    param([string]$Cmd, [hashtable]$Args, [int]$Id)
    $req = @{ id = $Id; cmd = $Cmd; args = $Args } | ConvertTo-Json -Compress -Depth 10
    $writer.WriteLine($req)
    $line = $reader.ReadLine()
    if (-not $line) { throw "Empty response from $Cmd" }
    return ($line | ConvertFrom-Json).result
}

# Determine width from type tag.
$typeKey = $Type.ToLowerInvariant()
$widthByType = @{
    'bool'='1';'boolean'='1';'system.boolean'='1'
    'sbyte'='1';'int8'='1';'system.sbyte'='1'
    'byte'='1';'uint8'='1';'system.byte'='1'
    'int16'='2';'short'='2';'system.int16'='2'
    'uint16'='2';'ushort'='2';'system.uint16'='2'
    'char'='2';'system.char'='2'
    'int32'='4';'int'='4';'system.int32'='4'
    'uint32'='4';'uint'='4';'system.uint32'='4'
    'int64'='8';'long'='8';'system.int64'='8'
    'uint64'='8';'ulong'='8';'system.uint64'='8'
    'float'='4';'single'='4';'system.single'='4'
    'double'='8';'system.double'='8'
    'address'='8';'intptr'='8';'uintptr'='8';'ptr'='8';'system.intptr'='8';'system.uintptr'='8'
}
if (-not $widthByType.ContainsKey($typeKey)) {
    throw "Unknown type '$Type'."
}
$size = [int]$widthByType[$typeKey]

# Compute field address (instance + offset).
$instInt = if ($Address -match '^0[xX]') { [convert]::ToUInt64($Address.Substring(2), 16) } else { [convert]::ToUInt64($Address) }
$fieldInt = $instInt + [uint64]$FieldOffset
$fieldAddr = '0x{0:X}' -f $fieldInt

try {
    Write-Host ''
    Write-Host "==[ setInstanceField smoke ]==" -ForegroundColor Green
    Write-Host ("instance     : {0}" -f $Address) -ForegroundColor Cyan
    Write-Host ("fieldOffset  : {0} (0x{0:X})" -f $FieldOffset) -ForegroundColor Cyan
    Write-Host ("fieldAddress : {0}" -f $fieldAddr) -ForegroundColor Cyan
    Write-Host ("type         : {0}  size={1}" -f $Type, $size) -ForegroundColor Cyan
    Write-Host ("value        : {0}" -f $Value) -ForegroundColor Cyan
    Write-Host ''

    # 1) Snapshot current bytes for restore.
    $before = Send-Rpc 'readMemory' @{ address = $fieldAddr; size = $size } 1
    if ($before.error) { throw "readMemory(before) failed: $($before.error)" }
    Write-Host ("before bytes : {0}" -f $before.hex) -ForegroundColor DarkGray

    # 2) Write via setInstanceField.
    # JSON conversion handles native types — booleans pass as JSON true/false,
    # numerics as numbers. Hex strings for address/big integers.
    $writeArgs = @{
        address     = $Address
        fieldOffset = $FieldOffset
        type        = $Type
        value       = $Value
    }
    $wr = Send-Rpc 'setInstanceField' $writeArgs 2
    if ($wr.error) { throw "setInstanceField failed: $($wr.error) (size=$($wr.size))" }
    if (-not $wr.ok) { throw "setInstanceField returned ok=false" }
    Write-Host ("written      : {0} bytes @ {1}" -f $wr.written, $wr.fieldAddress) -ForegroundColor Green

    # 3) Read back.
    $after = Send-Rpc 'readMemory' @{ address = $fieldAddr; size = $size } 3
    if ($after.error) { throw "readMemory(after) failed: $($after.error)" }
    Write-Host ("after bytes  : {0}" -f $after.hex) -ForegroundColor White

    if ($after.hex -eq $before.hex) {
        Write-Host "WARNING: bytes unchanged — value may equal original" -ForegroundColor Yellow
    }

    # 4) Restore unless -NoRestore.
    if (-not $NoRestore) {
        $rs = Send-Rpc 'writeMemory' @{ address = $fieldAddr; hex = $before.hex } 4
        if ($rs.error) { Write-Host "RESTORE FAILED: $($rs.error)" -ForegroundColor Red }
        else { Write-Host ("restored     : {0} bytes" -f $rs.written) -ForegroundColor DarkGray }
    }

    Write-Host ''
    Write-Host 'PASS' -ForegroundColor Green
}
finally {
    $client.Close()
}
