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

}  // namespace Discrete::Mono
