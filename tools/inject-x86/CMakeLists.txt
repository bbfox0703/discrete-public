# inject-x86 — Phase 12d-5. Tiny x86 helper that DiscreteUI shells out to
# when the user picks an x86 Mono target via Pick & Inject (DiscreteUI is
# x64 .NET-AOT and can't cleanly CreateRemoteThread into an x86 process —
# the WOW64 boundary means the LoadLibraryW we'd resolve in our address
# space isn't valid in the target's, plus the 32-bit HMODULE return value
# can't round-trip through the x64 thread API).
#
# Always built as a self-contained executable: no shared dependencies on
# anything in dll/src/, no need for the backend detector or the pipe
# server. Pure Win32 inject (OpenProcess → VirtualAllocEx →
# WriteProcessMemory → CreateRemoteThread). Build via
# `build.ps1 -Target DLL -Arch x86` (it's bundled into the x86 path —
# users always need it alongside the x86 backend DLL).

add_executable(inject-x86 main.cpp)

set_target_properties(inject-x86 PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# kernel32 only — no other deps. Linking explicitly to keep the import
# table minimal (smaller .exe).
target_link_libraries(inject-x86 PRIVATE kernel32)
