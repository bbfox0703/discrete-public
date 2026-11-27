// MonoDomain.cpp — implementation.

#include "MonoDomain.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/RuntimeHealth.h"
#include "../shared/SehFilter.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"

#include <windows.h>

#include <mutex>

namespace Discrete::Mono {

namespace {

// mono_assembly_foreach is not thread-safe across concurrent enumerations
// (multiple pipe clients can call EnumerateAssemblies concurrently).
std::mutex g_enumMutex;

struct EnumCtx {
    std::vector<AssemblyEntry>* out;
    bool                        callbackFailed = false;
};

void OnAssembly(void* asmPtr, void* userData) noexcept;

#pragma warning(push)
#pragma warning(disable: 4509)  // nonstandard extension: SEH used in function with object unwinding
bool SehAssemblyForeach(fn_assembly_foreach fn, EnumCtx* ctx, unsigned long& outFaultCode) {
    unsigned long code = 0;
    __try {
        fn(&OnAssembly, ctx);
        return true;
    } __except ((code = GetExceptionCode()),
                Seh::MemoryAccessExceptionFilter(code, GetExceptionInformation())) {
        outFaultCode = code;
        return false;
    }
}
#pragma warning(pop)

// B-M6 — OnAssembly runs INSIDE Mono's mono_assemblies_lock. It MUST be
// noexcept and swallow all C++ exceptions (e.g. std::bad_alloc under x86
// address-space pressure) locally so no exception ever unwinds across the
// runtime lock frame and leaks mono_assemblies_lock permanently.
void OnAssembly(void* asmPtr, void* userData) noexcept {
    auto* ctx = static_cast<EnumCtx*>(userData);
    if (ctx == nullptr || ctx->out == nullptr || ctx->callbackFailed) return;
    try {
        auto* assembly = static_cast<MonoAssembly*>(asmPtr);
        const auto& r = GetResolver();

        AssemblyEntry e{};
        e.image = nullptr;

        if (r.assembly_get_image != nullptr) {
            e.image = r.assembly_get_image(assembly);
        }
        if (e.image != nullptr && r.image_get_name != nullptr) {
            const char* n = r.image_get_name(e.image);
            if (n != nullptr) e.name = n;
        }
        if (e.name.empty() && r.assembly_get_name != nullptr && r.assembly_name_get_name != nullptr) {
            if (auto* an = r.assembly_get_name(assembly)) {
                const char* n = r.assembly_name_get_name(an);
                if (n != nullptr) e.name = n;
            }
        }
        if (e.image != nullptr && r.image_get_filename != nullptr) {
            const char* p = r.image_get_filename(e.image);
            if (p != nullptr) e.path = p;
        }
        ctx->out->push_back(std::move(e));
    } catch (...) {
        ctx->callbackFailed = true;
    }
}

}  // namespace

MonoDomain* GetRootDomain() {
    const auto& r = GetResolver();
    if (!r.resolved || r.get_root_domain == nullptr) return nullptr;
    return r.get_root_domain();
}

std::vector<AssemblyEntry> EnumerateAssemblies() {
    std::vector<AssemblyEntry> out;

    const auto& r = GetResolver();
    if (!r.resolved || r.assembly_foreach == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateAssemblies: resolver not ready");
        return out;
    }

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateAssemblies: no root domain");
        return out;
    }

    std::lock_guard<std::mutex> lock(g_enumMutex);
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateAssemblies: mono_thread_attach failed");
        return out;
    }

    EnumCtx ctx{&out, false};
    unsigned long faultCode = 0;
    if (!SehAssemblyForeach(r.assembly_foreach, &ctx, faultCode)) {
        RuntimeHealth::MarkRuntimeFault("mono_assembly_foreach", faultCode);
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "WARN EnumerateAssemblies: " + RuntimeHealth::FormatReason());
        out.clear();
    } else if (ctx.callbackFailed) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "WARN EnumerateAssemblies: allocation failure inside OnAssembly callback (mono_assemblies_lock preserved)");
        out.clear();
    }

    char line[128];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                  "EnumerateAssemblies: %zu assemblies", out.size());
    Discrete::Log::Write(MagicStrings::LogCategoryWalk, line);
    return out;
}

MonoImage* FindImageByName(const std::string& assemblyName) {
    if (assemblyName.empty()) return nullptr;
    auto all = EnumerateAssemblies();
    for (const auto& a : all) {
        if (a.name == assemblyName) return a.image;
    }
    return nullptr;
}

std::unordered_map<std::string, MonoImage*> BuildImageMap() {
    std::unordered_map<std::string, MonoImage*> map;
    auto all = EnumerateAssemblies();  // single domain walk
    map.reserve(all.size());
    for (auto& a : all) {
        if (a.image != nullptr) map.emplace(a.name, a.image);
    }
    return map;
}

}  // namespace Discrete::Mono
