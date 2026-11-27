// SehFilter.h — targeted Win32 SEH exception filter for memory probes (A-M6, A-L14).
//
// Using raw `__except(EXCEPTION_EXECUTE_HANDLER)` has two hazards:
//   1. It swallows MSVC C++ exceptions (0xE06D7363, e.g. std::bad_alloc on x86)
//      as if they were memory read failures, leaking exception objects and
//      silently truncating scan results (A-M6).
//   2. It swallows one-shot STATUS_GUARD_PAGE_VIOLATION (0x80000001) after the
//      OS clears PAGE_GUARD — if a TOCTOU or cached-readability probe touched
//      another thread's stack guard page, that thread can no longer grow its
//      stack (A-L14).
//
// This filter handles ONLY memory-fault codes, restores PAGE_GUARD on the exact
// faulted page before handling STATUS_GUARD_PAGE_VIOLATION, and returns
// EXCEPTION_CONTINUE_SEARCH for all other exceptions (including C++ throws).
#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

namespace Discrete::Seh {

inline int MemoryAccessExceptionFilter(unsigned long code,
                                       const EXCEPTION_POINTERS* ep) noexcept {
    if (code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_IN_PAGE_ERROR) {
        return EXCEPTION_EXECUTE_HANDLER;
    }
    if (code == STATUS_GUARD_PAGE_VIOLATION) {
        // Restore PAGE_GUARD on the exact faulted page so we never strip a
        // guard page (e.g. another thread's stack guard) on a TOCTOU hit.
        if (ep != nullptr && ep->ExceptionRecord != nullptr &&
            ep->ExceptionRecord->NumberParameters >= 2) {
            void* faultAddr = reinterpret_cast<void*>(
                ep->ExceptionRecord->ExceptionInformation[1]);
            MEMORY_BASIC_INFORMATION mbi{};
            if (::VirtualQuery(faultAddr, &mbi, sizeof(mbi)) != 0 &&
                mbi.State == MEM_COMMIT &&
                (mbi.Protect & PAGE_GUARD) == 0) {
                DWORD oldProt = 0;
                // Passing `faultAddr` with size 1 makes VirtualProtect act on
                // ONLY the single page containing `faultAddr`.
                ::VirtualProtect(faultAddr, 1, mbi.Protect | PAGE_GUARD, &oldProt);
            }
        }
        return EXCEPTION_EXECUTE_HANDLER;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

}  // namespace Discrete::Seh
