// IL2CPPAddressFinder.cpp — Phase 17a implementation.

#include "IL2CPPAddressFinder.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ManagedLayout.h"
#include "../shared/Memory.h"
#include "IL2CPPClassWalker.h"
#include "IL2CPPDomain.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"
#include "IL2CPPTypeParser.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <mutex>
#include <vector>

namespace UnityCEDumper::IL2CPP {

namespace {

// Cache of every Il2CppClass* observed during enumeration. Sorted ascending
// for O(log N) binary-search membership.
std::mutex                   g_cacheMutex;
std::vector<std::uintptr_t>  g_classSet;
std::uintptr_t               g_minKlass = 0;
std::uintptr_t               g_maxKlass = 0;
bool                         g_built    = false;

// SEH-protected name probe. il2cpp_class_get_name dereferences several layers
// of pointer state — when the candidate isn't a real Il2CppClass*, the chain
// faults at unpredictable depth. We catch the AV here so the worker thread
// keeps running.
#pragma warning(push)
#pragma warning(disable: 4509)
const char* SehClassNameProbe(fn_class_get_name fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassNamespaceProbe(fn_class_get_namespace fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const Il2CppImage* SehClassImageProbe(fn_class_get_image fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehImageNameProbe(fn_image_get_name fn, const Il2CppImage* img) {
    __try { return fn(img); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// Phase 17b — SEH-protected field-walk primitives. Used by ResolveFieldAt
// to enumerate a class's instance fields and compute each field's storage
// size without crashing on ill-formed metadata.
Il2CppClass* SehParentLookup(fn_class_get_parent fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
FieldInfo* SehNextFieldProbe(fn_class_get_fields fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehFieldNameProbe(fn_field_get_name fn, FieldInfo* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
std::size_t SehFieldOffsetProbe(fn_field_get_offset fn, FieldInfo* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
int SehFieldFlagsProbe(fn_field_get_flags fn, FieldInfo* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
const Il2CppType* SehFieldTypeProbe(fn_field_get_type fn, FieldInfo* f) {
    __try { return fn(f); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
Il2CppClass* SehClassFromTypeProbe(fn_class_from_il2cpp_type fn, const Il2CppType* t) {
    __try { return fn(t); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
int SehIsValueTypeProbe(fn_class_is_valuetype fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
int SehValueSizeProbe(fn_class_value_size fn, Il2CppClass* k) {
    __try { std::uint32_t a = 0; return fn(k, &a); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
// Phase 27c — array element class lookup. Non-null result means klass is
// an array type.
Il2CppClass* SehElementClassProbe(fn_class_get_element_class fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

bool ContainsClass(std::uintptr_t p) {
    if (g_classSet.empty()) return false;
    if (p < g_minKlass || p > g_maxKlass) return false;
    return std::binary_search(g_classSet.begin(), g_classSet.end(), p);
}

// Strip ".dll" suffix from image name so it round-trips against the
// EnumerateAssemblies output (which strips the suffix).
std::string StripDllSuffix(const std::string& s) {
    if (s.size() >= 4) {
        const std::string tail = s.substr(s.size() - 4);
        if (tail == ".dll" || tail == ".DLL") return s.substr(0, s.size() - 4);
    }
    return s;
}

// Phase 17b — resolved field row used by ResolveFieldAt. Same shape as
// FieldHit but without the offset-classifier yet (fillin happens after we
// pick a winner).
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

// Sizing rule for a primitive type by managed name. Used as a sanity check
// against class_value_size and a fallback when the type → class bridge
// isn't bound (older Unity / unusual builds).
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

// Walk the parent chain (depth-capped, mirrors walkInstance) and emit a flat
// list of every INSTANCE field in offset order, oldest-class fields first.
// Caller must hold a ThreadAttachGuard.
std::vector<ResolvedField> EnumerateInstanceFields(Il2CppClass* klass) {
    std::vector<ResolvedField> out;
    if (klass == nullptr) return out;
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_fields == nullptr) return out;

    std::vector<Il2CppClass*> chain;
    for (Il2CppClass* cur = klass; cur != nullptr && chain.size() < 16; ) {
        chain.push_back(cur);
        if (r.class_get_parent == nullptr) break;
        cur = SehParentLookup(r.class_get_parent, cur);
    }

    // Base class first so derived layout follows in memory order.
    for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
        Il2CppClass* cur = *it;
        std::string declaredIn = (r.class_get_name != nullptr)
            ? (SehClassNameProbe(r.class_get_name, cur) ?
               SehClassNameProbe(r.class_get_name, cur) : "")
            : "";

        void* iter = nullptr;
        for (;;) {
            FieldInfo* f = SehNextFieldProbe(r.class_get_fields, cur, &iter);
            if (f == nullptr) break;

            ResolvedField rf;
            rf.declaredIn = declaredIn;
            if (r.field_get_name != nullptr) {
                const char* n = SehFieldNameProbe(r.field_get_name, f);
                if (n != nullptr) rf.name = n;
            }
            if (r.field_get_offset != nullptr) {
                rf.offset = SehFieldOffsetProbe(r.field_get_offset, f);
            }
            if (r.field_get_flags != nullptr) {
                rf.flags = static_cast<std::uint32_t>(
                    SehFieldFlagsProbe(r.field_get_flags, f));
            }
            // Skip statics — they live outside the instance and would
            // otherwise pollute the offset match (their offsets are relative
            // to the static block, not the object base).
            constexpr std::uint32_t kStatic = 0x10u;
            if ((rf.flags & kStatic) != 0u) continue;

            const Il2CppType* ft = (r.field_get_type != nullptr)
                ? SehFieldTypeProbe(r.field_get_type, f) : nullptr;
            if (ft != nullptr) rf.typeName = TypeName(ft);

            // Determine size + value-type-ness from the field's runtime
            // class. Falls back to a primitive-name table when the
            // type → class bridge isn't bound; final fallback is 8.
            std::size_t sz = 0;
            if (ft != nullptr && r.class_from_il2cpp_type != nullptr) {
                Il2CppClass* fc = SehClassFromTypeProbe(r.class_from_il2cpp_type, ft);
                if (fc != nullptr) {
                    if (r.class_is_valuetype != nullptr) {
                        rf.isValueType =
                            SehIsValueTypeProbe(r.class_is_valuetype, fc) != 0;
                    }
                    // Detect enum (no `class_is_enum` export — every enum
                    // derives from System.Enum, same trick as ReadClassInfo).
                    if (r.class_get_parent != nullptr && r.class_get_name != nullptr) {
                        Il2CppClass* parent = SehParentLookup(r.class_get_parent, fc);
                        if (parent != nullptr) {
                            const char* pn = SehClassNameProbe(r.class_get_name, parent);
                            if (pn != nullptr && std::strcmp(pn, "Enum") == 0) {
                                rf.isEnum = true;
                                // Underlying type lives in the enum's
                                // `value__` instance field.
                                void* fit = nullptr;
                                for (;;) {
                                    FieldInfo* uf = SehNextFieldProbe(
                                        r.class_get_fields, fc, &fit);
                                    if (uf == nullptr) break;
                                    const char* un = (r.field_get_name != nullptr)
                                        ? SehFieldNameProbe(r.field_get_name, uf) : nullptr;
                                    if (un != nullptr && std::strcmp(un, "value__") == 0) {
                                        const Il2CppType* ut = (r.field_get_type != nullptr)
                                            ? SehFieldTypeProbe(r.field_get_type, uf)
                                            : nullptr;
                                        if (ut != nullptr) rf.underlyingTypeName = TypeName(ut);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (rf.isValueType && r.class_value_size != nullptr) {
                        int s = SehValueSizeProbe(r.class_value_size, fc);
                        if (s > 0) sz = static_cast<std::size_t>(s);
                    }
                }
            }
            if (sz == 0) {
                // Primitive-name fallback (covers BCL types when the
                // type → class bridge can't resolve the field).
                sz = SizeOfPrimitive(rf.typeName);
            }
            if (sz == 0) {
                // Reference type or unresolved type → pointer width.
                sz = 8;
            }
            rf.size = sz;
            out.push_back(std::move(rf));
        }
    }

    // Sort by offset so the contains-check + nearestField search are linear.
    std::sort(out.begin(), out.end(),
              [](const ResolvedField& a, const ResolvedField& b) {
                  return a.offset < b.offset;
              });
    return out;
}

// Pick the kind classifier for a matched field at the queried offset.
std::string ClassifyKind(const ResolvedField& rf, std::ptrdiff_t fieldOffset) {
    bool exact = (fieldOffset == static_cast<std::ptrdiff_t>(rf.offset));
    if (!exact) return "interior";
    if (rf.isEnum) return "leaf";
    if (rf.isValueType) {
        // Treat known primitive-shaped value types as "leaf" for caller
        // ergonomics (so a System.Int32 hit isn't reported as
        // "value-struct"). Anything else (Vector3, Color, Quaternion,
        // user struct …) → "value-struct".
        return TypeNameIsPrimitive(rf.typeName) ? "leaf" : "value-struct";
    }
    return "pointer";
}

// Project a ResolvedField + classifier back into the public FieldHit shape.
FieldHit MakeFieldHit(const ResolvedField& rf, std::ptrdiff_t fieldOffset, bool exact) {
    FieldHit fh;
    fh.present     = true;
    fh.name        = rf.name;
    fh.typeName    = rf.typeName;
    fh.offset      = rf.offset;
    fh.size        = rf.size;
    fh.flags       = rf.flags;
    fh.isStatic    = false;  // statics filtered out during enumeration
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

// Walks a class's full instance-field layout (parent chain included) and
// resolves the queried offset to either:
//   * A field whose [offset, offset+size) range contains the queried offset
//     — populated into `outField`, with `kind` classifier.
//   * The closest field whose offset <= queried offset — populated into
//     `outNearest`, with `outNearestDelta = queriedOffset - field.offset`.
// Both flags can be false if the class has no instance fields at all.
//
// Caller must hold a ThreadAttachGuard.
void ResolveFieldAt(Il2CppClass* klass, std::ptrdiff_t fieldOffset,
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
            // Prefer the first innermost match — ties broken by the smaller
            // size (more specific field wins). With static-field skip + size
            // fallback to 8 the typical case has no overlap.
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
                   "AddrFinder: BuildClassRangeCache: il2cpp not resolved");
        return false;
    }

    g_classSet.clear();
    g_minKlass = static_cast<std::uintptr_t>(-1);
    g_maxKlass = 0;

    auto assemblies = EnumerateAssemblies();
    for (const auto& a : assemblies) {
        if (a.image == nullptr) continue;
        auto classes = EnumerateClasses(a.image);
        for (const auto& c : classes) {
            if (c.klass == nullptr) continue;
            auto p = reinterpret_cast<std::uintptr_t>(c.klass);
            g_classSet.push_back(p);
            if (p < g_minKlass) g_minKlass = p;
            if (p > g_maxKlass) g_maxKlass = p;
        }
    }
    std::sort(g_classSet.begin(), g_classSet.end());
    g_classSet.erase(std::unique(g_classSet.begin(), g_classSet.end()),
                     g_classSet.end());
    if (g_classSet.empty()) {
        g_minKlass = 0;
        g_maxKlass = 0;
    }
    g_built = true;

    Log::Write(MagicStrings::LogCategoryWalk,
               "AddrFinder: cached " + std::to_string(g_classSet.size()) +
               " Il2CppClass* pointers, range [" +
               std::to_string(g_minKlass) + ", " + std::to_string(g_maxKlass) + "]");
    return !g_classSet.empty();
}

void InvalidateClassRangeCache() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    g_classSet.clear();
    g_minKlass = 0;
    g_maxKlass = 0;
    g_built    = false;
}

ClassRangeStats GetClassRangeStats() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    ClassRangeStats s;
    s.built      = g_built;
    s.classCount = g_classSet.size();
    s.minKlass   = g_minKlass;
    s.maxKlass   = g_maxKlass;
    return s;
}

AddressLookup WhatIsAt(std::uintptr_t addr, std::size_t maxBackwardScan) {
    AddressLookup out;
    out.queriedAddr = addr;

    if (addr == 0) {
        out.reason = "address is null";
        return out;
    }

    // Make sure the cache is ready. BuildClassRangeCache is idempotent.
    if (!BuildClassRangeCache()) {
        out.reason = "class_range_cache_unavailable";
        return out;
    }

    // 4 KB default per the spec; cap at 16 KB so a malformed request can't
    // pin the worker thread for long.
    if (maxBackwardScan == 0)       maxBackwardScan = 4096;
    if (maxBackwardScan > 16 * 1024) maxBackwardScan = 16 * 1024;

    // Round down to 8-byte boundary. Object headers are pointer-aligned.
    constexpr std::size_t kStride = 8;
    std::uintptr_t start = addr & ~static_cast<std::uintptr_t>(kStride - 1);

    // scannedFrom is the lowest address we'll try; scannedTo is `start + 8`
    // (one past the highest, exclusive) so the range matches the spec's
    // {from, to} convention.
    std::size_t maxSteps = maxBackwardScan / kStride;
    if (maxSteps == 0) maxSteps = 1;
    out.scannedFrom = (start >= maxSteps * kStride) ? (start - (maxSteps - 1) * kStride) : 0;
    out.scannedTo   = start + kStride;

    const auto& r = GetResolver();

    // We need a single thread-attach scope for the SEH-protected name probes
    // — every probe potentially calls back into managed-state code that
    // expects the thread to be attached.
    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) {
        out.reason = "no_il2cpp_domain";
        return out;
    }
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        out.reason = "il2cpp_thread_attach_failed";
        return out;
    }

    for (std::size_t i = 0; i < maxSteps; ++i) {
        std::uintptr_t p = start - i * kStride;
        out.scannedSteps = i + 1;

        // Read the candidate header qword. SafeReadBytes shields us from
        // unmapped pages; if the read fails, treat it as "scanned past the
        // start of the heap region" and stop.
        std::uintptr_t klassCandidate = 0;
        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(p),
                                   &klassCandidate, sizeof(klassCandidate))) {
            // Unmapped — keep walking. In practice, the heap allocator
            // keeps live objects in committed regions, so this almost
            // always means we walked off the start of a smaller commit.
            continue;
        }
        if (klassCandidate == 0) continue;
        if ((klassCandidate & 7u) != 0) continue;  // klass pointers are 8-aligned
        if (!ContainsClass(klassCandidate))  continue;

        // Range hit + alignment OK. Final filter: make sure the class object
        // still looks alive. il2cpp_class_get_name dereferences a chain of
        // metadata pointers, so an SEH catch is mandatory here.
        Il2CppClass* k = reinterpret_cast<Il2CppClass*>(klassCandidate);
        const char* name = (r.class_get_name != nullptr)
            ? SehClassNameProbe(r.class_get_name, k) : nullptr;
        if (name == nullptr || *name == '\0') continue;

        // Got it. Populate the result.
        out.found       = true;
        out.instance    = p;
        out.klassPtr    = klassCandidate;
        out.fieldOffset = static_cast<std::ptrdiff_t>(addr - p);
        out.className   = name;

        if (r.class_get_namespace != nullptr) {
            const char* nsStr = SehClassNamespaceProbe(r.class_get_namespace, k);
            if (nsStr != nullptr) out.ns = nsStr;
        }
        if (r.class_get_image != nullptr && r.image_get_name != nullptr) {
            const Il2CppImage* img = SehClassImageProbe(r.class_get_image, k);
            if (img != nullptr) {
                const char* imgName = SehImageNameProbe(r.image_get_name, img);
                if (imgName != nullptr) out.assembly = StripDllSuffix(imgName);
            }
        }

        // Phase 27c — array element resolution. If klass is an array
        // type, compute the element index from the queried offset and
        // report it as an array-element instead of falling through to
        // declared-field resolution (arrays have no user-declared fields).
        bool resolvedAsArray = false;
        if (r.class_get_element_class != nullptr) {
            Il2CppClass* elemClass = SehElementClassProbe(r.class_get_element_class, k);
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

                    std::ptrdiff_t into     = out.fieldOffset - dataOff;
                    std::size_t    elemIdx  = static_cast<std::size_t>(into) / elemSize;
                    std::size_t    interior = static_cast<std::size_t>(into) % elemSize;

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

        // Phase 17b — resolve which declared field contains the queried
        // offset. Reuses the same ThreadAttachGuard scope so the runtime
        // calls inside EnumerateInstanceFields don't have to re-attach.
        if (!resolvedAsArray) {
            ResolveFieldAt(k, out.fieldOffset, out.field, out.nearestField,
                           out.nearestFieldDelta);
        }
        return out;
    }

    out.reason = "no_klass_in_range";
    return out;
}

}  // namespace UnityCEDumper::IL2CPP
