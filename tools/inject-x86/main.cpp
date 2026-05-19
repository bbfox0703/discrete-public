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
//        under %LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\init\current.log.
//
// On a successful run, stdout looks like:
//   inject-x86: pid=12345 wow64=true dll=C:\...\UnityDumper-Mono.dll
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
        "inject-x86 — UnityCEDumper DLL injector for 32-bit Windows processes\n\n"
        "USAGE:\n"
        "  inject-x86.exe <PID> <absolute-path-to-DLL>\n"
        "  inject-x86.exe --version\n\n"
        "Exit codes:\n"
        "  0 = DLL loaded\n"
        "  1 = argument / OS / write failure (reason on stderr)\n"
        "  2 = target is x64 (use the x64 injector instead)\n"
        "  3 = LoadLibraryW returned NULL — DllMain failed (check the\n"
        "      per-PID log dir under %LOCALAPPDATA%\\UnityCEDumper\\Logs)\n",
        stderr);
}

// Stamp the build number into the binary so dist/x86/inject-x86.exe -V
// matches the rest of the artifacts when diagnosing a stale-binary report.
#ifndef UNITYCEDUMPER_BUILD_NUMBER
#  define UNITYCEDUMPER_BUILD_NUMBER 0
#endif

int CmdVersion() {
    std::printf("inject-x86 build %d (x86)\n", UNITYCEDUMPER_BUILD_NUMBER);
    return 0;
}

// Resolve the absolute, UTF-16 path to the DLL. The injected target reads
// the bytes we write via WriteProcessMemory and passes that pointer to
// LoadLibraryW, so the encoding must be UTF-16LE + NUL-terminated.
std::wstring Utf8ToWidePath(const char* utf8) {
    if (utf8 == nullptr) return {};
    int n = ::MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring out(static_cast<std::size_t>(n - 1), L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, utf8, -1, out.data(), n);
    return out;
}

bool TargetIsWow64(HANDLE hProc) {
    BOOL wow = FALSE;
    if (!::IsWow64Process(hProc, &wow)) {
        // Older Windows or access-denied → treat as unknown. The caller
        // verifies via bitness probe in C# anyway; this is a defensive
        // guard for the pathological case (Windows version too old to
        // expose IsWow64Process, which would be Win XP SP2 — pre-modern).
        return false;
    }
    return wow == TRUE;
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

    bool wow64 = TargetIsWow64(hProc);
    std::wprintf(L"inject-x86: pid=%d wow64=%s dll=%s\n",
        pid, wow64 ? L"true" : L"false", dllPath.c_str());

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
            "inject-x86: target is NOT a 32-bit process (or the 32-bit "
            "check failed). Refusing to inject x86 DLL into x64 target. "
            "Use the in-process x64 injector for x64 targets.\n");
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
        ::VirtualFreeEx(hProc, remoteAddr, 0, MEM_RELEASE);
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
            "Check %%LOCALAPPDATA%%\\UnityCEDumper\\Logs\\%d\\init\\current.log\n",
            pid);
        return 3;
    }

    std::printf("inject-x86: OK\n");
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc == 2 && (std::strcmp(argv[1], "--version") == 0 ||
                      std::strcmp(argv[1], "-V") == 0)) {
        return CmdVersion();
    }
    if (argc < 3) { Usage(); return 1; }

    char* end = nullptr;
    long pid = std::strtol(argv[1], &end, 10);
    if (end == argv[1] || pid <= 0 || pid > 0x7FFFFFFF) {
        std::fprintf(stderr, "inject-x86: invalid PID argument '%s'\n", argv[1]);
        return 1;
    }

    std::wstring dllPath = Utf8ToWidePath(argv[2]);
    if (dllPath.empty()) {
        std::fprintf(stderr, "inject-x86: invalid DLL path argument\n");
        return 1;
    }

    return CmdInject(static_cast<int>(pid), dllPath);
}
