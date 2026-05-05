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
#pragma once

#include "MonoResolver.h"
#include "MonoTypes.h"

namespace UnityCEDumper::Mono {

class ThreadAttachGuard {
public:
    explicit ThreadAttachGuard(MonoDomain* domain) {
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

    bool Attached() const noexcept { return thread_ != nullptr; }

private:
    MonoThread* thread_ = nullptr;
};

}  // namespace UnityCEDumper::Mono
