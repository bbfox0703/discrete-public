// MonoDomain.h — root domain access + assembly enumeration.
#pragma once

#include "MonoTypes.h"

#include <string>
#include <vector>

namespace Discrete::Mono {

struct AssemblyEntry {
    std::string name;   // e.g. "Assembly-CSharp"
    std::string path;   // full disk path of the assembly file (utf-8); empty if unknown
    MonoImage*  image;  // owning image — kept for later phases
};

// Returns Mono's root AppDomain pointer, or nullptr if mono module / exports
// are not yet available.
MonoDomain* GetRootDomain();

// Enumerates every assembly currently loaded by the Mono runtime.
// Internally attaches the calling thread to Mono via ThreadAttachGuard.
// Returns empty vector if the runtime is not ready.
std::vector<AssemblyEntry> EnumerateAssemblies();

// Looks up an assembly's MonoImage* by its short name (e.g. "Assembly-CSharp").
// Returns nullptr if not loaded. Walks EnumerateAssemblies under the hood.
MonoImage* FindImageByName(const std::string& assemblyName);

}  // namespace Discrete::Mono
