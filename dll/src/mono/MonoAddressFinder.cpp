// MonoAddressFinder.cpp — Phase 17a implementation.

#include "MonoAddressFinder.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ManagedLayout.h"
#include "../shared/Memory.h"
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

namespace UnityCEDumper::Mono {

namespace {

std::mutex                                       g_cacheMutex;
std::vector<std::uintptr_t>                      g_vtableSet;       // sorted
std::unordered_map<std::uintptr_t,
                   std::uintptr_t>                g_vtableToKlass;   // MonoVTable* → MonoClass*
std::vector<std::uintptr_t>                      g_classSet;        // sorted
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
#pragma warning(pop)

bool ContainsKlass(std::uintptr_t p) {
    if (g_classSet.empty()) return false;
    if (p < g_minKlass || p > g_maxKlass) return false;
    return std::binary_search(g_classSet.begin(), g_classSet.end(), p);
}

bool ContainsVtable(std::uintptr_t p) {
    if (g_vtableSet.empty()) return false;
    if (p < g_minVtable || p > g_maxVtable) return false;
    return std::binary_search(g_vtableSet.begin(), g_vtableSet.end(), p);
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

bool BuildClassRangeCache() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    if (g_built) return !g_classSet.empty();

    if (!IsResolved()) {
        Log::Write(MagicStrings::LogCategoryWalk,
                   "AddrFinder: BuildClassRangeCache: mono not resolved");
        return false;
    }

    g_classSet.clear();
    g_vtableSet.clear();
    g_vtableToKlass.clear();
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
    // checks THAT against `g_classSet`. The vtable→klass map below is
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
            g_classSet.push_back(kp);
            if (kp < g_minKlass) g_minKlass = kp;
            if (kp > g_maxKlass) g_maxKlass = kp;
        }
    }

    std::sort(g_classSet.begin(), g_classSet.end());
    g_classSet.erase(std::unique(g_classSet.begin(), g_classSet.end()),
                     g_classSet.end());

    if (g_classSet.empty()) { g_minKlass = 0; g_maxKlass = 0; }
    g_built = true;

    Log::Write(MagicStrings::LogCategoryWalk,
               "AddrFinder: cached " + std::to_string(g_classSet.size()) +
               " MonoClass* (root domain, lazy vtable)");
    return !g_classSet.empty();
}

void InvalidateClassRangeCache() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    g_classSet.clear();
    g_vtableSet.clear();
    g_vtableToKlass.clear();
    g_minKlass  = 0; g_maxKlass  = 0;
    g_minVtable = 0; g_maxVtable = 0;
    g_built     = false;
}

ClassRangeStats GetClassRangeStats() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    ClassRangeStats s;
    s.built       = g_built;
    s.classCount  = g_classSet.size();
    s.vtableCount = g_vtableSet.size();
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
    // doesn't hold g_cacheMutex while doing SEH-protected probes. Any new
    // (vtable, klass) pair we discover gets written back at the end of
    // the call under the mutex so future scans skip the deref+lookup.
    std::vector<std::uintptr_t>                        classSetSnap;
    std::unordered_map<std::uintptr_t, std::uintptr_t> vtMemoSnap;
    std::uintptr_t minK = 0, maxK = 0;
    {
        std::lock_guard<std::mutex> lk(g_cacheMutex);
        classSetSnap = g_classSet;
        vtMemoSnap   = g_vtableToKlass;
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

    for (std::size_t i = 0; i < maxSteps; ++i) {
        std::uintptr_t p = start - i * kStride;
        out.scannedSteps = i + 1;

        std::uintptr_t vtCandidate = 0;
        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(p),
                                   &vtCandidate, sizeof(vtCandidate))) {
            continue;
        }
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

        out.found       = true;
        out.instance    = p;
        out.vtablePtr   = vtCandidate;
        out.klassPtr    = klassCandidate;
        out.fieldOffset = static_cast<std::ptrdiff_t>(addr - p);
        out.className   = name;
        if (r.class_get_namespace != nullptr) {
            const char* nsStr = SehClassNamespaceProbe(r.class_get_namespace, k);
            if (nsStr != nullptr) out.ns = nsStr;
        }
        if (r.class_get_image != nullptr && r.image_get_name != nullptr) {
            MonoImage* img = SehClassImageProbe(r.class_get_image, k);
            if (img != nullptr) {
                const char* imgName = SehImageNameProbe(r.image_get_name, img);
                if (imgName != nullptr) out.assembly = StripDllSuffix(imgName);
            }
        }

        // Phase 27c — array element resolution. If klass is an array type
        // (T[] / List<T>'s _items), compute element index from the offset
        // instead of falling through to per-field resolution (arrays don't
        // have declared fields in the usual sense — the "fields" are the
        // bounds pointer + max_length + the slot vector).
        bool resolvedAsArray = false;
        if (r.class_get_element_class != nullptr) {
            MonoClass* elemClass = SehElementClassProbe(r.class_get_element_class, k);
            if (elemClass != nullptr) {
                std::ptrdiff_t dataOff = static_cast<std::ptrdiff_t>(ManagedLayout::kArrayDataOffset);
                if (out.fieldOffset >= dataOff) {
                    // Read max_length so we can report total length.
                    std::uintptr_t arrayLength = 0;
                    Memory::SafeReadBytes(
                        reinterpret_cast<void*>(p + ManagedLayout::kArrayMaxLengthOffset),
                        &arrayLength, sizeof(arrayLength));

                    // Determine element stride: value-type elements use
                    // class_value_size; reference-type elements take one
                    // pointer slot per array entry.
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
                    // Surface array length + index in declaredIn slot so the
                    // wire layer can extract them. Format: "len=N idx=M".
                    char meta[64];
                    std::snprintf(meta, sizeof(meta), "len=%zu idx=%zu",
                                  static_cast<std::size_t>(arrayLength), elemIdx);
                    out.field.declaredIn = meta;
                    resolvedAsArray = true;
                }
            }
        }

        // Phase 17b — resolve which declared field contains the queried
        // offset. Reuses the same outer ThreadAttachGuard scope. Skipped
        // when array resolver already populated the field.
        if (!resolvedAsArray) {
            ResolveFieldAt(k, out.fieldOffset, out.field, out.nearestField,
                           out.nearestFieldDelta);
        }

        // Merge newly-discovered (vtable, klass) pairs into the global
        // memo so subsequent calls hit the fast path.
        if (!newlyMemoized.empty()) {
            std::lock_guard<std::mutex> lk(g_cacheMutex);
            for (const auto& kv : newlyMemoized) {
                g_vtableToKlass[kv.first] = kv.second;
                g_vtableSet.push_back(kv.first);
                if (g_minVtable == 0 || kv.first < g_minVtable) g_minVtable = kv.first;
                if (kv.first > g_maxVtable) g_maxVtable = kv.first;
            }
            // Keep the set sorted+deduped for the (mostly unused by the
            // new code) range-rejection path in legacy callers.
            std::sort(g_vtableSet.begin(), g_vtableSet.end());
            g_vtableSet.erase(std::unique(g_vtableSet.begin(), g_vtableSet.end()),
                              g_vtableSet.end());
        }
        return out;
    }

    // No managed instance found in scan window. Still merge any vtables we
    // ruled in mid-scan (for partial matches that failed the name probe)
    // so we don't repeat the deref next time.
    if (!newlyMemoized.empty()) {
        std::lock_guard<std::mutex> lk(g_cacheMutex);
        for (const auto& kv : newlyMemoized) {
            g_vtableToKlass[kv.first] = kv.second;
            g_vtableSet.push_back(kv.first);
            if (g_minVtable == 0 || kv.first < g_minVtable) g_minVtable = kv.first;
            if (kv.first > g_maxVtable) g_maxVtable = kv.first;
        }
        std::sort(g_vtableSet.begin(), g_vtableSet.end());
        g_vtableSet.erase(std::unique(g_vtableSet.begin(), g_vtableSet.end()),
                          g_vtableSet.end());
    }

    out.reason = "no_klass_in_range";
    return out;
}

}  // namespace UnityCEDumper::Mono
