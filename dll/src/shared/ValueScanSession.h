// ValueScanSession.h — Phase 27b session container for CE-style First Scan /
// Next Scan workflow. The session holds the candidate list between RPCs so
// the client can `refineValueScan(sessionId, scanType)` instead of
// re-running the full memory scan on every step.
//
// Lifecycle:
//   beginValueScan   → SessionManager::Begin returns a new sessionId.
//   refineValueScan  → SessionManager::RefineWith runs the caller's
//                      comparator against the cached candidates,
//                      pruning in place.
//   endValueScan     → SessionManager::End drops the session immediately.
//
// Sessions also auto-expire after kExpirySeconds of inactivity (5 min) so
// a misbehaving client can't leak memory. Manager holds a global mutex;
// session ownership is via unique_ptr to keep the move semantics cheap
// when refining (the caller's lambda runs inside the lock holding a
// reference to the candidates vector — typical refine reads ~50K
// addresses via SafeReadBytes which is fast enough that a coarse lock
// is acceptable for v1).
#pragma once

#include "Memory.h"

#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace Discrete::ValueScan {

// One scan candidate as remembered between RPCs. We cache the metadata
// resolved by whatIsAt on the FIRST scan so refine rounds don't have to
// re-resolve (which is expensive — backward scan + name probe per address).
// The `prevValue` slot is updated to the LATEST read value on every
// successful refine, so changed/unchanged/etc. always compare against
// "what we saw last time we looked at this slot".
struct Candidate {
    std::uintptr_t addr        = 0;     // memory address of the value
    std::uintptr_t instAddr    = 0;     // managed instance base (whatIsAt)
    std::int64_t   fieldOffset = 0;     // bytes from instance base
    std::uint8_t   prevValue[8]= {};    // last-observed bytes; size = ValueDataTypeSize(dt)
    std::string    asmName;             // assembly name (no .dll suffix)
    std::string    ns;                  // namespace
    std::string    className;
    std::string    field;
    std::string    fieldType;           // CLR-style typename
    std::string    fieldKind;           // "leaf"|"interior"|"near"|"value-struct"|...
    std::string    nearestFieldName;    // populated when fieldKind=="near"
    std::int64_t   nearestFieldDelta = 0;
};

struct Session {
    std::uint64_t                          id        = 0;
    Memory::ValueDataType                  dt        = Memory::ValueDataType::Int32;
    std::vector<Candidate>                 candidates;
    std::chrono::steady_clock::time_point  lastUse;
};

class SessionManager {
public:
    static SessionManager& Instance();

    // Allocate a new session, take ownership of the candidate vector,
    // return its sessionId. Triggers a lazy expiry pass on existing
    // sessions so abandoned sessions don't accumulate.
    std::uint64_t Begin(Memory::ValueDataType dt, std::vector<Candidate> candidates);

    // Run `fn(dataType, candidates&)` under the manager's lock. Returns
    // false if the session doesn't exist (caller should map to wire
    // error "session_not_found"). `fn` may mutate the candidates vector
    // (typical Refine behavior is to prune entries that no longer match).
    template <typename Fn>
    bool RefineWith(std::uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        it->second->lastUse = std::chrono::steady_clock::now();
        fn(it->second->dt, it->second->candidates);
        return true;
    }

    // Read-only access — same lock as RefineWith but caller promises not
    // to mutate (used by Begin's optional "echo back the row set" path).
    template <typename Fn>
    bool ViewWith(std::uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        fn(it->second->dt, it->second->candidates);
        return true;
    }

    // Drop a specific session. Returns false if not found.
    bool End(std::uint64_t sessionId);

    // Drop sessions whose lastUse is older than kExpirySeconds.
    // Called lazily from Begin().
    void ExpireOldSessions();

    // Drop every session — called from pipe shutdown / DLL unload.
    void DropAll();

    // Diagnostic snapshot.
    struct Stats {
        std::size_t   sessionCount    = 0;
        std::size_t   totalCandidates = 0;
        std::uint64_t nextId          = 0;
    };
    Stats GetStats();

private:
    SessionManager() = default;
    SessionManager(const SessionManager&) = delete;
    SessionManager& operator=(const SessionManager&) = delete;

    static constexpr std::chrono::seconds kExpirySeconds{300};

    std::mutex                                                       mu_;
    std::unordered_map<std::uint64_t, std::unique_ptr<Session>>      sessions_;
    std::uint64_t                                                    nextId_ = 1;
};

}  // namespace Discrete::ValueScan
