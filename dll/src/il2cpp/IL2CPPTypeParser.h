// IL2CPPTypeParser.h — Il2CppType* → readable string via il2cpp_type_get_name.
#pragma once

#include "IL2CPPTypes.h"

#include <string>

namespace Discrete::IL2CPP {

std::string TypeName(const Il2CppType* type);

}  // namespace Discrete::IL2CPP
