// IL2CPPClassWalker.cpp — implementation.

#include "IL2CPPClassWalker.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/MethodSignatureFormat.h"
#include "IL2CPPDomain.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"
#include "IL2CPPTypeParser.h"
#include "MetadataParser.h"

#include <windows.h>

#include <cstring>

namespace Discrete::IL2CPP {

namespace {

const char* SafeStr(const char* s) { return s ? s : ""; }

#pragma warning(push)
#pragma warning(disable: 4509)
Il2CppClass* SehDeclaring(fn_class_get_declaring fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
Il2CppClass* SehNextNested(fn_class_get_nested_types fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

// Fill (name, ns) for a class. For nested types, walks the declaring chain
// and emits `Outer/Inner` (or deeper) so the lookup path can round-trip via
// FindClass + il2cpp_class_get_nested_types. Namespace is taken from the
// outermost type — IL2CPP/Mono only attach a namespace to top-level classes.
bool DescribeClass(Il2CppClass* klass, ClassEntry& out) {
    if (klass == nullptr) return false;
    const auto& r = GetResolver();
    out.klass = klass;
    if (r.class_get_name == nullptr) return true;

    std::string leaf = SafeStr(r.class_get_name(klass));

    // Walk declaring chain (depth cap 16 — same as walkInstance).
    Il2CppClass* cur = klass;
    Il2CppClass* outer = klass;
    if (r.class_get_declaring != nullptr) {
        for (int i = 0; i < 16; ++i) {
            Il2CppClass* d = SehDeclaring(r.class_get_declaring, cur);
            if (d == nullptr) break;
            std::string n = (r.class_get_name != nullptr) ? SafeStr(r.class_get_name(d)) : std::string{};
            if (!n.empty()) leaf = n + "/" + leaf;
            outer = d;
            cur = d;
        }
    }
    out.name = std::move(leaf);
    if (r.class_get_namespace != nullptr) out.ns = SafeStr(r.class_get_namespace(outer));
    return true;
}

// Search the nested-types of `outer` for one whose simple name matches.
Il2CppClass* FindNestedByName(Il2CppClass* outer, const std::string& nestedName) {
    const auto& r = GetResolver();
    if (outer == nullptr || r.class_get_nested_types == nullptr || r.class_get_name == nullptr) {
        return nullptr;
    }
    void* iter = nullptr;
    for (;;) {
        Il2CppClass* k = SehNextNested(r.class_get_nested_types, outer, &iter);
        if (k == nullptr) return nullptr;
        const char* n = r.class_get_name(k);
        if (n != nullptr && nestedName == n) return k;
    }
}

#pragma warning(push)
#pragma warning(disable: 4509)
std::size_t SehClassCount(fn_image_get_class_count fn, const Il2CppImage* img) {
    __try { return fn(img); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
Il2CppClass* SehClassByIdx(fn_image_get_class fn, const Il2CppImage* img, std::size_t i) {
    __try { return fn(img, i); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
Il2CppClass* SehClassFromName(fn_class_from_name fn, const Il2CppImage* img,
                              const char* ns, const char* nm) {
    __try { return fn(img, ns, nm); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
Il2CppClass* SehParent(fn_class_get_parent fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
FieldInfo* SehNextField(fn_class_get_fields fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const MethodInfo* SehNextMethod(fn_class_get_methods fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehStaticData(fn_class_get_static_data fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehMethodPtr(fn_method_get_pointer fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// MethodInfo's first qword (`methodPointer` field) is the AOT native entry
// point. Reading it directly is the only path on Unity 2021.3+ where
// il2cpp_method_get_pointer is no longer exported.
void* SehMethodPtrFromStruct(const MethodInfo* m) {
    __try { return *reinterpret_cast<void* const*>(m); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassName(fn_class_get_name fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
Il2CppClass* SehClassFromIl2CppType(fn_class_from_il2cpp_type fn, const Il2CppType* t) {
    __try { return fn(t); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
int SehClassIsValueType(fn_class_is_valuetype fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
std::uint32_t SehParamCount(fn_method_get_param_count fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
const Il2CppType* SehParamType(fn_method_get_param fn, const MethodInfo* m, std::uint32_t i) {
    __try { return fn(m, i); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehMethodName(fn_method_get_name fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
std::uint32_t SehMethodToken(fn_method_get_token fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
std::uint32_t SehMethodFlags(fn_method_get_flags fn, const MethodInfo* m, std::uint32_t* iflags) {
    __try { return fn(m, iflags); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
Il2CppClass* SehObjectGetClass(fn_object_get_class fn, Il2CppObject* o) {
    __try { return fn(o); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

void* ReadMethodPointer(const MethodInfo* m) {
    if (m == nullptr) return nullptr;
    const auto& r = GetResolver();
    // Prefer the runtime-exported helper when present; on older Unity it's
    // always there and gives the canonical pointer.
    if (r.method_get_pointer != nullptr) {
        if (void* p = SehMethodPtr(r.method_get_pointer, m)) return p;
    }
    // Fallback: read MethodInfo->methodPointer directly. Layout has been
    // stable across the Unity 2018-2023 IL2CPP versions we target.
    return SehMethodPtrFromStruct(m);
}

}  // namespace

bool ProbeKlassValid(Il2CppClass* klass) {
    if (klass == nullptr) return false;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_name == nullptr) return false;
    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return false;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return false;
    const char* nm = SehClassName(r.class_get_name, klass);
    return nm != nullptr && *nm != '\0';
}

std::vector<ClassEntry> EnumerateClasses(const Il2CppImage* image) {
    std::vector<ClassEntry> out;
    if (image == nullptr) return out;

    const auto& r = GetResolver();
    if (!r.resolved) return out;

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return out;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    // Fast path — Unity 2019+ exports il2cpp_image_get_class_count /
    // il2cpp_image_get_class. Iterate them directly for the canonical class
    // list (top-level + nested, in metadata-token order).
    if (r.image_get_class_count != nullptr && r.image_get_class != nullptr) {
        std::size_t n = SehClassCount(r.image_get_class_count, image);
        if (n == 0) return out;
        out.reserve(n);
        for (std::size_t i = 0; i < n; ++i) {
            Il2CppClass* k = SehClassByIdx(r.image_get_class, image, i);
            ClassEntry e;
            if (DescribeClass(k, e)) out.push_back(std::move(e));
        }
        return out;
    }

    // Fallback path — Unity 2018.x IL2CPP runtime didn't export the two
    // image-iter APIs above. Walk the global TypeDefinitions table from
    // global-metadata.dat, attempt il2cpp_class_from_name for each
    // (namespace, name) against the requested image. class_from_name
    // returns null for types belonging to a different image, so we
    // implicitly filter without needing class_get_image. Slower
    // (O(N_global_typedefs) regardless of image size) but functional.
    //
    // Caveats:
    //   * Nested types are stored in metadata with their unqualified name
    //     and an empty namespace; class_from_name with that key won't find
    //     them. They'll appear via their declaring outer class instead, so
    //     the consumer still reaches them via class_get_nested_types.
    //   * Generic type-defs whose runtime forms haven't been instantiated
    //     yet may also miss; that's a fundamental limitation of
    //     class_from_name and not specific to this fallback.
    if (r.class_from_name == nullptr) return out;

    const auto& mp = Metadata::Parser::Get();
    if (!mp.loaded()) {
        // No metadata.dat = no fallback enumeration available. Caller will
        // see an empty list; class lookup via FindClass still works for
        // explicitly-named types since it goes straight through
        // il2cpp_class_from_name.
        Discrete::Log::Write(MagicStrings::LogCategoryInit,
            "IL2CPPClassWalker: image_get_class_count missing AND "
            "global-metadata.dat unloaded — cannot enumerate classes");
        return out;
    }

    const std::uint32_t typeCount = mp.TypeDefinitionCount();
    out.reserve(typeCount / 16);  // most types belong to OTHER images
    for (std::uint32_t i = 0; i < typeCount; ++i) {
        auto td = mp.GetTypeDefinition(static_cast<std::int32_t>(i));
        if (!td || td->name.empty()) continue;
        Il2CppClass* k = SehClassFromName(
            r.class_from_name, image, td->ns.c_str(), td->name.c_str());
        if (k == nullptr) continue;  // belongs to a different image
        ClassEntry e;
        if (DescribeClass(k, e)) out.push_back(std::move(e));
    }
    return out;
}

Il2CppClass* FindClass(const Il2CppImage* image,
                       const std::string& ns, const std::string& name) {
    if (image == nullptr || name.empty()) return nullptr;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_from_name == nullptr) return nullptr;

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return nullptr;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return nullptr;

    // Fast path: simple top-level type.
    std::size_t slash = name.find('/');
    if (slash == std::string::npos) {
        return SehClassFromName(r.class_from_name, image, ns.c_str(), name.c_str());
    }

    // Nested form: walk Outer/Mid/Inner via class_get_nested_types.
    std::string outerName = name.substr(0, slash);
    Il2CppClass* cur = SehClassFromName(r.class_from_name, image, ns.c_str(), outerName.c_str());
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

ClassInfoResult ReadClassInfo(Il2CppClass* klass) {
    ClassInfoResult result;
    if (klass == nullptr) return result;

    const auto& r = GetResolver();
    if (!r.resolved) return result;

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return result;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return result;

    DescribeClass(klass, result.info.self);
    if (r.class_get_parent != nullptr) {
        if (Il2CppClass* p = SehParent(r.class_get_parent, klass)) {
            DescribeClass(p, result.info.parent);
        }
    }

    if (r.class_get_fields != nullptr) {
        void* iter = nullptr;
        for (;;) {
            FieldInfo* f = SehNextField(r.class_get_fields, klass, &iter);
            if (f == nullptr) break;
            FieldEntry fe;
            if (r.field_get_name != nullptr)   fe.name   = SafeStr(r.field_get_name(f));
            if (r.field_get_offset != nullptr) fe.offset = r.field_get_offset(f);
            if (r.field_get_flags != nullptr)  fe.flags  = static_cast<std::uint32_t>(r.field_get_flags(f));
            const Il2CppType* ft = (r.field_get_type != nullptr) ? r.field_get_type(f) : nullptr;
            if (ft != nullptr) fe.typeName = TypeName(ft);
            // Field-class introspection — IL2CPP doesn't export `class_is_enum`,
            // so we detect enums by checking the field class's parent (every
            // enum derives from System.Enum). Skipped when the type→class
            // bridge isn't bound (older runtimes).
            if (ft != nullptr && r.class_from_il2cpp_type != nullptr) {
                Il2CppClass* fc = SehClassFromIl2CppType(r.class_from_il2cpp_type, ft);
                if (fc != nullptr) {
                    if (r.class_is_valuetype != nullptr) {
                        fe.isValueType = SehClassIsValueType(r.class_is_valuetype, fc) != 0;
                    }
                    if (r.class_get_parent != nullptr && r.class_get_name != nullptr) {
                        Il2CppClass* parent = SehParent(r.class_get_parent, fc);
                        if (parent != nullptr) {
                            const char* pn = SehClassName(r.class_get_name, parent);
                            if (pn != nullptr && std::strcmp(pn, "Enum") == 0) {
                                fe.isEnum = true;
                                // Underlying type lives in the enum's `value__`
                                // instance field — same trick getEnumValues uses.
                                if (r.class_get_fields != nullptr) {
                                    void* fit = nullptr;
                                    for (;;) {
                                        FieldInfo* uf = SehNextField(r.class_get_fields, fc, &fit);
                                        if (uf == nullptr) break;
                                        const char* un = (r.field_get_name != nullptr)
                                            ? r.field_get_name(uf) : nullptr;
                                        if (un != nullptr && std::strcmp(un, "value__") == 0) {
                                            const Il2CppType* ut = (r.field_get_type != nullptr)
                                                ? r.field_get_type(uf) : nullptr;
                                            if (ut != nullptr) fe.underlyingTypeName = TypeName(ut);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            result.info.fields.push_back(std::move(fe));
        }
    }

    if (r.class_get_methods != nullptr) {
        void* iter = nullptr;
        for (;;) {
            const MethodInfo* m = SehNextMethod(r.class_get_methods, klass, &iter);
            if (m == nullptr) break;
            MethodEntry me;
            if (r.method_get_name != nullptr)    me.name = SafeStr(r.method_get_name(m));
            if (r.method_get_token != nullptr)   me.token = r.method_get_token(m);
            if (r.method_get_flags != nullptr) {
                std::uint32_t iflags = 0;
                me.flags = r.method_get_flags(m, &iflags);
            }
            me.pointer = ReadMethodPointer(m);  // works on Unity 2021.3+ via direct struct read
            result.info.methods.push_back(std::move(me));
        }
    }

    result.ok = true;
    return result;
}

Il2CppClass* ClassOfObject(std::uintptr_t objPtr) {
    if (objPtr == 0) return nullptr;
    const auto& r = GetResolver();
    if (!r.resolved || r.object_get_class == nullptr) return nullptr;
    return SehObjectGetClass(r.object_get_class, reinterpret_cast<Il2CppObject*>(objPtr));
}

StaticFieldAddress ResolveStaticFieldAddress(Il2CppClass* klass, const std::string& fieldName) {
    StaticFieldAddress out;
    if (klass == nullptr || fieldName.empty()) return out;

    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_static_data == nullptr || r.class_get_fields == nullptr) {
        return out;
    }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return out;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    FieldInfo* match = nullptr;
    void* iter = nullptr;
    for (;;) {
        FieldInfo* f = SehNextField(r.class_get_fields, klass, &iter);
        if (f == nullptr) break;
        const char* n = (r.field_get_name != nullptr) ? r.field_get_name(f) : nullptr;
        if (n != nullptr && fieldName == n) { match = f; break; }
    }
    if (match == nullptr) return out;

    std::uint32_t flags = (r.field_get_flags != nullptr)
                          ? static_cast<std::uint32_t>(r.field_get_flags(match)) : 0u;
    if ((flags & 0x10u) == 0) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "IL2CPP::ResolveStaticFieldAddress: field is not static");
        return out;
    }
    std::size_t offset = (r.field_get_offset != nullptr) ? r.field_get_offset(match) : 0u;
    std::string typeName = (r.field_get_type != nullptr) ? TypeName(r.field_get_type(match)) : std::string{};

    void* base = SehStaticData(r.class_get_static_data, klass);
    if (base == nullptr) {
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "IL2CPP::ResolveStaticFieldAddress: class_get_static_field_data returned null");
        return out;
    }

    out.ok       = true;
    out.address  = static_cast<std::uint8_t*>(base) + offset;
    out.offset   = offset;
    out.flags    = flags;
    out.typeName = std::move(typeName);
    return out;
}

// Phase 29a-v3 — batch static-base resolver (mirror of Mono's). For
// IL2CPP this is just one il2cpp_class_get_static_data call per class
// — no vtable indirection. Returns nullptr when the export isn't
// bound or the runtime returns null (some classes legitimately have
// no static storage). Caller computes per-static-field addresses as
// base + f.offset.
void* ResolveStaticBaseAddress(Il2CppClass* klass) {
    if (klass == nullptr) return nullptr;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_static_data == nullptr) return nullptr;
    return SehStaticData(r.class_get_static_data, klass);
}

MethodPointer GetMethodPointerByName(Il2CppClass* klass, const std::string& methodName) {
    MethodPointer out;
    if (klass == nullptr || methodName.empty()) return out;

    const auto& r = GetResolver();
    // method_get_pointer is no longer required — we fall back to reading
    // MethodInfo->methodPointer directly (Unity 2021.3+ doesn't export it).
    if (!r.resolved || r.class_get_methods == nullptr) {
        return out;
    }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return out;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    const MethodInfo* match = nullptr;
    void* iter = nullptr;
    for (;;) {
        const MethodInfo* m = SehNextMethod(r.class_get_methods, klass, &iter);
        if (m == nullptr) break;
        const char* n = (r.method_get_name != nullptr) ? r.method_get_name(m) : nullptr;
        if (n != nullptr && methodName == n) { match = m; break; }
    }
    if (match == nullptr) return out;

    out.name  = methodName;
    out.token = (r.method_get_token != nullptr) ? r.method_get_token(match) : 0u;
    out.address = ReadMethodPointer(match);
    out.ok = (out.address != nullptr);
    return out;
}

std::vector<MethodSignatureEntry> EnumerateMethodSignatures(Il2CppClass* klass) {
    std::vector<MethodSignatureEntry> out;
    if (klass == nullptr) return out;

    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_methods == nullptr) return out;

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return out;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return out;

    void* iter = nullptr;
    for (;;) {
        const MethodInfo* m = SehNextMethod(r.class_get_methods, klass, &iter);
        if (m == nullptr) break;

        MethodSignatureEntry me;
        if (r.method_get_name != nullptr) {
            const char* n = SehMethodName(r.method_get_name, m);
            if (n != nullptr) me.name = n;
        }
        if (r.method_get_token != nullptr) {
            me.token = SehMethodToken(r.method_get_token, m);
        }
        if (r.method_get_flags != nullptr) {
            std::uint32_t iflags = 0;
            me.flags = SehMethodFlags(r.method_get_flags, m, &iflags);
        }
        me.pointer = ReadMethodPointer(m);

        // Pull paramTypes when the resolver has the param accessors bound.
        // Missing on Unity 2018 builds where the export wasn't present —
        // callers degrade to humanSuffix = "noargs" (correct only for
        // 0-arg methods; ambiguous for overloads).
        std::uint32_t paramCount = 0;
        if (r.method_get_param_count != nullptr) {
            paramCount = SehParamCount(r.method_get_param_count, m);
        }
        if (paramCount > 0 && r.method_get_param != nullptr) {
            me.paramTypes.reserve(paramCount);
            for (std::uint32_t i = 0; i < paramCount; ++i) {
                const Il2CppType* pt = SehParamType(r.method_get_param, m, i);
                me.paramTypes.push_back(pt ? TypeName(pt) : std::string{});
            }
        }
        me.humanSuffix = Shared::MakeHumanSuffix(me.paramTypes);

        out.push_back(std::move(me));
    }
    return out;
}

}  // namespace Discrete::IL2CPP
