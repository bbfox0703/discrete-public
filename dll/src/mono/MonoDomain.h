// MonoDomain.h — root domain access + assembly enumeration.
#pragma once

#include "MonoTypes.h"

#include <string>
#include <unordered_map>
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
//
// HOT-PATH WARNING: each call re-walks the WHOLE Mono domain
// (mono_assembly_foreach + thread attach). NEVER call this in a loop over many
// assemblies — hammering mono_assembly_foreach from the pipe worker thread
// races the main-thread BDWGC GC and deadlocks the game (the documented
// "23K EnumerateAssemblies → freeze"). For multi-assembly resolution use
// BuildImageMap() once and look names up from the returned map.
MonoImage* FindImageByName(const std::string& assemblyName);

// Builds a short-name → MonoImage* map in ONE domain walk. The cheap,
// freeze-safe replacement for calling FindImageByName per assembly: collapses
// N mono_assembly_foreach walks into a single one. Names are unique per
// runtime; the first image wins on the (theoretical) duplicate.
std::unordered_map<std::string, MonoImage*> BuildImageMap();

}  // namespace Discrete::Mono
