// MonoClassWalker.cpp — implementation.

#include "MonoClassWalker.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "MonoTypeParser.h"

#include <windows.h>

namespace UnityCEDumper::Mono {

namespace {

const char* SafeStr(const char* s) { return s ? s : ""; }

// Fill a ClassEntry from a live MonoClass*. Returns false on null inputs.
bool DescribeClass(MonoClass* klass, std::uint32_t token, ClassEntry& out) {
    if (klass == nullptr) return false;
    const auto& r = GetResolver();
    out.klass = klass;
    out.token = token;
    if (r.class_get_name != nullptr)      out.name = SafeStr(r.class_get_name(klass));
    if (r.class_get_namespace != nullptr) out.ns   = SafeStr(r.class_get_namespace(klass));
    return true;
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
MonoVTable* SehVTable(fn_class_vtable fn, MonoDomain* d, MonoClass* k) {
    __try { return fn(d, k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehStaticData(fn_vtable_get_static_data fn, MonoVTable* v) {
    __try { return fn(v); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehCompile(fn_compile_method fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

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
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
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

    return SehClassFromName(r.class_from_name, image, ns.c_str(), name.c_str());
}

ClassInfoResult ReadClassInfo(MonoClass* klass) {
    ClassInfoResult result;
    if (klass == nullptr) return result;

    const auto& r = GetResolver();
    if (!r.resolved) return result;

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return result;

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return result;

    DescribeClass(klass, 0, result.info.self);

    if (r.class_get_parent != nullptr) {
        if (MonoClass* parent = SehParent(r.class_get_parent, klass)) {
            DescribeClass(parent, 0, result.info.parent);
        }
    }

    if (r.class_get_fields != nullptr) {
        void* iter = nullptr;
        for (;;) {
            MonoClassField* f = SehNextField(r.class_get_fields, klass, &iter);
            if (f == nullptr) break;
            FieldEntry fe;
            if (r.field_get_name != nullptr)   fe.name = SafeStr(r.field_get_name(f));
            if (r.field_get_offset != nullptr) fe.offset = r.field_get_offset(f);
            if (r.field_get_flags != nullptr)  fe.flags = r.field_get_flags(f);
            if (r.field_get_type != nullptr)   fe.typeName = TypeName(r.field_get_type(f));
            result.info.fields.push_back(std::move(fe));
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
    if ((flags & 0x10u) == 0) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
            "ResolveStaticFieldAddress: field is not static");
        return out;
    }
    int offset = (r.field_get_offset != nullptr) ? r.field_get_offset(match) : 0;
    std::string typeName = (r.field_get_type != nullptr) ? TypeName(r.field_get_type(match)) : std::string{};

    MonoVTable* vt = SehVTable(r.class_vtable, domain, klass);
    if (vt == nullptr) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
            "ResolveStaticFieldAddress: mono_class_vtable returned null");
        return out;
    }
    void* base = SehStaticData(r.vtable_get_static_data, vt);
    if (base == nullptr) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
            "ResolveStaticFieldAddress: vtable_get_static_field_data returned null");
        return out;
    }

    out.ok       = true;
    out.address  = static_cast<std::uint8_t*>(base) + offset;
    out.offset   = offset;
    out.flags    = flags;
    out.typeName = std::move(typeName);
    return out;
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
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryWalk,
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

}  // namespace UnityCEDumper::Mono
