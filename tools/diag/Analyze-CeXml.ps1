<#
.SYNOPSIS
    Analyze a Copy-CE-XML export to find which (Unity/system) types bloat the
    tree, so they can be added to the live-export recursion exclusion.

.DESCRIPTION
    Streams the XML (handles 30 MB / 700k lines). For each <CheatEntry> it reads
    the "<name> : <Type>" Description and tracks nesting via a stack, so it can
    attribute a SUBTREE LINE COST to the type at each expanded node — i.e. how
    many lines you'd save by NOT recursing into that type.

    Reports:
      1. Top types by EXPANDED subtree cost (the real bloat — what to exclude).
      2. Top types by node frequency.
      3. "Leaked" expansions: types that START WITH a framework prefix
         (UnityEngine./Unity./System./TMPro./Mono./Microsoft.) yet still have
         children — these SHOULD already be excluded by IsFrameworkReferenceType
         but slipped through (reached via a collection element or a runtime
         header-resolved type), pinpointing the gap.
      4. Value-struct sanity: confirms keepers (Vector3, Color, …) are cheap.

.PARAMETER Path
    The CE XML file. Default D:\tmp\temp.xml.
#>
[CmdletBinding()]
param([string] $Path = 'D:\tmp\temp.xml')

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Path)) { throw "not found: $Path" }

# Types that are inline value structs / data — KEEP (must stay cheap, never excluded).
$keepValueStructs = @(
  'UnityEngine.Vector2','UnityEngine.Vector3','UnityEngine.Vector4',
  'UnityEngine.Quaternion','UnityEngine.Color','UnityEngine.Color32',
  'UnityEngine.Rect','UnityEngine.Bounds','UnityEngine.Matrix4x4','UnityEngine.Ray',
  'UnityEngine.Vector2Int','UnityEngine.Vector3Int',
  'Unity.Mathematics.float2','Unity.Mathematics.float3','Unity.Mathematics.float4',
  'Unity.Mathematics.quaternion','Unity.Mathematics.int2','Unity.Mathematics.int3'
)
$fwPrefixes = @('UnityEngine.','Unity.','System.','TMPro.','Mono.','Microsoft.')

$rxType = [regex]::new(' : (.+?)(?:"| = |  )', 'Compiled')

$stack = New-Object System.Collections.Generic.Stack[object]
$freq  = @{}    # type -> node count
$cost  = @{}    # type -> summed immediate subtree line count
$childCount = @{} # type -> count of nodes that had children (were expanded)
$ln = 0
$total = 0

foreach ($line in [System.IO.File]::ReadLines($Path)) {
    $ln++
    $t = $line.TrimStart()
    if ($t.StartsWith('<CheatEntry>')) {
        $stack.Push([pscustomobject]@{ Type = $null; Start = $ln; HadChild = $false })
    }
    elseif ($t.StartsWith('<Description>')) {
        if ($stack.Count -gt 0 -and $null -eq $stack.Peek().Type) {
            $m = $rxType.Match($t)
            if ($m.Success) { $stack.Peek().Type = $m.Groups[1].Value }
        }
    }
    elseif ($t.StartsWith('<CheatEntries>')) {
        if ($stack.Count -gt 0) { $stack.Peek().HadChild = $true }
    }
    elseif ($t.StartsWith('</CheatEntry>')) {
        if ($stack.Count -gt 0) {
            $f = $stack.Pop()
            $total++
            if ($f.Type) {
                $ty = $f.Type
                $freq[$ty] = [int]$freq[$ty] + 1
                if ($f.HadChild) {
                    $cost[$ty]       = [int]$cost[$ty] + ($ln - $f.Start)
                    $childCount[$ty] = [int]$childCount[$ty] + 1
                }
            }
        }
    }
}

Write-Host ("file={0}  lines={1:N0}  cheatEntries={2:N0}" -f $Path, $ln, $total) -ForegroundColor Green

function IsFw([string]$ty) { foreach ($p in $fwPrefixes) { if ($ty.StartsWith($p)) { return $true } }; return $false }
function NsBucket([string]$ty) {
    # collapse generics, take leading namespace-ish prefix for grouping
    $b = $ty -replace '&lt;.*$',''  -replace '\[\]$',''
    $dot = $b.LastIndexOf('.')
    if ($dot -gt 0) { return $b.Substring(0, $dot) } else { return '(global)' }
}

Write-Host "`n==[ 1. Top 35 types by EXPANDED subtree cost (lines saved if NOT recursed) ]==" -ForegroundColor Green
Write-Host ("  {0,10} {1,8} {2,7}  {3}" -f 'cost(lines)','expands','nodes','type') -ForegroundColor DarkGray
$cost.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 35 | ForEach-Object {
    $ty = $_.Key
    $flag = if ($keepValueStructs -contains $ty) { 'KEEP ' } elseif (IsFw $ty) { 'LEAK?' } else { 'game ' }
    $color = if ($flag -eq 'KEEP ') { 'DarkGray' } elseif ($flag -eq 'LEAK?') { 'Yellow' } else { 'Cyan' }
    Write-Host ("  {0,10:N0} {1,8} {2,7}  [{3}] {4}" -f $_.Value, [int]$childCount[$ty], [int]$freq[$ty], $flag, $ty) -ForegroundColor $color
}

Write-Host "`n==[ 2. Top 25 types by node FREQUENCY ]==" -ForegroundColor Green
$freq.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 25 | ForEach-Object {
    $ty = $_.Key
    $flag = if ($keepValueStructs -contains $ty) { 'KEEP ' } elseif (IsFw $ty) { 'fw   ' } else { 'game ' }
    Write-Host ("  {0,8:N0}  [{1}] {2}" -f $_.Value, $flag, $ty)
}

Write-Host "`n==[ 3. LEAKED expansions — framework types that STILL got children (exclusion gap) ]==" -ForegroundColor Green
Write-Host "    (these should already be pointer-leaves; they slipped through — likely via collection elements)" -ForegroundColor DarkGray
$leaks = $childCount.GetEnumerator() | Where-Object { (IsFw $_.Key) -and ($keepValueStructs -notcontains $_.Key) } |
         Sort-Object { [int]$cost[$_.Key] } -Descending
$leakTotal = 0
$leaks | Select-Object -First 30 | ForEach-Object {
    $ty = $_.Key; $leakTotal += [int]$cost[$ty]
    Write-Host ("  cost={0,9:N0}  expands={1,6}  {2}" -f [int]$cost[$ty], $_.Value, $ty) -ForegroundColor Yellow
}
$leakAll = ($leaks | ForEach-Object { [int]$cost[$_.Key] } | Measure-Object -Sum).Sum
Write-Host ("  -> total leaked-expansion cost across ALL framework types: {0:N0} lines" -f $leakAll) -ForegroundColor Magenta

Write-Host "`n==[ 4. Expanded-cost grouped by namespace bucket ]==" -ForegroundColor Green
$byNs = @{}
foreach ($k in $cost.Keys) { $b = NsBucket $k; $byNs[$b] = [int]$byNs[$b] + [int]$cost[$k] }
$byNs.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 20 | ForEach-Object {
    Write-Host ("  {0,10:N0}  {1}" -f $_.Value, $_.Key)
}

Write-Host "`n==[ 5. Keeper value-structs (should be cheap & present) ]==" -ForegroundColor Green
foreach ($k in $keepValueStructs) {
    if ($freq.ContainsKey($k)) {
        Write-Host ("  {0,8:N0} nodes  expands={1,6}  cost={2,8:N0}  {3}" -f [int]$freq[$k], [int]$childCount[$k], [int]$cost[$k], $k) -ForegroundColor DarkGray
    }
}
