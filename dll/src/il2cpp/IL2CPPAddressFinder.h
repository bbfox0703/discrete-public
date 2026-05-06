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
