// WindowsDllInjector.cs — CreateRemoteThread + LoadLibraryW DLL injection.
//
// Direct port of tools/Inject-Dll.ps1. The remote thread runs LoadLibraryW
// against a UTF-16 path written into target-process memory; the thread's
// exit code is the low 32 bits of LoadLibraryW's HMODULE — non-zero = loaded.
//
// AOT-safe: LibraryImport source generators (no DllImport runtime marshalling).
// Synchronous internally; the async wrapper just shoves the work onto a
// thread-pool thread so the UI doesn't block on WaitForSingleObject.
//
// Phase 12d-5: x64 → x86 cross-bitness inject. The in-process injector can't
// usefully target a 32-bit process from x64 (LoadLibraryW lives in WOW64's
// kernel32, not the x64 one, and the HMODULE return can't round-trip through
// the x64 thread API). When the target is WOW64, we shell out to a tiny x86
// helper `dist/x86/inject-x86.exe` that does the inject from same-bitness
// world. The helper's exit code maps cleanly to InjectResult.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.Platform;

public sealed partial class WindowsDllInjector : IDllInjector
{
    private const uint PROCESS_ALL_ACCESS  = 0x1F0FFF;
    private const uint MEM_COMMIT_RESERVE  = 0x3000;
    private const uint MEM_RELEASE         = 0x8000;
    private const uint PAGE_READWRITE      = 0x04;
    private const uint WAIT_OBJECT_0       = 0x00000000;
    private const uint REMOTE_THREAD_TIMEOUT_MS = 10_000;

    public Task<InjectResult> InjectAsync(int pid, string dllPath, CancellationToken ct = default)
    {
        return Task.Run(() => InjectSync(pid, dllPath), ct);
    }

    private static InjectResult InjectSync(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
            return InjectResult.Failure($"DLL not found: {dllPath}");

        IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (hProc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return InjectResult.Failure($"OpenProcess failed (Win32 {err}). Try running elevated.");
        }

        try
        {
            // Phase 12d-5: cross-bitness inject. WOW64 = target is 32-bit; shell out
            // to dist/x86/inject-x86.exe so the LoadLibraryW call runs from a
            // same-bitness world. The caller is expected to have supplied an x86
            // DLL path (MainWindowViewModel routes through dist/x86/ on row.IsWow64).
            if (IsWow64Process(hProc, out bool wow64) && wow64)
            {
                CloseHandle(hProc);
                hProc = IntPtr.Zero;
                return InjectViaX86Helper(pid, dllPath);
            }

            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            UIntPtr size = (UIntPtr)pathBytes.Length;

            IntPtr remoteAddr = VirtualAllocEx(hProc, IntPtr.Zero, size,
                                               MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (remoteAddr == IntPtr.Zero)
                return InjectResult.Failure($"VirtualAllocEx failed (Win32 {Marshal.GetLastWin32Error()}).");

            // Cleared once the remote thread owns remoteAddr and has NOT been
            // observed to finish. remoteAddr is the lpParameter that thread's
            // LoadLibraryW reads, and ntdll wraps the caller's buffer in a
            // UNICODE_STRING rather than copying it until name resolution — so
            // releasing it under a live thread faults inside ntdll and kills the
            // GAME, turning a failed injection into a lost session. On timeout we
            // deliberately leak one page (a few KB) in the target instead.
            bool safeToFreeRemote = true;
            try
            {
                if (!WriteProcessMemory(hProc, remoteAddr, pathBytes, size, out _))
                    return InjectResult.Failure($"WriteProcessMemory failed (Win32 {Marshal.GetLastWin32Error()}).");

                IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
                if (hKernel32 == IntPtr.Zero)
                    return InjectResult.Failure("GetModuleHandle(kernel32.dll) returned NULL.");

                IntPtr loadLib = GetProcAddress(hKernel32, "LoadLibraryW");
                if (loadLib == IntPtr.Zero)
                    return InjectResult.Failure("GetProcAddress(LoadLibraryW) returned NULL.");

                IntPtr hThread = CreateRemoteThread(hProc, IntPtr.Zero, UIntPtr.Zero,
                                                    loadLib, remoteAddr, 0, IntPtr.Zero);
                // A failed CreateRemoteThread never took ownership of remoteAddr,
                // so safeToFreeRemote stays true and the finally reclaims it.
                if (hThread == IntPtr.Zero)
                    return InjectResult.Failure($"CreateRemoteThread failed (Win32 {Marshal.GetLastWin32Error()}).");

                try
                {
                    uint wait = WaitForSingleObject(hThread, REMOTE_THREAD_TIMEOUT_MS);
                    if (wait != WAIT_OBJECT_0)
                    {
                        safeToFreeRemote = false;   // thread still alive — leak the page
                        return InjectResult.Failure($"Remote thread did not finish in 10 s (wait code 0x{wait:X8}).");
                    }

                    if (!GetExitCodeThread(hThread, out uint exitCode))
                        return InjectResult.Failure($"GetExitCodeThread failed (Win32 {Marshal.GetLastWin32Error()}).");

                    if (exitCode == 0)
                    {
                        return InjectResult.Failure(
                            "LoadLibraryW returned NULL — DLL failed to load. " +
                            "Check %LOCALAPPDATA%\\discrete\\Logs.");
                    }
                    return InjectResult.Success(exitCode);
                }
                finally
                {
                    CloseHandle(hThread);
                }
            }
            finally
            {
                if (safeToFreeRemote)
                    VirtualFreeEx(hProc, remoteAddr, UIntPtr.Zero, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    /// <summary>
    /// Cross-bitness inject: shell out to the bundled x86 helper.
    /// Resolves dist/x86/inject-x86.exe next to DiscreteUI.exe.
    /// Maps the helper's exit codes (0/1/2/3) back to InjectResult so the
    /// caller's error-handling stays uniform between in-process and
    /// out-of-process paths.
    /// </summary>
    private static InjectResult InjectViaX86Helper(int pid, string dllPath)
    {
        // Resolve next to the actual .exe on disk, NOT AppContext.BaseDirectory —
        // same caveat as MainWindowViewModel's DLL-path resolution (single-file
        // self-extract puts BaseDirectory in a temp dir).
        string? baseDir = Environment.ProcessPath is { Length: > 0 } pp
                          ? Path.GetDirectoryName(pp) : null;
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
        string helperPath = Path.Combine(baseDir, "x86", "inject-x86.exe");
        if (!File.Exists(helperPath))
        {
            return InjectResult.Failure(
                $"x86 inject helper not found at {helperPath}. Build with " +
                "`build.ps1 -Target DLL -Arch x86` or copy dist\\x86\\inject-x86.exe " +
                "next to DiscreteUI.exe.");
        }

        var psi = new ProcessStartInfo(helperPath)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(dllPath);

        Process? started;
        try
        {
            started = Process.Start(psi);
            if (started is null)
                return InjectResult.Failure("Process.Start(inject-x86.exe) returned null.");
        }
        catch (Exception ex)
        {
            return InjectResult.Failure($"Failed to launch {helperPath}: {ex.Message}");
        }
        using var proc = started;   // dispose the process handle on every exit path

        // Drain both pipes concurrently: reading one to end while the child fills
        // the other would deadlock if the helper ever emitted heavily on both.
        // Harvest after the child exits (which closes both pipes and completes the
        // reads), so GetResult never blocks.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        // 30s budget is conservative; the helper itself caps at 10s for the
        // remote thread + ~ms for the rest. Anything beyond 30s is hung.
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            // Observe the read tasks before `using` disposes the streams, so a
            // read aborted by the kill doesn't surface as an unobserved exception.
            try { stdoutTask.GetAwaiter().GetResult(); } catch { /* killed */ }
            try { stderrTask.GetAwaiter().GetResult(); } catch { /* killed */ }
            return InjectResult.Failure(
                "inject-x86.exe did not exit within 30 s; killed.");
        }
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        int code = proc.ExitCode;
        if (code == 0)
        {
            // Parse `inject-x86: LoadLibraryW=0x<hex> (thread exit code)` out of
            // stdout so the caller's logger gets a useful HMODULE value too.
            uint hmod = 0;
            int idx = stdout.IndexOf("LoadLibraryW=0x", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int hexStart = idx + "LoadLibraryW=0x".Length;
                int hexEnd = hexStart;
                while (hexEnd < stdout.Length && IsHex(stdout[hexEnd])) hexEnd++;
                if (hexEnd > hexStart)
                {
                    _ = uint.TryParse(
                        stdout.AsSpan(hexStart, hexEnd - hexStart),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out hmod);
                }
            }
            return InjectResult.Success(hmod);
        }

        // Map the helper's non-zero exit codes back through InjectResult.
        // stderr already carries the human-readable reason; concat both
        // streams in case the helper logged context to stdout pre-failure.
        string reason = string.IsNullOrWhiteSpace(stderr)
            ? $"inject-x86.exe exited with code {code}."
            : stderr.Trim();
        if (code == 2)
        {
            reason = "inject-x86 refused to inject into an x64 target. " +
                     "Use the in-process injector for x64 processes. " +
                     "(Did ProcessPicker mis-detect bitness?)";
        }
        else if (code == 3)
        {
            reason = "x86 LoadLibraryW returned NULL — DLL failed to load. " +
                     "Check %LOCALAPPDATA%\\discrete\\Logs.";
        }
        return InjectResult.Failure(reason);
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // ---------- P/Invoke (LibraryImport — AOT-friendly source-gen) ----------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAllocEx(IntPtr hProc, IntPtr addr, UIntPtr size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(IntPtr hProc, IntPtr addr, UIntPtr size, uint type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr hProc, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);

    // LibraryImport does NOT auto-suffix A/W like classic DllImport does —
    // we must name the real export explicitly. GetModuleHandleW is the wide
    // variant; GetProcAddress takes ANSI symbol names (no W variant exists).
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true,
                   StringMarshalling = StringMarshalling.Custom,
                   StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
    private static partial IntPtr GetProcAddress(IntPtr hMod, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateRemoteThread(
        IntPtr hProc, IntPtr sa, UIntPtr stackSize,
        IntPtr startAddr, IntPtr param, uint flags, IntPtr threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr h, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeThread(IntPtr h, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
}
