<#
.SYNOPSIS
    Offline analysis of a Test-FindInstances-Diag.ps1 field dump.

.DESCRIPTION
    Loads the per-instance walkInstance field dump and answers:
      1. nNonNull histogram  -> are the fakes empty (template/dead) or populated?
      2. populated profile    -> which instances look "live"?
      3. the known REAL instance's non-null fields
      4. discriminator hunt   -> fields set on the REAL but on few/no others, and
                                 fields that cleanly split populated vs empty
      5. alignment vs populated cross-tab (is page-alignment a usable signal?)

.PARAMETER Json
    Path to the find-instances-fields-*.json produced by the diag script.

.PARAMETER RealIndex
    Index of the confirmed real instance (default 44 for the current dump).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Json,
    [int] $RealIndex = 44
)
$ErrorActionPreference = 'Stop'

function To-U64($v){ if($null -eq $v){return $null}; $s="$v"; if($s -eq ''){return $null}; if($s -like '0x*'){return [Convert]::ToUInt64($s.Substring(2),16)}; try { return [Convert]::ToUInt64($s) } catch { return $null } }
function Field-IsPopulated($f){
    if($null -ne $f.string -and "$($f.string)" -ne ''){return $true}
    if($null -ne $f.value){ if($f.value -is [bool]){return [bool]$f.value}; return ([double]$f.value -ne 0) }
    if($null -ne $f.ptr){ $p=To-U64 $f.ptr; return ($null -ne $p -and $p -ne 0) }
    return $false
}
function Field-Key($f){ "{0}::{1}@{2}" -f $f.from, $f.name, $f.offset }
function Field-ValStr($f){
    if($null -ne $f.string){ return "`"$($f.string)`"" }
    if($null -ne $f.value){ return "$($f.value)" }
    if($null -ne $f.ptr){ return "$($f.ptr)" }
    return '<none>'
}

Write-Host "Loading $Json ..." -ForegroundColor DarkGray
$doc = Get-Content -Raw -Path $Json | ConvertFrom-Json
$m = $doc.matches
Write-Host ("class={0}  backend={1}  instances={2}" -f $doc.meta.class.fqn, $doc.meta.backend, $m.Count) -ForegroundColor Green

# 1. nNonNull histogram --------------------------------------------------
Write-Host ''
Write-Host '==[ nNonNull histogram ]==' -ForegroundColor Green
$m | Group-Object nNonNull | Sort-Object { [int]$_.Name } | ForEach-Object {
    Write-Host ('  nNonNull={0,-4} : {1,3} instance(s)' -f $_.Name, $_.Count)
}
$empty = @($m | Where-Object { $_.nNonNull -eq 0 })
$pop   = @($m | Where-Object { $_.nNonNull -gt 0 })
Write-Host ("  -> empty(nNonNull=0): {0}   populated: {1}" -f $empty.Count, $pop.Count) -ForegroundColor Cyan

# 2. top populated instances --------------------------------------------
Write-Host ''
Write-Host '==[ top 20 by nNonNull ]==' -ForegroundColor Green
$m | Sort-Object @{Expression='nNonNull';Descending=$true}, index | Select-Object -First 20 | ForEach-Object {
    $algn = if ($_.pageAligned) {'page'} else {'.'}
    $mark = if ($_.index -eq $RealIndex) {' <== REAL'} else {''}
    $s = if ($_.sampleStr) { ($_.sampleStr -join ' | ') } else { '' }
    Write-Host ('  #{0,-3} {1} algn={2,-4} nF={3,-4} nNN={4,-4} {5}{6}' -f $_.index,$_.address,$algn,$_.nFields,$_.nNonNull,$s,$mark)
}

# 3. REAL instance non-null fields --------------------------------------
$real = $m | Where-Object { $_.index -eq $RealIndex }
Write-Host ''
Write-Host ("==[ REAL #{0} ({1}) non-null fields ]==" -f $RealIndex, $real.address) -ForegroundColor Green
$realNN = @($real.fields | Where-Object { Field-IsPopulated $_ })
Write-Host ("  nFields={0}  nNonNull={1}" -f $real.nFields, $realNN.Count)
$realNN | ForEach-Object {
    Write-Host ('    +0x{0:X4} {1,-32} {2,-26} = {3}' -f $_.offset, "$($_.from)::$($_.name)", $_.type, (Field-ValStr $_))
}

# 4. discriminator hunt --------------------------------------------------
# For each field key, count how many instances have it populated.
Write-Host ''
Write-Host '==[ discriminator hunt: fields populated on REAL, ranked by rarity ]==' -ForegroundColor Green
$popCount = @{}
$total = $m.Count
foreach ($inst in $m) {
    foreach ($f in $inst.fields) {
        if (Field-IsPopulated $f) {
            $k = Field-Key $f
            if ($popCount.ContainsKey($k)) { $popCount[$k]++ } else { $popCount[$k] = 1 }
        }
    }
}
# fields the REAL has populated, sorted by how few OTHER instances also have them
$realKeys = @{}
foreach ($f in $realNN) { $realKeys[(Field-Key $f)] = $f }
$ranked = $realKeys.Keys | ForEach-Object {
    [pscustomobject]@{ key=$_; popInAll=$popCount[$_]; field=$realKeys[$_] }
} | Sort-Object popInAll, key
Write-Host ("  (popInAll = how many of {0} instances have this field populated; 1 = unique to REAL)" -f $total) -ForegroundColor DarkGray
$ranked | Select-Object -First 30 | ForEach-Object {
    Write-Host ('    popInAll={0,-4} +0x{1:X4} {2,-34} {3,-22} = {4}' -f `
        $_.popInAll, $_.field.offset, "$($_.field.from)::$($_.field.name)", $_.field.type, (Field-ValStr $_.field))
}

# 5. alignment cross-tab -------------------------------------------------
Write-Host ''
Write-Host '==[ pageAligned vs populated cross-tab ]==' -ForegroundColor Green
foreach ($a in @($true,$false)) {
    $grp = @($m | Where-Object { $_.pageAligned -eq $a })
    $gp  = @($grp | Where-Object { $_.nNonNull -gt 0 })
    Write-Host ('  pageAligned={0,-5} total={1,3}  populated={2,3}  empty={3,3}' -f $a, $grp.Count, $gp.Count, ($grp.Count-$gp.Count))
}
Write-Host ''
Write-Host 'Interpretation:' -ForegroundColor Yellow
Write-Host '  * If empty>>populated and REAL is among the few populated -> fakes are template/'
Write-Host '    uninitialized objects; a content filter on a popInAll=1 field isolates the live one.'
Write-Host '  * If most instances are populated too -> they are genuine live (pooled) objects;'
Write-Host '    only liveness/semantic fields (e.g. an isLocal bool / non-null connection) separate them.'
