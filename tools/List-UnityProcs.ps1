<#
.SYNOPSIS
    List running processes that look like Unity games (Mono or IL2CPP).

.DESCRIPTION
    Scans every accessible 64-bit process in the user's session and reports
    those that have GameAssembly.dll (IL2CPP) or mono-2.0-bdwgc.dll / mono.dll
    (Mono) loaded. Output is a table; pipe to Format-List for full detail.

    Use the reported PID as input to .\tools\Test-Handshake.ps1.

.EXAMPLE
    .\tools\List-UnityProcs.ps1
    .\tools\List-UnityProcs.ps1 | Format-List
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

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
        $results += [pscustomobject]@{
            PID         = $p.Id
            Backend     = $backend
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

$results | Sort-Object Backend, ProcessName | Format-Table PID, Backend, ProcessName, Title -AutoSize
