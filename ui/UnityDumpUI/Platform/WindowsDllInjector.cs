// WindowsDllInjector.cs — CreateRemoteThread + LoadLibraryW DLL injection.
//
// Direct port of tools/Inject-Dll.ps1. The remote thread runs LoadLibraryW
// against a UTF-16 path written into target-process memory; the thread's
// exit code is the low 32 bits of LoadLibraryW's HMODULE — non-zero = loaded.
//
// AOT-safe: LibraryImport source generators (no DllImport runtime marshalling).
// Synchronous internally; the async wrapper just shoves the work onto a
// thread-pool thread so the UI doesn't block on WaitForSingleObject.

using System;
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
            // x64 guard — refuse 32-bit targets so we don't load an x64 DLL into WOW64.
            if (IsWow64Process(hProc, out bool wow64) && wow64)
                return InjectResult.Failure("Target is a 32-bit process. UnityCEDumper is x64-only.");

            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            UIntPtr size = (UIntPtr)pathBytes.Length;

            IntPtr remoteAddr = VirtualAllocEx(hProc, IntPtr.Zero, size,
                                               MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (remoteAddr == IntPtr.Zero)
                return InjectResult.Failure($"VirtualAllocEx failed (Win32 {Marshal.GetLastWin32Error()}).");

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
                if (hThread == IntPtr.Zero)
                    return InjectResult.Failure($"CreateRemoteThread failed (Win32 {Marshal.GetLastWin32Error()}).");

                try
                {
                    uint wait = WaitForSingleObject(hThread, REMOTE_THREAD_TIMEOUT_MS);
                    if (wait != WAIT_OBJECT_0)
                        return InjectResult.Failure($"Remote thread did not finish in 10 s (wait code 0x{wait:X8}).");

                    if (!GetExitCodeThread(hThread, out uint exitCode))
                        return InjectResult.Failure($"GetExitCodeThread failed (Win32 {Marshal.GetLastWin32Error()}).");

                    if (exitCode == 0)
                    {
                        return InjectResult.Failure(
                            "LoadLibraryW returned NULL — DLL failed to load. " +
                            "Check %LOCALAPPDATA%\\UnityCEDumper\\Logs.");
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
                VirtualFreeEx(hProc, remoteAddr, UIntPtr.Zero, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

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

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Custom,
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
