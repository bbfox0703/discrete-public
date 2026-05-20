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
            skipped_ = true;
            return;
        }
        const auto& r = GetResolver();
        if (r.resolved && domain != nullptr && r.thread_attach != nullptr) {
            thread_ = r.thread_attach(domain);
        }
    }
    ~ThreadAttachGuard() {
        if (thread_ == nullptr) return;
        const auto& r = GetResolver();
        if (r.thread_detach != nullptr) r.thread_detach(thread_);
    }
    ThreadAttachGuard(const ThreadAttachGuard&)            = delete;
    ThreadAttachGuard& operator=(const ThreadAttachGuard&) = delete;
    /// True if the guard either successfully attached OR deliberately
    /// skipped because we're already on the main thread.
    bool Attached() const noexcept { return thread_ != nullptr || skipped_; }
private:
    Il2CppThread* thread_  = nullptr;
    bool          skipped_ = false;
};

}  // namespace UnityCEDumper::IL2CPP
