<#
.SYNOPSIS
    Phase 5 v32+ smoke — verify metadata-cli accepts synthetic
    global-metadata.dat fixtures for versions v29/v31/v35/v38/v39.

.DESCRIPTION
    Builds minimal valid metadata blobs in-memory (just the headers + empty
    tables), writes each to a temp file, runs `metadata-cli info` against
    it, and asserts the parsed version + table counts match expectations.

    This is the "fully autonomous" leg of v32+ verification — no real Unity
    target needed. The real-game live-smoke (Songs of Silence / v39) is
    tracked separately as task 21d.

    Coverage:
      • v29 flat header (HeaderV29) — control case
      • v31 flat header — same shape as v29
      • v35 flat header — TypeDef stride drops 88→84 (elementTypeIndex
        removed) + StringLiteral entry drops 8→4 (length removed)
      • v38 section header (HeaderV38) — 31 Il2CppSectionMetadata
        triplets + variable-width metadata indices (all 1-byte at empty)
      • v39 section header — identical shape to v38

.PARAMETER MetadataCli
    Optional explicit path to metadata-cli.exe (defaults to dist/).
#>
[CmdletBinding()]
param(
    [string] $MetadataCli = (Join-Path $PSScriptRoot '..\dist\metadata-cli.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $MetadataCli)) {
    throw "metadata-cli not found at $MetadataCli — run build.ps1 -Target MetadataCli first."
}

# Header sizes match MetadataLayout.h. Sanity 0xFAB11BAF has the high bit
# set so PowerShell's signed-int32 literal overflows — parse it as a real
# uint32 to keep BitConverter happy.
$Sanity      = [System.Convert]::ToUInt32('FAB11BAF', 16)
$HeaderV29Sz = 168
$HeaderV38Sz = 8 + 31 * 12   # sanity+version (8) + 31 section triplets (12 each) = 380

function New-HeaderV29Blob {
    [CmdletBinding()] param([int] $Version)

    # 42 int32 fields total: sanity, version, then 20 (offset, size) pairs.
    # Index 0  = sanity              (uint32, written as int32 — same bytes)
    # Index 1  = version
    # Index 2/3 = stringLiteralOffset/Size
    # Index 4/5 = stringLiteralDataOffset/Size
    # Index 6/7 = stringOffset/Size
    # ... 17 more (offset, size) pairs trailing
    # Total = 2 + 2*20 = 42 ints = 168 bytes ✓
    $header = [byte[]]::new($HeaderV29Sz)
    [BitConverter]::GetBytes([uint32] $Sanity).CopyTo($header, 0)
    [BitConverter]::GetBytes([int32]  $Version).CopyTo($header, 4)
    # Every (offset, size) pair stays at (HeaderV29Sz, 0) — empty tables
    # past the header. inBounds-check passes since offset==data.size() and
    # size==0 satisfies (offset + size) <= data.size().
    for ($i = 2; $i -lt 42; $i += 2) {
        [BitConverter]::GetBytes([int32] $HeaderV29Sz).CopyTo($header, $i * 4)
        # size already 0 from byte[]::new
    }
    return $header
}

function New-HeaderV38Blob {
    [CmdletBinding()] param([int] $Version)

    # HeaderV38: uint32 sanity + int32 version + 31 Il2CppSectionMetadata
    # triplets. Each section = (uint32 offset, uint32 size, uint32 count).
    # All sections collapse to (HeaderV38Sz, 0, 0) — empty data, no rows.
    # indexSizes derivation:
    #   typeIndexSize: skipped because parameters.count==0
    #   typeDefinitionIndexSize: GetIndexSize(0) = 1
    #   genericContainerIndexSize: GetIndexSize(0) = 1
    #   parameterIndexSize: GetIndexSize(0) = 1
    # → all sizes default to 1 byte, but we don't read any TypeDef rows
    #   anyway, so the dispatch alone is what's exercised.
    $header = [byte[]]::new($HeaderV38Sz)
    [BitConverter]::GetBytes([uint32] $Sanity).CopyTo($header, 0)
    [BitConverter]::GetBytes([int32]  $Version).CopyTo($header, 4)
    # 31 triplets at offsets 8, 20, 32, ..., 8 + 30*12 = 368.
    for ($i = 0; $i -lt 31; $i++) {
        $base = 8 + $i * 12
        [BitConverter]::GetBytes([uint32] $HeaderV38Sz).CopyTo($header, $base)
        # size + count already 0.
    }
    return $header
}

function Invoke-MetadataCli {
    [CmdletBinding()] param([string] $Path)
    $stdout = & $MetadataCli 'info' $Path 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "metadata-cli exited $LASTEXITCODE for $Path"
    }
    return $stdout | ConvertFrom-Json
}

function Test-Version {
    [CmdletBinding()] param([int] $Version, [scriptblock] $BlobFactory)
    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        $blob = & $BlobFactory $Version
        [System.IO.File]::WriteAllBytes($tempFile, $blob)
        $result = Invoke-MetadataCli -Path $tempFile

        Write-Host ("v{0}: version={1} loaded={2} type_defs={3} string_literals={4}" -f
            $Version, $result.version, $result.loaded,
            $result.type_definition_count, $result.string_literal_count)
        if ($result.version -ne $Version) {
            throw "v${Version}: cli reported version=$($result.version)"
        }
        if (-not $result.loaded) {
            throw "v${Version}: cli reported loaded=false"
        }
        if ($result.type_definition_count -ne 0) {
            throw "v${Version}: expected empty type table, got $($result.type_definition_count)"
        }
        if ($result.string_literal_count -ne 0) {
            throw "v${Version}: expected empty string-literal table, got $($result.string_literal_count)"
        }
    } finally {
        Remove-Item $tempFile -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '==[ MetadataParser version-dispatch smoke ]==' -ForegroundColor Green

# Flat-header path (v16-v37).
Test-Version -Version 29 -BlobFactory ${function:New-HeaderV29Blob}
Test-Version -Version 31 -BlobFactory ${function:New-HeaderV29Blob}
Test-Version -Version 35 -BlobFactory ${function:New-HeaderV29Blob}
Test-Version -Version 37 -BlobFactory ${function:New-HeaderV29Blob}

# Section-header path (v38+).
Test-Version -Version 38 -BlobFactory ${function:New-HeaderV38Blob}
Test-Version -Version 39 -BlobFactory ${function:New-HeaderV38Blob}

# Reject path — version outside [16, 39] must fail load with exit code 1.
$tempFile = [System.IO.Path]::GetTempFileName()
try {
    $blob = New-HeaderV29Blob -Version 40
    [System.IO.File]::WriteAllBytes($tempFile, $blob)
    $stdout = & $MetadataCli 'info' $tempFile 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw "v40: expected reject, but cli exited 0"
    }
    Write-Host "v40: correctly rejected (exit=$LASTEXITCODE)"
} finally {
    Remove-Item $tempFile -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'PASS — version dispatch holds for v29/v31/v35/v37/v38/v39 + rejects v40' -ForegroundColor Green
