// MonoThreadAttach.h — RAII guard around mono_thread_attach / mono_thread_detach.
// Wrap any call into Mono runtime exports made from a thread that the runtime
// did not create (i.e. our pipe worker) with one of these. Forgetting this
// is the #1 source of crashes when calling Mono APIs from injected code.
//
// Policy (Phase 14d audit):
//   - Every Mono helper function in dll/src/mono/ that calls into the
//     runtime opens its own ThreadAttachGuard internally. Callers should
//     NOT wrap those helpers in an outer guard — nested attach/detach
//     destroyed BongoCat in build #54.
//   - Pipe handlers (ExportAPI.cpp) that call resolver functions DIRECTLY
//     (e.g. r.class_get_name, r.class_value_size) must hold their own
//     guard around the batch of direct calls. Find* / Enumerate* helpers
//     are called BEFORE that guard opens, since they manage their own.
//   - Each guard handles one attach + one detach. A failed attach
//     (Attached() == false) means "skip the runtime calls and bail" —
//     don't try to use the resolver pointer anyway.
//
// Phase 20-MT extension: when the dispatcher's Drain is currently
// invoking a work item on the Unity main thread, we MUST skip attach +
// detach — the main thread is already attached to the runtime, and
// nested attach/detach tears down Unity's own thread handle (BongoCat
// crashed first thing on the main-thread burst smoke; same failure
// mode as build #54).
#pragma once

#include "MonoResolver.h"
#include "MonoTypes.h"
#include "../shared/MainThreadDispatcher.h"

namespace UnityCEDumper::Mono {

class ThreadAttachGuard {
public:
    explicit ThreadAttachGuard(MonoDomain* domain) {
        // Phase 20-MT: never attach on the Unity main thread inside a
        // dispatcher work item. The thread is already attached; attach
        // there is at best a no-op, at worst (and observed live on
        // BongoCat 2026-05-20) a process-killer because the matching
        // detach unbinds Unity's own thread handle.
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
        if (r.thread_detach != nullptr) {
            r.thread_detach(thread_);
        }
    }

    ThreadAttachGuard(const ThreadAttachGuard&)            = delete;
    ThreadAttachGuard& operator=(const ThreadAttachGuard&) = delete;

    /// True if the guard either successfully attached the thread OR
    /// deliberately skipped attach because we're already on the main
    /// thread (in which case runtime calls are still safe to make).
    bool Attached() const noexcept { return thread_ != nullptr || skipped_; }

private:
    MonoThread* thread_  = nullptr;
    bool        skipped_ = false;  // true = main-thread, no attach needed
};

}  // namespace UnityCEDumper::Mono
