// MonoDomain.cpp — implementation.

#include "MonoDomain.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"

#include <windows.h>

#include <mutex>

namespace UnityCEDumper::Mono {

namespace {

// mono_assembly_foreach is not thread-safe across concurrent enumerations.
// Pipe worker is single-threaded today, but a mutex is cheap insurance.
std::mutex g_enumMutex;

struct EnumCtx {
    std::vector<AssemblyEntry>* out;
};

void OnAssembly(void* asmPtr, void* userData);

#pragma warning(push)
#pragma warning(disable: 4509)  // nonstandard extension: SEH used in function with object unwinding
bool SehAssemblyForeach(fn_assembly_foreach fn, EnumCtx* ctx) {
    __try {
        fn(&OnAssembly, ctx);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
#pragma warning(pop)

void OnAssembly(void* asmPtr, void* userData) {
    auto* ctx = static_cast<EnumCtx*>(userData);
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
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateAssemblies: resolver not ready");
        return out;
    }

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateAssemblies: no root domain");
        return out;
    }

    std::lock_guard<std::mutex> lock(g_enumMutex);
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateAssemblies: mono_thread_attach failed");
        return out;
    }

    EnumCtx ctx{&out};
    if (!SehAssemblyForeach(r.assembly_foreach, &ctx)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateAssemblies: SEH during mono_assembly_foreach");
        out.clear();
    }

    char line[128];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                  "EnumerateAssemblies: %zu assemblies", out.size());
    UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk, line);
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

}  // namespace UnityCEDumper::Mono
