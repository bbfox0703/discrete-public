// MonoResolver.cpp — implementation.

#include "MonoResolver.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <mutex>
#include <string>

namespace Discrete::Mono {

namespace {

// (the former g_once is gone — see GetResolver: latching on the first CALL
// made a too-early resolve permanent)
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
        Discrete::Log::Write(MagicStrings::LogCategoryInit, msg);
        return false;
    }
    return true;
}

void DoResolve() {
    const wchar_t* module = PickMonoModule();
    if (module == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryInit,
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
    // optBind binds an export but does NOT count toward `resolved`. Use for
    // exports that older Mono builds may genuinely lack — missing here just
    // means the dependent feature degrades, not that the whole runtime is off.
    auto optBind = [&](const char* name, auto& slot) {
        (void)Bind(module, name, slot);
    };

    tryBind("mono_get_root_domain",        g_resolver.get_root_domain);
    tryBind("mono_domain_get",             g_resolver.domain_get);
    tryBind("mono_thread_attach",          g_resolver.thread_attach);
    tryBind("mono_thread_detach",          g_resolver.thread_detach);
    tryBind("mono_assembly_foreach",       g_resolver.assembly_foreach);
    tryBind("mono_assembly_get_image",     g_resolver.assembly_get_image);
    // assembly_get_name + assembly_name_get_name: optional in practice.
    // MonoDomain::OnAssembly uses image_get_name as the primary source for
    // the friendly assembly name and only falls back to this pair when the
    // image-side path returns empty. Unity 5.3-era legacy mono.dll (pre-
    // bdwgc Mono 2.x fork) ships without these two exports — see Endless
    // Legend smoke 2026-05-19. Demoting to optBind unblocks legacy targets;
    // call sites are already null-guarded.
    optBind("mono_assembly_get_name",      g_resolver.assembly_get_name);
    optBind("mono_assembly_name_get_name", g_resolver.assembly_name_get_name);
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
    // mono_free: ideally we call it to free the char* mono_type_get_name
    // allocates, but Unity 5.3-era legacy mono.dll doesn't export it.
    // Without mono_free, we leak a small string per type-name lookup —
    // bounded, non-fatal. MonoTypeParser is already null-guarded at the
    // call site. Demoting unblocks legacy Unity 5.x Mono targets.
    optBind("mono_free",                   g_resolver.free);

    // Segment 3: static-field address + JIT compile.
    tryBind("mono_class_vtable",                g_resolver.class_vtable);
    tryBind("mono_vtable_get_static_field_data", g_resolver.vtable_get_static_data);
    tryBind("mono_compile_method",              g_resolver.compile_method);

    // Phase 5 follow-up: enum decode (true-optional — missing exports just
    // disable getEnumValues, don't take the whole resolver offline).
    optBind("mono_field_get_data",              g_resolver.field_get_data);
    optBind("mono_class_is_enum",               g_resolver.class_is_enum);
    optBind("mono_class_enum_basetype",         g_resolver.class_enum_basetype);
    optBind("mono_class_from_mono_type",        g_resolver.class_from_mono_type);
    // Phase 10 follow-up: array element-class metadata for walkArray.
    optBind("mono_class_get_element_class",     g_resolver.class_get_element_class);
    optBind("mono_class_value_size",            g_resolver.class_value_size);
    optBind("mono_class_is_valuetype",          g_resolver.class_is_valuetype);

    // Nested-type support — required to resolve `Outer/Inner` style names
    // emitted by EnumerateClasses for nested classes.
    optBind("mono_class_get_nesting_type",      g_resolver.class_get_nesting_type);
    optBind("mono_class_get_nested_types",      g_resolver.class_get_nested_types);

    // Phase 18 — method signature decoding. Optional: missing exports just
    // disable findMethodInstances signature output, the rest of the resolver
    // still works.
    optBind("mono_method_signature",            g_resolver.method_signature);
    optBind("mono_signature_get_param_count",   g_resolver.signature_get_param_count);
    optBind("mono_signature_get_params",        g_resolver.signature_get_params);
    optBind("mono_signature_get_return_type",   g_resolver.signature_get_return_type);
    optBind("mono_method_get_param_names",      g_resolver.method_get_param_names);

    // Phase 19 — managed-method invocation. Optional at resolve time so
    // pre-19 features (handshake / class walking / findMethodInstances)
    // keep working on minimal Mono builds where these are missing; the
    // callMethod handler refuses cleanly when runtime_invoke is unbound.
    optBind("mono_runtime_invoke",              g_resolver.runtime_invoke);
    optBind("mono_object_unbox",                g_resolver.object_unbox);
    optBind("mono_object_get_class",            g_resolver.object_get_class);
    optBind("mono_string_new",                  g_resolver.string_new);
    optBind("mono_string_chars",                g_resolver.string_chars);
    optBind("mono_string_length",               g_resolver.string_length);

    // Phase 28 — IL body access. Optional: missing exports just disable
    // findFieldReferences on this backend; the rest of the runtime stays up.
    // Every modern Mono build (Unity 5+) ships these; the optBind keeps the
    // resolver tolerant of legacy/stripped builds where they might not be.
    optBind("mono_method_get_header",           g_resolver.method_get_header);
    optBind("mono_method_header_get_code",      g_resolver.method_header_get_code);
    optBind("mono_metadata_free_mh",            g_resolver.metadata_free_mh);
    optBind("mono_class_get_field_token",       g_resolver.class_get_field_token);

    // Disconnect-time GC drain — closes the "game shutdown is slow after
    // a Phase 28 IL walker run" complaint. Exported by every BDWGC-based
    // Mono build (Unity 5+) under the canonical name.
    optBind("mono_gc_collect",                  g_resolver.gc_collect);

    // Phase 32 — live-object enumeration for findInstances liveOnly. Exported
    // by Unity's mono-2.0-bdwgc.dll; legacy/embedded Mono may lack them, in
    // which case liveOnly falls back to the raw pointer scan.
    optBind("mono_gc_walk_heap",                g_resolver.gc_walk_heap);
    optBind("mono_object_get_size",             g_resolver.object_get_size);

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
    Discrete::Log::Write(MagicStrings::LogCategoryInit, line);
}

}  // namespace

const Resolver& GetResolver() {
    // NOT std::call_once. call_once latches on the CALL, not on success, so a
    // first attempt made before the game had loaded mono-2.0-bdwgc.dll left
    // g_resolver.resolved == false FOREVER — DoResolve could never run again and
    // the `scan` re-detect RPC, whose whole purpose is to recover exactly that
    // state, was powerless. Retry until we actually resolve, then latch.
    //
    // Cost of the retry path is a handful of GetModuleHandle / GetProcAddress
    // lookups, and only while unresolved; once resolved this is a lock plus a
    // load. Callers are RPC entry points, not inner loops.
    static std::mutex resolveMu;
    std::lock_guard<std::mutex> lk(resolveMu);
    if (!g_resolver.resolved) DoResolve();
    return g_resolver;
}

bool IsResolved() { return GetResolver().resolved; }

}  // namespace Discrete::Mono
