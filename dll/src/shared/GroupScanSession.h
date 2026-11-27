// GroupScanSession.h — session container for the "Multiple values group scan"
// First Scan / Next Scan workflow (the group analogue of ValueScanSession.h).
//
// A group session holds, per CANDIDATE INSTANCE, the set of numeric fields
// ("leaves") that matched any slot on the first scan, plus each slot's
// convergence list (slot -> leaf indices). Refine re-reads each leaf and shrinks
// the per-slot lists; a candidate survives only while a System of Distinct
// Representatives still exists across all slots (Discrete::GroupMatch).
//
// Lifecycle (mirrors ValueScan::SessionManager):
//   beginGroupScan   -> SessionManager::Begin returns a new sessionId.
//   refineGroupScan  -> SessionManager::RefineWith re-reads leaves, applies each
//                       slot's (possibly prev-value) predicate, re-checks the SDR.
//   queryGroupCandidates -> SessionManager::ViewWith pages the candidate view.
//   endGroupScan     -> SessionManager::End drops the session immediately.
//
// Sessions auto-expire after kExpirySeconds of inactivity (5 min) so a
// misbehaving client can't leak memory. The manager holds a coarse global mutex
// (a refine re-reads a bounded candidate set via SafeReadBytes — fast enough).
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

namespace Discrete::GroupScan {

// One slot's fixed spec, captured at first scan. `dt` is the slot's data type
// (each slot scans/refines at its own width, exactly like a single value scan);
// refine packs its value(s) at this `dt`. The first-scan predicate is always
// exact/range; the prev-value predicates (changed/unchanged/increased/decreased)
// arrive per-slot on refine.
struct SlotSpec {
    Memory::ValueDataType dt = Memory::ValueDataType::Int32;
};

// One numeric field of a candidate instance that matched >= 1 slot. `addr` is the
// absolute field address (the distinctness key — one leaf per address);
// `prevValue` is updated to the latest read on every successful refine so
// changed/increased/etc. compare against "what we saw last time".
struct Leaf {
    std::uintptr_t        addr        = 0;
    std::int64_t          fieldOffset = 0;
    Memory::ValueDataType width       = Memory::ValueDataType::Int32;  // the field's own width — drives refine compare + decode (a slot may scan several widths in aggregate mode)
    std::string           field;
    std::string           fieldType;  // CLR-style typename
    std::string           fieldKind;  // "leaf" | "value-struct" | ...
    std::uint8_t          prevValue[8] = {};
};

// One candidate instance: its class identity + the matched leaves + each slot's
// convergence list (slot index -> indices into `leaves`).
struct Candidate {
    std::uintptr_t instAddr = 0;
    std::string    asmName;
    std::string    ns;
    std::string    className;
    std::vector<Leaf>             leaves;
    std::vector<std::vector<int>> slotLeaves;   // size == session.slots.size()
};

struct Session {
    std::uint64_t                          id            = 0;
    std::uint64_t                          ownerClientId = 0;  // C-L6: 0 = unowned/test
    std::vector<SlotSpec>                  slots;       // 2..4
    std::vector<Candidate>                 candidates;
    std::chrono::steady_clock::time_point  lastUse;
};

class SessionManager {
public:
    static SessionManager& Instance();

    // Allocate a new session, take ownership of the candidate vector, return its
    // sessionId. Triggers a lazy expiry pass.
    std::uint64_t Begin(std::vector<SlotSpec> slots,
                        std::vector<Candidate> candidates,
                        std::uint64_t ownerClientId = 0);

    // Run `fn(slots, candidates&)` under the manager's lock. Returns false if the
    // session doesn't exist, expired, or belongs to a different client (C-L6).
    template <typename Fn>
    bool RefineWith(std::uint64_t sessionId, Fn&& fn, std::uint64_t callerClientId = 0) {
        std::lock_guard<std::mutex> lk(mu_);
        ExpireOldSessionsLocked(std::chrono::steady_clock::now());
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        if (it->second->ownerClientId != 0 && callerClientId != 0 &&
            it->second->ownerClientId != callerClientId) {
            return false;
        }
        it->second->lastUse = std::chrono::steady_clock::now();
        fn(it->second->slots, it->second->candidates);
        return true;
    }

    // Read-only access — same lock as RefineWith but caller promises not to mutate.
    template <typename Fn>
    bool ViewWith(std::uint64_t sessionId, Fn&& fn, std::uint64_t callerClientId = 0) {
        std::lock_guard<std::mutex> lk(mu_);
        ExpireOldSessionsLocked(std::chrono::steady_clock::now());
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        if (it->second->ownerClientId != 0 && callerClientId != 0 &&
            it->second->ownerClientId != callerClientId) {
            return false;
        }
        it->second->lastUse = std::chrono::steady_clock::now();
        fn(it->second->slots, it->second->candidates);
        return true;
    }

    // Drop a specific session (enforces ownerClientId when both are non-zero).
    bool End(std::uint64_t sessionId, std::uint64_t callerClientId = 0);

    // C-L6: Drop all sessions owned by a disconnecting client.
    std::size_t DropForOwner(std::uint64_t ownerClientId);

    // Drop sessions whose lastUse is older than kExpirySeconds.
    void ExpireOldSessions();

    // Drop every session — pipe shutdown / DLL unload.
    void DropAll();

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

    void ExpireOldSessionsLocked(std::chrono::steady_clock::time_point now) {
        for (auto it = sessions_.begin(); it != sessions_.end(); ) {
            if (now - it->second->lastUse > kExpirySeconds) {
                it = sessions_.erase(it);
            } else {
                ++it;
            }
        }
    }

    static constexpr std::chrono::seconds kExpirySeconds{300};

    std::mutex                                                  mu_;
    std::unordered_map<std::uint64_t, std::unique_ptr<Session>> sessions_;
    std::uint64_t                                               nextId_ = 1;
};

}  // namespace Discrete::GroupScan
