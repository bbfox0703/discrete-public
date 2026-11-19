<#
.SYNOPSIS
    Inject Discrete DLL into a target process via CreateRemoteThread + LoadLibraryW.

.DESCRIPTION
    Phase 0 verification helper. Performs the classic Windows DLL injection:
        OpenProcess → VirtualAllocEx → WriteProcessMemory(DLL path) →
        CreateRemoteThread(LoadLibraryW)
    Then waits for the remote thread to finish and reports the loaded module handle.

    x64-only — refuses to inject if the target is a 32-bit process (per project decision).

.PARAMETER Target
    Either a PID (integer) or a process name (e.g. "notepad" or "notepad.exe").
    If multiple processes match the name, you'll see a list and need to pass -Pid.

.PARAMETER Backend
    Which DLL backend to inject. Default: Mono.
        Mono | IL2CPP

.PARAMETER DllPath
    Explicit DLL path. Overrides -Backend lookup. Useful for testing a build elsewhere.

.PARAMETER BuildDir
    Where to look for the built DLL. Default: build\dll-both-release\bin (matches build.ps1).

.EXAMPLE
    .\tools\Inject-Dll.ps1 -Target notepad
    .\tools\Inject-Dll.ps1 -Target 12345 -Backend IL2CPP
    .\tools\Inject-Dll.ps1 -Target notepad -DllPath C:\custom\path\my.dll
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Target,
    [ValidateSet('Mono','IL2CPP')] [string]$Backend = 'Mono',
    [string]$DllPath,
    [string]$BuildDir
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)

# ---------------------------------------------------------------------------
# Resolve DLL path
# ---------------------------------------------------------------------------
# Search order:
#   1. -DllPath (explicit override)
#   2. -BuildDir (explicit override)
#   3. dist\Discrete-{Backend}.dll                    (canonical, post-12d)
#   4. build\dll-{both,backend}-release-x64\bin\...      (arch-suffixed, post-12d)
#   5. build\dll-{both,backend}-release\bin\...          (pre-12d legacy)
# Putting dist\ first means whichever DLL was most recently produced (by
# `build.ps1 -Target DLL ...` of any arch/backend combo) wins — no stale
# build/dll-both-release/bin/ from the pre-12d era can shadow it.
if (-not $DllPath) {
    if (-not $BuildDir) {
        $candidates = @(
            (Join-Path $RepoRoot                       "dist\Discrete-$Backend.dll"),
            (Join-Path $RepoRoot                       "build\dll-both-release-x64\bin\Discrete-$Backend.dll"),
            (Join-Path $RepoRoot "build\dll-$($Backend.ToLower())-release-x64\bin\Discrete-$Backend.dll"),
            (Join-Path $RepoRoot                       "build\dll-both-release\bin\Discrete-$Backend.dll"),
            (Join-Path $RepoRoot "build\dll-$($Backend.ToLower())-release\bin\Discrete-$Backend.dll")
        )
        $DllPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    } else {
        $DllPath = Join-Path $BuildDir "Discrete-$Backend.dll"
    }
}
if (-not (Test-Path $DllPath)) {
    throw "DLL not found: $DllPath`n  Build first:  .\build.ps1 -Target DLL -Backend $Backend"
}
$DllPath = (Resolve-Path $DllPath).Path
Write-Host "DLL : $DllPath" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# Resolve target process
# ---------------------------------------------------------------------------
$proc = $null
if ($Target -match '^\d+$') {
    $proc = Get-Process -Id ([int]$Target) -ErrorAction Stop
} else {
    $name = $Target -replace '\.exe$',''
    $matches = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) { throw "No process named '$Target' is running." }
    if ($matches.Count -gt 1) {
        Write-Host "Multiple matches:" -ForegroundColor Yellow
        $matches | Format-Table Id, ProcessName, MainWindowTitle | Out-String | Write-Host
        throw "Pass an explicit PID: -Target <pid>"
    }
    $proc = $matches[0]
}
Write-Host "Proc: $($proc.ProcessName) (PID $($proc.Id))" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# Bitness detection — route through inject-x86.exe for WOW64 targets.
# Phase 12d-5: the old x64-only guard refused to inject into 32-bit
# processes. Now we shell out to the bundled x86 inject helper instead,
# matching DiscreteUI's WindowsDllInjector behaviour. If the caller
# passed -DllPath explicitly we honour it; otherwise auto-route to
# dist\x86\Discrete-Mono.dll alongside the helper.
# ---------------------------------------------------------------------------
# Type guard — List-UnityProcs.ps1 defines the same probe type, and the
# user may have already imported that script in this session. Add-Type
# blows up on duplicate registration so skip when the type is live.
if (-not ('UCED.BitnessProbe' -as [type])) {
    Add-Type -Namespace UCED -Name BitnessProbe -MemberDefinition @'
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsWow64Process(System.IntPtr hProcess, out bool wow64Process);
'@
}
$wow = $false
[void][UCED.BitnessProbe]::IsWow64Process($proc.Handle, [ref]$wow)
if ($wow) {
    if ($Backend -eq 'IL2CPP') {
        throw "Target is 32-bit but -Backend IL2CPP requested. No shipping IL2CPP target is 32-bit; re-check the picker."
    }
    $injectX86 = Join-Path $RepoRoot 'dist\x86\inject-x86.exe'
    if (-not (Test-Path $injectX86)) {
        throw "Target is 32-bit but $injectX86 is missing. Build with .\build.ps1 -Target DLL -Arch x86"
    }
    # Auto-route the DLL to dist\x86\ unless the caller passed -DllPath.
    # The current $DllPath came from the x64 search above, which would be wrong.
    if (-not $PSBoundParameters.ContainsKey('DllPath') -and
        $DllPath -notlike '*dist\x86\*') {
        $x86Dll = Join-Path $RepoRoot 'dist\x86\Discrete-Mono.dll'
        if (-not (Test-Path $x86Dll)) {
            throw "x86 Mono DLL not found at $x86Dll. Build with .\build.ps1 -Target DLL -Arch x86 -Backend Mono"
        }
        Write-Host "DLL : $x86Dll (auto-routed for 32-bit target)" -ForegroundColor Cyan
        $DllPath = $x86Dll
    }
    Write-Host "Routing through $injectX86 (32-bit target)" -ForegroundColor Cyan
    & $injectX86 $proc.Id $DllPath
    if ($LASTEXITCODE -ne 0) {
        throw "inject-x86.exe failed with exit code $LASTEXITCODE."
    }

    # Verify the per-PID log appeared (same shape as the x64 path's tail).
    $logFile = Join-Path $env:LOCALAPPDATA "discrete\Logs\$($proc.Id)\init\current.log"
    if (Test-Path $logFile) {
        Write-Host ''
        Write-Host "Log: $logFile" -ForegroundColor Green
        Write-Host '--- last 20 lines ---' -ForegroundColor DarkGray
        Get-Content $logFile -Tail 20
    }
    return
}

# ---------------------------------------------------------------------------
# P/Invoke surface
# ---------------------------------------------------------------------------
Add-Type -Namespace UCED -Name Inject -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern System.IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern System.IntPtr VirtualAllocEx(System.IntPtr hProc, System.IntPtr addr, System.UIntPtr size, uint type, uint protect);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(System.IntPtr hProc, System.IntPtr addr, System.UIntPtr size, uint type);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(System.IntPtr hProc, System.IntPtr addr, byte[] buf, System.UIntPtr size, out System.UIntPtr written);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern System.IntPtr GetModuleHandle(string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    public static extern System.IntPtr GetProcAddress(System.IntPtr hMod, string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern System.IntPtr CreateRemoteThread(System.IntPtr hProc, System.IntPtr sa, System.UIntPtr stackSize, System.IntPtr startAddr, System.IntPtr param, uint flags, System.IntPtr threadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(System.IntPtr h, uint ms);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeThread(System.IntPtr h, out uint code);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(System.IntPtr h);
'@

# ---------------------------------------------------------------------------
# Inject
# ---------------------------------------------------------------------------
$PROCESS_ALL_ACCESS = 0x1F0FFF
$MEM_COMMIT_RESERVE = 0x3000
$MEM_RELEASE        = 0x8000
$PAGE_READWRITE     = 0x04

$hProc = [UCED.Inject]::OpenProcess($PROCESS_ALL_ACCESS, $false, $proc.Id)
if ($hProc -eq [IntPtr]::Zero) {
    throw "OpenProcess failed (Win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())). Run elevated?"
}

try {
    # UTF-16LE encoded path + null terminator (LoadLibraryW expects wide chars).
    $pathBytes = [System.Text.Encoding]::Unicode.GetBytes($DllPath + "`0")
    $size      = [UIntPtr]::new([uint64]$pathBytes.Length)

    $remoteAddr = [UCED.Inject]::VirtualAllocEx($hProc, [IntPtr]::Zero, $size, $MEM_COMMIT_RESERVE, $PAGE_READWRITE)
    if ($remoteAddr -eq [IntPtr]::Zero) { throw "VirtualAllocEx failed." }

    try {
        $written = [UIntPtr]::Zero
        if (-not [UCED.Inject]::WriteProcessMemory($hProc, $remoteAddr, $pathBytes, $size, [ref]$written)) {
            throw "WriteProcessMemory failed."
        }

        $hKernel32 = [UCED.Inject]::GetModuleHandle('kernel32.dll')
        $loadLib   = [UCED.Inject]::GetProcAddress($hKernel32, 'LoadLibraryW')
        if ($loadLib -eq [IntPtr]::Zero) { throw "GetProcAddress(LoadLibraryW) failed." }

        $hThread = [UCED.Inject]::CreateRemoteThread($hProc, [IntPtr]::Zero, [UIntPtr]::Zero, $loadLib, $remoteAddr, 0, [IntPtr]::Zero)
        if ($hThread -eq [IntPtr]::Zero) { throw "CreateRemoteThread failed." }

        try {
            Write-Host "Waiting for LoadLibraryW to finish..." -ForegroundColor DarkGray
            $wait = [UCED.Inject]::WaitForSingleObject($hThread, 10000)
            if ($wait -ne 0) { throw "Remote thread did not finish in 10 s (wait code $wait)." }

            $code = 0
            [void][UCED.Inject]::GetExitCodeThread($hThread, [ref]$code)
            # On x64 the thread exit code is the low 32 bits of LoadLibraryW's HMODULE.
            # Non-zero means the DLL loaded; zero means LoadLibrary returned NULL.
            if ($code -eq 0) {
                throw "LoadLibraryW returned NULL — DLL failed to load. Check %LOCALAPPDATA%\discrete\Logs."
            }
            Write-Host "Injected. LoadLibraryW returned 0x$($code.ToString('X8')) (truncated to 32-bit)" -ForegroundColor Green
        } finally {
            [void][UCED.Inject]::CloseHandle($hThread)
        }
    } finally {
        [void][UCED.Inject]::VirtualFreeEx($hProc, $remoteAddr, [UIntPtr]::Zero, $MEM_RELEASE)
    }
} finally {
    [void][UCED.Inject]::CloseHandle($hProc)
}

# ---------------------------------------------------------------------------
# Verify log file appeared
# ---------------------------------------------------------------------------
$logFile = Join-Path $env:LOCALAPPDATA "discrete\Logs\$($proc.Id)\init\current.log"
if (Test-Path $logFile) {
    Write-Host ""
    Write-Host "Log: $logFile" -ForegroundColor Green
    Write-Host "--- last 20 lines ---" -ForegroundColor DarkGray
    Get-Content $logFile -Tail 20
} else {
    Write-Host ""
    Write-Host "WARNING: expected log file not found at $logFile" -ForegroundColor Yellow
    Write-Host "         The DLL loaded but Logger::Init may have failed." -ForegroundColor Yellow
}
