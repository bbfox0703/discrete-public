// MonoAddressFinder.cpp — Phase 17a implementation.

#include "MonoAddressFinder.h"

#include "../shared/AddressBounds.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ManagedLayout.h"
#include "../shared/Memory.h"
#include "../shared/RuntimeHealth.h"
#include "../shared/ScanCancel.h"
#include "../shared/SehFilter.h"
#include "MonoClassWalker.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "MonoTypeParser.h"
#include "MonoVtableFilter.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
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
struct ClassExtentInfo {
    bool        isArray       = false;
    bool        isString      = false;
    std::size_t instanceSize  = 0;
    std::size_t arrayElemSize = 0;
};

constexpr std::size_t kNotDynamicCap = (sizeof(void*) == 8) ? (1u << 19) : (1u << 16);
constexpr std::size_t kExtentInfoCap = 262144u;

struct Cache {
    std::mutex                                          mu;
    std::vector<std::uintptr_t>                         vtableSet;       // sorted
    std::unordered_map<std::uintptr_t, std::uintptr_t>  vtableToKlass;   // vtable → klass
    std::vector<std::uintptr_t>                         classSet;        // sorted (TypeDef)
    std::vector<std::uintptr_t>                         imageSet;        // sorted (loaded MonoImage*)
    std::unordered_set<std::uintptr_t>                  dynamicClassSet; // validated generic/array klasses
    std::unordered_set<std::uintptr_t>                  notDynamicClass; // negative memo (capped)
    std::unordered_map<std::uintptr_t, ClassExtentInfo> extentInfo;      // klass → static size info
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

std::once_flag                        g_klassOffsetsOnce;
Discrete::AddressBounds::KlassFieldOffsets g_klassOffsets;

#pragma warning(push)
#pragma warning(disable: 4509)
const char* SehClassNameProbe(fn_class_get_name fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehClassNamespaceProbe(fn_class_get_namespace fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoImage* SehClassImageProbe(fn_class_get_image fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehImageNameProbe(fn_image_get_name fn, MonoImage* img) {
    __try { return fn(img); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoVTable* SehGetVtable(fn_class_vtable fn, MonoDomain* d, MonoClass* k) {
    unsigned long code = 0;
    __try { return fn(d, k); }
    __except ((code = GetExceptionCode()),
              Seh::MemoryAccessExceptionFilter(code, GetExceptionInformation())) {
        RuntimeHealth::MarkRuntimeFault("mono_class_vtable", code);
        return nullptr;
    }
}
// Phase 17b — SEH-protected field-walk primitives.
MonoClass* SehParentLookup(fn_class_get_parent fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoClassField* SehNextFieldProbe(fn_class_get_fields fn, MonoClass* k, void** iter) {
    unsigned long code = 0;
    __try { return fn(k, iter); }
    __except ((code = GetExceptionCode()),
              Seh::MemoryAccessExceptionFilter(code, GetExceptionInformation())) {
        RuntimeHealth::MarkRuntimeFault("mono_class_get_fields", code);
        return nullptr;
    }
}
const char* SehFieldNameProbe(fn_field_get_name fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
int SehFieldOffsetProbe(fn_field_get_offset fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
std::uint32_t SehFieldFlagsProbe(fn_field_get_flags fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
MonoType* SehFieldTypeProbe(fn_field_get_type fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoClass* SehClassFromMonoType(fn_class_from_mono_type fn, MonoType* t) {
    __try { return fn(t); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
int SehIsValueTypeProbe(fn_class_is_valuetype fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
int SehIsEnumProbe(fn_class_is_enum fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
MonoType* SehEnumBasetypeProbe(fn_class_enum_basetype fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
int SehValueSizeProbe(fn_class_value_size fn, MonoClass* k) {
    __try { std::uint32_t a = 0; return fn(k, &a); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
std::int32_t SehClassInstanceSizeProbe(fn_class_instance_size fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
std::uint32_t SehObjectGetSizeProbe(fn_object_get_size fn, MonoObject* o) {
    __try { return fn(o); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
// Phase 27c — array element class lookup + B-L2 rank check (enums also have
// element_class != klass, pointing at their underlying integer type, so rank
// or !is_enum is required to avoid misclassifying boxed enums as arrays).
MonoClass* SehElementClassProbe(fn_class_get_element_class fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
int SehClassRankProbe(fn_class_get_rank fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
// Phase 27d follow-up — walk nesting chain for nested-class name resolution.
MonoClass* SehNestingProbe(fn_class_get_nesting_type fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
// Leaf SEH for dereferencing a pointer-sized word (vtable[0] / vtable+2*ptr).
bool SehReadKlassAt(std::uintptr_t addr, std::uintptr_t& outWord) noexcept {
    __try {
        outWord = *reinterpret_cast<const std::uintptr_t*>(addr);
        return true;
    } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) {
        return false;
    }
}
// Strict SEH-leaf window copy (zero C++ destructors/allocations inside __try).
bool SehCopyRegionWindow(const std::uint8_t* src, void* dst, std::size_t n) noexcept {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) {
        return false;
    }
}
#pragma warning(pop)

// B-H2: calibrate MonoClass::{image, element_class, name} offsets from known
// TypeDef classes so dynamic generic/array klasses can be validated purely via
// SafeReadBytes (zero runtime API calls on unvalidated memory).
void CalibrateMonoKlassOffsets() {
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_image == nullptr ||
        r.class_get_element_class == nullptr || r.class_get_name == nullptr) {
        return;
    }
    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) return;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return;

    constexpr std::size_t kScanLimit     = 512u;
    constexpr std::size_t kStep          = sizeof(void*);
    constexpr std::size_t kSamplesWanted = 16u;

    std::vector<std::size_t> imgCand, elemCand, nameCand;
    bool imgSeeded = false, elemSeeded = false, nameSeeded = false;
    std::size_t sampled = 0u;

    auto assemblies = EnumerateAssemblies();
    for (const auto& a : assemblies) {
        if (a.image == nullptr) continue;
        auto classes = EnumerateClasses(a.image);
        std::size_t perAsm = 0u;
        for (const auto& ce : classes) {
            if (ce.klass == nullptr) continue;
            if (SehElementClassProbe(r.class_get_element_class, ce.klass) != ce.klass) continue;
            MonoImage* img = SehClassImageProbe(r.class_get_image, ce.klass);
            const char* nm = SehClassNameProbe(r.class_get_name, ce.klass);
            if (img == nullptr || nm == nullptr || *nm == '\0') continue;

            const auto selfPtr = reinterpret_cast<std::uintptr_t>(ce.klass);
            const auto imgPtr  = reinterpret_cast<std::uintptr_t>(img);
            const auto namePtr = reinterpret_cast<std::uintptr_t>(nm);
            const auto* bytes  = reinterpret_cast<const std::uint8_t*>(ce.klass);

            std::vector<std::size_t> imgHits, elemHits, nameHits;
            for (std::size_t off = 0u; off + kStep <= kScanLimit; off += kStep) {
                std::uintptr_t v = 0u;
                if (!Memory::SafeReadBytes(bytes + off, &v, sizeof(v))) break;
                if (v == imgPtr)  imgHits.push_back(off);
                if (v == selfPtr) elemHits.push_back(off);
                if (v == namePtr) nameHits.push_back(off);
            }
            Discrete::AddressBounds::IntersectCandidateOffsets(imgCand,  imgSeeded,  imgHits);
            Discrete::AddressBounds::IntersectCandidateOffsets(elemCand, elemSeeded, elemHits);
            Discrete::AddressBounds::IntersectCandidateOffsets(nameCand, nameSeeded, nameHits);

            ++sampled;
            ++perAsm;
            if (sampled >= kSamplesWanted || perAsm >= 4u) break;
        }
        if (sampled >= kSamplesWanted) break;
    }

    if (!imgCand.empty())  { g_klassOffsets.imageOffset     = imgCand.front();  g_klassOffsets.hasImageOffset = true; }
    if (!elemCand.empty()) { g_klassOffsets.elemClassOffset = elemCand.front(); g_klassOffsets.hasElemOffset  = true; }
    if (!nameCand.empty()) { g_klassOffsets.nameOffset      = nameCand.front(); g_klassOffsets.hasNameOffset  = true; }

    if (g_klassOffsets.Ready()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "AddrFinder(Mono): calibrated klass offsets image=" +
            std::to_string(g_klassOffsets.imageOffset) +
            " elem=" + std::to_string(g_klassOffsets.elemClassOffset) +
            " name=" + std::to_string(g_klassOffsets.nameOffset) +
            " (from " + std::to_string(sampled) + " samples)");
    } else {
        Log::Write(MagicStrings::LogCategoryWalk,
            "AddrFinder(Mono): klass offset calibration incomplete — dynamic generic/array discovery disabled");
    }
}

void EnsureMonoKlassOffsetsCalibrated() {
    std::call_once(g_klassOffsetsOnce, CalibrateMonoKlassOffsets);
}

bool TryAcceptDynamicMonoKlass(
    std::uintptr_t candidate,
    const std::vector<std::uintptr_t>& imageSetSnap,
    const std::vector<std::uintptr_t>& classSetSnap)
{
    if (!g_klassOffsets.Ready() || imageSetSnap.empty()) return false;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        if (cache().dynamicClassSet.find(candidate) != cache().dynamicClassSet.end()) return true;
        if (cache().notDynamicClass.find(candidate) != cache().notDynamicClass.end()) return false;
    }

    auto readPtr = [](std::uintptr_t addr, std::uintptr_t& out) -> bool {
        return Memory::SafeReadBytes(reinterpret_cast<const void*>(addr), &out, sizeof(out));
    };
    auto readBytes = [](std::uintptr_t addr, void* dst, std::size_t n) -> bool {
        return Memory::SafeReadBytes(reinterpret_cast<const void*>(addr), dst, n);
    };
    auto containsImage = [&imageSetSnap](std::uintptr_t img) -> bool {
        return std::binary_search(imageSetSnap.begin(), imageSetSnap.end(), img);
    };
    auto containsClass = [&classSetSnap](std::uintptr_t k) -> bool {
        return std::binary_search(classSetSnap.begin(), classSetSnap.end(), k);
    };

    bool isArray = false;
    std::uintptr_t elemClass = 0u;
    const bool ok = Discrete::AddressBounds::ValidateDynamicKlassMemory(
        candidate, g_klassOffsets, readPtr, readBytes,
        containsImage, containsClass, isArray, elemClass);

    {
        std::lock_guard<std::mutex> lk(cache().mu);
        if (ok) {
            cache().dynamicClassSet.insert(candidate);
        } else if (cache().notDynamicClass.size() < kNotDynamicCap) {
            cache().notDynamicClass.insert(candidate);
        }
    }
    return ok;
}

// B-L2: Distinguish genuine managed arrays from enums (Mono sets an enum's
// element_class to its underlying integer type, so element_class != k alone
// misclassifies boxed enums as arrays). Caller holds a ThreadAttachGuard.
bool IsMonoArrayClass(MonoClass* k, MonoClass*& outElemClass) {
    outElemClass = nullptr;
    if (k == nullptr) return false;
    const auto& r = GetResolver();
    if (r.class_get_element_class == nullptr) return false;
    if (r.class_get_rank != nullptr) {
        if (SehClassRankProbe(r.class_get_rank, k) <= 0) return false;
    } else if (r.class_is_enum != nullptr && SehIsEnumProbe(r.class_is_enum, k) != 0) {
        return false;
    }
    MonoClass* elem = SehElementClassProbe(r.class_get_element_class, k);
    if (elem == nullptr || elem == k) return false;
    outElemClass = elem;
    return true;
}

// Compute (and cache per-class) the byte extent of a confirmed Mono object
// so WhatIsAt and ReverseLookupIndex reject out-of-bounds neighbor hits.
// Caller holds a ThreadAttachGuard.
ClassExtentInfo GetMonoClassExtentInfo(MonoClass* k) {
    const auto kp = reinterpret_cast<std::uintptr_t>(k);
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        auto it = cache().extentInfo.find(kp);
        if (it != cache().extentInfo.end()) return it->second;
    }

    ClassExtentInfo info;
    const auto& r = GetResolver();
    MonoClass* elem = nullptr;
    if (IsMonoArrayClass(k, elem)) {
        info.isArray = true;
        info.arrayElemSize = ManagedLayout::kPointerSize;
        if (r.class_is_valuetype != nullptr &&
            SehIsValueTypeProbe(r.class_is_valuetype, elem) != 0 &&
            r.class_value_size != nullptr) {
            int sz = SehValueSizeProbe(r.class_value_size, elem);
            if (sz > 0) info.arrayElemSize = static_cast<std::size_t>(sz);
        }
    }
    if (!info.isArray) {
        const char* nm = (r.class_get_name != nullptr)
            ? SehClassNameProbe(r.class_get_name, k) : nullptr;
        const char* ns = (r.class_get_namespace != nullptr)
            ? SehClassNamespaceProbe(r.class_get_namespace, k) : nullptr;
        if (nm != nullptr && ns != nullptr &&
            std::strcmp(nm, "String") == 0 && std::strcmp(ns, "System") == 0) {
            info.isString = true;
        }
        if (r.class_instance_size != nullptr) {
            std::int32_t sz = SehClassInstanceSizeProbe(r.class_instance_size, k);
            if (sz > 0) info.instanceSize = static_cast<std::size_t>(sz);
        }
    }

    {
        std::lock_guard<std::mutex> lk(cache().mu);
        if (cache().extentInfo.size() < kExtentInfoCap) {
            cache().extentInfo.emplace(kp, info);
        }
    }
    return info;
}

std::size_t ComputeMonoInstanceExtent(std::uintptr_t instanceAddr, MonoClass* k) {
    const ClassExtentInfo ci = GetMonoClassExtentInfo(k);
    Discrete::AddressBounds::ObjectExtentSpec spec;
    spec.isArray       = ci.isArray;
    spec.isString      = ci.isString;
    spec.instanceSize  = ci.instanceSize;
    spec.arrayElemSize = ci.arrayElemSize;

    if (ci.isArray) {
        std::uintptr_t len = 0u;
        if (Memory::SafeReadBytes(
                reinterpret_cast<const void*>(instanceAddr + ManagedLayout::kArrayMaxLengthOffset),
                &len, sizeof(len))) {
            spec.arrayLength = static_cast<std::size_t>(len);
        }
    } else if (ci.isString) {
        std::int32_t clen = -1;
        if (Memory::SafeReadBytes(
                reinterpret_cast<const void*>(instanceAddr + ManagedLayout::kStringLengthOffset),
                &clen, sizeof(clen))) {
            spec.stringChars = clen;
        }
    } else if (spec.instanceSize == 0u) {
        const auto& r = GetResolver();
        if (r.object_get_size != nullptr) {
            std::uint32_t osz = SehObjectGetSizeProbe(
                r.object_get_size, reinterpret_cast<MonoObject*>(instanceAddr));
            if (osz > 0u) spec.instanceSize = static_cast<std::size_t>(osz);
        }
    }
    return Discrete::AddressBounds::ComputeObjectExtent(spec);
}

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
    // Pointer-sized, NOT a literal 8: this backend builds x86 as well.
    if (t == "System.IntPtr"  || t == "System.UIntPtr") return ManagedLayout::kPointerSize;
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
            // Last resort: an unresolved field is a reference (pointer) slot.
            // MUST be pointer-sized, not a literal 8 — Mono builds x86 too, and
            // rf.size decides field containment (see the `interior` / `end`
            // checks below). Four bytes too wide on x86 makes a field swallow
            // the start of the NEXT one, so whatIsAt / the address finder name
            // the wrong field with no sign anything went wrong.
            if (sz == 0) sz = ManagedLayout::kPointerSize;
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
    //
    // GUARD: mono_class_get_element_class returns the class ITSELF for a
    // NON-array class (element_class == klass), so `elemClass != nullptr` alone
    // misclassifies EVERY plain object as an array — reporting real declared
    // fields as "[N]" elements with a bogus pointer-size element stride, and
    // (via field.present being set unconditionally below) defeating the group
    // scan's droppedNoField prune. Only treat it as an array when the element
    // class is genuinely a DIFFERENT class. This mirrors the IL2CPP idiom at
    // IL2CPPAddressFinder.cpp (`SehElementClassProbe(...) != ce.klass`).
    bool resolvedAsArray = false;
    MonoClass* elemClass = nullptr;
    if (IsMonoArrayClass(k, elemClass)) {
        {
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
    bool ok = false;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        if (g_built) return !cache().classSet.empty();

        if (!IsResolved()) {
            Log::Write(MagicStrings::LogCategoryWalk,
                       "AddrFinder: BuildClassRangeCache: mono not resolved");
            return false;
        }

        cache().classSet.clear();
        cache().imageSet.clear();
        cache().dynamicClassSet.clear();
        cache().notDynamicClass.clear();
        cache().extentInfo.clear();
        cache().vtableSet.clear();
        cache().vtableToKlass.clear();
        g_minKlass  = static_cast<std::uintptr_t>(-1);
        g_maxKlass  = 0;
        // B-L3: Match InvalidateClassRangeCache's 0 sentinel (all min updates
        // check `g_minVtable == 0 || vt < g_minVtable`).
        g_minVtable = 0;
        g_maxVtable = 0;

        auto assemblies = EnumerateAssemblies();
        for (const auto& a : assemblies) {
            if (a.image == nullptr) continue;
            cache().imageSet.push_back(reinterpret_cast<std::uintptr_t>(a.image));
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
        std::sort(cache().imageSet.begin(), cache().imageSet.end());
        cache().imageSet.erase(std::unique(cache().imageSet.begin(), cache().imageSet.end()),
                         cache().imageSet.end());

        if (cache().classSet.empty()) { g_minKlass = 0; g_maxKlass = 0; }
        g_built = true;
        ok = !cache().classSet.empty();

        Log::Write(MagicStrings::LogCategoryWalk,
                   "AddrFinder: cached " + std::to_string(cache().classSet.size()) +
                   " MonoClass* across " + std::to_string(cache().imageSet.size()) +
                   " images (root domain, lazy vtable)");
    }
    if (ok) EnsureMonoKlassOffsetsCalibrated();
    return ok;
}

void InvalidateClassRangeCache() {
    std::lock_guard<std::mutex> lk(cache().mu);
    cache().classSet.clear();
    cache().imageSet.clear();
    cache().dynamicClassSet.clear();
    cache().notDynamicClass.clear();
    cache().extentInfo.clear();
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

// B-M11 — unified MonoVTable structural + class validation shared across
// WhatIsAt, FindAllInstances, and ReverseLookupIndex::Build so all three paths
// enforce identical rules (including MonoVTable::domain == rootDomainAddr on
// BOTH static classSet hits and dynamic generic/array klasses) and the global
// vtableToKlass memo never caches unvalidated non-vtable structs (MonoType,
// RuntimeTypeHandle, etc.).
bool IsPlausibleVTable(std::uintptr_t vtCandidate,
                       std::uintptr_t rootDomainAddr,
                       const std::vector<std::uintptr_t>& classSet,
                       std::uintptr_t minKlass,
                       std::uintptr_t maxKlass,
                       const std::vector<std::uintptr_t>& imageSet,
                       std::uintptr_t& outKlass) {
    if (!IsPlausibleMonoVtableCandidate(vtCandidate)) return false;

    std::uintptr_t klassMaybe = 0;
    if (!SehReadKlassAt(vtCandidate, klassMaybe)) return false;

    std::uintptr_t domainAt = 0;
    if (!SehReadKlassAt(vtCandidate + kMonoVtableDomainOffset, domainAt)) return false;

    if (!ValidateMonoVtableHeader(vtCandidate, klassMaybe, domainAt, rootDomainAddr)) {
        return false;
    }

    const bool inClassSet = (klassMaybe >= minKlass && klassMaybe <= maxKlass) &&
        std::binary_search(classSet.begin(), classSet.end(), klassMaybe);
    if (!inClassSet && !TryAcceptDynamicMonoKlass(klassMaybe, imageSet, classSet)) {
        return false;
    }

    outKlass = klassMaybe;
    return true;
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
    // B-L11: unified near-zero clamp so scannedFrom == rangeStart and
    // bufIdx == (p - rangeStart) for every step i in [0, maxSteps).
    const auto scanRange = Discrete::AddressBounds::ComputeBackwardScanRange(
        addr, maxBackwardScan, kStride);
    const std::uintptr_t start      = scanRange.start;
    const std::size_t    maxSteps   = scanRange.maxSteps;
    const std::size_t    bulkBytes  = scanRange.bulkBytes;
    const std::uintptr_t rangeStart = scanRange.rangeStart;
    out.scannedFrom = rangeStart;
    out.scannedTo   = scanRange.scannedTo;

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
    const std::uintptr_t rootDomainAddr = reinterpret_cast<std::uintptr_t>(domain);
    std::vector<std::uintptr_t>                        classSetSnap;
    std::vector<std::uintptr_t>                        imageSetSnap;
    std::unordered_map<std::uintptr_t, std::uintptr_t> vtMemoSnap;
    std::uintptr_t minK = 0, maxK = 0;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        classSetSnap = cache().classSet;
        imageSetSnap = cache().imageSet;
        vtMemoSnap   = cache().vtableToKlass;
        minK = g_minKlass;
        maxK = g_maxKlass;
    }
    if (classSetSnap.empty()) {
        out.reason = "class_set_empty";
        return out;
    }

    // Track newly-discovered (vtable, klass) pairs to merge into the
    // global memo at the end of this WhatIsAt call.
    std::vector<std::pair<std::uintptr_t, std::uintptr_t>> newlyMemoized;

    // Phase 27 perf: bulk-read the entire backward range in one shot
    // instead of paying per-8-byte SafeReadBytes (each does a VirtualQuery
    // ~µs). For a 16 KB scan, that's 2048 → 4 syscalls. On 活俠傳 a 500-
    // result scan dropped from 16 s to ~4-5 s. Pages that AV are zero-
    // filled so the inner loop sees "no candidate" — same effective
    // behavior as the old per-step code.
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
        if (!IsAlignedMonoVtablePtr(vtCandidate)) continue;

        // Fast path: already-seen domain-verified vtable.
        std::uintptr_t klassCandidate = 0;
        auto memoIt = vtMemoSnap.find(vtCandidate);
        if (memoIt != vtMemoSnap.end()) {
            klassCandidate = memoIt->second;
        } else {
            // Slow path (B-M11): shared IsPlausibleVTable enforces vtable[0]
            // klass alignment, vtable+2*ptr == rootDomain, and classSet /
            // dynamic-klass membership — byte-identical to FindAllInstances
            // and ReverseLookupIndex::Build.
            if (!IsPlausibleVTable(vtCandidate, rootDomainAddr,
                                   classSetSnap, minK, maxK, imageSetSnap,
                                   klassCandidate)) {
                continue;
            }
            // Memoize only fully verified (vtable, klass) pairs.
            vtMemoSnap[vtCandidate] = klassCandidate;
            newlyMemoized.emplace_back(vtCandidate, klassCandidate);
        }

        MonoClass* k = reinterpret_cast<MonoClass*>(klassCandidate);
        const char* name = (r.class_get_name != nullptr)
            ? SehClassNameProbe(r.class_get_name, k) : nullptr;
        if (name == nullptr || *name == '\0') continue;

        // B-H2: enforce object byte-extent so an address sitting past the end
        // of an earlier neighbor object (e.g. inside an unmanaged gap or a
        // subsequent object) is never misattributed to that neighbor.
        const std::size_t objExtent = ComputeMonoInstanceExtent(p, k);
        if (!Discrete::AddressBounds::IsOffsetWithinExtent(
                static_cast<std::ptrdiff_t>(addr - p), objExtent)) {
            continue;
        }

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

// Strict SEH-leaf region sweeper (B-M6): copies 64 KiB windows through
// SehCopyRegionWindow into a local stack buffer, then filters and pushes
// candidates strictly outside __try so std::bad_alloc / vector growth never
// executes inside an SEH frame.
void SehSweepRegionLoad(const std::uint8_t* base, std::size_t size,
                         std::vector<std::uintptr_t>& outQwords,
                         std::vector<const std::uint8_t*>& outAddrs)
{
    constexpr std::size_t kWindowBytes = 64u * 1024u;
    constexpr std::size_t kWordBytes   = sizeof(std::uintptr_t);
    constexpr std::size_t kWindowWords = kWindowBytes / kWordBytes;
    const std::size_t totalWords = size / kWordBytes;
    constexpr std::size_t kMaxReservePerRegion = 1u << 20;
    const std::size_t want = totalWords < kMaxReservePerRegion ? totalWords : kMaxReservePerRegion;

    try {
        outQwords.reserve(outQwords.size() + want);
        outAddrs.reserve(outAddrs.size() + want);

        std::uintptr_t windowBuf[kWindowWords];
        std::size_t off = 0;
        while (off + kWordBytes <= size) {
            const std::size_t avail = size - off;
            const std::size_t chunkBytes = (avail < kWindowBytes ? avail : kWindowBytes) & ~(kWordBytes - 1u);
            if (chunkBytes == 0) break;
            if (!SehCopyRegionWindow(base + off, windowBuf, chunkBytes)) {
                break;  // unreadable page partway through region
            }
            const std::size_t words = chunkBytes / kWordBytes;
            for (std::size_t i = 0; i < words; ++i) {
                const std::uintptr_t qw = windowBuf[i];
                if (!IsPlausibleMonoVtableCandidate(qw)) continue;
                outQwords.push_back(qw);
                outAddrs.push_back(base + off + i * kWordBytes);
            }
            off += chunkBytes;
        }
    } catch (const std::bad_alloc&) {
        // Keep whatever was collected so far rather than crashing/terminating.
    }
}

// Cap for NEGATIVE vtable-memo entries. A positive memo is bounded by the
// number of real vtables, but a negative is recorded for every DISTINCT
// non-vtable qword in the entire heap walk — and heap words are overwhelmingly
// unique (floats, counters, one-off pointers, string bytes). At roughly 48
// bytes per unordered_map node a multi-GB heap grew this to hundreds of MB
// *inside the game's own address space*. Past the cap we stop recording new
// negatives; everything already recorded still serves, so the
// frequently-repeated values — the ones the memo exists for — stay cached
// because they are seen early. The 32-bit build gets a far smaller cap: it
// shares the game's 2 GB.
//
// Namespace-scope on purpose: both heap walkers below (FindAllInstances and
// ReverseLookupIndex::Build) memoise, and keeping the cap in one place is what
// stops the two from drifting apart again.
constexpr std::size_t kMaxMemoEntries =
    (sizeof(void*) == 8) ? (2u * 1024u * 1024u) : (256u * 1024u);

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
    std::vector<std::uintptr_t> imageSet;
    std::uintptr_t minKlass = 0, maxKlass = 0;
    std::unordered_map<std::uintptr_t, std::uintptr_t> vtMemo;

    // Negative memo entries are the unbounded ones — see kMaxMemoEntries above,
    // which the comment "most qwords are repeat vtables" only holds for the
    // positives.
    auto memoNegative = [&vtMemo](std::uintptr_t key) {
        if (vtMemo.size() < kMaxMemoEntries) vtMemo[key] = 0;
    };

    {
        std::lock_guard<std::mutex> lk(cache().mu);
        classSet = cache().classSet;
        imageSet = cache().imageSet;
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
        if (ScanCancel::IsCancelRequested()) {
            out.cancelled = true;
            break;
        }
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
            if ((i & 4095u) == 0u && ScanCancel::IsCancelRequested()) {
                out.cancelled = true;
                break;
            }
            std::uintptr_t qw = qwords[i];

            // Cache hit path. Most qwords after the first few thousand
            // are repeat vtables; the memo skips the deref entirely.
            auto memoIt = vtMemo.find(qw);
            std::uintptr_t klass = 0;
            if (memoIt != vtMemo.end()) {
                klass = memoIt->second;
                if (klass == 0) continue;   // memoised negative (not a vtable)
            } else {
                if (!IsPlausibleVTable(qw, rootDomainAddr,
                                       classSet, minKlass, maxKlass, imageSet,
                                       klass)) {
                    memoNegative(qw);
                    continue;
                }
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
        if (out.cancelled) break;
    }

    if (out.cancelled || ScanCancel::IsCancelRequested()) {
        out.cancelled = true;
        out.byClass.clear();
        out.totalInstances = 0;
        Log::Write(MagicStrings::LogCategoryScan,
                   "Snapshot: FindAllInstances(Mono) cancelled — partial results discarded");
        return out;
    }

    // Merge new discoveries back into the global vtable→klass cache so
    // follow-up WhatIsAt calls don't pay the deref again.
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        // B-L3: Deduplicate via vtableToKlass.emplace().second in O(1) instead
        // of calling std::binary_search on vtableSet while mutating it unsorted.
        bool addedAny = false;
        for (const auto& [vt, kl] : vtMemo) {
            if (kl == 0) continue;
            if (cache().vtableToKlass.emplace(vt, kl).second) {
                cache().vtableSet.push_back(vt);
                if (g_minVtable == 0 || vt < g_minVtable) g_minVtable = vt;
                if (vt > g_maxVtable) g_maxVtable = vt;
                addedAny = true;
            }
        }
        if (addedAny) {
            std::sort(cache().vtableSet.begin(), cache().vtableSet.end());
            cache().vtableSet.erase(
                std::unique(cache().vtableSet.begin(), cache().vtableSet.end()),
                cache().vtableSet.end());
        }
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
    lastExtent_ = 0;
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
    std::vector<std::uintptr_t> imageSet;
    std::uintptr_t minKlass = 0, maxKlass = 0;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        classSet = cache().classSet;
        imageSet = cache().imageSet;
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
    // this is a throwaway index. Negatives are capped exactly as in
    // FindAllInstances: this walk covers the same multi-GB heap, so an
    // uncapped negative memo grows just as far inside the game's address space.
    std::unordered_map<std::uintptr_t, std::uintptr_t> vtMemo;
    auto memoNegative = [&vtMemo](std::uintptr_t key) {
        if (vtMemo.size() < kMaxMemoEntries) vtMemo[key] = 0;
    };

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
                if (!IsPlausibleVTable(qw, rootDomainAddr,
                                       classSet, minKlass, maxKlass, imageSet,
                                       klass)) {
                    memoNegative(qw);
                    continue;
                }
                vtMemo[qw] = klass;
            }

            const std::uintptr_t instAddr = reinterpret_cast<std::uintptr_t>(slots[i]);
            if ((instAddr & 7u) != 0) continue;   // real objects are 8-aligned
            bases_.push_back(Entry{instAddr, qw, klass});
        }
    }

    // A cancelled walk left `bases_` a low-address PREFIX of the heap (the region
    // loop above breaks on cancel). Publishing that would stamp a near-empty index
    // as `valid` in the process-wide reuse cache and report success, so every later
    // Lookup() — including ones from a *different*, uncancelled request that reuses
    // the cache — would silently miss. Discard it and fail: callers fall back to
    // the (slower) direct backward scan and stay correct.
    if (ScanCancel::IsCancelRequested()) {
        Log::Write(MagicStrings::LogCategoryScan,
                   "ReverseLookupIndex(Mono): build cancelled — partial index discarded, cache untouched");
        bases_.clear();
        return false;
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

    // Locality fast-path: when `start` lies in [lastBase_, lastNextBase_) AND
    // `addr` lies inside lastBase_'s cached byte extent (B-H2), lastBase_ is
    // provably the containing instance — skip the binary search.
    if (lastBase_ != 0 && start >= lastBase_ &&
        (lastNextBase_ == 0 || start < lastNextBase_) &&
        addr >= lastBase_ &&
        Discrete::AddressBounds::IsOffsetWithinExtent(
            static_cast<std::ptrdiff_t>(addr - lastBase_), lastExtent_)) {
        if (lastBase_ < lowest) {
            out.reason = "no_klass_in_range";
            return out;
        }
        ++localityHits_;
        ++hits_;
        ResolveInstanceLookup(addr, lastBase_, lastVtable_, lastKlass_, out);
        return out;
    }

    // Binary search for the nearest entry ≤ start whose object byte-extent
    // actually contains `addr` (B-H2: prevents neighbor-object misattribution
    // while backtracking over embedded klass/vtable pointers).
    auto sel = Discrete::AddressBounds::SelectContainingEntry(
        bases_, addr, start, lowest,
        [](const Entry& e) -> std::size_t {
            return ComputeMonoInstanceExtent(e.base, reinterpret_cast<MonoClass*>(e.klass));
        });
    if (!sel.found) {
        out.reason = "no_klass_in_range";
        return out;
    }

    lastBase_     = sel.entry.base;
    lastVtable_   = sel.entry.vtable;
    lastKlass_    = sel.entry.klass;
    lastNextBase_ = sel.nextBase;
    lastExtent_   = sel.extent;
    ++hits_;
    ResolveInstanceLookup(addr, sel.entry.base, sel.entry.vtable, sel.entry.klass, out);
    return out;
}

}  // namespace Discrete::Mono
