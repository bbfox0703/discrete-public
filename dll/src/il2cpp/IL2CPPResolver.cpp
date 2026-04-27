// IL2CPPResolver.cpp — implementation.

#include "IL2CPPResolver.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <mutex>
#include <string>

namespace UnityCEDumper::IL2CPP {

namespace {

std::once_flag g_once;
Resolver       g_resolver;

enum class Reqd { Required, Optional };

template <typename Fn>
bool Bind(const wchar_t* module, const char* exportName, Fn& slot, Reqd r) {
    void* p = ModuleHelper::ResolveExport(module, exportName);
    slot = reinterpret_cast<Fn>(p);
    if (p == nullptr) {
        std::string msg = (r == Reqd::Required)
            ? "IL2CPPResolver: missing REQUIRED export "
            : "IL2CPPResolver: missing optional export ";
        msg += exportName;
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryInit, msg);
        return false;
    }
    return true;
}

void DoResolve() {
    if (ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP) == nullptr) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryInit,
            "IL2CPPResolver: GameAssembly.dll not loaded");
        g_resolver.resolved = false;
        return;
    }
    g_resolver.moduleName = MagicStrings::ModuleIL2CPP;
    const wchar_t* m = MagicStrings::ModuleIL2CPP;

    int okReq = 0,  totalReq = 0;
    int okOpt = 0,  totalOpt = 0;
    auto bindReq = [&](const char* name, auto& slot) {
        ++totalReq;
        if (Bind(m, name, slot, Reqd::Required)) ++okReq;
    };
    auto bindOpt = [&](const char* name, auto& slot) {
        ++totalOpt;
        if (Bind(m, name, slot, Reqd::Optional)) ++okOpt;
    };

    // --- required: minimal set for assembly / class / field / method walking ---
    bindReq("il2cpp_domain_get",                g_resolver.domain_get);
    bindReq("il2cpp_thread_attach",             g_resolver.thread_attach);
    bindReq("il2cpp_thread_detach",             g_resolver.thread_detach);
    bindReq("il2cpp_domain_get_assemblies",     g_resolver.domain_get_assemblies);
    bindReq("il2cpp_assembly_get_image",        g_resolver.assembly_get_image);
    bindReq("il2cpp_image_get_name",            g_resolver.image_get_name);
    bindReq("il2cpp_image_get_class_count",     g_resolver.image_get_class_count);
    bindReq("il2cpp_image_get_class",           g_resolver.image_get_class);
    bindReq("il2cpp_class_from_name",           g_resolver.class_from_name);
    bindReq("il2cpp_class_get_name",            g_resolver.class_get_name);
    bindReq("il2cpp_class_get_namespace",       g_resolver.class_get_namespace);
    bindReq("il2cpp_class_get_fields",          g_resolver.class_get_fields);
    bindReq("il2cpp_class_get_methods",         g_resolver.class_get_methods);
    bindReq("il2cpp_field_get_name",            g_resolver.field_get_name);
    bindReq("il2cpp_field_get_type",            g_resolver.field_get_type);
    bindReq("il2cpp_field_get_offset",          g_resolver.field_get_offset);
    bindReq("il2cpp_method_get_name",           g_resolver.method_get_name);
    bindReq("il2cpp_type_get_name",             g_resolver.type_get_name);

    // --- optional: nicer-to-have, varies across Unity / IL2CPP versions ------
    bindOpt("il2cpp_image_get_filename",        g_resolver.image_get_filename);
    bindOpt("il2cpp_class_get_parent",          g_resolver.class_get_parent);
    bindOpt("il2cpp_class_get_image",           g_resolver.class_get_image);
    bindOpt("il2cpp_class_get_static_field_data", g_resolver.class_get_static_data);
    bindOpt("il2cpp_field_get_flags",           g_resolver.field_get_flags);
    bindOpt("il2cpp_method_get_token",          g_resolver.method_get_token);
    bindOpt("il2cpp_method_get_flags",          g_resolver.method_get_flags);
    bindOpt("il2cpp_method_get_pointer",        g_resolver.method_get_pointer);
    bindOpt("il2cpp_free",                      g_resolver.free);

    g_resolver.resolved = (okReq == totalReq);

    char line[200];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                  "IL2CPPResolver: GameAssembly.dll — required %d/%d, optional %d/%d (resolved=%s)",
                  okReq, totalReq, okOpt, totalOpt,
                  g_resolver.resolved ? "yes" : "no");
    UnityCEDumper::Log::Write(MagicStrings::LogCategoryInit, line);
}

}  // namespace

const Resolver& GetResolver() {
    std::call_once(g_once, DoResolve);
    return g_resolver;
}

bool IsResolved() { return GetResolver().resolved; }

}  // namespace UnityCEDumper::IL2CPP
