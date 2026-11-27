// RuntimeHealth.h — process-wide runtime lock/corruption poison latch (B-M6).
//
// When a Win32 SEH memory fault (ACCESS_VIOLATION / IN_PAGE_ERROR / GUARD_PAGE)
// unwinds out of a lock-acquiring Mono/IL2CPP runtime API (e.g.
// mono_assembly_foreach, mono_class_vtable, *_class_get_fields,
// *_runtime_invoke), internal runtime mutexes (assemblies lock, loader lock,
// class-init lock, GC lock) may have been leaked in a locked state. Silently
// continuing causes subsequent RPCs or game threads to deadlock indefinitely.
//
// Latching a poisoned flag on such faults lets ExportAPI fail-fast heavy RPCs
// with an actionable "restart the game" error while keeping lightweight
// diagnostics (handshake, cancel, log read) responsive.
#pragma once

#include <atomic>
#include <cstdio>
#include <string>

namespace Discrete::RuntimeHealth {

namespace detail {
inline std::atomic<bool>          g_poisoned{false};
inline std::atomic<const char*>   g_operation{nullptr};
inline std::atomic<unsigned long> g_exceptionCode{0ul};
inline std::atomic<unsigned long> g_faultCount{0ul};
}  // namespace detail

struct Snapshot {
    bool          poisoned      = false;
    const char*   operation     = "";
    unsigned long exceptionCode = 0ul;
    unsigned long faultCount    = 0ul;
};

// Safe to call immediately after catching a Win32 SEH fault (no allocations,
// noexcept). Preserves the first faulting operation as root cause while
// incrementing the total fault counter.
inline void MarkRuntimeFault(const char* operation, unsigned long exceptionCode) noexcept {
    detail::g_faultCount.fetch_add(1ul, std::memory_order_relaxed);
    const char* expected = nullptr;
    const char* op = (operation != nullptr) ? operation : "runtime_call";
    if (detail::g_operation.compare_exchange_strong(
            expected, op, std::memory_order_acq_rel, std::memory_order_acquire)) {
        detail::g_exceptionCode.store(exceptionCode, std::memory_order_release);
    }
    detail::g_poisoned.store(true, std::memory_order_release);
}

inline bool IsPoisoned() noexcept {
    return detail::g_poisoned.load(std::memory_order_acquire);
}

inline Snapshot GetSnapshot() noexcept {
    Snapshot s{};
    s.poisoned      = detail::g_poisoned.load(std::memory_order_acquire);
    const char* op  = detail::g_operation.load(std::memory_order_acquire);
    s.operation     = (op != nullptr) ? op : "";
    s.exceptionCode = detail::g_exceptionCode.load(std::memory_order_acquire);
    s.faultCount    = detail::g_faultCount.load(std::memory_order_acquire);
    return s;
}

inline std::string FormatReason() {
    const Snapshot s = GetSnapshot();
    if (!s.poisoned) return {};
    char buf[256];
    ::_snprintf_s(buf, sizeof(buf), _TRUNCATE,
                  "SEH fault 0x%08lX inside %s (count=%lu); runtime locks may be leaked — restart the game",
                  s.exceptionCode,
                  (s.operation && *s.operation) ? s.operation : "runtime_call",
                  s.faultCount);
    return std::string(buf);
}

// Offline unit-test hook only.
inline void ResetForTests() noexcept {
    detail::g_poisoned.store(false, std::memory_order_release);
    detail::g_operation.store(nullptr, std::memory_order_release);
    detail::g_exceptionCode.store(0ul, std::memory_order_release);
    detail::g_faultCount.store(0ul, std::memory_order_release);
}

}  // namespace Discrete::RuntimeHealth
