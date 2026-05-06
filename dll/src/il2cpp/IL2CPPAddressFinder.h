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

namespace UnityCEDumper::IL2CPP {

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

}  // namespace UnityCEDumper::IL2CPP
