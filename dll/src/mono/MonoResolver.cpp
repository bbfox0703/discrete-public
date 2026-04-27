// MonoResolver.cpp — implementation.

#include "MonoResolver.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <mutex>
#include <string>

namespace UnityCEDumper::Mono {

namespace {

std::once_flag g_once;
Resolver       g_resolver;

const wchar_t* PickMonoModule() {
    if (ModuleHelper::GetModuleBase(MagicStrings::ModuleMonoBdwgc) != nullptr) {
        return MagicStrings::ModuleMonoBdwgc;
    }
    if (ModuleHelper::GetModuleBase(MagicStrings::ModuleMonoLegacy) != nullptr) {
        return MagicStrings::ModuleMonoLegacy;
    }
    return nullptr;
}

template <typename Fn>
bool Bind(const wchar_t* module, const char* exportName, Fn& slot) {
    void* p = ModuleHelper::ResolveExport(module, exportName);
    slot = reinterpret_cast<Fn>(p);
    if (p == nullptr) {
        std::string msg = "MonoResolver: missing export ";
        msg += exportName;
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryInit, msg);
        return false;
    }
    return true;
}

void DoResolve() {
    const wchar_t* module = PickMonoModule();
    if (module == nullptr) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryInit,
            "MonoResolver: no mono module loaded (mono-2.0-bdwgc.dll / mono.dll)");
        g_resolver.resolved = false;
        return;
    }
    g_resolver.moduleName = module;

    int ok = 0;
    int total = 0;
    auto tryBind = [&](const char* name, auto& slot) {
        ++total;
        if (Bind(module, name, slot)) ++ok;
    };

    tryBind("mono_get_root_domain",        g_resolver.get_root_domain);
    tryBind("mono_domain_get",             g_resolver.domain_get);
    tryBind("mono_thread_attach",          g_resolver.thread_attach);
    tryBind("mono_thread_detach",          g_resolver.thread_detach);
    tryBind("mono_assembly_foreach",       g_resolver.assembly_foreach);
    tryBind("mono_assembly_get_image",     g_resolver.assembly_get_image);
    tryBind("mono_assembly_get_name",      g_resolver.assembly_get_name);
    tryBind("mono_assembly_name_get_name", g_resolver.assembly_name_get_name);
    tryBind("mono_image_get_name",         g_resolver.image_get_name);
    tryBind("mono_image_get_filename",     g_resolver.image_get_filename);

    // Segment 2: class / field / method walking.
    tryBind("mono_image_get_table_info",   g_resolver.image_get_table_info);
    tryBind("mono_table_info_get_rows",    g_resolver.table_info_get_rows);
    tryBind("mono_class_get",              g_resolver.class_get);
    tryBind("mono_class_from_name",        g_resolver.class_from_name);
    tryBind("mono_class_get_name",         g_resolver.class_get_name);
    tryBind("mono_class_get_namespace",    g_resolver.class_get_namespace);
    tryBind("mono_class_get_parent",       g_resolver.class_get_parent);
    tryBind("mono_class_get_image",        g_resolver.class_get_image);
    tryBind("mono_class_get_fields",       g_resolver.class_get_fields);
    tryBind("mono_class_get_methods",      g_resolver.class_get_methods);
    tryBind("mono_field_get_name",         g_resolver.field_get_name);
    tryBind("mono_field_get_type",         g_resolver.field_get_type);
    tryBind("mono_field_get_offset",       g_resolver.field_get_offset);
    tryBind("mono_field_get_flags",        g_resolver.field_get_flags);
    tryBind("mono_method_get_name",        g_resolver.method_get_name);
    tryBind("mono_method_get_token",       g_resolver.method_get_token);
    tryBind("mono_method_get_flags",       g_resolver.method_get_flags);
    tryBind("mono_type_get_name",          g_resolver.type_get_name);
    tryBind("mono_free",                   g_resolver.free);

    // Segment 3: static-field address + JIT compile.
    tryBind("mono_class_vtable",                g_resolver.class_vtable);
    tryBind("mono_vtable_get_static_field_data", g_resolver.vtable_get_static_data);
    tryBind("mono_compile_method",              g_resolver.compile_method);

    g_resolver.resolved = (ok == total);

    char line[256];
    {
        // Convert wide module name to narrow for the log line.
        int n = ::WideCharToMultiByte(CP_UTF8, 0, module, -1, nullptr, 0, nullptr, nullptr);
        std::string narrow(static_cast<std::size_t>(n > 0 ? n - 1 : 0), '\0');
        if (n > 1) {
            ::WideCharToMultiByte(CP_UTF8, 0, module, -1, narrow.data(), n, nullptr, nullptr);
        }
        ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                      "MonoResolver: %s — bound %d/%d exports",
                      narrow.c_str(), ok, total);
    }
    UnityCEDumper::Log::Write(MagicStrings::LogCategoryInit, line);
}

}  // namespace

const Resolver& GetResolver() {
    std::call_once(g_once, DoResolve);
    return g_resolver;
}

bool IsResolved() { return GetResolver().resolved; }

}  // namespace UnityCEDumper::Mono
