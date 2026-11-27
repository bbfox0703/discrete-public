// MonoResolver.cpp — implementation.

#include "MonoResolver.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ModuleHelper.h"
#include "../shared/ThrottledRetry.h"

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

namespace Discrete::Mono {

namespace {

// B-L1: Lock-free publish-once on success, immutable unresolved fallback on
// failure, with 1s throttled retries so a missing export neither races with
// concurrent readers (writing moduleName / slots in-place) nor floods logs.
std::atomic<const Resolver*> g_resolved{nullptr};
const Resolver               g_unresolved{};
std::mutex                   g_resolveMu;
std::uint64_t                g_lastFailTickMs = 0;

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

void DoResolve(Resolver& out) {
    const wchar_t* module = PickMonoModule();
    if (module == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryInit,
            "MonoResolver: no mono module loaded (mono-2.0-bdwgc.dll / mono.dll)");
        out.resolved = false;
        return;
    }
    out.moduleName = module;

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

    tryBind("mono_get_root_domain",        out.get_root_domain);
    // B-L3: mono_domain_get is unused by the Mono backend (root domain is used
    // everywhere); demote to optBind so missing it never disables the backend.
    optBind("mono_domain_get",             out.domain_get);
    tryBind("mono_thread_attach",          out.thread_attach);
    tryBind("mono_thread_detach",          out.thread_detach);
    tryBind("mono_assembly_foreach",       out.assembly_foreach);
    tryBind("mono_assembly_get_image",     out.assembly_get_image);
    // assembly_get_name + assembly_name_get_name: optional in practice.
    // MonoDomain::OnAssembly uses image_get_name as the primary source for
    // the friendly assembly name and only falls back to this pair when the
    // image-side path returns empty. Unity 5.3-era legacy mono.dll (pre-
    // bdwgc Mono 2.x fork) ships without these two exports — see Endless
    // Legend smoke 2026-05-19. Demoting to optBind unblocks legacy targets;
    // call sites are already null-guarded.
    optBind("mono_assembly_get_name",      out.assembly_get_name);
    optBind("mono_assembly_name_get_name", out.assembly_name_get_name);
    tryBind("mono_image_get_name",         out.image_get_name);
    tryBind("mono_image_get_filename",     out.image_get_filename);

    // Segment 2: class / field / method walking.
    tryBind("mono_image_get_table_info",   out.image_get_table_info);
    tryBind("mono_table_info_get_rows",    out.table_info_get_rows);
    tryBind("mono_class_get",              out.class_get);
    tryBind("mono_class_from_name",        out.class_from_name);
    tryBind("mono_class_get_name",         out.class_get_name);
    tryBind("mono_class_get_namespace",    out.class_get_namespace);
    tryBind("mono_class_get_parent",       out.class_get_parent);
    tryBind("mono_class_get_image",        out.class_get_image);
    tryBind("mono_class_get_fields",       out.class_get_fields);
    tryBind("mono_class_get_methods",      out.class_get_methods);
    tryBind("mono_field_get_name",         out.field_get_name);
    tryBind("mono_field_get_type",         out.field_get_type);
    tryBind("mono_field_get_offset",       out.field_get_offset);
    tryBind("mono_field_get_flags",        out.field_get_flags);
    tryBind("mono_method_get_name",        out.method_get_name);
    tryBind("mono_method_get_token",       out.method_get_token);
    tryBind("mono_method_get_flags",       out.method_get_flags);
    tryBind("mono_type_get_name",          out.type_get_name);
    // mono_free: ideally we call it to free the char* mono_type_get_name
    // allocates, but Unity 5.3-era legacy mono.dll doesn't export it.
    // Without mono_free, we leak a small string per type-name lookup —
    // bounded, non-fatal. MonoTypeParser is already null-guarded at the
    // call site. Demoting unblocks legacy Unity 5.x Mono targets.
    optBind("mono_free",                   out.free);

    // Segment 3: static-field address + JIT compile.
    tryBind("mono_class_vtable",                out.class_vtable);
    tryBind("mono_vtable_get_static_field_data", out.vtable_get_static_data);
    tryBind("mono_compile_method",              out.compile_method);
    optBind("mono_class_is_generic",            out.class_is_generic);
    optBind("mono_method_is_generic",           out.method_is_generic);

    // Phase 5 follow-up: enum decode (true-optional — missing exports just
    // disable getEnumValues, don't take the whole resolver offline).
    optBind("mono_field_get_data",              out.field_get_data);
    optBind("mono_class_is_enum",               out.class_is_enum);
    optBind("mono_class_enum_basetype",         out.class_enum_basetype);
    optBind("mono_class_from_mono_type",        out.class_from_mono_type);
    // Phase 10 follow-up: array element-class metadata for walkArray + B-L2 rank check.
    optBind("mono_class_get_element_class",     out.class_get_element_class);
    optBind("mono_class_get_rank",              out.class_get_rank);
    optBind("mono_class_value_size",            out.class_value_size);
    optBind("mono_class_instance_size",         out.class_instance_size);
    optBind("mono_class_is_valuetype",          out.class_is_valuetype);

    // Nested-type support — required to resolve `Outer/Inner` style names
    // emitted by EnumerateClasses for nested classes.
    optBind("mono_class_get_nesting_type",      out.class_get_nesting_type);
    optBind("mono_class_get_nested_types",      out.class_get_nested_types);

    // Phase 18 — method signature decoding. Optional: missing exports just
    // disable findMethodInstances signature output, the rest of the resolver
    // still works.
    optBind("mono_method_signature",            out.method_signature);
    optBind("mono_signature_get_param_count",   out.signature_get_param_count);
    optBind("mono_signature_get_params",        out.signature_get_params);
    optBind("mono_signature_get_return_type",   out.signature_get_return_type);
    optBind("mono_method_get_param_names",      out.method_get_param_names);

    // Phase 19 — managed-method invocation. Optional at resolve time so
    // pre-19 features (handshake / class walking / findMethodInstances)
    // keep working on minimal Mono builds where these are missing; the
    // callMethod handler refuses cleanly when runtime_invoke is unbound.
    optBind("mono_runtime_invoke",              out.runtime_invoke);
    optBind("mono_object_unbox",                out.object_unbox);
    optBind("mono_object_get_class",            out.object_get_class);
    optBind("mono_string_new",                  out.string_new);
    optBind("mono_string_chars",                out.string_chars);
    optBind("mono_string_length",               out.string_length);
    optBind("mono_gchandle_new",                out.gchandle_new);
    optBind("mono_gchandle_free",               out.gchandle_free);

    // Phase 28 — IL body access. Optional: missing exports just disable
    // findFieldReferences on this backend; the rest of the runtime stays up.
    // Every modern Mono build (Unity 5+) ships these; the optBind keeps the
    // resolver tolerant of legacy/stripped builds where they might not be.
    optBind("mono_method_get_header",           out.method_get_header);
    optBind("mono_method_header_get_code",      out.method_header_get_code);
    optBind("mono_metadata_free_mh",            out.metadata_free_mh);
    optBind("mono_class_get_field_token",       out.class_get_field_token);
    optBind("mono_field_from_token",            out.field_from_token);
    optBind("mono_field_get_parent",            out.field_get_parent);

    // Disconnect-time GC drain — closes the "game shutdown is slow after
    // a Phase 28 IL walker run" complaint. Exported by every BDWGC-based
    // Mono build (Unity 5+) under the canonical name.
    optBind("mono_gc_collect",                  out.gc_collect);

    // Phase 32 — live-object enumeration for findInstances liveOnly. Exported
    // by Unity's mono-2.0-bdwgc.dll; legacy/embedded Mono may lack them, in
    // which case liveOnly falls back to the raw pointer scan.
    optBind("mono_gc_walk_heap",                out.gc_walk_heap);
    optBind("mono_object_get_size",             out.object_get_size);

    out.resolved = (ok == total);

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
    if (const Resolver* p = g_resolved.load(std::memory_order_acquire)) {
        return *p;
    }
    std::lock_guard<std::mutex> lk(g_resolveMu);
    if (const Resolver* p = g_resolved.load(std::memory_order_relaxed)) {
        return *p;
    }
    const std::uint64_t now = ::GetTickCount64();
    if (!RetryPolicy::CanRetryNow(g_lastFailTickMs, now)) {
        return g_unresolved;
    }
    Resolver local;
    DoResolve(local);
    if (local.resolved) {
        // Heap-allocated, intentionally leaked immutable singleton — never
        // mutated after publication and safe across process teardown.
        auto* published = new Resolver(std::move(local));
        g_resolved.store(published, std::memory_order_release);
        return *published;
    }
    g_lastFailTickMs = RetryPolicy::NonZeroTick(::GetTickCount64());
    return g_unresolved;
}

bool IsResolved() { return GetResolver().resolved; }

}  // namespace Discrete::Mono
