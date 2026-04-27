// IL2CPPDomain.h — domain access + assembly enumeration for IL2CPP.
#pragma once

#include "IL2CPPTypes.h"

#include <string>
#include <vector>

namespace UnityCEDumper::IL2CPP {

struct AssemblyEntry {
    std::string         name;       // e.g. "Assembly-CSharp"
    std::string         path;       // empty if image_get_filename unavailable
    const Il2CppImage*  image;
};

Il2CppDomain* GetDomain();
std::vector<AssemblyEntry> EnumerateAssemblies();
const Il2CppImage* FindImageByName(const std::string& assemblyName);

}  // namespace UnityCEDumper::IL2CPP
