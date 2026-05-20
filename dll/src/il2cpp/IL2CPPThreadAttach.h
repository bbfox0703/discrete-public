// IL2CPPThreadAttach.h — RAII guard around il2cpp_thread_attach / detach.
// IL2CPP also requires the calling thread to be attached for runtime APIs
// that touch managed state.
//
// Phase 20-MT extension: when the dispatcher's Drain is currently
// invoking a work item on the Unity main thread, we MUST skip attach +
// detach — the main thread is already attached to the runtime, and
// nested attach/detach tears down Unity's own thread handle (mirror of
// the Mono build #54 forensic; same failure mode reproduces on IL2CPP).
#pragma once

#include "IL2CPPResolver.h"
#include "IL2CPPTypes.h"
#include "../shared/MainThreadDispatcher.h"

namespace UnityCEDumper::IL2CPP {

class ThreadAttachGuard {
public:
    explicit ThreadAttachGuard(Il2CppDomain* domain) {
        if (UnityCEDumper::MainThreadDispatcher::IsCurrentlyDrainingOnMainThread()) {
            mainThreadSkip_ = true;
            return;
        }
        // Phase 27 follow-up — reentrancy counter (mirror of the Mono
        // guard's fix). 4000+ per-candidate WhatIsAt attaches were
        // leaking finalizable Il2CppThread refs that BDWGC ran on
        // process shutdown, accumulating a 30-40s hang. Outer scan-
        // handler guard does ONE real attach; inner guards become
        // counter-only no-ops.
        if (s_attach_depth > 0) {
            ++s_attach_depth;
            reentrant_ = true;
            return;
        }
        const auto& r = GetResolver();
        if (r.resolved && domain != nullptr && r.thread_attach != nullptr) {
            thread_ = r.thread_attach(domain);
            if (thread_ != nullptr) ++s_attach_depth;
        }
    }
    ~ThreadAttachGuard() {
        if (reentrant_) {
            --s_attach_depth;
            return;
        }
        if (thread_ != nullptr) {
            --s_attach_depth;
            const auto& r = GetResolver();
            if (r.thread_detach != nullptr) r.thread_detach(thread_);
            return;
        }
        // mainThreadSkip_ or failed-attach: nothing to do.
    }
    ThreadAttachGuard(const ThreadAttachGuard&)            = delete;
    ThreadAttachGuard& operator=(const ThreadAttachGuard&) = delete;
    /// True if the guard either successfully attached OR deliberately
    /// skipped because we're already on the main thread / outer-guard
    /// reentrant scope.
    bool Attached() const noexcept {
        return thread_ != nullptr || mainThreadSkip_ || reentrant_;
    }
private:
    Il2CppThread* thread_         = nullptr;
    bool          mainThreadSkip_ = false;
    bool          reentrant_      = false;
    inline static thread_local int s_attach_depth = 0;
};

}  // namespace UnityCEDumper::IL2CPP
