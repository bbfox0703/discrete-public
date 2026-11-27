// PipeSecurity.h — Windows named-pipe security descriptor and flag helpers (A-M3).
//
// Extracted into a shared header so both PipeServer.cpp and offline C++
// self-tests (dll/tests/dll_core_test.cpp) exercise the exact same kernel
// security descriptor and CreateNamedPipeW flags on x64 and x86.
#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <sddl.h>

#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::PipeSecurity {

/// Builds a protected DACL SDDL string granting GENERIC_ALL exclusively to
/// SYSTEM (SY) and the current process token's user SID. Falls back to
/// SYSTEM + Creator Owner (CO) if token inspection fails.
inline std::wstring BuildCurrentUserAndSystemSddl() {
    HANDLE token = nullptr;
    if (::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) {
        DWORD needed = 0;
        ::GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
        if (needed > 0) {
            std::vector<std::uint8_t> buf(needed);
            if (::GetTokenInformation(token, TokenUser, buf.data(), needed, &needed)) {
                const auto* tu = reinterpret_cast<const TOKEN_USER*>(buf.data());
                LPWSTR sidStr = nullptr;
                if (tu != nullptr && tu->User.Sid != nullptr &&
                    ::ConvertSidToStringSidW(tu->User.Sid, &sidStr) && sidStr != nullptr) {
                    std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GA;;;";
                    sddl += sidStr;
                    sddl += L")";
                    ::LocalFree(sidStr);
                    ::CloseHandle(token);
                    return sddl;
                }
            }
        }
        ::CloseHandle(token);
    }
    return L"D:P(A;;GA;;;SY)(A;;GA;;;CO)";
}

/// Open-mode flags for CreateNamedPipeW (A-M3).
/// Passing FILE_FLAG_FIRST_PIPE_INSTANCE on the first instance created by a
/// PipeServer guarantees we detect if another process already owns the pipe
/// name instead of silently sharing or hijacking it.
inline DWORD MakePipeOpenMode(bool firstInstance) noexcept {
    DWORD mode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED;
    if (firstInstance) {
        mode |= FILE_FLAG_FIRST_PIPE_INSTANCE;
    }
    return mode;
}

/// Pipe-mode flags for CreateNamedPipeW (A-M3).
/// PIPE_REJECT_REMOTE_CLIENTS blocks network / SMB clients from connecting to
/// the arbitrary-memory-read/write + invoke RPC surface.
inline DWORD MakePipeMode() noexcept {
    return PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS;
}

/// RAII wrapper around a LocalAlloc-backed SECURITY_DESCRIPTOR built from
/// BuildCurrentUserAndSystemSddl().
class ScopedPipeSecurityAttributes {
public:
    ScopedPipeSecurityAttributes() noexcept {
        try {
            sddl_ = BuildCurrentUserAndSystemSddl();
            PSECURITY_DESCRIPTOR sd = nullptr;
            if (::ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl_.c_str(), SDDL_REVISION_1, &sd, nullptr) &&
                sd != nullptr) {
                sa_.nLength              = sizeof(SECURITY_ATTRIBUTES);
                sa_.lpSecurityDescriptor = sd;
                sa_.bInheritHandle       = FALSE;
                valid_                   = true;
            }
        } catch (...) {
            valid_ = false;
        }
    }

    ~ScopedPipeSecurityAttributes() noexcept {
        if (sa_.lpSecurityDescriptor != nullptr) {
            ::LocalFree(sa_.lpSecurityDescriptor);
            sa_.lpSecurityDescriptor = nullptr;
        }
    }

    ScopedPipeSecurityAttributes(const ScopedPipeSecurityAttributes&)            = delete;
    ScopedPipeSecurityAttributes& operator=(const ScopedPipeSecurityAttributes&) = delete;

    bool IsValid() const noexcept { return valid_; }
    const std::wstring& Sddl() const noexcept { return sddl_; }

    SECURITY_ATTRIBUTES* Get() noexcept {
        return valid_ ? &sa_ : nullptr;
    }

private:
    SECURITY_ATTRIBUTES sa_{};
    std::wstring        sddl_;
    bool                valid_ = false;
};

}  // namespace Discrete::PipeSecurity
