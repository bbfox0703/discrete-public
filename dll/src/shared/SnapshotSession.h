// SnapshotSession.h — Phase 29 session container for "全抓" snapshot &
// diff. Mirrors the Phase 27b ValueScanSession pattern: Begin populates
// a per-class plan (class metadata + cached instance address list); Chunk
// streams per-class decoded data (statics + per-instance field reads +
// array elements) to keep individual RPC latency bounded; End frees the
// cached plan.
//
// Lifecycle:
//   snapshotBegin  → SessionManager::Begin runs ONE wide heap walk to
//                    group every live managed instance by its class
//                    pointer (FindAllInstances on the active backend).
//                    Returns sessionId + total counts + per-assembly
//                    plan for progress display.
//   snapshotChunk  → SessionManager::View walks the next N plan rows,
//                    decodes statics + instances + arrays inline,
//                    advances cursor in place.
//   snapshotEnd    → SessionManager::End drops the cached plan.
//
// Sessions also auto-expire after kExpirySeconds of inactivity (5 min)
// so a misbehaving client can't leak the heap-walk result. Manager
// holds a global mutex; session ownership is via unique_ptr to keep
// move semantics cheap when chunking. The plan vector is what eats
// memory (~10s of MB for a SaGa F2R-scale snapshot with ~3K classes
// × ~100 instance addresses each) so swap-on-move matters.
#pragma once

#include <nlohmann/json.hpp>

#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace UnityCEDumper::Snapshot {

// One class's contribution to a snapshot plan. The instance address list
// is pre-populated at Begin time by the wide heap walk so chunk emit
// doesn't have to re-scan. klass is the raw backend-specific class
// pointer (Il2CppClass* / MonoClass*) — cast at the chunk emit site.
struct ClassPlanItem {
    std::string                  assembly;       // e.g. "Assembly-CSharp"
    std::string                  ns;             // namespace; empty for global
    std::string                  name;           // class name (slash-form for nested)
    void*                        klass = nullptr; // backend-specific class pointer
    std::vector<std::uintptr_t>  instances;      // up to instanceCap addresses
};

struct PlanAssemblyEntry {
    std::string  assembly;
    std::size_t  classCount    = 0;
    std::size_t  instanceCount = 0;   // sum across all classes in this assembly
};

// One class's fully-decoded snapshot payload. Built once at snapshotBegin
// time so snapshotChunk can stream it without making any further runtime
// calls — that's the design rule that eliminates the per-chunk attach
// accumulation Mono BDWGC suffers from (the 32-bit Mono 活俠傳 freeze
// pattern: N chunks × N attach cycles × N queued finalizers → main-
// thread stall once the queue gets long enough).
//
// Type is `nlohmann::json` because that's already the wire serializer the
// pipe layer uses; storing the decoded payload in its final shape lets
// chunk emit be a single move into the response array.
using ClassPayload = nlohmann::json;

struct Session {
    std::uint64_t                          id              = 0;
    std::string                            capturedAt;     // ISO-8601 UTC
    std::string                            gameBuildId;    // (file_size, write_time) FNV-1a hash hex
    std::string                            gameSessionId;  // per-DLL-load random hex
    std::size_t                            instanceCap     = 100;
    std::size_t                            arrayCap        = 50;
    std::vector<ClassPlanItem>             plan;            // for the heap-walk result (kept for diagnostics)
    std::vector<PlanAssemblyEntry>         planByAssembly;  // for the Begin response
    std::size_t                            totalInstances  = 0;

    // Phase 29a-v2 — pre-decoded per-class payload. One entry per plan
    // item, in plan order. Chunk emit slices [cursor, cursor+limit) and
    // copies into the response. Decoded once under a single outer
    // ThreadAttachGuard at Begin time.
    std::vector<ClassPayload>              classes;

    std::chrono::steady_clock::time_point  lastUse;
};

class SessionManager {
public:
    static SessionManager& Instance();

    // Allocate a new session, take ownership of the plan, return sessionId.
    // Triggers a lazy expiry pass so abandoned sessions don't pile up.
    std::uint64_t Begin(std::unique_ptr<Session> session);

    // View a session under the manager lock. Callers use this to walk
    // the plan during chunk emit. Returns false on unknown sessionId.
    template <typename Fn>
    bool View(std::uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        it->second->lastUse = std::chrono::steady_clock::now();
        fn(*it->second);
        return true;
    }

    bool End(std::uint64_t sessionId);
    void ExpireOldSessions();
    void DropAll();

    struct Stats {
        std::size_t   sessionCount    = 0;
        std::size_t   totalPlanItems  = 0;
        std::uint64_t nextId          = 0;
    };
    Stats GetStats();

    // Computed once per DLL load; same value returned for every snapshot
    // taken in this game session. UI uses this to tell within-session
    // diffs (instance addrs are stable) from cross-session diffs
    // (instance addrs are NOT stable; only statics + singletons match).
    static const std::string& GameSessionId();

private:
    SessionManager() = default;
    SessionManager(const SessionManager&) = delete;
    SessionManager& operator=(const SessionManager&) = delete;

    static constexpr std::chrono::seconds kExpirySeconds{300};

    std::mutex                                                    mu_;
    std::unordered_map<std::uint64_t, std::unique_ptr<Session>>   sessions_;
    std::uint64_t                                                 nextId_ = 1;
};

// Compute a stable, low-overhead "build id" for the game binary. Used
// by 29c cross-session compare to detect "different build, offsets
// may not match" without paying SHA256 cost on every snapshot. Returns
// hex string from FNV-1a hash of (module path + size + last-write-time).
// `moduleName` is the OS module name (e.g. "GameAssembly.dll" for IL2CPP,
// "mono-2.0-bdwgc.dll" / "mono.dll" for Mono); falls back to host EXE
// name when the module isn't loaded.
std::string ComputeGameBuildId(const wchar_t* moduleName);

// Compute / cached: the current ISO-8601 UTC timestamp string. Called
// once per Begin. Format "YYYY-MM-DDTHH:MM:SSZ".
std::string CapturedAtUtcNow();

}  // namespace UnityCEDumper::Snapshot
