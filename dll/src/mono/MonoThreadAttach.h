// MonoThreadAttach.h — RAII guard around mono_thread_attach / mono_thread_detach.
// Wrap any call into Mono runtime exports made from a thread that the runtime
// did not create (i.e. our pipe worker) with one of these. Forgetting this
// is the #1 source of crashes when calling Mono APIs from injected code.
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
