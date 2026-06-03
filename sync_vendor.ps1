# sync_vendor.ps1 — refresh the third-party source trees this repo
# depends on or cross-checks against. Two buckets:
#
#   $dll_deps  → cloned into  dll/vendor/<name>/
#                C++ build dependencies the DLL actually links against
#                (nlohmann/json single-header, MinHook).
#
#   $deps      → cloned into  vendor/<name>/
#                Reference repos kept around for cross-checking IL2CPP
#                struct layouts, looking up Mono runtime exports, etc.
#                Not built or shipped — they're documentation.
#
# Both subtrees are ignored by .gitignore (each clone has its own .git/),
# so running this script doesn't dirty `git status` on the parent repo.
#
# Run from anywhere — script anchors itself to its own directory.

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

$dll_deps = @{
    "nlohmann"    = "https://github.com/nlohmann/json.git"
    "minihook"    = "https://github.com/TsudaKageyu/minhook.git"
}

# Recursive deps — clone with --recurse-submodules. Zydis bundles Zycore
# as a submodule; flat clone won't pull it and CMake configure will fail.
# Phase 28b-v2 x64 disassembler for IL2CPP class-pointer chain detection.
$dll_deps_recursive = @{
    "zydis"       = "https://github.com/zyantific/zydis.git"
}

$deps = @{
    "Il2CppDumper"             = "https://github.com/Perfare/Il2CppDumper"
	"Il2CppDumperV35plus"      = "https://github.com/roytu/Il2CppDumper"
    "UnityExplorer"            = "https://github.com/sinai-dev/UnityExplorer"
    "Il2CppAssemblyUnhollower" = "https://github.com/knah/Il2CppAssemblyUnhollower"
    "BepInEx"                  = "https://github.com/BepInEx/BepInEx"
    "mono"                     = "https://github.com/mono/mono"
    "libil2cpp"                = "https://github.com/MlgmXyysd/libil2cpp"
	"Il2CppInspectorRedux"     = "https://github.com/LukeFZ/Il2CppInspectorRedux"
}

if (!(Test-Path "dll"))        { New-Item -ItemType Directory -Path "dll" | Out-Null }
if (!(Test-Path "dll\vendor")) { New-Item -ItemType Directory -Path "dll\vendor" | Out-Null }
if (!(Test-Path "vendor"))     { New-Item -ItemType Directory -Path "vendor" | Out-Null }

function Sync-One {
    param([string]$path, [string]$url, [switch]$Recursive)
    if (Test-Path "$path\.git") {
        Write-Host "[更新] $path" -ForegroundColor Cyan
        Push-Location $path
        try {
            # `git pull --ff-only` can't advance a `--depth 1` shallow clone
            # (there's no shared merge base to fast-forward against), so deps —
            # Zydis included — silently stayed frozen at their first-clone
            # snapshot. Fetch the current upstream tip shallowly and hard-reset
            # the working tree to it, so the sync actually moves forward; then
            # re-sync submodules (Zydis carries Zycore as a submodule).
            git fetch --depth 1 origin
            git reset --hard FETCH_HEAD
            if ($Recursive) { git submodule update --init --recursive --depth 1 --force }
        } finally { Pop-Location }
    } else {
        Write-Host "[下載] $path" -ForegroundColor Green
        if ($Recursive) {
            git clone --depth 1 --recurse-submodules --shallow-submodules $url $path
        } else {
            git clone --depth 1 $url $path
        }
    }
}

foreach ($item in $dll_deps.GetEnumerator())           { Sync-One "dll\vendor\$($item.Key)" $item.Value }
foreach ($item in $dll_deps_recursive.GetEnumerator()) { Sync-One "dll\vendor\$($item.Key)" $item.Value -Recursive }
foreach ($item in $deps.GetEnumerator())               { Sync-One     "vendor\$($item.Key)" $item.Value }

Write-Host "同步完成！" -ForegroundColor Yellow
