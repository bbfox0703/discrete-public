<#
.SYNOPSIS
    Phase 5 segment 3 — call getMetadataStats and pretty-print the verdict.

.DESCRIPTION
    Probes the loaded metadata for stripped/obfuscated patterns. Reports the
    fraction of single-letter and two-letter type names (excluding C#
    compiler-generated `<...>` types) and gives a verdict:

      clean         — < 10% short user names. Vanilla Unity sits ~1-2%.
      partial_strip — 10-30%. Typical of mods or selective obfuscation.
      heavy_strip   — > 30%. Symbol-stripping obfuscator very likely in use.

.EXAMPLE
    .\tools\Test-MetadataStats.ps1
#>
[CmdletBinding()]
param(
    [int] $TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DiscreteBfx', 'InOut')
try { $client.Connect($TimeoutMs) }
catch { throw 'Pipe connect failed. Inject the DLL first via Test-Handshake.ps1.' }

$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

try {
    Write-Host ''
    Write-Host '==[ getMetadataStats ]==' -ForegroundColor Green
    $writer.WriteLine((@{ id = 1; cmd = 'getMetadataStats' } | ConvertTo-Json -Compress))
    $line = $reader.ReadLine()
    if (-not $line) { throw 'Empty response' }
    $r = ($line | ConvertFrom-Json).result
    if ($r.error) { Write-Host "ERROR: $($r.error)" -ForegroundColor Red; return }

    Write-Host ("scope             : {0}" -f $r.scope) -ForegroundColor Cyan
    if ($r.assemblies_scanned)  { Write-Host ("assemblies        : {0}" -f $r.assemblies_scanned) }
    Write-Host ("total classes     : {0}" -f $r.total_classes)
    Write-Host ("with namespace    : {0}" -f $r.with_namespace)
    Write-Host ("compiler-generated: {0}  (`<...`> types — excluded from verdict)" -f $r.compiler_generated)
    Write-Host ("single-char names : {0}" -f $r.single_char_names)
    Write-Host ("two-char names    : {0}" -f $r.short_names_2)
    Write-Host ("numeric-lead      : {0}" -f $r.numeric_lead)
    if ($null -ne $r.avg_name_length) {
        Write-Host ("avg name length   : {0:N1}" -f $r.avg_name_length)
    }
    Write-Host ("min / max length  : {0} / {1}" -f $r.min_name_length, $r.max_name_length)
    if ($null -ne $r.strip_ratio) {
        Write-Host ('strip ratio       : {0:P2}' -f $r.strip_ratio)
    }
    if ($r.verdict) {
        $clr = switch ($r.verdict) {
            'clean'         { 'Green' }
            'partial_strip' { 'Yellow' }
            'heavy_strip'   { 'Red' }
            default         { 'White' }
        }
        Write-Host ''
        Write-Host ("verdict           : {0}" -f $r.verdict.ToUpper()) -ForegroundColor $clr
    }
}
finally {
    $client.Close()
}
