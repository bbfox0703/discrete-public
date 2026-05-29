// SnapshotSession.cpp — implementation of the Phase 29 session container.

#include "SnapshotSession.h"

#include <windows.h>

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <random>

namespace Discrete::Snapshot {

SessionManager& SessionManager::Instance() {
    // Heap-allocated, intentionally leaked singleton — same rationale as
    // ValueScanSession's manager: the plan vector can hold tens of MB of
    // small allocations (per-instance address vectors + string class
    // names), and running the destructor chain over those at process
    // exit can make the game APPEAR to hang during shutdown.
    static SessionManager* inst = new SessionManager();
    return *inst;
}

std::uint64_t SessionManager::Begin(std::unique_ptr<Session> session) {
    ExpireOldSessions();
    std::lock_guard<std::mutex> lk(mu_);
    std::uint64_t id = nextId_++;
    session->id      = id;
    session->lastUse = std::chrono::steady_clock::now();
    sessions_.emplace(id, std::move(session));
    return id;
}

bool SessionManager::End(std::uint64_t sessionId) {
    std::lock_guard<std::mutex> lk(mu_);
    return sessions_.erase(sessionId) > 0;
}

void SessionManager::ExpireOldSessions() {
    auto now = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lk(mu_);
    for (auto it = sessions_.begin(); it != sessions_.end(); ) {
        if (now - it->second->lastUse > kExpirySeconds) {
            it = sessions_.erase(it);
        } else {
            ++it;
        }
    }
}

void SessionManager::DropAll() {
    std::lock_guard<std::mutex> lk(mu_);
    sessions_.clear();
}

SessionManager::Stats SessionManager::GetStats() {
    Stats s;
    std::lock_guard<std::mutex> lk(mu_);
    s.sessionCount = sessions_.size();
    s.nextId       = nextId_;
    for (const auto& kv : sessions_) {
        s.totalPlanItems += kv.second->plan.size();
    }
    return s;
}

const std::string& SessionManager::GameSessionId() {
    // Generated once per DLL load. Cheap RNG; this isn't a security token,
    // just a way for the UI to detect "same game launch" vs "after restart".
    static const std::string id = []() -> std::string {
        std::random_device rd;
        std::mt19937_64 gen(rd());
        char buf[17];
        std::snprintf(buf, sizeof(buf), "%016llx",
                      static_cast<unsigned long long>(gen()));
        return std::string(buf);
    }();
    return id;
}

// FNV-1a 64-bit. Used for build-id derivation — fast, no external deps,
// good-enough collision resistance for cross-machine "same build?" checks.
static std::uint64_t Fnv1a64(const void* data, std::size_t n) {
    constexpr std::uint64_t kOffset = 14695981039346656037ull;
    constexpr std::uint64_t kPrime  = 1099511628211ull;
    const std::uint8_t* p = static_cast<const std::uint8_t*>(data);
    std::uint64_t h = kOffset;
    for (std::size_t i = 0; i < n; ++i) {
        h ^= static_cast<std::uint64_t>(p[i]);
        h *= kPrime;
    }
    return h;
}

std::string ComputeGameBuildId(const wchar_t* moduleName) {
    // Resolve module → on-disk path → (size, write_time). FNV-1a over the
    // path+metadata triple gives us a stable per-build hash without paying
    // SHA256 cost.
    HMODULE mod = (moduleName != nullptr) ? ::GetModuleHandleW(moduleName) : nullptr;
    if (mod == nullptr) {
        // Fallback to host EXE so we still emit something usable.
        mod = ::GetModuleHandleW(nullptr);
    }

    wchar_t pathBuf[MAX_PATH] = {};
    DWORD pathLen = (mod != nullptr)
        ? ::GetModuleFileNameW(mod, pathBuf, MAX_PATH)
        : 0u;

    std::uint64_t fileSize     = 0;
    std::uint64_t lastWriteRaw = 0;
    if (pathLen > 0 && pathLen < MAX_PATH) {
        WIN32_FILE_ATTRIBUTE_DATA fa{};
        if (::GetFileAttributesExW(pathBuf, GetFileExInfoStandard, &fa)) {
            fileSize     = (static_cast<std::uint64_t>(fa.nFileSizeHigh) << 32) |
                            static_cast<std::uint64_t>(fa.nFileSizeLow);
            lastWriteRaw = (static_cast<std::uint64_t>(fa.ftLastWriteTime.dwHighDateTime) << 32) |
                            static_cast<std::uint64_t>(fa.ftLastWriteTime.dwLowDateTime);
        }
    }

    std::uint64_t h = Fnv1a64(pathBuf, static_cast<std::size_t>(pathLen) * sizeof(wchar_t));
    h ^= Fnv1a64(&fileSize,     sizeof(fileSize));
    h ^= Fnv1a64(&lastWriteRaw, sizeof(lastWriteRaw));

    char hex[17];
    std::snprintf(hex, sizeof(hex), "%016llx",
                  static_cast<unsigned long long>(h));
    return std::string(hex);
}

std::string CapturedAtUtcNow() {
    // Use std::chrono for time acquisition; format via gmtime_s so we
    // don't pull in std::format (AOT/trim-friendly + matches the rest
    // of the codebase's printf-style formatting).
    using clock = std::chrono::system_clock;
    auto now = clock::now();
    std::time_t t = clock::to_time_t(now);
    std::tm tm{};
    gmtime_s(&tm, &t);
    char buf[32];
    std::snprintf(buf, sizeof(buf),
                  "%04d-%02d-%02dT%02d:%02d:%02dZ",
                  tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday,
                  tm.tm_hour, tm.tm_min, tm.tm_sec);
    return std::string(buf);
}

}  // namespace Discrete::Snapshot
