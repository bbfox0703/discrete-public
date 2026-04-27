// IL2CPPThreadAttach.h — RAII guard around il2cpp_thread_attach / detach.
// IL2CPP also requires the calling thread to be attached for runtime APIs
// that touch managed state.
#pragma once

#include "IL2CPPResolver.h"
#include "IL2CPPTypes.h"

namespace UnityCEDumper::IL2CPP {

class ThreadAttachGuard {
public:
    explicit ThreadAttachGuard(Il2CppDomain* domain) {
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
    bool Attached() const noexcept { return thread_ != nullptr; }
private:
    Il2CppThread* thread_ = nullptr;
};

}  // namespace UnityCEDumper::IL2CPP
