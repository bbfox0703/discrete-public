<#
.SYNOPSIS
    Phase 6 segment C — heap scan for instances of a class.

.DESCRIPTION
    Scans the host's committed RW pages for the class header pointer.
    For IL2CPP, that's Il2CppClass*; for Mono, MonoVTable* of the class
    in the root domain.

    Most matches are real instance starts. False positives can occur when
    the class pointer is also stored in fields (e.g. Type tables) or in
    the runtime's own bookkeeping. Filtering against `instance_size`
    alignment will land in a later pass.

.PARAMETER ClassAssembly / Namespace / Name
    Class to scan for. Default: UnityEngine.Transform (almost every
    GameObject in a scene has one).

.PARAMETER Limit
    Max matches to return. Default 100 (DLL caps at 4096).

.EXAMPLE
    .\tools\Test-FindInstances.ps1
    .\tools\Test-FindInstances.ps1 -Namespace UnityEngine -Name GameObject
    .\tools\Test-FindInstances.ps1 -ClassAssembly Assembly-CSharp -Namespace '' -Name GameManager -Limit 10
#>
[CmdletBinding()]
param(
    [string] $ClassAssembly = 'UnityEngine.CoreModule',
    [string] $Namespace     = 'UnityEngine',
    [string] $Name          = 'Transform',
    [int]    $Limit         = 100,
    [int]    $TimeoutMs     = 5000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'UnityDumpBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

try {
    Write-Host ''
    Write-Host "==[ findInstances $Namespace.$Name (assembly=$ClassAssembly, limit=$Limit) ]==" -ForegroundColor Green
    $req = @{
        id   = 1
        cmd  = 'findInstances'
        args = @{ assembly = $ClassAssembly; namespace = $Namespace; name = $Name; limit = $Limit }
    } | ConvertTo-Json -Compress
    $writer.WriteLine($req)

    $start = Get-Date
    $line = $reader.ReadLine()
    $elapsed = ((Get-Date) - $start).TotalMilliseconds

    if (-not $line) { throw 'Empty response' }
    $r = ($line | ConvertFrom-Json).result
    if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }

    $headerLabel = if ($r.class_ptr) { 'class_ptr ' } else { 'vtable    ' }
    $headerVal   = if ($r.class_ptr) { $r.class_ptr } else { $r.vtable }
    Write-Host ("$headerLabel : {0}" -f $headerVal) -ForegroundColor Cyan
    Write-Host ("count       : {0}  (limit {1}, scan took {2:N0} ms)" -f `
        $r.count, $r.limit, $elapsed)

    # Phase 23 — heap-convergence telemetry. Confirms MEM_IMAGE + thread-stack
    # filtering kicked in. `regions_skip_image` should be non-zero (game DLLs
    # always have writable .data sections); `regions_skip_stack` >= 1 per
    # active managed thread.
    if ($r.scan_stats) {
        $s = $r.scan_stats
        Write-Host ('scan_stats  : regions total={0} scanned={1} skipImage={2} skipStack={3} bytes={4:N0} stacks={5}' -f `
            $s.regions_total, $s.regions_scanned, $s.regions_skip_image, `
            $s.regions_skip_stack, $s.bytes_scanned, $s.thread_stacks) -ForegroundColor DarkGray
    }
    Write-Host ''

    $shown = [Math]::Min($r.count, 20)
    for ($i = 0; $i -lt $shown; ++$i) {
        '{0,4}: {1}' -f $i, $r.matches[$i] | Write-Host
    }
    if ($r.count -gt $shown) {
        Write-Host ("... ({0} more)" -f ($r.count - $shown)) -ForegroundColor DarkGray
    }
}
finally {
    $client.Close()
}
