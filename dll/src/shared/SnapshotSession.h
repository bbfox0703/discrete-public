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

namespace Discrete::Snapshot {

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

// Phase 30b — game-side streaming decode. The decoder owns the heap-walk
// result + all decode context that snapshotBegin used to hold as locals, so
// snapshotChunk can decode plan rows ON DEMAND (and free each chunk's payload
// after it ships) instead of pre-building the WHOLE snapshot into
// `Session::classes`. That bounds the game-process transient peak to ~one
// chunk + the plan + the ClassInfo cache, instead of the entire decoded tree
// (the ~4.5 GB buffer on Utawarerumono).
//
// Type-erased so the shared layer stays backend-agnostic: the concrete
// implementations live in the per-backend `#ifdef` branches of ExportAPI.cpp.
// IL2CPP-only this phase — IL2CPP has no BDWGC finalizer-accumulation hazard,
// so DecodeRange can open a fresh ThreadAttachGuard per call. Mono keeps
// decode-in-Begin (the 活俠傳 freeze lives there) and gets a session-held-attach
// follow-up.
struct ISnapshotStreamDecoder {
    virtual ~ISnapshotStreamDecoder() = default;

    // Decode plan rows [cursor, cursor+limit); append each class's deduped-wire
    // payload to `out` (which must be a json array). Updates internal
    // watchdog/budget/stats. Returns the next cursor (== min(cursor+limit,
    // PlanSize())). `out` holds at most one chunk's payloads — the chunk handler
    // serializes then frees it, so the whole snapshot is never resident at once.
    virtual std::size_t DecodeRange(std::size_t cursor, std::size_t limit,
                                    nlohmann::json& out) = 0;

    // Total plan rows (one per class). nextCursor >= PlanSize() ⇒ stream done.
    virtual std::size_t PlanSize() const = 0;

    // Accumulated decode stats (same shape as the old Begin `decodeStats`
    // object) — final only once the whole stream has been decoded, so the
    // chunk handler ships it on the done=true chunk.
    virtual nlohmann::json DecodeStats() const = 0;
    virtual bool          TruncatedByMemory() const = 0;
    virtual bool          TruncatedByCount()  const = 0;
    virtual std::uint64_t PeakPrivateUsage()  const = 0;
};

struct Session {
    std::uint64_t                          id              = 0;
    std::string                            capturedAt;     // ISO-8601 UTC
    std::string                            gameBuildId;    // (file_size, write_time) FNV-1a hash hex
    std::string                            gameSessionId;  // per-DLL-load random hex
    std::size_t                            instanceCap     = 100;
    std::size_t                            arrayCap        = 50;

    // Phase 29f (Layer B) — reference-array inner-field capture. When
    // enabled, each reference-type array element gets its element object's
    // own fields walked one level deep (so the UI can pivot array elements
    // by an inner field instead of by index). elementFieldCap bounds the
    // per-element field count. arrayElementTargets, when non-empty, limits
    // capture to matching "FQN" (all ref-array fields of that class) or
    // "FQN.fieldName" entries — the x86 path requires targets (blanket
    // capture on 32-bit is refused for OOM safety); x64 leaves it empty
    // for blanket capture.
    bool                                   arrayElementFields = false;
    std::size_t                            elementFieldCap    = 16;
    std::vector<std::string>               arrayElementTargets;
    // Total reference-array elements deep-captured per snapshot — a hard
    // memory ceiling so blanket capture on a large game (158K instances /
    // 21K arrays) can't explode the JSON cache into an OOM CTD. Once hit,
    // remaining elements stay ptr-only. Targeted captures rarely approach it.
    std::size_t                            elementCaptureBudget = 20000;

    // Phase 31-2 (Phase B) — generic value-type struct member fan-out. When on
    // (default), snapshot decode recurses into custom value-type struct fields
    // (int/double/… members, not just named Vector3) and emits their members as
    // `fields` (same wire shape as the Phase A named structs), so SPC / diff /
    // pivot can see per-member values. Off restores the pre-31-2 single
    // pointer-word behaviour for unknown structs. structMemberCap bounds the
    // per-struct member count (and the array-element inner struct), mirroring
    // elementFieldCap; named math structs are unaffected (decoded in decodeField).
    bool                                   valueStructFields = true;
    std::size_t                            structMemberCap   = 32;

    // Capture static-field VALUES (IL2CPP). Default off: static metadata
    // (name/type/offset) is always emitted, but the value decode is skipped
    // unless the user opts in — keeps snapshots leaner + matches the Mono
    // path (which captures static metadata only to avoid the vtable freeze).
    bool                                   captureStaticValues = false;
    std::vector<ClassPlanItem>             plan;            // for the heap-walk result (kept for diagnostics)
    std::vector<PlanAssemblyEntry>         planByAssembly;  // for the Begin response
    std::size_t                            totalInstances  = 0;

    // Phase 29a-v2 — pre-decoded per-class payload. One entry per plan
    // item, in plan order. Chunk emit slices [cursor, cursor+limit) and
    // copies into the response. Decoded once under a single outer
    // ThreadAttachGuard at Begin time. Used by the decode-in-Begin path
    // (Mono, and the IL2CPP fallback); EMPTY for a Phase 30b streaming
    // session (see `streamDecoder`).
    std::vector<ClassPayload>              classes;

    // Phase 30b — game-side streaming decode (IL2CPP). When set, snapshotChunk
    // decodes plan rows on demand via this decoder instead of slicing the
    // pre-built `classes`; `classes`/`plan` stay empty (the decoder owns the
    // heap-walk data) to avoid duplicating the instance-address vectors. null
    // ⇒ decode-in-Begin path (Mono / legacy).
    //
    // C-M5(3): held via shared_ptr so CmdSnapshotChunk can grab a local ref
    // inside SessionManager::View and immediately release the global mutex
    // before running DecodeRange / gc_collect / Sleep — preventing a slow
    // chunk from blocking concurrent snapshotEnd / DropAll / snapshotBegin
    // while keeping the decoder alive against concurrent session teardown.
    std::shared_ptr<ISnapshotStreamDecoder> streamDecoder;

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

}  // namespace Discrete::Snapshot
