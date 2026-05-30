// MonoAddressFinder.h — Phase 17a Strategy A backward-scan reverse lookup.
// Mirror of il2cpp/IL2CPPAddressFinder.h, but for the Mono backend.
//
// Mono difference: an object's first qword points at a MonoVTable*, not a
// MonoClass*. The cache therefore tracks two parallel sorted vectors:
//   * MonoVTable* (what we expect to find at the object header)
//   * MonoClass*  (the class the vtable resolves to — derived from the vtable
//                  by reading offset 0, which holds `MonoClass *klass`)
// The cache is built by enumerating all classes in the root domain and
// calling mono_class_vtable for each. We accept the resulting vtables AND
// the classes they point at.
#pragma once

#include "MonoTypes.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace Discrete::Mono {

// Phase 17b — same shape as the IL2CPP `FieldHit`. Documented in detail in
// il2cpp/IL2CPPAddressFinder.h.
struct FieldHit {
    bool          present = false;
    std::string   name;
    std::string   typeName;
    std::size_t   offset      = 0;
    std::size_t   size        = 0;
    std::uint32_t flags       = 0;
    bool          isStatic    = false;
    bool          isValueType = false;
    bool          isEnum      = false;
    std::string   underlyingTypeName;
    std::string   declaredIn;
    std::string   kind;
    std::ptrdiff_t interiorOffset = 0;
};

struct AddressLookup {
    bool         found = false;
    std::string  reason;
    std::uintptr_t instance    = 0;
    std::uintptr_t klassPtr    = 0;      // resolved MonoClass*
    std::uintptr_t vtablePtr   = 0;      // MonoVTable* found in object header
    std::string  assembly;
    std::string  ns;
    std::string  className;
    std::ptrdiff_t fieldOffset = 0;
    // Phase 17b
    FieldHit       field;
    FieldHit       nearestField;
    std::ptrdiff_t nearestFieldDelta = 0;
    std::uintptr_t queriedAddr = 0;
    std::uintptr_t scannedFrom = 0;
    std::uintptr_t scannedTo   = 0;
    std::size_t  scannedSteps  = 0;
};

struct ClassRangeStats {
    bool          built       = false;
    std::size_t   classCount  = 0;
    std::size_t   vtableCount = 0;
    std::uintptr_t minKlass   = 0;
    std::uintptr_t maxKlass   = 0;
    std::uintptr_t minVtable  = 0;
    std::uintptr_t maxVtable  = 0;
};

bool BuildClassRangeCache();
void InvalidateClassRangeCache();
ClassRangeStats GetClassRangeStats();

AddressLookup WhatIsAt(std::uintptr_t addr, std::size_t maxBackwardScan);

// Phase 30 P2 (Mono port) — shared identity + field/array resolution, given a
// confirmed managed instance (`instance` base, its MonoVTable* `vtablePtr`, and
// the resolved MonoClass* `klassPtr`) and the queried `addr`. Populates `out`
// exactly as WhatIsAt's tail does, so the backward-scan path and the
// interval-tree reverse-lookup path return identical results. PRECONDITION:
// caller holds a ThreadAttachGuard for the Mono root domain (the SEH
// name/namespace/image + field-walk probes need it). Leaves
// scannedFrom/To/Steps untouched (backward-scan diagnostics). Mirror of
// IL2CPP::ResolveInstanceLookup, but carries the Mono vtable pointer too.
void ResolveInstanceLookup(std::uintptr_t addr, std::uintptr_t instance,
                           std::uintptr_t vtablePtr, std::uintptr_t klassPtr,
                           AddressLookup& out);

// Phase 29 — wide-heap "all instances of all known classes" scan. Mirror
// of the IL2CPP function but with Mono's vtable indirection: the qword
// at instance+0 is a MonoVTable*, not a MonoClass*, so per-qword we
// SEH-deref vtable→klass and binary-search the resulting klass against
// `classSet`. The vtable→klass mapping is memoised across the scan
// (and persists into the address-finder cache for follow-up WhatIsAt
// calls) so repeat vtables are O(1) instead of O(log N + deref).
struct BulkInstancesResult {
    std::unordered_map<MonoClass*, std::vector<std::uintptr_t>> byClass;
    std::size_t                          totalQwordsScanned = 0;
    std::size_t                          totalInstances     = 0;
    std::size_t                          regionsTotal       = 0;
    std::size_t                          regionsScanned     = 0;
    std::size_t                          regionsSkipImage   = 0;
    std::size_t                          regionsSkipStack   = 0;
    std::size_t                          bytesScanned       = 0;
    std::size_t                          vtablesResolved    = 0;
};
BulkInstancesResult FindAllInstances(std::size_t perClassCap);

// ---------------------------------------------------------------------------
// Phase 30 P2 (Mono port) — batch reverse-lookup index (interval-tree +
// locality cache). Mirror of IL2CPP::ReverseLookupIndex; see that header for
// the full design rationale.
// ---------------------------------------------------------------------------
//
// WhatIsAt() answers ONE address by walking backwards up to maxBackwardScan
// bytes hunting for a vtable pointer whose klass is known. A value scan calls
// it once PER candidate, so a scan returning thousands of hits pays
// O(candidates × probes). This index flips that: ONE up-front heap walk records
// every managed instance's base (vtable + resolved klass), sorted; then each
// candidate is an O(log N) binary search for the nearest base ≤ addr (with an
// O(1) locality fast-path for clustered candidates), reusing
// ResolveInstanceLookup so the answer is byte-identical to WhatIsAt's.
//
// Mono difference vs IL2CPP: the walk can't binary-search the header qword
// directly (it's a MonoVTable*, not a MonoClass*). Build() derefs each
// candidate vtable to its klass + validates the MonoVTable::domain field
// against the root domain (same structural check FindAllInstances uses) before
// recording the instance. Build() is single-threaded by design — the Mono
// heap walk carries the documented BDWGC-freeze risk on 活俠傳 (32-bit Mono),
// so it reuses FindAllInstances' Sleep(1)-yield-every-16MB pacing rather than
// the IL2CPP index's parallel sweep. (Mono FindAllInstances parallelization is
// tracked as a separate backlog item.)
//
// Lifetime: build once before a candidate loop, throw away after. NOT cached
// across calls — a GC cycle would invalidate the bases and we get no GC
// notification (same staleness rationale as WhatIsAt's per-call freshness).
class ReverseLookupIndex {
public:
    // Build the index: one single-threaded heap walk (mirrors FindAllInstances'
    // region filter + vtable→klass deref + domain validation + Sleep(1) yield)
    // collecting a FLAT, uncapped (instanceBase, vtable, klass) list, sorted by
    // base. Returns false if the class cache is unavailable (caller should fall
    // back to WhatIsAt). Does not need a ThreadAttachGuard (raw memory only —
    // no runtime calls; vtable→klass is a SEH-protected SafeRead).
    bool Build();

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

private:
    struct Entry { std::uintptr_t base; std::uintptr_t vtable; std::uintptr_t klass; };
    std::vector<Entry> bases_;           // sorted ascending by base
    // Locality cache — see IL2CPP::ReverseLookupIndex for why caching
    // [lastBase_, lastNextBase_) (not [base, base+window)) is the correct
    // invariant for skipping the binary search.
    std::uintptr_t lastBase_     = 0;
    std::uintptr_t lastVtable_   = 0;
    std::uintptr_t lastKlass_    = 0;
    std::uintptr_t lastNextBase_ = 0;    // base of the next instance above lastBase_
    std::size_t    hits_         = 0;
    std::size_t    localityHits_ = 0;
};

}  // namespace Discrete::Mono
