// MonoClassWalker.cpp — implementation.

#include "MonoClassWalker.h"

#include "../shared/AddressBounds.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ManagedLayout.h"
#include "../shared/SehFilter.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "MonoTypeParser.h"

#include <windows.h>

namespace Discrete::Mono {

namespace {

const char* SafeStr(const char* s) { return s ? s : ""; }

#pragma warning(push)
#pragma warning(disable: 4509)
MonoClass* SehNesting(fn_class_get_nesting_type fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClass* SehNextNested(fn_class_get_nested_types fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

// Fill a ClassEntry. Nested classes are surfaced as `Outer/Inner` so
// FindClass can round-trip them via mono_class_get_nested_types. Namespace
// is taken from the outermost class — Mono only attaches a namespace there.
bool DescribeClass(MonoClass* klass, std::uint32_t token, ClassEntry& out) {
    if (klass == nullptr) return false;
    const auto& r = GetResolver();
    out.klass = klass;
    out.token = token;
    if (r.class_get_name == nullptr) return true;

    std::string leaf = SafeStr(r.class_get_name(klass));

    MonoClass* cur = klass;
    MonoClass* outer = klass;
    if (r.class_get_nesting_type != nullptr) {
        for (int i = 0; i < 16; ++i) {
            MonoClass* n = SehNesting(r.class_get_nesting_type, cur);
            if (n == nullptr) break;
            std::string nm = SafeStr(r.class_get_name(n));
            if (!nm.empty()) leaf = nm + "/" + leaf;
            outer = n;
            cur = n;
        }
    }
    out.name = std::move(leaf);
    if (r.class_get_namespace != nullptr) out.ns = SafeStr(r.class_get_namespace(outer));
    return true;
}

// Search direct nested children of `outer` for a name match.
MonoClass* FindNestedByName(MonoClass* outer, const std::string& nestedName) {
    const auto& r = GetResolver();
    if (outer == nullptr || r.class_get_nested_types == nullptr || r.class_get_name == nullptr) {
        return nullptr;
    }
    void* iter = nullptr;
    for (;;) {
        MonoClass* k = SehNextNested(r.class_get_nested_types, outer, &iter);
        if (k == nullptr) return nullptr;
        const char* n = r.class_get_name(k);
        if (n != nullptr && nestedName == n) return k;
    }
}

#pragma warning(push)
#pragma warning(disable: 4509)
// Each *_Seh helper wraps a single Mono call; SEH cannot cohabit a frame with
// destructors, so the helpers stay leaf-only.
const MonoTableInfo* SehGetTable(fn_image_get_table_info fn, MonoImage* img, int idx) {
    __try { return fn(img, idx); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
int SehGetRows(fn_table_info_get_rows fn, const MonoTableInfo* t) {
    __try { return fn(t); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}
MonoClass* SehClassGet(fn_class_get fn, MonoImage* img, std::uint32_t tok) {
    __try { return fn(img, tok); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClass* SehClassFromName(fn_class_from_name fn, MonoImage* img, const char* ns, const char* nm) {
    __try { return fn(img, ns, nm); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClass* SehParent(fn_class_get_parent fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClassField* SehNextField(fn_class_get_fields fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoMethod* SehNextMethod(fn_class_get_methods fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClass* SehClassFromMonoType(fn_class_from_mono_type fn, MonoType* t) {
    __try { return fn(t); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
int SehClassIsEnum(fn_class_is_enum fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
int SehClassIsValueType(fn_class_is_valuetype fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
MonoType* SehEnumBasetype(fn_class_enum_basetype fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClass* SehObjectGetClass(fn_object_get_class fn, MonoObject* o) {
    __try { return fn(o); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassNameProbe(fn_class_get_name fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoVTable* SehVTable(fn_class_vtable fn, MonoDomain* d, MonoClass* k) {
    __try { return fn(d, k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehStaticData(fn_vtable_get_static_data fn, MonoVTable* v) {
    __try { return fn(v); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehCompile(fn_compile_method fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoImage* SehClassImageProbe(fn_class_get_image fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehImageNameProbe(fn_image_get_name fn, MonoImage* img) {
    __try { return fn(img); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// Copy a NUL-terminated runtime name into a bounded local buffer under SEH so
// arbitrary garbage pointers never AV off mapped memory, then validate as
// printable ASCII or strict UTF-8 (B-M5: allows CJK / Unicode obfuscated names
// while still rejecting control chars, invalid UTF-8, empty or unterminated
// garbage).
bool SehCopyNameBounded(const char* s, char* out, std::size_t cap) noexcept {
    __try {
        for (std::size_t i = 0; i < cap; ++i) {
            char ch = s[i];
            out[i] = ch;
            if (ch == '\0') return i > 0;
        }
        return false;  // no NUL within cap bytes → garbage
    } __except (Discrete::Seh::MemoryAccessExceptionFilter(
                    GetExceptionCode(), GetExceptionInformation())) {
        return false;
    }
}

bool SehNameLooksSane(const char* s) {
    if (s == nullptr) return false;
    char buf[512];
    if (!SehCopyNameBounded(s, buf, sizeof(buf))) return false;
    return Discrete::AddressBounds::LooksLikePlausibleClassName(buf, sizeof(buf));
}
#pragma warning(pop)

// Shared validity gate, assuming the Mono thread is ALREADY attached (caller
// holds a ThreadAttachGuard) so it never nests attach/detach. A genuine
// MonoClass has a sane type name AND belongs to an image whose own name is sane.
// A managed-heap object misread as a class derefs klass->image to garbage, which
// has no sane image name — the image round-trip is the discriminator the
// original name-only check lacked. Image accessors absent (older runtime) →
// name-sanity stands alone.
bool KlassLooksValid_Attached(MonoClass* klass) {
    const auto& r = GetResolver();
    if (klass == nullptr || r.class_get_name == nullptr) return false;
    if (!SehNameLooksSane(SehClassNameProbe(r.class_get_name, klass))) return false;
    if (r.class_get_image != nullptr && r.image_get_name != nullptr) {
        MonoImage* img = SehClassImageProbe(r.class_get_image, klass);
        if (img == nullptr) return false;
        if (!SehNameLooksSane(SehImageNameProbe(r.image_get_name, img))) return false;
    }
    return true;
}

}  // namespace

std::vector<ClassEntry> EnumerateClasses(MonoImage* image) {
    std::vector<ClassEntry> out;
    if (image == nullptr) return out;

    const auto& r = GetResolver();
    if (!r.resolved || r.image_get_table_info == nullptr ||
        r.table_info_get_rows == nullptr || r.class_get == nullptr) {
        return out;
    }

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return out;

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    const MonoTableInfo* table = SehGetTable(r.image_get_table_info, image, MonoTableTypedef);
    if (table == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "EnumerateClasses: image_get_table_info returned null");
        return out;
    }
    int rows = SehGetRows(r.table_info_get_rows, table);
    if (rows <= 0) return out;

    out.reserve(static_cast<std::size_t>(rows));
    // Row 0 (token 0x02000001) is the synthetic <Module> class — skip it.
    for (int i = 1; i < rows; ++i) {
        std::uint32_t token = MonoTokenTypeDef | static_cast<std::uint32_t>(i + 1);
        MonoClass* klass = SehClassGet(r.class_get, image, token);
        ClassEntry entry;
        if (DescribeClass(klass, token, entry)) {
            out.push_back(std::move(entry));
        }
    }
    return out;
}

MonoClass* FindClass(MonoImage* image, const std::string& ns, const std::string& name) {
    if (image == nullptr || name.empty()) return nullptr;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_from_name == nullptr) return nullptr;

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return nullptr;

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return nullptr;

    // Fast path: simple top-level type.
    std::size_t slash = name.find('/');
    if (slash == std::string::npos) {
        MonoClass* k = SehClassFromName(r.class_from_name, image, ns.c_str(), name.c_str());
        if (k != nullptr) return k;

        // Phase 31-6 (b) — nested-type fallback. A nested type's FQN comes
        // across with a '.' separator (e.g. "CharacterData.NonSerialized"),
        // which callers split as ns="CharacterData", name="NonSerialized".
        // But "CharacterData" is the ENCLOSING TYPE, not a namespace, so the
        // direct lookup fails. Retry: treat the last dot-segment of `ns` as
        // the outer type (its own namespace = the remaining head), resolve it,
        // then find the nested `name` via mono_class_get_nested_types. Handles
        // one level of nesting — the common case (CE expands these; we should
        // too). Failure-only, so top-level lookups are unaffected.
        if (!ns.empty() && r.class_get_nested_types != nullptr) {
            std::size_t dot = ns.rfind('.');
            std::string nsHead   = (dot == std::string::npos) ? std::string() : ns.substr(0, dot);
            std::string outerName = (dot == std::string::npos) ? ns : ns.substr(dot + 1);
            MonoClass* outer = SehClassFromName(r.class_from_name, image, nsHead.c_str(), outerName.c_str());
            if (outer != nullptr) {
                MonoClass* nested = FindNestedByName(outer, name);
                if (nested != nullptr) return nested;
            }
        }
        return nullptr;
    }

    // Nested form: walk Outer/Mid/Inner via mono_class_get_nested_types.
    std::string outerName = name.substr(0, slash);
    MonoClass* cur = SehClassFromName(r.class_from_name, image, ns.c_str(), outerName.c_str());
    if (cur == nullptr) return nullptr;

    std::size_t pos = slash + 1;
    while (pos < name.size() && cur != nullptr) {
        std::size_t next = name.find('/', pos);
        std::string segment = (next == std::string::npos)
            ? name.substr(pos)
            : name.substr(pos, next - pos);
        cur = FindNestedByName(cur, segment);
        if (next == std::string::npos) break;
        pos = next + 1;
    }
    return cur;
}

ClassInfoResult ReadClassInfo(MonoClass* klass, bool forceSetup) {
    ClassInfoResult result;
    if (klass == nullptr) return result;

    const auto& r = GetResolver();
    if (!r.resolved) return result;

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return result;

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return result;

    // Defence-in-depth: never decode a pointer that doesn't look like a real
    // MonoClass. A garbage class (a managed-object pointer pulled from a field
    // slot, or a stale parent from class_get_parent) survives the per-accessor
    // SEH guards but AVs in the unbounded strlen that builds the name string
    // (DescribeClass below calls class_get_name RAW). Reuse the held guard.
    if (!KlassLooksValid_Attached(klass)) return result;

    DescribeClass(klass, 0, result.info.self);

    if (r.class_get_parent != nullptr) {
        if (MonoClass* parent = SehParent(r.class_get_parent, klass)) {
            DescribeClass(parent, 0, result.info.parent);
        }
    }

    // Collect this class's OWN fields. Wrapped in a lambda so it can run a
    // second time after a forced class setup (below). `mono_class_get_fields`
    // returns NOTHING for a class whose metadata hasn't been set up yet — a
    // class only ever reached via a field-type reference / Inspector drilldown
    // (never JIT-touched). The find-instances path implicitly sets up its
    // target through `mono_class_vtable`, which is why a top-level inspected
    // class shows every field but a nested element class (e.g. 活俠傳
    // Mortal.Core.StatConvertData reached through StatConvertCollectionData.
    // _list) came back with only the inherited m_CachedPtr.
    auto collectFields = [&]() {
        if (r.class_get_fields == nullptr) return;
        void* iter = nullptr;
        for (;;) {
            MonoClassField* f = SehNextField(r.class_get_fields, klass, &iter);
            if (f == nullptr) break;
            FieldEntry fe;
            if (r.field_get_name != nullptr)   fe.name = SafeStr(r.field_get_name(f));
            if (r.field_get_offset != nullptr) fe.offset = Discrete::ManagedLayout::NormalizeFieldOffset32(r.field_get_offset(f));
            if (r.field_get_flags != nullptr)  fe.flags = r.field_get_flags(f);
            MonoType* ft = (r.field_get_type != nullptr) ? r.field_get_type(f) : nullptr;
            if (ft != nullptr) fe.typeName = TypeName(ft);
            // Field-class introspection — drives the inspector's classifier.
            // Skipped for STATIC fields (they're filtered out at emit time
            // anyway) and when the runtime export isn't available. Without
            // class_from_mono_type we can't bridge MonoType*→MonoClass*, so
            // is_enum / is_value_type stay false and the UI keeps its old
            // best-effort behaviour.
            if (ft != nullptr && r.class_from_mono_type != nullptr) {
                MonoClass* fc = SehClassFromMonoType(r.class_from_mono_type, ft);
                if (fc != nullptr) {
                    if (r.class_is_valuetype != nullptr) {
                        fe.isValueType = SehClassIsValueType(r.class_is_valuetype, fc) != 0;
                    }
                    if (r.class_is_enum != nullptr) {
                        fe.isEnum = SehClassIsEnum(r.class_is_enum, fc) != 0;
                    }
                    if (fe.isEnum && r.class_enum_basetype != nullptr) {
                        MonoType* base = SehEnumBasetype(r.class_enum_basetype, fc);
                        if (base != nullptr) fe.underlyingTypeName = TypeName(base);
                    }
                    // Phase 31-2 (Phase B) — remember the field's runtime class
                    // for inline value-type STRUCT fields (not enums) so the
                    // decode paths can recurse into the struct's members by
                    // class pointer (exact, nested-type-safe). Primitives also
                    // match isValueType but the decode-site gate excludes them
                    // (they decode to a scalar value, never a bare pointer).
                    if (fe.isValueType && !fe.isEnum) fe.valueTypeClass = fc;
                }
            }
            result.info.fields.push_back(std::move(fe));
        }
    };
    collectFields();

    // One-shot force-setup: if the class reported NO own fields, resolve it via
    // a SINGLE mono_class_vtable(rootDomain, klass) — the same safe per-class
    // call find-instances already makes (ExportAPI CmdFindInstances), NOT the
    // batched 17K-call form that wedged BDWGC on the reverse-lookup path. This
    // sets up the field table so the retry sees the real fields. Only fires on
    // the rare empty-field case, so already-set-up classes pay nothing.
    //
    // GATED on forceSetup: bulk metadata scanners (searchSymbols / dumpClassInfo)
    // pass false because mono_class_vtable from the worker thread deadlocks the
    // BDWGC GC, and a bulk scan hits thousands of never-set-up classes — each
    // one a freeze roll. One call from a single-class drilldown is safe; a
    // thousand from a bulk scan is not. (Core Keeper freeze, 2026-06-06.)
    if (forceSetup && result.info.fields.empty() && r.class_vtable != nullptr) {
        if (SehVTable(r.class_vtable, domain, klass) != nullptr) {
            collectFields();
        }
    }

    if (r.class_get_methods != nullptr) {
        void* iter = nullptr;
        for (;;) {
            MonoMethod* m = SehNextMethod(r.class_get_methods, klass, &iter);
            if (m == nullptr) break;
            MethodEntry me;
            if (r.method_get_name != nullptr)  me.name = SafeStr(r.method_get_name(m));
            if (r.method_get_token != nullptr) me.token = r.method_get_token(m);
            if (r.method_get_flags != nullptr) {
                std::uint32_t iflags = 0;
                me.flags = r.method_get_flags(m, &iflags);
            }
            result.info.methods.push_back(std::move(me));
        }
    }

    result.ok = true;
    return result;
}

MonoClass* ClassOfObject(std::uintptr_t objPtr) {
    if (objPtr == 0) return nullptr;
    const auto& r = GetResolver();
    if (!r.resolved || r.object_get_class == nullptr) return nullptr;
    return SehObjectGetClass(r.object_get_class, reinterpret_cast<MonoObject*>(objPtr));
}

bool ProbeKlassValid(MonoClass* klass) {
    if (klass == nullptr) return false;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_name == nullptr) return false;
    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return false;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return false;
    return KlassLooksValid_Attached(klass);
}

StaticFieldAddress ResolveStaticFieldAddress(MonoClass* klass, const std::string& fieldName) {
    StaticFieldAddress out;
    if (klass == nullptr || fieldName.empty()) return out;

    const auto& r = GetResolver();
    if (!r.resolved || r.class_vtable == nullptr || r.vtable_get_static_data == nullptr ||
        r.class_get_fields == nullptr) {
        return out;
    }

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return out;

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    // Find the field by name first; we need its offset and type before touching the vtable.
    MonoClassField* match = nullptr;
    void* iter = nullptr;
    for (;;) {
        MonoClassField* f = SehNextField(r.class_get_fields, klass, &iter);
        if (f == nullptr) break;
        const char* n = (r.field_get_name != nullptr) ? r.field_get_name(f) : nullptr;
        if (n != nullptr && fieldName == n) { match = f; break; }
    }
    if (match == nullptr) return out;

    std::uint32_t flags = (r.field_get_flags != nullptr) ? r.field_get_flags(match) : 0u;
    std::int32_t offset = (r.field_get_offset != nullptr)
        ? Discrete::ManagedLayout::NormalizeFieldOffset32(r.field_get_offset(match))
        : -1;
    std::string typeName = (r.field_get_type != nullptr) ? TypeName(r.field_get_type(match)) : std::string{};
    out.offset   = offset;
    out.flags    = flags;
    out.typeName = typeName;

    // B-M4: reject non-static, const/literal (0x40), and [ThreadStatic] (-1)
    // fields before touching vtable/static_data so we never return base-1 or
    // alias offset 0 of static storage.
    const auto kind = Discrete::ManagedLayout::ClassifyStaticFieldStorage(flags, offset);
    if (kind != Discrete::ManagedLayout::StaticFieldStorageKind::Regular) {
        out.reason = Discrete::ManagedLayout::StaticFieldStorageReason(kind);
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            std::string("ResolveStaticFieldAddress: rejected (") + out.reason + ")");
        return out;
    }

    MonoVTable* vt = SehVTable(r.class_vtable, domain, klass);
    if (vt == nullptr) {
        out.reason = "vtable_null";
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "ResolveStaticFieldAddress: mono_class_vtable returned null");
        return out;
    }
    void* base = SehStaticData(r.vtable_get_static_data, vt);
    if (base == nullptr) {
        out.reason = "static_data_null";
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "ResolveStaticFieldAddress: vtable_get_static_field_data returned null");
        return out;
    }

    out.ok       = true;
    out.address  = static_cast<std::uint8_t*>(base) + static_cast<std::size_t>(offset);
    return out;
}

// Phase 29a-v3 — batch static-base resolver. Calls vtable + static_data
// once per class; caller computes per-field addresses locally.
void* ResolveStaticBaseAddress(MonoClass* klass) {
    if (klass == nullptr) return nullptr;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_vtable == nullptr || r.vtable_get_static_data == nullptr) {
        return nullptr;
    }
    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return nullptr;

    // Caller is expected to hold a ThreadAttachGuard (reentrant guard
    // turns this into a no-op when nested under the snapshot's outer
    // guard). We still open a fallback guard here so direct callers
    // outside the snapshot path stay correct.
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return nullptr;

    MonoVTable* vt = SehVTable(r.class_vtable, domain, klass);
    if (vt == nullptr) return nullptr;
    return SehStaticData(r.vtable_get_static_data, vt);
}

CompiledMethodAddress CompileMethodByName(MonoClass* klass, const std::string& methodName) {
    CompiledMethodAddress out;
    if (klass == nullptr || methodName.empty()) return out;

    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_methods == nullptr || r.compile_method == nullptr) return out;

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return out;

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    MonoMethod* match = nullptr;
    void* iter = nullptr;
    for (;;) {
        MonoMethod* m = SehNextMethod(r.class_get_methods, klass, &iter);
        if (m == nullptr) break;
        const char* n = (r.method_get_name != nullptr) ? r.method_get_name(m) : nullptr;
        if (n != nullptr && methodName == n) { match = m; break; }
    }
    if (match == nullptr) return out;

    out.name  = methodName;
    out.token = (r.method_get_token != nullptr) ? r.method_get_token(match) : 0u;
    void* p = SehCompile(r.compile_method, match);
    if (p == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "CompileMethodByName: mono_compile_method returned null / faulted");
        return out;
    }
    out.address = p;
    out.ok = true;
    return out;
}

namespace {
#pragma warning(push)
#pragma warning(disable: 4509)
const char* SehFieldData(fn_field_get_data fn, MonoClassField* f) {
    __try { return fn(f); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)
}  // namespace

std::vector<LiteralFieldEntry> EnumerateLiteralFields(MonoClass* klass) {
    std::vector<LiteralFieldEntry> out;
    if (klass == nullptr) return out;

    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_fields == nullptr) return out;
    if (r.field_get_data == nullptr) return out;  // optional export missing

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return out;

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    void* iter = nullptr;
    for (;;) {
        MonoClassField* f = SehNextField(r.class_get_fields, klass, &iter);
        if (f == nullptr) break;
        std::uint32_t flags = (r.field_get_flags != nullptr) ? r.field_get_flags(f) : 0u;
        // FIELD_ATTRIBUTE_LITERAL = 0x40 (ECMA-335 §II.23.1.5).
        if (!(flags & 0x40u)) continue;

        LiteralFieldEntry e;
        if (r.field_get_name != nullptr)  e.name     = SafeStr(r.field_get_name(f));
        if (r.field_get_type != nullptr)  e.typeName = TypeName(r.field_get_type(f));
        e.data = SehFieldData(r.field_get_data, f);
        if (e.name.empty()) continue;
        out.push_back(std::move(e));
    }
    return out;
}

}  // namespace Discrete::Mono
