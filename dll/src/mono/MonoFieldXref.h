// MonoFieldXref.h — Phase 28a findFieldReferences backend (Mono).
//
// Inverse of Phase 25 searchSymbols: given a Unity field, walk every method
// in the target assembly's IL bytecode and return each one that touches the
// field via ldfld/stfld/ldsfld/stsfld/ldflda/ldsflda.
//
// Exact, no false positives — IL operand tokens uniquely identify the field
// even across overloads. Generic methods are scanned once at the open-generic
// level; instantiated versions share the same IL, so a token match at the
// open-generic level counts for every instantiation (mirrors dnSpy).
#pragma once

#include "MonoTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::Mono {

// Which IL operations qualify as a "reference" for filtering purposes.
enum class FieldRefKind : int {
    Any   = 0,   // read or write
    Read  = 1,   // ldfld / ldflda / ldsfld / ldsflda
    Write = 2,   // stfld / stsfld
};

// One method that references the target field. Caller renders these into the
// pipe response.
struct FieldRefMatch {
    std::string   ns;
    std::string   className;          // "Outer/Inner" form for nested types
    std::string   methodName;
    std::uint32_t methodToken = 0;
    std::uint32_t methodFlags = 0;
    bool          isStatic    = false;
    bool          isMethodStatic = false;
    std::uint32_t ilOffset    = 0;    // byte offset of the matching IL op
    std::string   refKind;            // "read" | "write"
    std::string   confidence;         // "exact" for Mono IL walk
};

struct FindFieldRefsResult {
    bool          ok = false;
    std::string   error;
    std::uint32_t fieldToken = 0;     // target field's metadata token (0x04xxxxxx)
    std::vector<FieldRefMatch> matches;
    std::size_t   scannedMethods = 0;
    bool          truncated = false;
};

// Resolves `image` + `ns` + `class` + `fieldName` to a MonoClassField, then
// walks every class in `image` and every declared method's IL body looking
// for `ld/stfld` ops whose token immediate matches the field's
// `mono_class_get_field_token`. Scope: same-assembly references only —
// cross-assembly references (MemberRef tokens) are not resolved in v1.
// Match cap: `limit` (caller validated against 5000).
FindFieldRefsResult FindFieldReferences(MonoImage*         image,
                                        const std::string& ns,
                                        const std::string& className,
                                        const std::string& fieldName,
                                        FieldRefKind       kindFilter,
                                        std::size_t        limit);

}  // namespace Discrete::Mono
