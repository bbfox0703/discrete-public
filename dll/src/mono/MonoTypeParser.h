// MonoTypeParser.h — converts MonoType* into a readable string (e.g.
// "System.Int32", "MyNs.Foo[]", "System.Collections.Generic.List`1<...>").
// Backed by mono_type_get_name; ownership of the C string is reclaimed via
// mono_free, so callers always get a std::string back.
#pragma once

#include "MonoTypes.h"

#include <string>

namespace UnityCEDumper::Mono {

std::string TypeName(MonoType* type);

}  // namespace UnityCEDumper::Mono
