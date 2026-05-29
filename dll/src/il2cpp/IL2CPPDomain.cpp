// IL2CPPDomain.cpp — implementation.

#include "IL2CPPDomain.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"

#include <windows.h>

#include <mutex>

namespace Discrete::IL2CPP {

namespace {

std::mutex g_enumMutex;
const char* SafeStr(const char* s) { return s ? s : ""; }

#pragma warning(push)
#pragma warning(disable: 4509)  // SEH used in function with object unwinding
const Il2CppAssembly** SehGetAssemblies(fn_domain_get_assemblies fn,
                                        Il2CppDomain* d, std::size_t* sz) {
    __try { return fn(d, sz); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

}  // namespace

Il2CppDomain* GetDomain() {
    const auto& r = GetResolver();
    if (!r.resolved || r.domain_get == nullptr) return nullptr;
    return r.domain_get();
}

std::vector<AssemblyEntry> EnumerateAssemblies() {
    std::vector<AssemblyEntry> out;
    const auto& r = GetResolver();
    if (!r.resolved || r.domain_get_assemblies == nullptr || r.assembly_get_image == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "IL2CPP::EnumerateAssemblies: resolver not ready");
        return out;
    }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "IL2CPP::EnumerateAssemblies: no domain");
        return out;
    }

    std::lock_guard<std::mutex> lock(g_enumMutex);
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "IL2CPP::EnumerateAssemblies: il2cpp_thread_attach failed");
        return out;
    }

    std::size_t count = 0;
    const Il2CppAssembly** arr = SehGetAssemblies(r.domain_get_assemblies, domain, &count);
    if (arr == nullptr || count == 0) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "IL2CPP::EnumerateAssemblies: domain_get_assemblies returned empty");
        return out;
    }

    out.reserve(count);
    for (std::size_t i = 0; i < count; ++i) {
        const Il2CppAssembly* a = arr[i];
        if (a == nullptr) continue;
        AssemblyEntry e{};
        e.image = r.assembly_get_image(a);
        if (e.image != nullptr && r.image_get_name != nullptr) {
            e.name = SafeStr(r.image_get_name(e.image));
            // IL2CPP returns "Assembly-CSharp.dll" but Mono returns "Assembly-CSharp".
            // Strip the trailing ".dll" so the UI / pipe args stay backend-agnostic.
            if (e.name.size() > 4 &&
                e.name.compare(e.name.size() - 4, 4, ".dll") == 0) {
                e.name.resize(e.name.size() - 4);
            }
        }
        if (e.image != nullptr && r.image_get_filename != nullptr) {
            e.path = SafeStr(r.image_get_filename(e.image));
        }
        out.push_back(std::move(e));
    }

    char line[128];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                  "IL2CPP::EnumerateAssemblies: %zu assemblies", out.size());
    Discrete::Log::Write(MagicStrings::LogCategoryWalk, line);
    return out;
}

const Il2CppImage* FindImageByName(const std::string& assemblyName) {
    if (assemblyName.empty()) return nullptr;
    auto all = EnumerateAssemblies();
    for (const auto& a : all) {
        if (a.name == assemblyName) return a.image;
    }
    return nullptr;
}

}  // namespace Discrete::IL2CPP
