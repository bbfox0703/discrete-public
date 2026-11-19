<#
.SYNOPSIS
    List running processes that look like Unity games (Mono or IL2CPP).

.DESCRIPTION
    Scans every accessible process in the user's session and reports those
    that have GameAssembly.dll (IL2CPP) or mono-2.0-bdwgc.dll / mono.dll
    (Mono) loaded. Output is a table; pipe to Format-List for full detail.

    The Arch column distinguishes 32-bit (x86, WOW64) from 64-bit (x64)
    targets — Test-Handshake.ps1 / Inject-Dll.ps1 auto-route 32-bit
    targets through dist\x86\inject-x86.exe + dist\x86\Discrete-Mono.dll
    (Phase 12d-5), but you should still notice the column so you know
    which DLL is going to load.

    Use the reported PID as input to .\tools\Test-Handshake.ps1.

.EXAMPLE
    .\tools\List-UnityProcs.ps1
    .\tools\List-UnityProcs.ps1 | Format-List
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Bitness probe — IsWow64Process returns true iff the target is a 32-bit
# process on 64-bit Windows. We're always on x64 Windows (DLL is built
# x64), so the negation is "true x64 native". Matches the detection
# Inject-Dll.ps1 uses to decide whether to shell out to inject-x86.exe.
if (-not ('UCED.BitnessProbe' -as [type])) {
    Add-Type -Namespace UCED -Name BitnessProbe -MemberDefinition @'
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsWow64Process(System.IntPtr hProcess, out bool wow64Process);
'@
}

function Get-ProcessArch([System.Diagnostics.Process] $proc) {
    try {
        $wow = $false
        if ([UCED.BitnessProbe]::IsWow64Process($proc.Handle, [ref]$wow)) {
            if ($wow) { return 'x86' } else { return 'x64' }
        }
    } catch {
        # Handle invalid (process exited?) or access denied — fall through.
    }
    return '?'
}

$results = @()
foreach ($p in Get-Process) {
    if (-not $p.MainWindowTitle) { continue }
    $backend = $null
    $unityModule = $null
    try {
        foreach ($m in $p.Modules) {
            switch -Wildcard ($m.ModuleName) {
                'GameAssembly.dll'      { $backend = 'IL2CPP'; $unityModule = $m.FileName; break }
                'mono-2.0-bdwgc.dll'    { $backend = 'Mono';   $unityModule = $m.FileName; break }
                'mono.dll'              { if (-not $backend) { $backend = 'Mono'; $unityModule = $m.FileName } }
                'UnityPlayer.dll'       { if (-not $backend) { $backend = 'Unity?'; $unityModule = $m.FileName } }
            }
            if ($backend -and $backend -ne 'Unity?') { break }
        }
    } catch {
        # Access denied (e.g. elevated process) -- skip silently
        continue
    }
    if ($backend) {
        $exePath = '<denied>'
        try { $exePath = $p.MainModule.FileName } catch { }
        $arch = Get-ProcessArch $p
        $results += [pscustomobject]@{
            PID         = $p.Id
            Backend     = $backend
            Arch        = $arch
            ProcessName = $p.ProcessName
            Title       = $p.MainWindowTitle
            ExePath     = $exePath
            UnityModule = $unityModule
        }
    }
}

if (-not $results) {
    Write-Host 'No Unity (Mono / IL2CPP) processes detected in this session.' -ForegroundColor Yellow
    Write-Host 'Tips:' -ForegroundColor DarkGray
    Write-Host '  - Make sure the game is past the splash screen and showing a window.'
    Write-Host '  - Anti-cheat-protected games will hide their modules; do not test those.'
    return
}

$results | Sort-Object Backend, Arch, ProcessName | Format-Table PID, Backend, Arch, ProcessName, Title -AutoSize
