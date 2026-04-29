// IL2CPPMetadataRegistration.h — locate the runtime Il2CppMetadataRegistration
// struct in GameAssembly.dll. The struct is not exported by name; we AOB-scan
// the module's writable / read-only-init data sections for its shape.
//
// Once located, the runtime types[] table maps a metadata FieldDefault's
// `typeIndex` to an `Il2CppType*`, whose tag byte tells us how the field's
// default value blob was encoded (compressed-int / raw LE / float / string).
// This unlocks type-aware decode for non-enum const literals — see Phase 5
// segment 2d follow-up in plan.md.
#pragma once

#include <cstddef>
#include <cstdint>

namespace UnityCEDumper::IL2CPP::MetadataRegistration {

// Lazy-init: first call AOB-scans GameAssembly.dll. Returns true once the
// struct is located (cached for the lifetime of the DLL).
bool Find();

// Returns the address of the located registration struct, or nullptr.
// Useful for diagnostics / pipe responses.
const void* GetBase();

// Returns the runtime `types[]` array (an array of `const Il2CppType*`).
// nullptr if the registration hasn't been found yet.
const void* const* TypesArray();
std::size_t        TypesCount();

// Reads the type-tag byte (offset 10 of `Il2CppType`) for the given
// metadata type index. The tag is one of `Il2CppTypeEnum`, e.g.
//   0x02 Boolean, 0x04 I1, 0x08 I4, 0x0C R4, 0x0E String, 0x11 ValueType.
// Returns 0 on failure (registration missing, index out of range,
// unreadable type pointer).
std::uint8_t GetTypeTag(std::int32_t typeIndex);

}  // namespace UnityCEDumper::IL2CPP::MetadataRegistration
