// MonoAddressFinder.cpp — Phase 17a implementation.

#include "MonoAddressFinder.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ManagedLayout.h"
#include "../shared/Memory.h"
#include "../shared/ScanCancel.h"
#include "MonoClassWalker.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "MonoTypeParser.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace Discrete::Mono {

namespace {

// Phase 27 follow-up — the cache containers (vector + unordered_map)
// previously sat as direct file-statics. At process termination their
// destructors iterated 10-17K entries to free each one — observed live
// to contribute to a >30s game-shutdown stall on 活俠傳 after a value
// scan (each whatIsAt call grows the vtable memo by hundreds of entries;
// the class set is 10-17K entries baseline). Wrapping them in a
// heap-allocated leaked Cache lets Windows reclaim the whole heap in
// one OS sweep at process exit instead of iterating destructors.
//
// POD scalars stay direct — their "destructor" is a no-op.
struct Cache {
    std::mutex                                          mu;
    std::vector<std::uintptr_t>                         vtableSet;     // sorted
    std::unordered_map<std::uintptr_t, std::uintptr_t>  vtableToKlass; // vtable → klass
    std::vector<std::uintptr_t>                         classSet;      // sorted
};
Cache& cache() {
    static Cache* p = new Cache();  // intentional leak
    return *p;
}
// Bound-scalar mirrors of the cache state stay in static storage —
// trivially-destructible POD, no shutdown cost.
std::uintptr_t                                   g_minKlass  = 0;
std::uintptr_t                                   g_maxKlass  = 0;
std::uintptr_t                                   g_minVtable = 0;
std::uintptr_t                                   g_maxVtable = 0;
bool                                             g_built     = false;

#pragma warning(push)
#pragma warning(disable: 4509)
const char* SehClassNameProbe(fn_class_get_name fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassNamespaceProbe(fn_class_get_namespace fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoImage* SehClassImageProbe(fn_class_get_image fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehImageNameProbe(fn_image_get_name fn, MonoImage* img) {
    __try { return fn(img); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoVTable* SehGetVtable(fn_class_vtable fn, MonoDomain* d, MonoClass* k) {
    __try { return fn(d, k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// Phase 17b — SEH-protected field-walk primitives.
MonoClass* SehParentLookup(fn_class_get_parent fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClassField* SehNextFieldProbe(fn_class_get_fields fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehFieldNameProbe(fn_field_get_name fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
int SehFieldOffsetProbe(fn_field_get_offset fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
std::uint32_t SehFieldFlagsProbe(fn_field_get_flags fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
MonoType* SehFieldTypeProbe(fn_field_get_type fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClass* SehClassFromMonoType(fn_class_from_mono_type fn, MonoType* t) {
    __try { return fn(t); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
int SehIsValueTypeProbe(fn_class_is_valuetype fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
int SehIsEnumProbe(fn_class_is_enum fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
MonoType* SehEnumBasetypeProbe(fn_class_enum_basetype fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
int SehValueSizeProbe(fn_class_value_size fn, MonoClass* k) {
    __try { std::uint32_t a = 0; return fn(k, &a); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
// Phase 27c — array element class lookup. Non-null result means klass is
// an array type (System.Object[], int[], etc).
MonoClass* SehElementClassProbe(fn_class_get_element_class fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// Phase 27d follow-up — walk nesting chain for nested-class name resolution.
MonoClass* SehNestingProbe(fn_class_get_nesting_type fn, MonoClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

bool ContainsKlass(std::uintptr_t p) {
    if (cache().classSet.empty()) return false;
    if (p < g_minKlass || p > g_maxKlass) return false;
    return std::binary_search(cache().classSet.begin(), cache().classSet.end(), p);
}

bool ContainsVtable(std::uintptr_t p) {
    if (cache().vtableSet.empty()) return false;
    if (p < g_minVtable || p > g_maxVtable) return false;
    return std::binary_search(cache().vtableSet.begin(), cache().vtableSet.end(), p);
}

std::string StripDllSuffix(const std::string& s) {
    if (s.size() >= 4) {
        const std::string tail = s.substr(s.size() - 4);
        if (tail == ".dll" || tail == ".DLL") return s.substr(0, s.size() - 4);
    }
    return s;
}

// Phase 17b — see il2cpp/IL2CPPAddressFinder.cpp for the design rationale
// (the two implementations are kept symmetric except where the Mono and
// IL2CPP runtime APIs diverge).
struct ResolvedField {
    std::string   name;
    std::string   typeName;
    std::size_t   offset      = 0;
    std::size_t   size        = 0;
    std::uint32_t flags       = 0;
    bool          isValueType = false;
    bool          isEnum      = false;
    std::string   underlyingTypeName;
    std::string   declaredIn;
    // Phase 31-5 — for an inline value-type STRUCT field (isValueType &&
    // !isEnum, not a primitive), the field's struct class, so a hit landing
    // INSIDE the struct can be named to the specific component (`_position.y`).
    MonoClass*    valueTypeClass = nullptr;
};

std::size_t SizeOfPrimitive(const std::string& t) {
    if (t == "System.Boolean" || t == "System.Byte" || t == "System.SByte") return 1;
    if (t == "System.Int16"   || t == "System.UInt16" || t == "System.Char") return 2;
    if (t == "System.Int32"   || t == "System.UInt32" || t == "System.Single") return 4;
    if (t == "System.Int64"   || t == "System.UInt64" || t == "System.Double") return 8;
    if (t == "System.IntPtr"  || t == "System.UIntPtr") return 8;
    return 0;
}

bool TypeNameIsPrimitive(const std::string& t) {
    return SizeOfPrimitive(t) != 0;
}

// Caller MUST hold a ThreadAttachGuard. mono_field_get_offset returns
// offsets relative to MonoObject's start (header included), so they
// map directly against `addr - instance_base`.
std::vector<ResolvedField> EnumerateInstanceFields(MonoClass* klass) {
    std::vector<ResolvedField> out;
    if (klass == nullptr) return out;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_fields == nullptr) return out;

    std::vector<MonoClass*> chain;
    for (MonoClass* cur = klass; cur != nullptr && chain.size() < 16; ) {
        chain.push_back(cur);
        if (r.class_get_parent == nullptr) break;
        cur = SehParentLookup(r.class_get_parent, cur);
    }

    for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
        MonoClass* cur = *it;
        std::string declaredIn;
        if (r.class_get_name != nullptr) {
            const char* n = SehClassNameProbe(r.class_get_name, cur);
            if (n != nullptr) declaredIn = n;
        }

        void* iter = nullptr;
        for (;;) {
            MonoClassField* f = SehNextFieldProbe(r.class_get_fields, cur, &iter);
            if (f == nullptr) break;

            ResolvedField rf;
            rf.declaredIn = declaredIn;
            if (r.field_get_name != nullptr) {
                const char* n = SehFieldNameProbe(r.field_get_name, f);
                if (n != nullptr) rf.name = n;
            }
            if (r.field_get_offset != nullptr) {
                rf.offset = static_cast<std::size_t>(
                    SehFieldOffsetProbe(r.field_get_offset, f));
            }
            if (r.field_get_flags != nullptr) {
                rf.flags = SehFieldFlagsProbe(r.field_get_flags, f);
            }
            constexpr std::uint32_t kStatic = 0x10u;
            if ((rf.flags & kStatic) != 0u) continue;

            MonoType* ft = (r.field_get_type != nullptr)
                ? SehFieldTypeProbe(r.field_get_type, f) : nullptr;
            if (ft != nullptr) rf.typeName = TypeName(ft);

            std::size_t sz = 0;
            if (ft != nullptr && r.class_from_mono_type != nullptr) {
                MonoClass* fc = SehClassFromMonoType(r.class_from_mono_type, ft);
                if (fc != nullptr) {
                    if (r.class_is_valuetype != nullptr) {
                        rf.isValueType =
                            SehIsValueTypeProbe(r.class_is_valuetype, fc) != 0;
                    }
                    if (r.class_is_enum != nullptr &&
                        SehIsEnumProbe(r.class_is_enum, fc) != 0) {
                        rf.isEnum = true;
                        if (r.class_enum_basetype != nullptr) {
                            MonoType* ut = SehEnumBasetypeProbe(r.class_enum_basetype, fc);
                            if (ut != nullptr) rf.underlyingTypeName = TypeName(ut);
                        }
                    }
                    if (rf.isValueType && r.class_value_size != nullptr) {
                        int s = SehValueSizeProbe(r.class_value_size, fc);
                        if (s > 0) sz = static_cast<std::size_t>(s);
                    }
                    // Phase 31-5 — remember the struct class for value-type
                    // (non-enum, NON-primitive) fields so an interior hit can be
                    // named to its component. Primitives MUST stay null: their
                    // single internal field (e.g. System.Single.m_value) is of
                    // their own type, so storing the class would make
                    // NameStructComponent recurse into a self-referential field
                    // with an unchanged interior offset → infinite recursion.
                    if (rf.isValueType && !rf.isEnum && !TypeNameIsPrimitive(rf.typeName))
                        rf.valueTypeClass = fc;
                }
            }
            if (sz == 0) sz = SizeOfPrimitive(rf.typeName);
            if (sz == 0) sz = 8;
            rf.size = sz;
            out.push_back(std::move(rf));
        }
    }

    std::sort(out.begin(), out.end(),
              [](const ResolvedField& a, const ResolvedField& b) {
                  return a.offset < b.offset;
              });
    return out;
}

std::string ClassifyKind(const ResolvedField& rf, std::ptrdiff_t fieldOffset) {
    bool exact = (fieldOffset == static_cast<std::ptrdiff_t>(rf.offset));
    if (!exact) return "interior";
    if (rf.isEnum) return "leaf";
    if (rf.isValueType) {
        return TypeNameIsPrimitive(rf.typeName) ? "leaf" : "value-struct";
    }
    return "pointer";
}

// Phase 31-5 — when the queried offset lands INSIDE an inline value-type struct
// field (e.g. a Vector3 `_position` at +0x14C, hit at +0x150 = the `.y` float),
// name the specific component (`_position.y`) instead of reporting a generic
// "interior" hit on the whole struct. The struct's own member offsets are
// header-inclusive (boxed convention — Vector3.x = 0x10), so the struct-relative
// offset is `member.offset - kObjectHeader`, which matches the queried interior
// offset. Rewrites `fh` in place (name → parent.member, type/offset/size → the
// member). Recurses for nested struct members (Bounds.center.y), depth-capped
// as a belt-and-suspenders guard against any repeating-class / zero-progress
// chain. Caller holds the ThreadAttachGuard (EnumerateInstanceFields needs it).
void NameStructComponent(const ResolvedField& parent, std::ptrdiff_t fieldOffset,
                         FieldHit& fh, int depthLeft = 4) {
    if (depthLeft <= 0) return;
    if (!parent.isValueType || parent.isEnum || parent.valueTypeClass == nullptr) return;
    const std::ptrdiff_t interior = fieldOffset - static_cast<std::ptrdiff_t>(parent.offset);
    if (interior <= 0) return;   // hit at the struct base → keep value-struct kind
    const long hdr = static_cast<long>(ManagedLayout::kObjectHeader);
    auto members = EnumerateInstanceFields(parent.valueTypeClass);
    for (const auto& m : members) {
        const std::ptrdiff_t rel = static_cast<std::ptrdiff_t>(m.offset) - hdr;
        if (rel < 0) continue;
        if (interior < rel || interior >= rel + static_cast<std::ptrdiff_t>(m.size)) continue;
        const std::ptrdiff_t absOffset = static_cast<std::ptrdiff_t>(parent.offset) + rel;
        fh.name        = parent.name + "." + m.name;
        fh.typeName    = m.typeName;
        fh.offset      = static_cast<std::size_t>(absOffset);
        fh.size        = m.size;
        fh.isValueType = m.isValueType;
        fh.isEnum      = m.isEnum;
        fh.underlyingTypeName = m.underlyingTypeName;
        const std::ptrdiff_t memberInterior = interior - rel;
        fh.kind = (memberInterior == 0)
            ? ClassifyKind(m, static_cast<std::ptrdiff_t>(m.offset))  // member-exact
            : "interior";
        fh.interiorOffset = memberInterior;
        // Nested struct member (e.g. Bounds.center.y) → recurse one more level.
        // valueTypeClass is null for primitives (set above), so a primitive
        // member never recurses; the depth cap bounds any other chain.
        if (memberInterior > 0 && m.isValueType && !m.isEnum &&
            m.valueTypeClass != nullptr) {
            ResolvedField nested = m;
            nested.name   = fh.name;                       // already parent.member
            nested.offset = static_cast<std::size_t>(absOffset);
            NameStructComponent(nested, fieldOffset, fh, depthLeft - 1);
        }
        return;
    }
}

FieldHit MakeFieldHit(const ResolvedField& rf, std::ptrdiff_t fieldOffset, bool exact) {
    FieldHit fh;
    fh.present     = true;
    fh.name        = rf.name;
    fh.typeName    = rf.typeName;
    fh.offset      = rf.offset;
    fh.size        = rf.size;
    fh.flags       = rf.flags;
    fh.isStatic    = false;
    fh.isValueType = rf.isValueType;
    fh.isEnum      = rf.isEnum;
    fh.underlyingTypeName = rf.underlyingTypeName;
    fh.declaredIn  = rf.declaredIn;
    fh.kind        = exact ? ClassifyKind(rf, fieldOffset) : "interior";
    fh.interiorOffset = (fh.kind == "interior")
        ? (fieldOffset - static_cast<std::ptrdiff_t>(rf.offset))
        : 0;
    // Phase 31-5 — promote an interior hit on a value-type struct to the
    // specific component (`_position.y`). No-op for primitives / pointers /
    // base-of-struct hits.
    if (exact) NameStructComponent(rf, fieldOffset, fh);
    return fh;
}

void ResolveFieldAt(MonoClass* klass, std::ptrdiff_t fieldOffset,
                    FieldHit& outField, FieldHit& outNearest,
                    std::ptrdiff_t& outNearestDelta) {
    auto fields = EnumerateInstanceFields(klass);
    if (fields.empty()) return;

    const ResolvedField* exact = nullptr;
    const ResolvedField* nearest = nullptr;
    for (const auto& rf : fields) {
        auto begin = static_cast<std::ptrdiff_t>(rf.offset);
        auto end   = begin + static_cast<std::ptrdiff_t>(rf.size);
        if (fieldOffset >= begin && fieldOffset < end) {
            if (exact == nullptr || rf.size < exact->size) exact = &rf;
        }
        if (begin <= fieldOffset) {
            if (nearest == nullptr ||
                static_cast<std::ptrdiff_t>(nearest->offset) < begin) {
                nearest = &rf;
            }
        }
    }

    if (exact != nullptr) {
        outField = MakeFieldHit(*exact, fieldOffset, /*exact=*/true);
    } else if (nearest != nullptr) {
        outNearest      = MakeFieldHit(*nearest, fieldOffset, /*exact=*/false);
        outNearestDelta = fieldOffset - static_cast<std::ptrdiff_t>(nearest->offset);
    }
}

}  // namespace

// Phase 30 P2 (Mono port) — shared tail of WhatIsAt (steps 5-8: declaring/
// nesting chain + namespace/image + array-element / declared-field resolution),
// extracted so the backward-scan path and the interval-tree path produce
// byte-identical answers. PRECONDITION: caller holds a ThreadAttachGuard.
void ResolveInstanceLookup(std::uintptr_t addr, std::uintptr_t instance,
                           std::uintptr_t vtablePtr, std::uintptr_t klassPtr,
                           AddressLookup& out) {
    const auto& r = GetResolver();
    MonoClass* k = reinterpret_cast<MonoClass*>(klassPtr);
    std::uintptr_t p = instance;

    out.found       = true;
    out.instance    = p;
    out.vtablePtr   = vtablePtr;
    out.klassPtr    = klassPtr;
    out.fieldOffset = static_cast<std::ptrdiff_t>(addr - p);

    // Phase 27d follow-up — walk nesting chain so nested classes round-trip
    // via walkInstance / FindClass. Mono uses `mono_class_get_nesting_type`
    // (counterpart of IL2CPP's class_get_declaring).
    const char* name = (r.class_get_name != nullptr)
        ? SehClassNameProbe(r.class_get_name, k) : nullptr;
    std::string leaf = (name != nullptr) ? name : "";
    MonoClass* outer = k;
    if (r.class_get_nesting_type != nullptr) {
        MonoClass* cur = k;
        for (int depth = 0; depth < 16; ++depth) {
            MonoClass* d = SehNestingProbe(r.class_get_nesting_type, cur);
            if (d == nullptr) break;
            const char* dn = SehClassNameProbe(r.class_get_name, d);
            if (dn != nullptr && *dn != '\0') {
                leaf = std::string(dn) + "/" + leaf;
            }
            outer = d;
            cur = d;
        }
    }
    out.className = std::move(leaf);
    if (r.class_get_namespace != nullptr) {
        const char* nsStr = SehClassNamespaceProbe(r.class_get_namespace, outer);
        if (nsStr != nullptr) out.ns = nsStr;
    }
    if (r.class_get_image != nullptr && r.image_get_name != nullptr) {
        MonoImage* img = SehClassImageProbe(r.class_get_image, k);
        if (img != nullptr) {
            const char* imgName = SehImageNameProbe(r.image_get_name, img);
            if (imgName != nullptr) out.assembly = StripDllSuffix(imgName);
        }
    }

    // Phase 27c — array element resolution. If klass is an array type (T[] /
    // List<T>'s _items), compute element index from the offset instead of
    // falling through to per-field resolution.
    bool resolvedAsArray = false;
    if (r.class_get_element_class != nullptr) {
        MonoClass* elemClass = SehElementClassProbe(r.class_get_element_class, k);
        if (elemClass != nullptr) {
            std::ptrdiff_t dataOff = static_cast<std::ptrdiff_t>(ManagedLayout::kArrayDataOffset);
            if (out.fieldOffset >= dataOff) {
                std::uintptr_t arrayLength = 0;
                Memory::SafeReadBytes(
                    reinterpret_cast<void*>(p + ManagedLayout::kArrayMaxLengthOffset),
                    &arrayLength, sizeof(arrayLength));

                std::size_t elemSize = ManagedLayout::kPointerSize;
                bool elemIsValueType = false;
                if (r.class_is_valuetype != nullptr) {
                    elemIsValueType = SehIsValueTypeProbe(r.class_is_valuetype, elemClass) != 0;
                }
                if (elemIsValueType && r.class_value_size != nullptr) {
                    int sz = SehValueSizeProbe(r.class_value_size, elemClass);
                    if (sz > 0) elemSize = static_cast<std::size_t>(sz);
                }

                std::ptrdiff_t into       = out.fieldOffset - dataOff;
                std::size_t    elemIdx    = static_cast<std::size_t>(into) / elemSize;
                std::size_t    interior   = static_cast<std::size_t>(into) % elemSize;

                out.field.present    = true;
                out.field.name       = "[" + std::to_string(elemIdx) + "]";
                out.field.offset     = dataOff + elemIdx * elemSize;
                out.field.size       = elemSize;
                out.field.kind       = (interior == 0) ? "array-element" : "array-interior";
                out.field.isValueType = elemIsValueType;
                out.field.interiorOffset = static_cast<std::ptrdiff_t>(interior);
                if (r.class_get_name != nullptr) {
                    const char* enm = SehClassNameProbe(r.class_get_name, elemClass);
                    if (enm != nullptr) out.field.typeName = enm;
                }
                char meta[64];
                std::snprintf(meta, sizeof(meta), "len=%zu idx=%zu",
                              static_cast<std::size_t>(arrayLength), elemIdx);
                out.field.declaredIn = meta;
                resolvedAsArray = true;
            }
        }
    }

    // Phase 17b — resolve which declared field contains the queried offset.
    if (!resolvedAsArray) {
        ResolveFieldAt(k, out.fieldOffset, out.field, out.nearestField,
                       out.nearestFieldDelta);
    }
}

bool BuildClassRangeCache() {
    std::lock_guard<std::mutex> lk(cache().mu);
    if (g_built) return !cache().classSet.empty();

    if (!IsResolved()) {
        Log::Write(MagicStrings::LogCategoryWalk,
                   "AddrFinder: BuildClassRangeCache: mono not resolved");
        return false;
    }

    cache().classSet.clear();
    cache().vtableSet.clear();
    cache().vtableToKlass.clear();
    g_minKlass  = static_cast<std::uintptr_t>(-1);
    g_maxKlass  = 0;
    g_minVtable = static_cast<std::uintptr_t>(-1);
    g_maxVtable = 0;

    // Phase 27a follow-up (2026-05-20): the old design pre-computed every
    // class's MonoVTable* via `mono_class_vtable` in a 17K-call batch
    // under a single ThreadAttachGuard. On legacy 32-bit Mono (活俠傳 /
    // Unity 2020.3.49) with the game's main thread focus-paused, that
    // mass forced-vtable-init wedged the BDWGC heap badly enough that
    // the game stayed hung after focus returned — even though our worker
    // thread had long since disconnected. The 17K runtime calls touch
    // a global GC lock and trigger per-class vtable allocation; whatever
    // bookkeeping the runtime expects from the main thread to acknowledge
    // those allocations never gets to happen while focus is lost.
    //
    // The fix is to drop the eager vtable batch entirely. `WhatIsAt` now
    // resolves vtable→klass lazily: when it finds a candidate qword in
    // process memory (the supposed object header), it does a single SEH-
    // protected SafeReadBytes(*qword) to get vtable[0] = MonoClass*, and
    // checks THAT against `cache().classSet`. The vtable→klass map below is
    // populated lazily as WhatIsAt validates each new vtable it sees, so
    // repeat hits are still O(log N) without any runtime calls.
    //
    // Cost shifts: BuildClassRangeCache goes from ~5s + 17K runtime calls
    // to ~500ms + 0 runtime calls (just enumeration). WhatIsAt pays one
    // extra SafeReadBytes per backward step, which is cheap (no SEH on
    // committed RW pages, fast on PAGE_NOACCESS gaps). Net win for
    // single-shot lookups; cumulative cost of N scans pays off after the
    // first ~few scans because the lazy memo skips the deref for known
    // vtables.
    auto assemblies = EnumerateAssemblies();
    for (const auto& a : assemblies) {
        if (a.image == nullptr) continue;
        auto cs = EnumerateClasses(a.image);
        for (const auto& c : cs) {
            if (c.klass == nullptr) continue;
            auto kp = reinterpret_cast<std::uintptr_t>(c.klass);
            cache().classSet.push_back(kp);
            if (kp < g_minKlass) g_minKlass = kp;
            if (kp > g_maxKlass) g_maxKlass = kp;
        }
    }

    std::sort(cache().classSet.begin(), cache().classSet.end());
    cache().classSet.erase(std::unique(cache().classSet.begin(), cache().classSet.end()),
                     cache().classSet.end());

    if (cache().classSet.empty()) { g_minKlass = 0; g_maxKlass = 0; }
    g_built = true;

    Log::Write(MagicStrings::LogCategoryWalk,
               "AddrFinder: cached " + std::to_string(cache().classSet.size()) +
               " MonoClass* (root domain, lazy vtable)");
    return !cache().classSet.empty();
}

void InvalidateClassRangeCache() {
    std::lock_guard<std::mutex> lk(cache().mu);
    cache().classSet.clear();
    cache().vtableSet.clear();
    cache().vtableToKlass.clear();
    g_minKlass  = 0; g_maxKlass  = 0;
    g_minVtable = 0; g_maxVtable = 0;
    g_built     = false;
}

ClassRangeStats GetClassRangeStats() {
    std::lock_guard<std::mutex> lk(cache().mu);
    ClassRangeStats s;
    s.built       = g_built;
    s.classCount  = cache().classSet.size();
    s.vtableCount = cache().vtableSet.size();
    s.minKlass    = g_minKlass;
    s.maxKlass    = g_maxKlass;
    s.minVtable   = g_minVtable;
    s.maxVtable   = g_maxVtable;
    return s;
}

AddressLookup WhatIsAt(std::uintptr_t addr, std::size_t maxBackwardScan) {
    AddressLookup out;
    out.queriedAddr = addr;
    if (addr == 0) {
        out.reason = "address is null";
        return out;
    }
    if (!BuildClassRangeCache()) {
        out.reason = "class_range_cache_unavailable";
        return out;
    }

    if (maxBackwardScan == 0)        maxBackwardScan = 4096;
    if (maxBackwardScan > 16 * 1024) maxBackwardScan = 16 * 1024;

    constexpr std::size_t kStride = 8;
    std::uintptr_t start = addr & ~static_cast<std::uintptr_t>(kStride - 1);
    std::size_t maxSteps = maxBackwardScan / kStride;
    if (maxSteps == 0) maxSteps = 1;
    out.scannedFrom = (start >= maxSteps * kStride) ? (start - (maxSteps - 1) * kStride) : 0;
    out.scannedTo   = start + kStride;

    const auto& r = GetResolver();
    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) {
        out.reason = "no_mono_domain";
        return out;
    }
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        out.reason = "mono_thread_attach_failed";
        return out;
    }

    // Phase 27a follow-up: lazy vtable resolution. Snapshot the class set
    // and the (incrementally-populated) vtable→klass memo so the scan loop
    // doesn't hold cache().mu while doing SEH-protected probes. Any new
    // (vtable, klass) pair we discover gets written back at the end of
    // the call under the mutex so future scans skip the deref+lookup.
    std::vector<std::uintptr_t>                        classSetSnap;
    std::unordered_map<std::uintptr_t, std::uintptr_t> vtMemoSnap;
    std::uintptr_t minK = 0, maxK = 0;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        classSetSnap = cache().classSet;
        vtMemoSnap   = cache().vtableToKlass;
        minK = g_minKlass;
        maxK = g_maxKlass;
    }
    if (classSetSnap.empty()) {
        out.reason = "class_set_empty";
        return out;
    }

    auto klassInSet = [&](std::uintptr_t kp) {
        if (kp < minK || kp > maxK) return false;
        return std::binary_search(classSetSnap.begin(), classSetSnap.end(), kp);
    };

    // Track newly-discovered (vtable, klass) pairs to merge into the
    // global memo at the end of this WhatIsAt call.
    std::vector<std::pair<std::uintptr_t, std::uintptr_t>> newlyMemoized;

    // Phase 27 perf: bulk-read the entire backward range in one shot
    // instead of paying per-8-byte SafeReadBytes (each does a VirtualQuery
    // ~µs). For a 16 KB scan, that's 2048 → 4 syscalls. On 活俠傳 a 500-
    // result scan dropped from 16 s to ~4-5 s. Pages that AV are zero-
    // filled so the inner loop sees "no candidate" — same effective
    // behavior as the old per-step code.
    const std::size_t bulkBytes = maxSteps * kStride;
    const std::uintptr_t rangeStart = start >= (maxSteps - 1) * kStride
        ? start - (maxSteps - 1) * kStride
        : 0;
    std::vector<std::uint8_t> bulkBuf(bulkBytes, 0);
    {
        constexpr std::size_t kPageBytes = 4096;
        std::size_t off = 0;
        while (off < bulkBytes) {
            std::size_t chunk = std::min<std::size_t>(kPageBytes, bulkBytes - off);
            Memory::SafeReadBytes(reinterpret_cast<void*>(rangeStart + off),
                                   bulkBuf.data() + off, chunk);
            off += chunk;
        }
    }

    for (std::size_t i = 0; i < maxSteps; ++i) {
        std::uintptr_t p = start - i * kStride;
        out.scannedSteps = i + 1;

        // Index into pre-read buffer in REVERSE (i=0 means start, which
        // is the rightmost qword in the buffer; i=maxSteps-1 means oldest).
        const std::size_t bufIdx = (maxSteps - 1 - i) * kStride;
        std::uintptr_t vtCandidate = 0;
        std::memcpy(&vtCandidate, bulkBuf.data() + bufIdx, sizeof(vtCandidate));
        if (vtCandidate == 0) continue;
        if ((vtCandidate & 7u) != 0) continue;

        // Fast path: already-seen vtable.
        std::uintptr_t klassCandidate = 0;
        auto memoIt = vtMemoSnap.find(vtCandidate);
        if (memoIt != vtMemoSnap.end()) {
            klassCandidate = memoIt->second;
        } else {
            // Slow path: deref the candidate vtable's first qword to read
            // MonoVTable::klass (which is at offset 0 on every Mono build).
            // This is one extra SafeReadBytes per backward step on first
            // sight of a vtable — cheap, no runtime call, no GC lock.
            std::uintptr_t klassMaybe = 0;
            if (!Memory::SafeReadBytes(reinterpret_cast<void*>(vtCandidate),
                                       &klassMaybe, sizeof(klassMaybe))) {
                continue;
            }
            if (klassMaybe == 0 || (klassMaybe & 7u) != 0) continue;
            if (!klassInSet(klassMaybe)) continue;
            klassCandidate = klassMaybe;
            // Memoize so future iterations of this loop (and future
            // WhatIsAt calls) skip the deref.
            vtMemoSnap[vtCandidate] = klassCandidate;
            newlyMemoized.emplace_back(vtCandidate, klassCandidate);
        }

        MonoClass* k = reinterpret_cast<MonoClass*>(klassCandidate);
        const char* name = (r.class_get_name != nullptr)
            ? SehClassNameProbe(r.class_get_name, k) : nullptr;
        if (name == nullptr || *name == '\0') continue;

        // Got it — instance base `p`, vtable `vtCandidate`, validated klass
        // `klassCandidate`. Identity + field/array resolution (steps 5-8) is
        // shared with the Phase 30 P2 interval-tree path so both produce
        // identical answers. We already hold the ThreadAttachGuard.
        ResolveInstanceLookup(addr, p, vtCandidate, klassCandidate, out);

        // Merge newly-discovered (vtable, klass) pairs into the global
        // memo so subsequent calls hit the fast path.
        if (!newlyMemoized.empty()) {
            std::lock_guard<std::mutex> lk(cache().mu);
            for (const auto& kv : newlyMemoized) {
                cache().vtableToKlass[kv.first] = kv.second;
                cache().vtableSet.push_back(kv.first);
                if (g_minVtable == 0 || kv.first < g_minVtable) g_minVtable = kv.first;
                if (kv.first > g_maxVtable) g_maxVtable = kv.first;
            }
            // Keep the set sorted+deduped for the (mostly unused by the
            // new code) range-rejection path in legacy callers.
            std::sort(cache().vtableSet.begin(), cache().vtableSet.end());
            cache().vtableSet.erase(std::unique(cache().vtableSet.begin(), cache().vtableSet.end()),
                              cache().vtableSet.end());
        }
        return out;
    }

    // No managed instance found in scan window. Still merge any vtables we
    // ruled in mid-scan (for partial matches that failed the name probe)
    // so we don't repeat the deref next time.
    if (!newlyMemoized.empty()) {
        std::lock_guard<std::mutex> lk(cache().mu);
        for (const auto& kv : newlyMemoized) {
            cache().vtableToKlass[kv.first] = kv.second;
            cache().vtableSet.push_back(kv.first);
            if (g_minVtable == 0 || kv.first < g_minVtable) g_minVtable = kv.first;
            if (kv.first > g_maxVtable) g_maxVtable = kv.first;
        }
        std::sort(cache().vtableSet.begin(), cache().vtableSet.end());
        cache().vtableSet.erase(std::unique(cache().vtableSet.begin(), cache().vtableSet.end()),
                          cache().vtableSet.end());
    }

    out.reason = "no_klass_in_range";
    return out;
}

// ---------------------------------------------------------------------------
// Phase 29 — wide-heap "every instance of every known class" scan (Mono).
// ---------------------------------------------------------------------------
//
// Mono difference vs IL2CPP: an object's first qword is a MonoVTable*,
// not a MonoClass*. We can't binary-search the qword against `classSet`
// directly. Instead, we read the qword as a vtable pointer, lazily
// dereference vtable→klass (vtable[0] is `MonoClass *klass`), and check
// THAT against `classSet`. A per-scan vtable→klass memo absorbs the
// cost: a class with N instances triggers ONE deref (the first), then
// (N-1) cache hits.
//
// The memo also persists into the global `cache().vtableToKlass` map so
// follow-up WhatIsAt calls don't have to re-derive the same vtables.

namespace {

#pragma warning(push)
#pragma warning(disable: 4509)  // SEH in function with non-trivial dtor — none here.

// Inner SEH leaf: scan one region, calling Visit() for each qword. Visit
// takes the qword's payload (the supposed vtable) and the slot's address.
// Returns true to keep going, false to stop. SEH catches an AV inside
// the inner read so a single bad page only loses that region.
//
// For Mono we don't get to do a cheap range filter on the qword itself —
// it might be ANY vtable, not a class. So we capture the qword, check
// cheap filters (non-null, aligned), then defer the heavy check to the
// caller via the visit lambda. The visit lambda holds the unordered_maps
// (with destructors), which means we can't __try around it; SEH must
// wrap only the inner read of the qword from process memory.
//
// Result: we __try around `for(i) load(arr[i])`. Visit (out-of-__try)
// then validates the loaded value. Costs one extra ld + br vs IL2CPP's
// single-__try path but stays SEH-safe.
void SehSweepRegionLoad(const std::uint8_t* base, std::size_t size,
                         std::vector<std::uintptr_t>& outQwords,
                         std::vector<const std::uint8_t*>& outAddrs) noexcept
{
    __try {
        const std::size_t count = size / sizeof(std::uintptr_t);
        const std::uintptr_t* arr = reinterpret_cast<const std::uintptr_t*>(base);
        outQwords.reserve(outQwords.size() + count);
        outAddrs.reserve(outAddrs.size() + count);
        for (std::size_t i = 0; i < count; ++i) {
            std::uintptr_t qw = arr[i];
            if (qw == 0) continue;
            if ((qw & 7u) != 0) continue;
            // Quick range sanity — typical user-mode ptrs sit in
            // [0x10000, 0x7FFFFFFFFFFF] on x64; we want to throw away
            // obvious garbage before paying the deref cost in the caller.
            if (qw < 0x10000u) continue;
            outQwords.push_back(qw);
            outAddrs.push_back(base + i * sizeof(std::uintptr_t));
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Region had an unreadable page partway through; bail out with
        // what we collected so far.
    }
}

// Leaf SEH for dereferencing a candidate vtable. The deref might AV if
// the qword wasn't actually a vtable; we silently drop those.
bool SehReadKlassAt(std::uintptr_t vtable, std::uintptr_t& outKlass) noexcept {
    __try {
        outKlass = *reinterpret_cast<const std::uintptr_t*>(vtable);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

#pragma warning(pop)

}  // namespace

BulkInstancesResult FindAllInstances(std::size_t perClassCap) {
    BulkInstancesResult out;
    if (perClassCap == 0) perClassCap = 100;
    // Raised 4096 → 8192 on 2026-05-27 to match ExportAPI snapshotBegin
    // hard-cap. Dense-population classes (UnityEngine.Transform, custom
    // game state containers) can legitimately exceed 4096 live instances.
    if (perClassCap > 8192) perClassCap = 8192;

    if (!BuildClassRangeCache()) {
        Log::Write(MagicStrings::LogCategoryScan,
                   "Snapshot: FindAllInstances(Mono) aborted — class range cache unavailable");
        return out;
    }

    // 2026-05-28 vtable structural check — read the MonoDomain* field at
    // MonoVTable+2*ptr and require it to equal the known root domain.
    // Real vtables ALWAYS have this field pointing to their domain;
    // false-positive "vtables" (random structures whose first qword
    // happens to be a class pointer, e.g. MonoMethod entries in some
    // RGCTX caches, internal lookup tables that store vtable pointers
    // as values rather than at object headers) almost never have the
    // domain pointer at that offset. Live-smoke on Mortal: cuts false
    // positives from ~100/class to near zero. See lessons-learned for
    // the MonoVTable layout reference.
    const std::uintptr_t rootDomainAddr =
        reinterpret_cast<std::uintptr_t>(GetRootDomain());

    // Snapshot class set + cache pointers under lock, then release. We
    // intentionally do NOT carry the prior vtableToKlass cache into this
    // walk — older walks that pre-date the domain check would have
    // memoised some non-vtable structures as vtables. Start fresh so
    // the validation runs on every qw value we encounter.
    std::vector<std::uintptr_t> classSet;
    std::uintptr_t minKlass = 0, maxKlass = 0;
    std::unordered_map<std::uintptr_t, std::uintptr_t> vtMemo;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        classSet = cache().classSet;
        minKlass = g_minKlass;
        maxKlass = g_maxKlass;
    }
    if (classSet.empty()) return out;

    constexpr DWORD kRW =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

    auto regions = Memory::EnumerateCommittedRegions();
    auto stacks  = Memory::CollectThreadStackRanges();

    // Per-region: collect all qwords + slot addrs (SEH-safe), then walk
    // them outside __try doing the deref + classSet membership check.
    std::vector<std::uintptr_t>     qwords;
    std::vector<const std::uint8_t*> slots;
    qwords.reserve(1024);
    slots.reserve(1024);

    // Phase 29a-v4 — yield every N bytes scanned (running tally
    // across regions). Live-smoke on 活俠傳 (Mono x86 / Unity
    // 2020.3) build #627 showed the heap walk freezing the game
    // even though the decode loop already yields per batch: the
    // walk reads ~1.1 GB of process memory in a tight inner loop,
    // saturating memory bandwidth + polluting CPU cache, starving
    // the game's main thread regardless of attach state (walk does
    // not hold an attach). Sleep(0) every ~16 MB scanned gives the
    // game's main thread scheduling slices without meaningfully
    // slowing the walk (Sleep(0) returns in microseconds when no
    // other thread is ready). 16 MB on a 1 GB heap = ~64 yields.
    // Sleep(1) instead of Sleep(0). Sleep(0) only yields if another
    // thread is ready; Sleep(1) forcibly gives up >=1ms of timeslice
    // regardless. For 活俠傳-class targets the game's main thread may
    // not be "ready" by Windows scheduler accounting (waiting on
    // page-ins after our scan cleared its working set from L3), so
    // Sleep(0) returned immediately and yielded nothing. Sleep(1)
    // gives the kernel time to page the game's working set back in.
    constexpr std::size_t kYieldEveryBytes = 16ull * 1024ull * 1024ull;
    std::size_t bytesSinceYield = 0;

    for (const auto& r : regions) {
        if ((r.protect & kRW) == 0) continue;
        out.regionsTotal++;

        if (r.type == MEM_IMAGE) { out.regionsSkipImage++; continue; }
        // 2026-05-28: skip MEM_MAPPED too. Mono BDWGC heap is always
        // MEM_PRIVATE (VirtualAlloc/HeapAlloc backed), so file-backed
        // mappings (asset files, save data, memory-mapped resources)
        // are pure noise for the snapshot. Live-smoke on Mortal
        // (活俠傳) build #647 showed ~100 false positives per snapshot
        // coming from MEM_MAPPED regions where game data happened to
        // contain values matching known Mono vtable pointers. Each
        // false positive eats one slot of the per-class cap, pushing
        // real instances past the limit. Counted in regionsSkipImage
        // for now (didn't add a separate counter to avoid touching
        // BulkInstancesResult layout — same family of skip).
        if (r.type == MEM_MAPPED) { out.regionsSkipImage++; continue; }

        const std::uint8_t* regionEnd = r.base + r.size;
        bool onStack = false;
        for (const auto& s : stacks) {
            if (s.low >= regionEnd) break;
            if (s.high > r.base && s.low < regionEnd) { onStack = true; break; }
        }
        if (onStack) { out.regionsSkipStack++; continue; }

        out.regionsScanned++;
        out.bytesScanned += r.size;
        bytesSinceYield += r.size;
        if (bytesSinceYield >= kYieldEveryBytes) {
            ::Sleep(1);
            bytesSinceYield = 0;
        }

        qwords.clear();
        slots.clear();
        SehSweepRegionLoad(r.base, r.size, qwords, slots);
        out.totalQwordsScanned += (r.size / sizeof(std::uintptr_t));

        // Phase: classify each qword as a vtable → instance.
        for (std::size_t i = 0; i < qwords.size(); ++i) {
            std::uintptr_t qw = qwords[i];

            // Cache hit path. Most qwords after the first few thousand
            // are repeat vtables; the memo skips the deref entirely.
            auto memoIt = vtMemo.find(qw);
            std::uintptr_t klass = 0;
            if (memoIt != vtMemo.end()) {
                klass = memoIt->second;
                if (klass == 0) continue;   // memoised negative (not a vtable)
            } else {
                std::uintptr_t derefed = 0;
                if (!SehReadKlassAt(qw, derefed)) {
                    vtMemo[qw] = 0;  // memo negative
                    continue;
                }
                // Klass candidate range filter (cheap reject).
                if (derefed < minKlass || derefed > maxKlass) {
                    vtMemo[qw] = 0;
                    continue;
                }
                if ((derefed & 7u) != 0) { vtMemo[qw] = 0; continue; }
                if (!std::binary_search(classSet.begin(), classSet.end(), derefed)) {
                    vtMemo[qw] = 0;
                    continue;
                }
                // 2026-05-28 vtable structural validation. MonoVTable
                // layout (mono/metadata/class-internals.h):
                //   +0           MonoClass*   ← already in classSet
                //   +ptr_size    MonoGCDescriptor gc_descr
                //   +2*ptr_size  MonoDomain*  ← must equal root domain
                //   +3*ptr_size  MonoType*
                //   ...
                // Reading the domain field of a non-vtable structure
                // returns ~arbitrary memory. Only real vtables have the
                // domain pointer at that offset.
                std::uintptr_t domainAt = 0;
                if (!SehReadKlassAt(qw + 2 * sizeof(std::uintptr_t), domainAt) ||
                    domainAt != rootDomainAddr) {
                    vtMemo[qw] = 0;  // structurally not a vtable
                    continue;
                }
                klass = derefed;
                vtMemo[qw] = klass;
                out.vtablesResolved++;
            }

            // 2026-05-28 — false-positive filter on the instance slot.
            // Real Mono BDWGC objects are 8-byte aligned (matches the
            // already-required alignment of the vtable pointer they
            // start with). Random memory locations that happen to
            // contain a vtable pointer value (locals, captured ptrs,
            // ad-hoc fields) typically aren't aligned to 8 bytes.
            // Live-smoke on Mortal: this single check filters out
            // ~30-50% of false positives per class on the 活俠傳
            // snapshot path. Without it the per-class cap on 32-bit
            // Mono targets fills with noise before reaching the real
            // money / HP / etc. instances.
            const std::uintptr_t instAddr =
                reinterpret_cast<std::uintptr_t>(slots[i]);
            if ((instAddr & 7u) != 0) continue;

            auto* k = reinterpret_cast<MonoClass*>(klass);
            auto& list = out.byClass[k];
            if (list.size() < perClassCap) {
                list.push_back(instAddr);
                ++out.totalInstances;
            }
        }
    }

    // Merge new discoveries back into the global vtable→klass cache so
    // follow-up WhatIsAt calls don't pay the deref again.
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        for (const auto& [vt, kl] : vtMemo) {
            if (kl == 0) continue;
            cache().vtableToKlass.emplace(vt, kl);
            // Maintain the vtableSet alongside the map so callers that
            // binary-search via ContainsVtable benefit too.
            if (cache().vtableSet.empty() ||
                !std::binary_search(cache().vtableSet.begin(),
                                    cache().vtableSet.end(), vt)) {
                cache().vtableSet.push_back(vt);
                if (vt < g_minVtable) g_minVtable = vt;
                if (vt > g_maxVtable) g_maxVtable = vt;
            }
        }
        // Re-sort the vtable vector if we added anything (cheap; vector
        // is typically tens of thousands).
        std::sort(cache().vtableSet.begin(), cache().vtableSet.end());
        cache().vtableSet.erase(
            std::unique(cache().vtableSet.begin(), cache().vtableSet.end()),
            cache().vtableSet.end());
    }

    Log::Write(MagicStrings::LogCategoryScan,
               "Snapshot: FindAllInstances(Mono) classes=" + std::to_string(out.byClass.size()) +
               " instances=" + std::to_string(out.totalInstances) +
               " vtablesResolved=" + std::to_string(out.vtablesResolved) +
               " regionsScanned=" + std::to_string(out.regionsScanned) +
               " bytes=" + std::to_string(out.bytesScanned));
    return out;
}

// ---------------------------------------------------------------------------
// Phase 30 P2 (Mono port) — ReverseLookupIndex
// ---------------------------------------------------------------------------

// Process-wide reuse cache for the sorted base list. The aggregate "any
// numeric width" value scan fans out N back-to-back beginValueScan calls whose
// instance-base lists are identical (the heap doesn't change between them), so
// rebuilding the ~48s index per width is pure waste. When a caller opts in via
// reuseWindowMs > 0 and a prior Build() finished within that window, we copy
// the cached bases instead of re-walking. Single / refine scans pass 0 and
// always rebuild — a GC cycle between independent user actions could otherwise
// hand back stale bases. Heap-leaked (matches the class-cache pattern) so
// process exit doesn't run the vector destructor.
struct RevIndexCache {
    std::mutex                 mu;
    std::vector<ReverseLookupIndex::Entry> bases;
    DWORD                      builtAtTick = 0;   // GetTickCount() at last build
    bool                       valid       = false;
};
RevIndexCache& revIndexCache() {
    static RevIndexCache* p = new RevIndexCache();   // intentional leak
    return *p;
}

bool ReverseLookupIndex::Build(std::uint32_t reuseWindowMs) {
    bases_.clear();
    lastBase_ = lastVtable_ = lastKlass_ = lastNextBase_ = 0;
    hits_ = localityHits_ = 0;

    // Reuse fast-path: copy a recent build's bases if still inside the window.
    if (reuseWindowMs > 0) {
        std::lock_guard<std::mutex> lk(revIndexCache().mu);
        auto& c = revIndexCache();
        if (c.valid && (GetTickCount() - c.builtAtTick) <= reuseWindowMs) {
            bases_ = c.bases;
            Log::Write(MagicStrings::LogCategoryScan,
                       "ReverseLookupIndex(Mono): reused cached index instances=" +
                       std::to_string(bases_.size()));
            return true;
        }
    }

    if (!BuildClassRangeCache()) {
        Log::Write(MagicStrings::LogCategoryScan,
                   "ReverseLookupIndex(Mono): build aborted — class range cache unavailable");
        return false;
    }

    // Root domain pointer for the MonoVTable::domain structural check (same as
    // FindAllInstances). A non-vtable structure whose first qword happens to be
    // a class pointer almost never has the root domain at +2*ptr.
    const std::uintptr_t rootDomainAddr =
        reinterpret_cast<std::uintptr_t>(GetRootDomain());

    std::vector<std::uintptr_t> classSet;
    std::uintptr_t minKlass = 0, maxKlass = 0;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        classSet = cache().classSet;
        minKlass = g_minKlass;
        maxKlass = g_maxKlass;
    }
    if (classSet.empty()) return false;

    constexpr DWORD kRW =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

    auto regions = Memory::EnumerateCommittedRegions();
    auto stacks  = Memory::CollectThreadStackRanges();

    // Per-scan vtable→klass memo (a class with N instances pays ONE deref +
    // domain check, then N-1 cache hits). Not merged into the global cache —
    // this is a throwaway index.
    std::unordered_map<std::uintptr_t, std::uintptr_t> vtMemo;

    // Single-threaded by design (see header): the Mono heap walk carries the
    // 活俠傳 BDWGC-freeze risk, so we reuse FindAllInstances' Sleep(1)-yield
    // pacing rather than the IL2CPP index's parallel sweep.
    constexpr std::size_t kYieldEveryBytes = 16ull * 1024ull * 1024ull;
    std::size_t bytesSinceYield = 0;
    std::size_t qtotal = 0;

    std::vector<std::uintptr_t>      qwords;
    std::vector<const std::uint8_t*> slots;
    qwords.reserve(1024);
    slots.reserve(1024);

    for (const auto& r : regions) {
        if (ScanCancel::IsCancelRequested()) break;   // UI cancelled / disconnected
        if ((r.protect & kRW) == 0) continue;
        if (r.type == MEM_IMAGE || r.type == MEM_MAPPED) continue;
        const std::uint8_t* regionEnd = r.base + r.size;
        bool onStack = false;
        for (const auto& s : stacks) {
            if (s.low >= regionEnd) break;
            if (s.high > r.base && s.low < regionEnd) { onStack = true; break; }
        }
        if (onStack) continue;

        bytesSinceYield += r.size;
        if (bytesSinceYield >= kYieldEveryBytes) {
            ::Sleep(1);
            bytesSinceYield = 0;
        }

        qwords.clear();
        slots.clear();
        SehSweepRegionLoad(r.base, r.size, qwords, slots);
        qtotal += (r.size / sizeof(std::uintptr_t));

        for (std::size_t i = 0; i < qwords.size(); ++i) {
            std::uintptr_t qw = qwords[i];   // candidate MonoVTable*
            std::uintptr_t klass = 0;

            auto memoIt = vtMemo.find(qw);
            if (memoIt != vtMemo.end()) {
                klass = memoIt->second;
                if (klass == 0) continue;   // memoised negative
            } else {
                std::uintptr_t derefed = 0;
                if (!SehReadKlassAt(qw, derefed)) { vtMemo[qw] = 0; continue; }
                if (derefed < minKlass || derefed > maxKlass) { vtMemo[qw] = 0; continue; }
                if ((derefed & 7u) != 0) { vtMemo[qw] = 0; continue; }
                if (!std::binary_search(classSet.begin(), classSet.end(), derefed)) {
                    vtMemo[qw] = 0; continue;
                }
                // MonoVTable structural validation: +2*ptr is MonoDomain*.
                std::uintptr_t domainAt = 0;
                if (!SehReadKlassAt(qw + 2 * sizeof(std::uintptr_t), domainAt) ||
                    domainAt != rootDomainAddr) {
                    vtMemo[qw] = 0; continue;
                }
                klass = derefed;
                vtMemo[qw] = klass;
            }

            const std::uintptr_t instAddr = reinterpret_cast<std::uintptr_t>(slots[i]);
            if ((instAddr & 7u) != 0) continue;   // real objects are 8-aligned
            bases_.push_back(Entry{instAddr, qw, klass});
        }
    }

    // Sort ascending by base for the binary-search lookup.
    std::sort(bases_.begin(), bases_.end(),
              [](const Entry& a, const Entry& b) { return a.base < b.base; });

    Log::Write(MagicStrings::LogCategoryScan,
               "ReverseLookupIndex(Mono): instances=" + std::to_string(bases_.size()) +
               " qwords=" + std::to_string(qtotal));

    // Publish to the reuse cache so a back-to-back aggregate fan-out can copy
    // these bases instead of re-walking. Stamped even when reuseWindowMs was 0
    // for this call — a later opt-in call within the window still benefits.
    {
        std::lock_guard<std::mutex> lk(revIndexCache().mu);
        auto& c = revIndexCache();
        c.bases       = bases_;
        c.builtAtTick = GetTickCount();
        c.valid       = true;
    }
    return true;
}

AddressLookup ReverseLookupIndex::Lookup(std::uintptr_t addr, std::size_t maxBackwardScan) {
    AddressLookup out;
    out.queriedAddr = addr;

    // Match WhatIsAt's window math: round addr down to 8, allow a base as far
    // as (maxSteps-1)*8 below it.
    if (maxBackwardScan == 0)        maxBackwardScan = 4096;
    if (maxBackwardScan > 16 * 1024) maxBackwardScan = 16 * 1024;
    constexpr std::size_t kStride = 8;
    std::uintptr_t start = addr & ~static_cast<std::uintptr_t>(kStride - 1);
    std::size_t maxSteps = maxBackwardScan / kStride;
    if (maxSteps == 0) maxSteps = 1;
    std::uintptr_t lowest = (start >= (maxSteps - 1) * kStride)
        ? start - (maxSteps - 1) * kStride : 0;
    out.scannedFrom = lowest;
    out.scannedTo   = start + kStride;

    // Locality fast-path: when `start` lies in [lastBase_, lastNextBase_),
    // lastBase_ is PROVABLY the greatest base ≤ start (next base up is
    // lastNextBase_ > start), so skip the binary search. Still subject to the
    // backward-window check.
    if (lastBase_ != 0 && start >= lastBase_ &&
        (lastNextBase_ == 0 || start < lastNextBase_)) {
        if (lastBase_ < lowest) {
            out.reason = "no_klass_in_range";
            return out;
        }
        ++localityHits_;
        ++hits_;
        ResolveInstanceLookup(addr, lastBase_, lastVtable_, lastKlass_, out);
        return out;
    }

    // Binary search for the greatest base ≤ start.
    Entry probe{start, 0, 0};
    auto it = std::upper_bound(bases_.begin(), bases_.end(), probe,
        [](const Entry& a, const Entry& b) { return a.base < b.base; });
    std::uintptr_t nextBase = (it != bases_.end()) ? it->base : 0;
    if (it == bases_.begin()) {
        out.reason = "no_klass_in_range";
        return out;
    }
    --it;   // greatest base ≤ start
    if (it->base < lowest) {
        out.reason = "no_klass_in_range";
        return out;
    }

    lastBase_     = it->base;
    lastVtable_   = it->vtable;
    lastKlass_    = it->klass;
    lastNextBase_ = nextBase;
    ++hits_;
    ResolveInstanceLookup(addr, it->base, it->vtable, it->klass, out);
    return out;
}

}  // namespace Discrete::Mono
