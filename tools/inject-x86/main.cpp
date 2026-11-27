// inject-x86 — DLL injector for 32-bit Windows processes (Phase 12d-5).
//
// Why it exists:
//   DiscreteUI runs as x64 (.NET 10 native-AOT). Windows' CreateRemoteThread
//   into a 32-bit target from an x64 process is fragile: the remote
//   LoadLibraryW lives in WOW64's kernel32.dll, not the x64 one, and the
//   thread-handle exit code can't carry a 32-bit HMODULE value back through
//   the x64 thread API cleanly. The robust pattern (used by BepInEx,
//   MelonLoader, etc.) is to spawn a tiny x86 helper that does the inject
//   from same-bitness world. That's this tool.
//
// Usage:
//   inject-x86.exe <PID> <absolute-path-to-DLL>
//   inject-x86.exe --version           — prints build number
//
// Exit codes:
//   0  — DLL loaded; LoadLibraryW returned non-NULL HMODULE (truncated
//        to 32 bits, echoed on stdout as `loaded: 0x...`).
//   1  — argument / file-not-found / OpenProcess / Alloc / Write / Thread
//        failure. Reason goes to stderr.
//   2  — target is x64 (would refuse to inject; that's the x64 injector's
//        job).
//   3  — LoadLibraryW returned NULL — DLL exists but failed its DllMain
//        (e.g. missing dep). Caller should check the per-PID log dir
//        under %LOCALAPPDATA%\discrete\Logs\<pid>\init\current.log.
//
// On a successful run, stdout looks like:
//   inject-x86: pid=12345 wow64=true dll=C:\...\Discrete-Mono.dll
//   inject-x86: remote_alloc=0x00ABC000 size=128
//   inject-x86: LoadLibraryW=0x00DEF000 (thread exit code)
//   inject-x86: OK
//
// Everything except "OK" is informational; the caller (DiscreteUI's
// WindowsDllInjector) just needs exit-code 0 to know it worked.

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

namespace {

constexpr DWORD kAccess =
    PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE |
    PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION;

constexpr DWORD kRemoteThreadWaitMs = 10000;

void Usage() {
    std::fputs(
        "inject-x86 — Discrete DLL injector for 32-bit Windows processes\n\n"
        "USAGE:\n"
        "  inject-x86.exe <PID> <absolute-path-to-DLL>\n"
        "  inject-x86.exe --version\n\n"
        "Exit codes:\n"
        "  0 = DLL loaded\n"
        "  1 = argument / OS / write failure (reason on stderr)\n"
        "  2 = target is x64 (use the x64 injector instead)\n"
        "  3 = LoadLibraryW returned NULL — DllMain failed (check the\n"
        "      per-PID log dir under %LOCALAPPDATA%\\discrete\\Logs)\n",
        stderr);
}

// Stamp the build number into the binary so dist/x86/inject-x86.exe -V
// matches the rest of the artifacts when diagnosing a stale-binary report.
#ifndef DISCRETE_BUILD_NUMBER
#  define DISCRETE_BUILD_NUMBER 0
#endif

int CmdVersion() {
    std::printf("inject-x86 build %d (x86)\n", DISCRETE_BUILD_NUMBER);
    return 0;
}

// NOTE: there is deliberately no narrow-to-wide path conversion here any more.
// We take wchar_t** argv straight from wmain, so the UTF-16 the parent built
// with CreateProcessW arrives byte-for-byte intact.
//
// The previous version took char** argv and decoded it with
// MultiByteToWideChar(CP_UTF8, ...). That was a straight encoding mismatch:
// the UCRT builds narrow argv from GetCommandLineA, i.e. the process ANSI code
// page (CP950 on a Traditional-Chinese install), never UTF-8. Any non-ASCII
// byte decoded as invalid UTF-8 became U+FFFD — silently, because dwFlags did
// not include MB_ERR_INVALID_CHARS — so a game under e.g. D:\遊戲\ failed with
// a misleading "DLL not found" for a path that exists.

// A-L9: Convert UTF-16 strings to UTF-8 for stdout/stderr so non-ASCII paths
// (e.g. CJK game folders) are never truncated by narrow-stream wprintf under
// the default C locale.
std::string WideToUtf8(const std::wstring& w) {
    if (w.empty()) return {};
    int needed = ::WideCharToMultiByte(
        CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<std::size_t>(needed), '\0');
    ::WideCharToMultiByte(
        CP_UTF8, 0, w.data(), static_cast<int>(w.size()), out.data(), needed, nullptr, nullptr);
    return out;
}

// A-L9: Distinguish IsWow64Process API failure (exit 1 with GetLastError) from
// a confirmed x64 target (exit 2), so access-denied errors never report a bogus
// bitness-mismatch message to the UI.
bool QueryTargetIsWow64(HANDLE hProc, bool& outIsWow64, DWORD& outErr) {
    BOOL wow = FALSE;
    if (!::IsWow64Process(hProc, &wow)) {
        outErr = ::GetLastError();
        outIsWow64 = false;
        return false;
    }
    outErr = 0;
    outIsWow64 = (wow == TRUE);
    return true;
}

int CmdInject(int pid, const std::wstring& dllPath) {
    // Verify the DLL exists + is readable from this process. The remote
    // LoadLibraryW will reject any bad path silently, returning NULL, so
    // we surface a clear "file not found" up front when we can.
    DWORD attrs = ::GetFileAttributesW(dllPath.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES) {
        std::fprintf(stderr, "inject-x86: DLL not found at supplied path\n");
        return 1;
    }
    if (attrs & FILE_ATTRIBUTE_DIRECTORY) {
        std::fprintf(stderr, "inject-x86: supplied path is a directory\n");
        return 1;
    }

    HANDLE hProc = ::OpenProcess(kAccess, FALSE, static_cast<DWORD>(pid));
    if (hProc == nullptr) {
        DWORD err = ::GetLastError();
        std::fprintf(stderr,
            "inject-x86: OpenProcess(pid=%d) failed (Win32 error %lu). "
            "Run elevated? Process exited?\n", pid, err);
        return 1;
    }

    bool wow64 = false;
    DWORD wowErr = 0;
    if (!QueryTargetIsWow64(hProc, wow64, wowErr)) {
        std::fprintf(stderr,
            "inject-x86: IsWow64Process(pid=%d) failed (Win32 error %lu). "
            "Run elevated? Process exited?\n", pid, wowErr);
        ::CloseHandle(hProc);
        return 1;
    }

    const std::string dllPathUtf8 = WideToUtf8(dllPath);
    std::printf("inject-x86: pid=%d wow64=%s dll=%s\n",
        pid, wow64 ? "true" : "false", dllPathUtf8.c_str());

    // The injector is x86, so we can ONLY usefully inject into a 32-bit
    // target (== a WOW64 process on x64 Windows). If the target is x64,
    // bail loudly with exit 2; the caller should use the in-process x64
    // injector instead.
    //
    // Edge case: on a 32-bit Windows install, the target is x86 and
    // IsWow64Process returns false (there's no WOW64 layer). We don't
    // ship 32-bit Windows support — that hasn't been a Steam SKU for
    // ~5 years — so treat false == "definitely not a 32-bit process"
    // on the assumption we're on x64 Windows. Better to fail loud than
    // silently inject the wrong-bitness DLL.
    if (!wow64) {
        std::fprintf(stderr,
            "inject-x86: target is NOT a 32-bit process. Refusing to inject "
            "x86 DLL into x64 target. Use the in-process x64 injector for "
            "x64 targets.\n");
        ::CloseHandle(hProc);
        return 2;
    }

    // UTF-16LE + NUL. LoadLibraryW reads exactly until the NUL.
    std::size_t pathBytes = (dllPath.size() + 1) * sizeof(wchar_t);

    LPVOID remoteAddr = ::VirtualAllocEx(
        hProc, nullptr, pathBytes,
        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remoteAddr == nullptr) {
        DWORD err = ::GetLastError();
        std::fprintf(stderr,
            "inject-x86: VirtualAllocEx failed (Win32 error %lu)\n", err);
        ::CloseHandle(hProc);
        return 1;
    }
    std::printf("inject-x86: remote_alloc=0x%08X size=%u\n",
        static_cast<unsigned>(reinterpret_cast<std::uintptr_t>(remoteAddr)),
        static_cast<unsigned>(pathBytes));

    SIZE_T written = 0;
    if (!::WriteProcessMemory(hProc, remoteAddr, dllPath.c_str(),
                              pathBytes, &written) ||
        written != pathBytes) {
        DWORD err = ::GetLastError();
        std::fprintf(stderr,
            "inject-x86: WriteProcessMemory failed (Win32 error %lu, "
            "wrote %zu / %zu bytes)\n", err, written, pathBytes);
        ::VirtualFreeEx(hProc, remoteAddr, 0, MEM_RELEASE);
        ::CloseHandle(hProc);
        return 1;
    }

    // LoadLibraryW lives in kernel32.dll. Both this injector AND the
    // target are x86, so the function pointer we resolve in our address
    // space is valid in the target's address space (kernel32.dll loads
    // at the same base across all WOW64 processes on a given session
    // — KnownDLLs guarantee). The classic remote-thread inject works.
    HMODULE kernel32 = ::GetModuleHandleW(L"kernel32.dll");
    if (kernel32 == nullptr) {
        std::fprintf(stderr,
            "inject-x86: GetModuleHandleW(kernel32) failed\n");
        ::VirtualFreeEx(hProc, remoteAddr, 0, MEM_RELEASE);
        ::CloseHandle(hProc);
        return 1;
    }
    FARPROC loadLibW = ::GetProcAddress(kernel32, "LoadLibraryW");
    if (loadLibW == nullptr) {
        std::fprintf(stderr,
            "inject-x86: GetProcAddress(LoadLibraryW) failed\n");
        ::VirtualFreeEx(hProc, remoteAddr, 0, MEM_RELEASE);
        ::CloseHandle(hProc);
        return 1;
    }

    DWORD threadId = 0;
    HANDLE hThread = ::CreateRemoteThread(
        hProc, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(loadLibW),
        remoteAddr, 0, &threadId);
    if (hThread == nullptr) {
        DWORD err = ::GetLastError();
        std::fprintf(stderr,
            "inject-x86: CreateRemoteThread failed (Win32 error %lu)\n", err);
        ::VirtualFreeEx(hProc, remoteAddr, 0, MEM_RELEASE);
        ::CloseHandle(hProc);
        return 1;
    }

    DWORD wait = ::WaitForSingleObject(hThread, kRemoteThreadWaitMs);
    if (wait != WAIT_OBJECT_0) {
        std::fprintf(stderr,
            "inject-x86: remote thread did not finish within %lu ms "
            "(WaitForSingleObject=%lu)\n", kRemoteThreadWaitMs, wait);
        ::CloseHandle(hThread);
        // DELIBERATELY NOT freeing remoteAddr. A non-signalled wait means the
        // remote thread is STILL ALIVE and remoteAddr is the lpParameter its
        // LoadLibraryW is about to read (ntdll wraps the caller's buffer in a
        // UNICODE_STRING and does not copy it until name resolution). Releasing
        // it here unmaps the page under a live thread → access violation inside
        // ntdll → the GAME dies, turning a failed injection into a lost session.
        // The cost of not freeing is one leaked page (typically 4 KB) in the
        // target, which is the right trade. The other error paths below/above
        // DO free: there the thread either never started or has already exited.
        ::CloseHandle(hProc);
        return 1;
    }

    // On x86 the thread exit code IS the full HMODULE — no truncation
    // surprise like on x64 where the 64-bit HMODULE narrows to the low
    // 32 bits. NULL means LoadLibraryW failed (bad DLL / missing dep).
    DWORD exitCode = 0;
    if (!::GetExitCodeThread(hThread, &exitCode)) {
        std::fprintf(stderr, "inject-x86: GetExitCodeThread failed\n");
        ::CloseHandle(hThread);
        ::VirtualFreeEx(hProc, remoteAddr, 0, MEM_RELEASE);
        ::CloseHandle(hProc);
        return 1;
    }
    std::printf("inject-x86: LoadLibraryW=0x%08X (thread exit code)\n",
        static_cast<unsigned>(exitCode));

    ::CloseHandle(hThread);
    ::VirtualFreeEx(hProc, remoteAddr, 0, MEM_RELEASE);
    ::CloseHandle(hProc);

    if (exitCode == 0) {
        std::fprintf(stderr,
            "inject-x86: LoadLibraryW returned NULL — DLL failed to load. "
            "Check %%LOCALAPPDATA%%\\discrete\\Logs\\%d\\init\\current.log\n",
            pid);
        return 3;
    }

    std::printf("inject-x86: OK\n");
    return 0;
}

}  // namespace

// wmain, NOT main: the DLL path must survive as UTF-16 all the way from the
// parent's CreateProcessW command line to the WriteProcessMemory buffer the
// remote LoadLibraryW reads. Going through narrow argv would re-encode it
// through the ANSI code page and mangle every non-ASCII path.
int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && (std::wcscmp(argv[1], L"--version") == 0 ||
                      std::wcscmp(argv[1], L"-V") == 0)) {
        return CmdVersion();
    }
    if (argc < 3) { Usage(); return 1; }

    wchar_t* end = nullptr;
    long pid = std::wcstol(argv[1], &end, 10);
    if (end == argv[1] || pid <= 0 || pid > 0x7FFFFFFF) {
        const std::string badPid = WideToUtf8(argv[1]);
        std::fprintf(stderr, "inject-x86: invalid PID argument '%s'\n", badPid.c_str());
        return 1;
    }

    std::wstring dllPath = argv[2];
    if (dllPath.empty()) {
        std::fprintf(stderr, "inject-x86: invalid DLL path argument\n");
        return 1;
    }

    return CmdInject(static_cast<int>(pid), dllPath);
}
