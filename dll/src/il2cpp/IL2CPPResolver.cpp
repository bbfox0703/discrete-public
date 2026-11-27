// IL2CPPResolver.cpp — implementation.

#include "IL2CPPResolver.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ModuleHelper.h"
#include "../shared/ThrottledRetry.h"

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

namespace Discrete::IL2CPP {

namespace {

// B-L1: Latch permanently on success; on failure return an immutable
// unresolved Resolver and throttle retries to once per second so early
// startup probes never lock out later recovery.
std::atomic<const Resolver*> g_resolved{nullptr};
const Resolver               g_unresolved{};
std::mutex                   g_resolveMu;
std::uint64_t                g_lastFailTickMs = 0;

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
        Discrete::Log::Write(MagicStrings::LogCategoryInit, msg);
        return false;
    }
    return true;
}

void DoResolve(Resolver& out) {
    if (ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP) == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryInit,
            "IL2CPPResolver: GameAssembly.dll not loaded");
        out.resolved = false;
        return;
    }
    out.moduleName = MagicStrings::ModuleIL2CPP;
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
    bindReq("il2cpp_domain_get",                out.domain_get);
    bindReq("il2cpp_thread_attach",             out.thread_attach);
    bindReq("il2cpp_thread_detach",             out.thread_detach);
    bindReq("il2cpp_domain_get_assemblies",     out.domain_get_assemblies);
    bindReq("il2cpp_assembly_get_image",        out.assembly_get_image);
    bindReq("il2cpp_image_get_name",            out.image_get_name);
    // Unity 2018.x IL2CPP runtime didn't export these two yet — they were
    // added between Unity 2018.x and 2019.x. Demoted from REQUIRED to OPTIONAL
    // 2026-05-08 so the resolver's `resolved` flag flips green for Unity
    // 2018.x targets (e.g. R-Type Dimensions EX). When unbound, the class
    // walker falls back to the MetadataParser-table-driven path that uses
    // `il2cpp_class_from_name` per type — slower but functional.
    bindOpt("il2cpp_image_get_class_count",     out.image_get_class_count);
    bindOpt("il2cpp_image_get_class",           out.image_get_class);
    bindReq("il2cpp_class_from_name",           out.class_from_name);
    bindReq("il2cpp_class_get_name",            out.class_get_name);
    bindReq("il2cpp_class_get_namespace",       out.class_get_namespace);
    bindReq("il2cpp_class_get_fields",          out.class_get_fields);
    bindReq("il2cpp_class_get_methods",         out.class_get_methods);
    bindReq("il2cpp_field_get_name",            out.field_get_name);
    bindReq("il2cpp_field_get_type",            out.field_get_type);
    bindReq("il2cpp_field_get_offset",          out.field_get_offset);
    bindReq("il2cpp_method_get_name",           out.method_get_name);
    bindReq("il2cpp_type_get_name",             out.type_get_name);

    // --- optional: nicer-to-have, varies across Unity / IL2CPP versions ------
    bindOpt("il2cpp_image_get_filename",        out.image_get_filename);
    bindOpt("il2cpp_class_get_parent",          out.class_get_parent);
    bindOpt("il2cpp_class_get_image",           out.class_get_image);
    // Nested-type support — required to resolve `Outer/Inner` style names
    // emitted by EnumerateClasses for nested classes.
    bindOpt("il2cpp_class_get_declaring_type",  out.class_get_declaring);
    bindOpt("il2cpp_class_get_nested_types",    out.class_get_nested_types);
    bindOpt("il2cpp_class_get_static_field_data", out.class_get_static_data);
    // Phase 10 follow-up: array element-class metadata for walkArray.
    bindOpt("il2cpp_class_get_element_class",   out.class_get_element_class);
    bindOpt("il2cpp_class_value_size",          out.class_value_size);
    bindOpt("il2cpp_class_instance_size",       out.class_instance_size);
    bindOpt("il2cpp_object_get_size",           out.object_get_size);
    bindOpt("il2cpp_class_is_valuetype",        out.class_is_valuetype);
    bindOpt("il2cpp_class_is_enum",             out.class_is_enum);
    bindOpt("il2cpp_class_enum_basetype",       out.class_enum_basetype);
    bindOpt("il2cpp_field_get_flags",           out.field_get_flags);
    bindOpt("il2cpp_method_get_token",          out.method_get_token);
    bindOpt("il2cpp_method_get_flags",          out.method_get_flags);
    bindOpt("il2cpp_method_get_pointer",        out.method_get_pointer);
    // Phase 18 — method signature decoding.
    bindOpt("il2cpp_method_get_param_count",    out.method_get_param_count);
    bindOpt("il2cpp_method_get_param",          out.method_get_param);
    bindOpt("il2cpp_method_get_param_name",     out.method_get_param_name);
    bindOpt("il2cpp_method_get_return_type",    out.method_get_return_type);
    bindOpt("il2cpp_free",                      out.free);
    // Type → class bridge for inspector enum / value-type detection.
    bindOpt("il2cpp_class_from_il2cpp_type",    out.class_from_il2cpp_type);
    // Phase 19 — managed-method invocation. Optional at resolve time so
    // pre-19 features (handshake / class walking / findMethodInstances)
    // keep working on minimal Unity builds where these are missing; the
    // callMethod handler refuses cleanly when runtime_invoke is unbound.
    bindOpt("il2cpp_runtime_invoke",            out.runtime_invoke);
    bindOpt("il2cpp_object_unbox",              out.object_unbox);
    bindOpt("il2cpp_object_get_class",          out.object_get_class);
    bindOpt("il2cpp_string_new",                out.string_new);
    bindOpt("il2cpp_string_chars",              out.string_chars);
    bindOpt("il2cpp_string_length",             out.string_length);
    bindOpt("il2cpp_gchandle_new",              out.gchandle_new);
    bindOpt("il2cpp_gchandle_free",             out.gchandle_free);

    // Disconnect-time GC drain — closes the "game shutdown is slow after
    // a Phase 28 IL walker / value-scan run" complaint. Exported by every
    // modern Unity IL2CPP runtime under the canonical name.
    bindOpt("il2cpp_gc_collect",                out.gc_collect);

    out.resolved = (okReq == totalReq);

    char line[200];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                  "IL2CPPResolver: GameAssembly.dll — required %d/%d, optional %d/%d (resolved=%s)",
                  okReq, totalReq, okOpt, totalOpt,
                  out.resolved ? "yes" : "no");
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
        auto* published = new Resolver(std::move(local));
        g_resolved.store(published, std::memory_order_release);
        return *published;
    }
    g_lastFailTickMs = RetryPolicy::NonZeroTick(::GetTickCount64());
    return g_unresolved;
}

bool IsResolved() { return GetResolver().resolved; }

}  // namespace Discrete::IL2CPP
