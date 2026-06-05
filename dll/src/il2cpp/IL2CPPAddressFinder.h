// IL2CPPAddressFinder.h — Phase 17a Strategy A backward-scan reverse lookup.
// Given an arbitrary process address, walk backwards in 8-byte strides looking
// for a candidate Il2Cpp object header (first qword == Il2CppClass*) and
// resolve which managed instance the queried address sits inside.
//
// Class-pointer membership is validated against a cache built lazily on first
// use (or proactively at handshake). The cache enumerates every assembly's
// classes once and records min/max bounds + a sorted vector for O(log N)
// binary-search membership tests. After the bounds + set check, we still
// SEH-probe il2cpp_class_get_name to filter out pointers that happen to fall
// in range but aren't real classes.
#pragma once

#include "IL2CPPTypes.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace Discrete::IL2CPP {

// Phase 17b — field-offset resolution result. One declared field that the
// queried address falls inside (or the closest one below it for the
// "no-exact-match" case). Populated by ResolveFieldAt(klass, fieldOffset);
// surfaced through the AddressLookup result below.
struct FieldHit {
    bool          present = false;
    std::string   name;
    std::string   typeName;
    std::size_t   offset      = 0;       // declared offset inside the instance
    std::size_t   size        = 0;       // size of this field's storage
    std::uint32_t flags       = 0;
    bool          isStatic    = false;
    bool          isValueType = false;
    bool          isEnum      = false;
    std::string   underlyingTypeName;     // when isEnum == true
    std::string   declaredIn;             // class name where this field was declared
    // Classifier for the queried address:
    //   "leaf"           — exact match on a primitive-sized value-type field
    //   "value-struct"   — exact match on a multi-field value type (Vector3 etc.)
    //   "pointer"        — exact match on a reference-type field (string / class / array)
    //   "interior"       — address falls inside the field but not at its boundary
    std::string   kind;
    std::ptrdiff_t interiorOffset = 0;    // when kind == "interior", offset within the field
};

struct AddressLookup {
    bool         found = false;          // true → scan landed on a managed instance
    std::string  reason;                  // populated when found == false
    // Populated when found == true:
    std::uintptr_t instance    = 0;      // base of the containing managed object
    std::uintptr_t klassPtr    = 0;      // Il2CppClass* read from object header
    std::string  assembly;                // e.g. "Assembly-CSharp"
    std::string  ns;                      // namespace; "" for global
    std::string  className;               // e.g. "PLY_WORK"
    std::ptrdiff_t fieldOffset = 0;      // bytes from instance base to queried address
    // Phase 17b — field resolution. `field` carries the matching declared
    // field when the queried offset falls inside one; otherwise `field.present`
    // is false and `nearestField` carries the closest field below the offset
    // (with `nearestFieldDelta` measuring the gap).
    FieldHit       field;
    FieldHit       nearestField;
    std::ptrdiff_t nearestFieldDelta = 0;
    // Always populated:
    std::uintptr_t queriedAddr = 0;
    std::uintptr_t scannedFrom = 0;       // inclusive
    std::uintptr_t scannedTo   = 0;       // exclusive
    std::size_t  scannedSteps  = 0;       // number of 8-byte probes attempted
};

struct ClassRangeStats {
    bool          built       = false;
    std::size_t   classCount  = 0;
    std::uintptr_t minKlass   = 0;
    std::uintptr_t maxKlass   = 0;
};

// Build (or refresh) the cached set of Il2CppClass* pointers reachable from
// the runtime domain. Idempotent; safe to call from any thread. Returns true
// when the cache holds at least one entry. Internally walks every assembly
// → image → class enumerated by IL2CPPClassWalker, so it costs one full
// class enumeration on first call (~tens of ms for typical games).
bool BuildClassRangeCache();

// Discard cached state so the next BuildClassRangeCache pass picks up newly
// loaded assemblies (e.g. after a `scan` RPC).
void InvalidateClassRangeCache();

ClassRangeStats GetClassRangeStats();

// Strategy A backward scan from `addr`. Walks backwards in 8-byte strides up
// to `maxBackwardScan` bytes, validates each candidate via the class-range
// cache + SEH-protected name probe, returns the first hit. Builds the cache
// implicitly on first use.
AddressLookup WhatIsAt(std::uintptr_t addr, std::size_t maxBackwardScan);

// Superset of WhatIsAt that also accepts value-type-element array objects
// (int[], ItemInfo[], List<T>._items backing arrays) whose runtime-synthesized
// klass is absent from the metadata class cache. Used as the value-scan
// fallback for candidates the ReverseLookupIndex couldn't attribute, so a
// scalar hit inside a struct array element resolves to ElementType[idx].member
// instead of being dropped. SAFE: an unvalidated candidate is only read via
// Memory::SafeReadBytes (never passed to a runtime API); every API call runs on
// the validated element class. Same backward-scan + ThreadAttachGuard contract
// as WhatIsAt.
AddressLookup WhatIsAtAcceptingArrays(std::uintptr_t addr, std::size_t maxBackwardScan);

// Phase 30 P2 — shared identity + field/array resolution, given a confirmed
// managed instance (`instance` base + its validated Il2CppClass* `klassPtr`)
// and the queried `addr`. Populates `out` exactly as WhatIsAt's tail does, so
// the backward-scan path and the interval-tree reverse-lookup path return
// identical results. PRECONDITION: caller holds a ThreadAttachGuard for the
// IL2CPP domain (the SEH name/declaring/namespace/image probes need it). Leaves
// scannedFrom/To/Steps untouched (backward-scan diagnostics).
void ResolveInstanceLookup(std::uintptr_t addr, std::uintptr_t instance,
                           std::uintptr_t klassPtr, AddressLookup& out);

// Phase 29 — wide-heap "all instances of all known classes" scan in one
// pass. Walks committed RW non-image non-stack regions once; for every
// 8-aligned qword, checks the existing class range cache (binary search
// against the same sorted vector WhatIsAt uses), and groups instance
// addresses by their class pointer.
//
// Returns at most `perClassCap` instance addresses per class — early-out
// happens at push time, so a class with 100K instances doesn't blow the
// session's memory budget. Total heap walk cost on SaGa F2R-scale: 5-15s
// for ~4-8 GB of RW heap, vs. N × ScanForPointer which would be hours.
//
// The map's keys are raw Il2CppClass* — caller resolves them to names
// outside this function (where ThreadAttachGuard is already held).
struct BulkInstancesResult {
    std::unordered_map<Il2CppClass*, std::vector<std::uintptr_t>> byClass;
    std::size_t                          totalQwordsScanned = 0;
    std::size_t                          totalInstances     = 0;
    std::size_t                          regionsTotal       = 0;
    std::size_t                          regionsScanned     = 0;
    std::size_t                          regionsSkipImage   = 0;
    std::size_t                          regionsSkipStack   = 0;
    std::size_t                          bytesScanned       = 0;
};
BulkInstancesResult FindAllInstances(std::size_t perClassCap);

// ---------------------------------------------------------------------------
// Phase 30 P2 — batch reverse-lookup index (interval-tree + locality cache).
// ---------------------------------------------------------------------------
//
// WhatIsAt() answers ONE address by walking backwards up to maxBackwardScan
// bytes (~2048 8-byte probes worst case) hunting for a class pointer. A value
// scan calls it once PER candidate, so a scan that returns thousands of hits
// pays O(candidates × probes) — the dominant cost on a value scan (see
// Memory-Scanning-Internals.md §16). This index flips that: ONE up-front heap
// walk records every managed instance's base, sorted; then each candidate is an
// O(log N) binary search for the nearest base ≤ addr (with an O(1) locality
// fast-path for clustered candidates), reusing ResolveInstanceLookup so the
// answer is byte-identical to WhatIsAt's.
//
// Lifetime: build once before a candidate loop, throw away after. NOT cached
// across calls — a GC cycle would invalidate the bases and we get no GC
// notification (same staleness rationale as WhatIsAt's per-call freshness).
class ReverseLookupIndex {
public:
    // Build the index: one parallel heap walk (mirrors FindAllInstances'
    // region filter + chunking) collecting a FLAT, uncapped (instanceBase,
    // klassPtr) list, sorted by base. Returns false if the class cache is
    // unavailable (caller should fall back to WhatIsAt). Does not need a
    // ThreadAttachGuard (raw memory only — no runtime calls).
    //
    // reuseWindowMs > 0 opts into a process-wide cache of the sorted base list:
    // if a previous Build() ran within the window, its bases are copied instead
    // of re-walking the heap. Used by the aggregate "any numeric width" value
    // scan, which fans out N back-to-back scans whose instance-base lists are
    // identical. 0 (default) = always a fresh walk (single / refine scans must
    // stay fresh — a GC cycle between calls would invalidate stale bases).
    bool Build(std::uint32_t reuseWindowMs = 0);

    // Resolve `addr` to its managed instance + field, matching WhatIsAt's
    // contract exactly. `maxBackwardScan` bounds how far below `addr` a base
    // may sit to still count as containing it (same window WhatIsAt uses).
    // PRECONDITION: caller holds a ThreadAttachGuard (ResolveInstanceLookup's
    // SEH name probes need it). On a miss, returns AddressLookup{found=false}
    // with reason "no_klass_in_range".
    AddressLookup Lookup(std::uintptr_t addr, std::size_t maxBackwardScan);

    std::size_t InstanceCount() const { return bases_.size(); }
    std::size_t HitCount()      const { return hits_; }
    std::size_t LocalityHits()  const { return localityHits_; }

    // One sorted (base, klass) instance record. Public so the process-wide
    // reuse cache (see Build's reuseWindowMs) can store/copy it.
    struct Entry { std::uintptr_t base; std::uintptr_t klass; };

private:
    std::vector<Entry> bases_;           // sorted ascending by base
    // Locality cache — adjacent value-scan candidates usually fall in the same
    // instance. Remember the last hit's base AND the next base above it; a new
    // addr whose 8-rounded `start` lies in [lastBase_, lastNextBase_) provably
    // has lastBase_ as its nearest base ≤ start (no other base in between), so
    // we can skip the binary search. Caching just [base, base+window) would be
    // WRONG — a closer base could sit between lastBase_ and addr.
    std::uintptr_t lastBase_     = 0;
    std::uintptr_t lastKlass_    = 0;
    std::uintptr_t lastNextBase_ = 0;    // base of the next instance above lastBase_
    std::size_t    hits_         = 0;
    std::size_t    localityHits_ = 0;
};

}  // namespace Discrete::IL2CPP
