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
#include <atomic>
#include <cstring>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace Discrete::IL2CPP {

namespace {

// Phase 27 follow-up — heap-leaked Cache so process-exit doesn't iterate
// 10K class-pointer destructors. Mirror of the same pattern in
// MonoAddressFinder.cpp. Scalars stay direct (trivial destruction).
struct Cache {
    std::mutex                   mu;
    std::vector<std::uintptr_t>  classSet;
};
Cache& cache() {
    static Cache* p = new Cache();   // intentional leak; Windows reclaims at exit
    return *p;
}
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
// Phase 27d follow-up — walk declaring-type chain to support nested
// classes (Outer/Inner). Returns the outer class via `out`. Used to
// build the slash-form name that FindClass + walkInstance expect.
Il2CppClass* SehClassDeclaringProbe(fn_class_get_declaring fn, Il2CppClass* k) {
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
    if (cache().classSet.empty()) return false;
    if (p < g_minKlass || p > g_maxKlass) return false;
    return std::binary_search(cache().classSet.begin(), cache().classSet.end(), p);
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
    // Phase 31-5 — struct class of an inline value-type (non-enum) field, so a
    // hit landing inside it can be named to the component (`_position.y`).
    Il2CppClass*  valueTypeClass = nullptr;
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
                    // Phase 31-5 — remember the struct class for value-type
                    // (non-enum, NON-primitive) fields so an interior hit names
                    // its component. Primitives MUST stay null: their internal
                    // self-typed field would make NameStructComponent recurse
                    // forever on an unchanged interior offset (see Mono twin).
                    if (rf.isValueType && !rf.isEnum && !TypeNameIsPrimitive(rf.typeName))
                        rf.valueTypeClass = fc;
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

// Phase 31-5 — promote an interior hit on an inline value-type struct field to
// the specific component (`_position.y`). Mirror of the Mono twin: struct member
// offsets are header-inclusive, so struct-relative = member.offset −
// kObjectHeader matches the queried interior offset. Rewrites `fh` in place;
// recurses for nested struct members, depth-capped against any repeating-class
// chain. Caller holds the ThreadAttachGuard.
// Core of the struct-member-by-offset resolution, shared by the inline
// value-type FIELD path (NameStructComponent) and the value-type ARRAY-ELEMENT
// path (ResolveArrayElementSafe). Given a value-type struct class and an
// `interior` byte offset WITHIN the struct (0 = struct base), find the member
// containing it and rewrite `fh`: name = namePrefix + member ("_position." +
// "y", or "[3]." + "count_"), plus type/size/kind/interiorOffset. `baseOffset`
// is the struct's absolute offset within the containing object so fh.offset
// stays absolute. Recurses one level per nested value struct, depth-capped.
// PRECONDITION: `structClass` is a validated real Il2CppClass; caller holds a
// ThreadAttachGuard.
void NameValueStructMember(Il2CppClass* structClass, std::ptrdiff_t interior,
                           const std::string& namePrefix, std::ptrdiff_t baseOffset,
                           FieldHit& fh, int depthLeft = 4) {
    if (depthLeft <= 0 || structClass == nullptr || interior <= 0) return;
    const long hdr = static_cast<long>(ManagedLayout::kObjectHeader);
    auto members = EnumerateInstanceFields(structClass);
    for (const auto& m : members) {
        const std::ptrdiff_t rel = static_cast<std::ptrdiff_t>(m.offset) - hdr;
        if (rel < 0) continue;
        if (interior < rel || interior >= rel + static_cast<std::ptrdiff_t>(m.size)) continue;
        const std::ptrdiff_t absOffset = baseOffset + rel;
        fh.name        = namePrefix + m.name;
        fh.typeName    = m.typeName;
        fh.offset      = static_cast<std::size_t>(absOffset);
        fh.size        = m.size;
        fh.isValueType = m.isValueType;
        fh.isEnum      = m.isEnum;
        fh.underlyingTypeName = m.underlyingTypeName;
        const std::ptrdiff_t memberInterior = interior - rel;
        fh.kind = (memberInterior == 0)
            ? ClassifyKind(m, static_cast<std::ptrdiff_t>(m.offset))
            : "interior";
        fh.interiorOffset = memberInterior;
        if (memberInterior > 0 && m.isValueType && !m.isEnum &&
            m.valueTypeClass != nullptr) {
            NameValueStructMember(m.valueTypeClass, memberInterior,
                                  fh.name + ".", absOffset, fh, depthLeft - 1);
        }
        return;
    }
}

void NameStructComponent(const ResolvedField& parent, std::ptrdiff_t fieldOffset,
                         FieldHit& fh, int depthLeft = 4) {
    if (!parent.isValueType || parent.isEnum || parent.valueTypeClass == nullptr) return;
    const std::ptrdiff_t interior = fieldOffset - static_cast<std::ptrdiff_t>(parent.offset);
    if (interior <= 0) return;   // hit at the struct base → keep value-struct kind
    NameValueStructMember(parent.valueTypeClass, interior, parent.name + ".",
                          static_cast<std::ptrdiff_t>(parent.offset), fh, depthLeft);
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
    // Phase 31-5 — name the struct component on an interior value-struct hit.
    if (exact) NameStructComponent(rf, fieldOffset, fh);
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

// ---------------------------------------------------------------------------
// Value-type ARRAY-element reverse lookup (safe). Runtime-synthesized array
// klasses (e.g. ItemInfo[], the List<T>._items backing array) are NOT in the
// metadata classSet, so the reverse-lookup index drops hits that land inside
// them. To recover those WITHOUT the fatal mistake of calling il2cpp class
// APIs on unvalidated heap pointers (which CTD'd the game), we:
//   (1) dynamically calibrate the offset of Il2CppClass::element_class once,
//       using only classSet-validated classes + the bound API;
//   (2) at scan time, READ that field off a candidate via SafeReadBytes
//       (VirtualQuery+SEH-guarded, garbage-safe) and only trust the candidate
//       as an array klass if the read points at a real classSet member;
//   (3) run every subsequent API call on that validated ELEMENT class only —
//       never on the array klass itself.
// ---------------------------------------------------------------------------

std::once_flag g_ecOnce;
std::size_t    g_ecOffset = 0;   // offsetof(Il2CppClass, element_class); 0 = failed

// Discover offsetof(Il2CppClass, element_class) by the same brute-force-find +
// cache recipe as IL2CPPStaticFieldsOffset.cpp. For a NON-array class libil2cpp
// sets element_class == self, so we scan classSet samples for the qword equal
// to the class pointer and intersect the matching offsets across samples; the
// lowest stable offset is element_class (castClass sits one slot higher and,
// for szarrays, equals element_class anyway — so either is safe; lowest is
// canonical). All API calls here are on classSet-validated pointers.
void DiscoverElementClassOffset() {
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_element_class == nullptr) return;
    if (!BuildClassRangeCache()) return;
    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) return;
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) return;

    constexpr std::size_t kScanLimit     = 512;
    constexpr std::size_t kStep          = sizeof(void*);
    constexpr std::size_t kSamplesWanted = 16;

    std::vector<std::size_t> candidates;   // self-holding offsets, ascending
    bool seeded = false;
    std::size_t sampled = 0;

    auto assemblies = EnumerateAssemblies();
    for (const auto& a : assemblies) {
        if (a.image == nullptr) continue;
        auto classes = EnumerateClasses(a.image);
        for (const auto& ce : classes) {
            if (ce.klass == nullptr) continue;
            const std::uintptr_t self = reinterpret_cast<std::uintptr_t>(ce.klass);
            // Clean non-array sample: API returns the class itself.
            if (SehElementClassProbe(r.class_get_element_class, ce.klass) != ce.klass) continue;
            const auto* bytes = reinterpret_cast<const std::uint8_t*>(ce.klass);
            std::vector<std::size_t> hits;
            for (std::size_t off = 0; off + kStep <= kScanLimit; off += kStep) {
                std::uintptr_t v = 0;
                if (!Memory::SafeReadBytes(bytes + off, &v, sizeof(v))) break;
                if (v == self) hits.push_back(off);
            }
            if (hits.empty()) continue;
            if (!seeded) { candidates = std::move(hits); seeded = true; }
            else {
                std::vector<std::size_t> next;
                for (std::size_t o : candidates)
                    if (std::find(hits.begin(), hits.end(), o) != hits.end()) next.push_back(o);
                candidates.swap(next);
            }
            if (seeded && candidates.empty()) {  // inconsistent — give up safely
                Log::Write(MagicStrings::LogCategoryWalk,
                    "AddrFinder: element_class offset discovery inconsistent — array reverse-lookup disabled");
                return;
            }
            if (++sampled >= kSamplesWanted) break;
        }
        if (sampled >= kSamplesWanted) break;
    }

    if (!candidates.empty()) {
        g_ecOffset = candidates.front();   // lowest = element_class
        Log::Write(MagicStrings::LogCategoryWalk,
            "AddrFinder: discovered element_class offset = " + std::to_string(g_ecOffset) +
            " (from " + std::to_string(sampled) + " samples)");
    } else {
        Log::Write(MagicStrings::LogCategoryWalk,
            "AddrFinder: element_class offset discovery failed — array reverse-lookup disabled");
    }
}

std::size_t GetElementClassOffset() {
    std::call_once(g_ecOnce, DiscoverElementClassOffset);
    return g_ecOffset;
}

// Memo of confirmed value-type-element array klasses → their element class.
struct ArrayKlassEntry { std::uintptr_t arrayKlass; std::uintptr_t elemKlass; };
std::mutex                    g_arrayKlassMu;
std::vector<ArrayKlassEntry>  g_arrayKlassSet;   // sorted by arrayKlass

bool LookupArrayKlassMemo(std::uintptr_t k, std::uintptr_t& elemOut) {
    std::lock_guard<std::mutex> lk(g_arrayKlassMu);
    auto it = std::lower_bound(g_arrayKlassSet.begin(), g_arrayKlassSet.end(), k,
        [](const ArrayKlassEntry& e, std::uintptr_t v) { return e.arrayKlass < v; });
    if (it != g_arrayKlassSet.end() && it->arrayKlass == k) { elemOut = it->elemKlass; return true; }
    return false;
}
void RememberArrayKlass(std::uintptr_t k, std::uintptr_t elem) {
    std::lock_guard<std::mutex> lk(g_arrayKlassMu);
    auto it = std::lower_bound(g_arrayKlassSet.begin(), g_arrayKlassSet.end(), k,
        [](const ArrayKlassEntry& e, std::uintptr_t v) { return e.arrayKlass < v; });
    if (it == g_arrayKlassSet.end() || it->arrayKlass != k)
        g_arrayKlassSet.insert(it, ArrayKlassEntry{k, elem});
}

// NEGATIVE memo — header qword values proven NOT to be value-type-element array
// klasses. This is the perf-critical lever: the array fallback runs a backward
// scan per index-dropped candidate (tens of thousands), and adjacent candidates'
// windows overlap heavily, so the SAME header values recur constantly. Without a
// negative memo every recurrence re-pays the VirtualQuery-backed element_class
// read → a 300s scan. Keyed by VALUE (hash set, O(1)); shared across the whole
// scan so each distinct value is probed at most once. Hard-capped so total probes
// are bounded: once full, unknowns are treated as non-arrays (feature degrades to
// already-known array klasses only — never hangs).
constexpr std::size_t        kNotArrayCap = 1u << 19;   // 524288 distinct values
std::mutex                   g_notArrayMu;
std::unordered_set<std::uintptr_t> g_notArrayKlass;

bool IsKnownNotArray(std::uintptr_t k) {
    std::lock_guard<std::mutex> lk(g_notArrayMu);
    return g_notArrayKlass.find(k) != g_notArrayKlass.end();
}
// Returns false once the cap is hit (signals "stop probing unknowns").
bool RememberNotArray(std::uintptr_t k) {
    std::lock_guard<std::mutex> lk(g_notArrayMu);
    if (g_notArrayKlass.size() >= kNotArrayCap) return false;
    g_notArrayKlass.insert(k);
    return true;
}

// Discard the array-klass memos (called from InvalidateClassRangeCache so a
// `scan` RPC that loads new assemblies doesn't keep stale verdicts).
void ResetArrayKlassMemos() {
    { std::lock_guard<std::mutex> lk(g_arrayKlassMu); g_arrayKlassSet.clear(); }
    { std::lock_guard<std::mutex> lk(g_notArrayMu);   g_notArrayKlass.clear(); }
}

// SAFE array-klass test: decide whether `candidate` is an array klass whose
// element type is a known value type — using ONLY a raw SafeReadBytes of the
// calibrated element_class field. No runtime API call ever touches `candidate`.
// On success, `elemOut` is the (classSet-validated) element class.
bool LooksLikeValueTypeElementArray(std::uintptr_t candidate, Il2CppClass*& elemOut) {
    if (candidate == 0) return false;
    std::uintptr_t memoElem = 0;
    if (LookupArrayKlassMemo(candidate, memoElem)) {      // known array → O(log n)
        elemOut = reinterpret_cast<Il2CppClass*>(memoElem);
        return true;
    }
    if (IsKnownNotArray(candidate)) return false;          // known non-array → O(1), no probe
    const std::size_t O = GetElementClassOffset();
    if (O == 0) return false;
    // First sighting of this value → one element_class read (the expensive op,
    // now amortized by the negative memo across the whole scan).
    std::uintptr_t elemPtr = 0;
    bool isArray = false;
    if (Memory::SafeReadBytes(reinterpret_cast<const void*>(candidate + O),
                              &elemPtr, sizeof(elemPtr)) &&
        elemPtr != 0 && elemPtr != candidate &&            // non-array (self)
        ContainsClass(elemPtr)) {                          // element must be a real class
        Il2CppClass* elem = reinterpret_cast<Il2CppClass*>(elemPtr);
        const auto& r = GetResolver();
        if (r.class_is_valuetype != nullptr &&
            SehIsValueTypeProbe(r.class_is_valuetype, elem) != 0) {   // value-type only
            elemOut = elem;
            RememberArrayKlass(candidate, elemPtr);
            isArray = true;
        }
    }
    if (!isArray) RememberNotArray(candidate);             // don't re-probe this value
    return isArray;
}

// Fill `out` for a hit at `addr` inside the array object based at `p`, whose
// element class is the VALIDATED `elemClass`. Uses only `elemClass` for every
// API call (never the unvalidated array klass). Caller holds a ThreadAttachGuard.
void ResolveArrayElementSafe(std::uintptr_t addr, std::uintptr_t p,
                             Il2CppClass* elemClass, AddressLookup& out) {
    const auto& r = GetResolver();
    out.found       = true;
    out.instance    = p;
    out.klassPtr    = 0;   // array klass deliberately not exposed (unvalidated)
    out.fieldOffset = static_cast<std::ptrdiff_t>(addr - p);

    std::string elemName;
    if (r.class_get_name != nullptr) {
        const char* n = SehClassNameProbe(r.class_get_name, elemClass);
        if (n != nullptr) elemName = n;
    }
    out.className = elemName.empty() ? "[]" : (elemName + "[]");
    if (r.class_get_namespace != nullptr) {
        const char* ns = SehClassNamespaceProbe(r.class_get_namespace, elemClass);
        if (ns != nullptr) out.ns = ns;
    }
    if (r.class_get_image != nullptr && r.image_get_name != nullptr) {
        const Il2CppImage* img = SehClassImageProbe(r.class_get_image, elemClass);
        if (img != nullptr) {
            const char* in = SehImageNameProbe(r.image_get_name, img);
            if (in != nullptr) out.assembly = StripDllSuffix(in);
        }
    }

    const std::ptrdiff_t dataOff = static_cast<std::ptrdiff_t>(ManagedLayout::kArrayDataOffset);
    if (out.fieldOffset < dataOff) {   // hit in the array header (length/bounds)
        out.field.present = true;
        out.field.name    = "(array header)";
        out.field.kind    = "array-interior";
        return;
    }
    std::size_t elemSize = ManagedLayout::kPointerSize;
    if (r.class_value_size != nullptr) {
        int sz = SehValueSizeProbe(r.class_value_size, elemClass);
        if (sz > 0) elemSize = static_cast<std::size_t>(sz);
    }
    const std::ptrdiff_t into     = out.fieldOffset - dataOff;
    const std::size_t    idx      = static_cast<std::size_t>(into) / elemSize;
    const std::size_t    interior = static_cast<std::size_t>(into) % elemSize;

    out.field.present     = true;
    out.field.name        = "[" + std::to_string(idx) + "]";
    out.field.offset      = static_cast<std::size_t>(dataOff) + idx * elemSize;
    out.field.size        = elemSize;
    out.field.isValueType = true;
    out.field.kind        = (interior == 0) ? "array-element" : "array-interior";
    out.field.interiorOffset = static_cast<std::ptrdiff_t>(interior);
    out.field.typeName    = elemName;
    if (interior > 0) {
        NameValueStructMember(elemClass, static_cast<std::ptrdiff_t>(interior),
                              "[" + std::to_string(idx) + "].",
                              dataOff + static_cast<std::ptrdiff_t>(idx * elemSize),
                              out.field);
    }
}

}  // namespace

bool BuildClassRangeCache() {
    std::lock_guard<std::mutex> lk(cache().mu);
    if (g_built) return !cache().classSet.empty();

    if (!IsResolved()) {
        Log::Write(MagicStrings::LogCategoryWalk,
                   "AddrFinder: BuildClassRangeCache: il2cpp not resolved");
        return false;
    }

    cache().classSet.clear();
    g_minKlass = static_cast<std::uintptr_t>(-1);
    g_maxKlass = 0;

    auto assemblies = EnumerateAssemblies();
    for (const auto& a : assemblies) {
        if (a.image == nullptr) continue;
        auto classes = EnumerateClasses(a.image);
        for (const auto& c : classes) {
            if (c.klass == nullptr) continue;
            auto p = reinterpret_cast<std::uintptr_t>(c.klass);
            cache().classSet.push_back(p);
            if (p < g_minKlass) g_minKlass = p;
            if (p > g_maxKlass) g_maxKlass = p;
        }
    }
    std::sort(cache().classSet.begin(), cache().classSet.end());
    cache().classSet.erase(std::unique(cache().classSet.begin(), cache().classSet.end()),
                     cache().classSet.end());
    if (cache().classSet.empty()) {
        g_minKlass = 0;
        g_maxKlass = 0;
    }
    g_built = true;

    Log::Write(MagicStrings::LogCategoryWalk,
               "AddrFinder: cached " + std::to_string(cache().classSet.size()) +
               " Il2CppClass* pointers, range [" +
               std::to_string(g_minKlass) + ", " + std::to_string(g_maxKlass) + "]");
    return !cache().classSet.empty();
}

void InvalidateClassRangeCache() {
    std::lock_guard<std::mutex> lk(cache().mu);
    cache().classSet.clear();
    g_minKlass = 0;
    g_maxKlass = 0;
    g_built    = false;
    // Drop the array-klass verdicts too — a new assembly load can change which
    // pointers are classSet members. (element_class offset is struct-stable, so
    // g_ecOffset/g_ecOnce are intentionally left alone.)
    ResetArrayKlassMemos();
}

ClassRangeStats GetClassRangeStats() {
    std::lock_guard<std::mutex> lk(cache().mu);
    ClassRangeStats s;
    s.built      = g_built;
    s.classCount = cache().classSet.size();
    s.minKlass   = g_minKlass;
    s.maxKlass   = g_maxKlass;
    return s;
}

// Phase 30 P2 — shared field/identity resolution, factored out of WhatIsAt's
// tail so the backward-scan path (WhatIsAt) and the interval-tree reverse-lookup
// path produce IDENTICAL results. Given a confirmed managed instance — base
// pointer `instance` + its validated Il2CppClass* `klassPtr` — and the queried
// `addr`, populate `out`: found flag, instance/klassPtr/fieldOffset, the
// declaring-type chain (className / ns / assembly), and the array-element or
// declared-field resolution. Mirrors WhatIsAt steps 5-8 verbatim.
//
// PRECONDITION: the caller holds a ThreadAttachGuard for the IL2CPP domain —
// the SEH-protected name/declaring/namespace/image probes call back into
// managed-state code that expects an attached thread. Does NOT touch
// scannedFrom/To/Steps (those are the backward-scan's diagnostics; the
// interval-tree path leaves them at their defaults).
void ResolveInstanceLookup(std::uintptr_t addr, std::uintptr_t instance,
                           std::uintptr_t klassPtr, AddressLookup& out) {
    const auto& r = GetResolver();
    Il2CppClass* k = reinterpret_cast<Il2CppClass*>(klassPtr);
    std::uintptr_t p = instance;

    out.found       = true;
    out.instance    = p;
    out.klassPtr    = klassPtr;
    out.fieldOffset = static_cast<std::ptrdiff_t>(addr - p);

    // Walk declaring-type chain so nested classes round-trip cleanly back
    // through walkInstance / FindClass (same logic as
    // IL2CPPClassWalker::DescribeClass).
    const char* name = (r.class_get_name != nullptr)
        ? SehClassNameProbe(r.class_get_name, k) : nullptr;
    std::string leaf = (name != nullptr) ? name : "";
    Il2CppClass* outer = k;
    if (r.class_get_declaring != nullptr) {
        Il2CppClass* cur = k;
        for (int depth = 0; depth < 16; ++depth) {
            Il2CppClass* d = SehClassDeclaringProbe(r.class_get_declaring, cur);
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

    // Namespace comes from the OUTER class (nested types have empty ns).
    if (r.class_get_namespace != nullptr) {
        const char* nsStr = SehClassNamespaceProbe(r.class_get_namespace, outer);
        if (nsStr != nullptr) out.ns = nsStr;
    }
    if (r.class_get_image != nullptr && r.image_get_name != nullptr) {
        const Il2CppImage* img = SehClassImageProbe(r.class_get_image, k);
        if (img != nullptr) {
            const char* imgName = SehImageNameProbe(r.image_get_name, img);
            if (imgName != nullptr) out.assembly = StripDllSuffix(imgName);
        }
    }

    // Phase 27c — array element resolution. If klass is an array type, compute
    // the element index from the queried offset and report it as an array-
    // element instead of falling through to declared-field resolution.
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

    // Phase 17b — resolve which declared field contains the queried offset.
    if (!resolvedAsArray) {
        ResolveFieldAt(k, out.fieldOffset, out.field, out.nearestField,
                       out.nearestFieldDelta);
    }
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

    // Phase 27 perf — bulk-read the entire backward range in 4KB chunks
    // instead of one SafeReadBytes per 8-byte step. Cuts ~2K VirtualQuery
    // syscalls down to ~4 per WhatIsAt call. On a 500-result global
    // scan, end-to-end goes from ~16s to ~4-5s.
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

        // Read the candidate header qword from the pre-read buffer.
        // Unreadable pages were zero-filled above so the alignment / non-
        // null filter below silently skips them.
        const std::size_t bufIdx = (maxSteps - 1 - i) * kStride;
        std::uintptr_t klassCandidate = 0;
        std::memcpy(&klassCandidate, bulkBuf.data() + bufIdx, sizeof(klassCandidate));
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

        // Got it — instance base `p` + validated klass `klassCandidate`.
        // Identity + field/array resolution (steps 5-8) is shared with the
        // Phase 30 P2 interval-tree path so both produce identical answers.
        // We already hold the ThreadAttachGuard (the helper's precondition).
        ResolveInstanceLookup(addr, p, klassCandidate, out);
        return out;
    }

    out.reason = "no_klass_in_range";
    return out;
}

AddressLookup WhatIsAtAcceptingArrays(std::uintptr_t addr, std::size_t maxBackwardScan) {
    // Superset of WhatIsAt: also accepts value-type-element ARRAY objects
    // (int[], ItemInfo[], List<T>._items) whose runtime array klass is absent
    // from classSet. SAFE — an unvalidated candidate is only ever touched via
    // SafeReadBytes (see LooksLikeValueTypeElementArray); all runtime API calls
    // run on the validated element class inside ResolveArrayElementSafe.
    AddressLookup out;
    out.queriedAddr = addr;
    if (addr == 0) { out.reason = "address is null"; return out; }
    if (!BuildClassRangeCache()) { out.reason = "class_range_cache_unavailable"; return out; }

    if (maxBackwardScan == 0)        maxBackwardScan = 4096;
    if (maxBackwardScan > 16 * 1024) maxBackwardScan = 16 * 1024;
    // Perf: the array base sits just below the hit (element offset = idx*stride),
    // so a small window finds it for the low-index elements worth resolving, and
    // keeps this per-dropped-candidate scan cheap (it runs tens of thousands of
    // times). Large-index elements in huge arrays are intentionally not resolved.
    constexpr std::size_t kArrayFallbackWindow = 1024;
    if (maxBackwardScan > kArrayFallbackWindow) maxBackwardScan = kArrayFallbackWindow;

    constexpr std::size_t  kStride = 8;
    constexpr std::uintptr_t kMaxPlausibleArrayLen = 0x4000000;  // 64M elements
    std::uintptr_t start = addr & ~static_cast<std::uintptr_t>(kStride - 1);
    std::size_t maxSteps = maxBackwardScan / kStride;
    if (maxSteps == 0) maxSteps = 1;
    out.scannedFrom = (start >= maxSteps * kStride) ? (start - (maxSteps - 1) * kStride) : 0;
    out.scannedTo   = start + kStride;

    const auto& r = GetResolver();
    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) { out.reason = "no_il2cpp_domain"; return out; }
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { out.reason = "il2cpp_thread_attach_failed"; return out; }

    const std::size_t bulkBytes = maxSteps * kStride;
    const std::uintptr_t rangeStart = start >= (maxSteps - 1) * kStride
        ? start - (maxSteps - 1) * kStride : 0;
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

        const std::size_t bufIdx = (maxSteps - 1 - i) * kStride;   // == p - rangeStart
        std::uintptr_t klassCandidate = 0;
        std::memcpy(&klassCandidate, bulkBuf.data() + bufIdx, sizeof(klassCandidate));
        if (klassCandidate == 0) continue;
        if ((klassCandidate & 7u) != 0) continue;

        if (ContainsClass(klassCandidate)) {
            // Known metadata class — identical to WhatIsAt.
            Il2CppClass* k = reinterpret_cast<Il2CppClass*>(klassCandidate);
            const char* name = (r.class_get_name != nullptr)
                ? SehClassNameProbe(r.class_get_name, k) : nullptr;
            if (name == nullptr || *name == '\0') continue;
            ResolveInstanceLookup(addr, p, klassCandidate, out);
            return out;
        }

        // Not a known class — try it as a value-type-element array. Cheap
        // structural gate from the bulk buffer first (sane length at
        // p+kArrayMaxLengthOffset) so the VirtualQuery-backed element-class
        // read only fires on array-shaped headers.
        const std::size_t lenIdx = bufIdx + ManagedLayout::kArrayMaxLengthOffset;
        if (lenIdx + sizeof(std::uintptr_t) > bulkBytes) continue;
        std::uintptr_t arrLen = 0;
        std::memcpy(&arrLen, bulkBuf.data() + lenIdx, sizeof(arrLen));
        if (arrLen == 0 || arrLen > kMaxPlausibleArrayLen) continue;

        Il2CppClass* elem = nullptr;
        if (!LooksLikeValueTypeElementArray(klassCandidate, elem)) continue;
        ResolveArrayElementSafe(addr, p, elem, out);
        return out;
    }

    out.reason = "no_klass_in_range";
    return out;
}

// ---------------------------------------------------------------------------
// Phase 29 — wide-heap "every instance of every known class" scan.
// ---------------------------------------------------------------------------
//
// Visits each 8-aligned qword in committed RW non-image non-stack memory
// exactly once. For each qword that's a known Il2CppClass* (binary search
// the same `classSet` WhatIsAt uses), record (klass, instance addr) into
// the per-class map. Per-class cap stops a singleton-leak class from
// consuming the whole budget.
//
// Filters mirror Memory::ScanForPointer exactly so module .data / stack
// residue don't pollute the result (they hold class-pointer values for
// other legitimate reasons — RGCTX cache, stale locals).
//
// SEH-protected per-region scan: inner loop sits inside __try so an AV
// inside an AC-stripped page only loses that region, not the whole walk.

namespace {

#pragma warning(push)
#pragma warning(disable: 4509)  // SEH in function with non-trivial dtor — none here.

void SehSweepRegion(
    const std::uint8_t* base, std::size_t size,
    std::uintptr_t minKlass, std::uintptr_t maxKlass,
    const std::vector<std::uintptr_t>& classSet,
    std::unordered_map<Il2CppClass*, std::vector<std::uintptr_t>>& byClass,
    std::size_t perClassCap,
    std::size_t& totalInstances,
    std::size_t& qwordsScanned) noexcept
{
    __try {
        const std::size_t count = size / sizeof(std::uintptr_t);
        const std::uintptr_t* arr = reinterpret_cast<const std::uintptr_t*>(base);
        for (std::size_t i = 0; i < count; ++i) {
            ++qwordsScanned;
            std::uintptr_t qw = arr[i];
            if (qw == 0) continue;
            if (qw < minKlass || qw > maxKlass) continue;
            if ((qw & 7u) != 0) continue;
            if (!std::binary_search(classSet.begin(), classSet.end(), qw)) continue;

            // 2026-05-28 — false-positive filter on the instance slot,
            // mirror of the Mono finder. Real Il2CppObjects are 8-byte
            // aligned. The 4-byte loop stride on x86 (IL2CPP doesn't
            // ship there today, but the code is bitness-symmetric)
            // can land on 4-byte-aligned-but-not-8-byte-aligned
            // qwords that happen to equal a class pointer — those are
            // structurally not object headers and would only add
            // noise to the per-class cap.
            const std::uintptr_t instAddr =
                reinterpret_cast<std::uintptr_t>(base + i * sizeof(std::uintptr_t));
            if ((instAddr & 7u) != 0) continue;

            auto* k = reinterpret_cast<Il2CppClass*>(qw);
            auto& list = byClass[k];
            if (list.size() < perClassCap) {
                list.push_back(instAddr);
                ++totalInstances;
            }
            // Else: cap hit for this class — count the qword as scanned
            // but don't grow the vector. The user-visible result is "this
            // class had at least `perClassCap` live instances".
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Region had an unreadable page partway through; the bytes
        // already pushed stay in the map. Move on to the next region.
    }
}

// Phase 30 P2 — flat (uncapped) variant of SehSweepRegion for the reverse-
// lookup index. Same membership test + 8-aligned instance filter, but records
// every hit as a flat (instanceAddr, klass) pair instead of a per-class map —
// the index needs ALL instances (no cap) to answer "nearest base ≤ addr".
#pragma warning(push)
#pragma warning(disable: 4509)
void SehSweepRegionFlat(
    const std::uint8_t* base, std::size_t size,
    std::uintptr_t minKlass, std::uintptr_t maxKlass,
    const std::vector<std::uintptr_t>& classSet,
    std::vector<std::pair<std::uintptr_t, std::uintptr_t>>& out,
    std::size_t& qwordsScanned) noexcept
{
    __try {
        const std::size_t count = size / sizeof(std::uintptr_t);
        const std::uintptr_t* arr = reinterpret_cast<const std::uintptr_t*>(base);
        for (std::size_t i = 0; i < count; ++i) {
            ++qwordsScanned;
            std::uintptr_t qw = arr[i];
            if (qw == 0) continue;
            if (qw < minKlass || qw > maxKlass) continue;
            if ((qw & 7u) != 0) continue;
            if (!std::binary_search(classSet.begin(), classSet.end(), qw)) continue;
            const std::uintptr_t instAddr =
                reinterpret_cast<std::uintptr_t>(base + i * sizeof(std::uintptr_t));
            if ((instAddr & 7u) != 0) continue;
            out.emplace_back(instAddr, qw);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Unreadable page partway through — keep what we have, move on.
    }
}
#pragma warning(pop)

}  // namespace

BulkInstancesResult FindAllInstances(std::size_t perClassCap) {
    BulkInstancesResult out;
    if (perClassCap == 0) perClassCap = 100;
    // Raised 4096 → 8192 on 2026-05-27 in lockstep with the Mono finder
    // + ExportAPI snapshotBegin ceiling raise.
    if (perClassCap > 8192) perClassCap = 8192;

    // Cache is mandatory — without it we have no class-pointer set to
    // binary-search against.
    if (!BuildClassRangeCache()) {
        Log::Write(MagicStrings::LogCategoryScan,
                   "Snapshot: FindAllInstances aborted — class range cache unavailable");
        return out;
    }

    // Snapshot the class set under the cache lock, then release. The
    // wide heap walk takes seconds; we don't want to hold the cache
    // mutex across it (would block any concurrent WhatIsAt).
    std::vector<std::uintptr_t> classSet;
    std::uintptr_t minKlass = 0;
    std::uintptr_t maxKlass = 0;
    {
        std::lock_guard<std::mutex> lk(cache().mu);
        classSet = cache().classSet;   // copy — typically ~10K entries × 8 B = 80 KB
        minKlass = g_minKlass;
        maxKlass = g_maxKlass;
    }
    if (classSet.empty()) return out;

    constexpr DWORD kRW =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

    // --- Serial pass: filter regions + region-level stats + chunk worklist ---
    auto regions = Memory::EnumerateCommittedRegions();
    auto stacks  = Memory::CollectThreadStackRanges();

    std::vector<std::pair<const std::uint8_t*, std::size_t>> accepted;
    accepted.reserve(regions.size());
    for (const auto& r : regions) {
        if ((r.protect & kRW) == 0) continue;
        out.regionsTotal++;
        // Skip MEM_IMAGE (.data/.bss class tables) and MEM_MAPPED (file-backed
        // game data holding stray Il2CppClass* values — noise). IL2CPP managed
        // heap is VirtualAlloc'd → MEM_PRIVATE.
        if (r.type == MEM_IMAGE || r.type == MEM_MAPPED) {
            out.regionsSkipImage++;
            continue;
        }
        const std::uint8_t* regionEnd = r.base + r.size;
        bool onStack = false;
        for (const auto& s : stacks) {
            if (s.low >= regionEnd) break;
            if (s.high > r.base && s.low < regionEnd) { onStack = true; break; }
        }
        if (onStack) { out.regionsSkipStack++; continue; }
        out.regionsScanned++;
        out.bytesScanned += r.size;
        accepted.emplace_back(r.base, r.size);
    }

    // Class pointers sit at 8-aligned object headers; split accepted regions
    // into 8-aligned 1 MB chunks. elemSize == stride == 8 so a qword never
    // straddles a chunk seam — each owned by exactly one chunk (no overlap).
    struct Chunk { const std::uint8_t* base; std::size_t size; };
    constexpr std::size_t kChunk = 1u * 1024u * 1024u;   // multiple of 8
    std::vector<Chunk> chunks;
    for (const auto& [rb, rs] : accepted) {
        for (std::size_t off = 0; off < rs; off += kChunk) {
            chunks.push_back(Chunk{rb + off, std::min(kChunk, rs - off)});
        }
    }

    // --- Phase P1: parallel sweep into per-thread maps (no lock on the hot
    // path). Each thread caps its per-class list at perClassCap; the merge
    // below sorts each class by address and truncates to perClassCap, so the
    // captured subset is the deterministic lowest-address set regardless of
    // thread timing — important for cross-snapshot diff stability. Worker
    // threads only touch process memory + the (read-only) classSet snapshot;
    // no Unity runtime calls happen here (name resolution is the caller's job,
    // under ThreadAttachGuard), so no per-thread attach is needed. ---
    unsigned int nthreads = Memory::ScanThreadCount();
    if (nthreads > chunks.size()) nthreads = static_cast<unsigned int>(chunks.size());
    if (nthreads < 1) nthreads = 1;

    std::vector<std::unordered_map<Il2CppClass*, std::vector<std::uintptr_t>>> perThread(nthreads);
    std::vector<std::size_t> qScanned(nthreads, 0);
    std::atomic<std::size_t> nextChunk{0};

    auto worker = [&](unsigned int tid) {
        auto& map = perThread[tid];
        std::size_t qs = 0;
        std::size_t dummyInstances = 0;   // per-thread; real count recomputed at merge
        for (;;) {
            std::size_t idx = nextChunk.fetch_add(1, std::memory_order_relaxed);
            if (idx >= chunks.size()) break;
            SehSweepRegion(chunks[idx].base, chunks[idx].size, minKlass, maxKlass,
                           classSet, map, perClassCap, dummyInstances, qs);
        }
        qScanned[tid] = qs;
    };

    if (nthreads <= 1) {
        worker(0);
    } else {
        std::vector<std::thread> pool;
        pool.reserve(nthreads);
        for (unsigned int t = 0; t < nthreads; ++t) pool.emplace_back(worker, t);
        for (auto& th : pool) th.join();
    }

    // --- Merge per-thread maps → out.byClass; sort each class by address and
    // cap to perClassCap (deterministic lowest-address subset). ---
    for (auto& m : perThread) {
        for (auto& kv : m) {
            auto& dst = out.byClass[kv.first];
            dst.insert(dst.end(), kv.second.begin(), kv.second.end());
        }
    }
    for (auto& kv : out.byClass) {
        std::sort(kv.second.begin(), kv.second.end());
        if (kv.second.size() > perClassCap) kv.second.resize(perClassCap);
        out.totalInstances += kv.second.size();
    }
    for (std::size_t q : qScanned) out.totalQwordsScanned += q;

    Log::Write(MagicStrings::LogCategoryScan,
               "Snapshot: FindAllInstances(IL2CPP) classes=" + std::to_string(out.byClass.size()) +
               " instances=" + std::to_string(out.totalInstances) +
               " regionsScanned=" + std::to_string(out.regionsScanned) +
               " bytes=" + std::to_string(out.bytesScanned) +
               " threads=" + std::to_string(nthreads));
    return out;
}

// ---------------------------------------------------------------------------
// Phase 30 P2 — ReverseLookupIndex
// ---------------------------------------------------------------------------

// Process-wide reuse cache for the sorted base list — see the Mono port for
// the full rationale. The aggregate "any numeric width" value scan fans out N
// back-to-back beginValueScan calls whose instance-base lists are identical;
// reuseWindowMs > 0 copies a recent build instead of re-walking. Heap-leaked
// (matches the class-cache pattern) so process exit skips the destructor.
struct RevIndexCache {
    std::mutex                 mu;
    std::vector<ReverseLookupIndex::Entry> bases;
    DWORD                      builtAtTick = 0;
    bool                       valid       = false;
};
RevIndexCache& revIndexCache() {
    static RevIndexCache* p = new RevIndexCache();   // intentional leak
    return *p;
}

bool ReverseLookupIndex::Build(std::uint32_t reuseWindowMs) {
    bases_.clear();
    lastBase_ = lastKlass_ = lastNextBase_ = 0;
    hits_ = localityHits_ = 0;

    // Reuse fast-path: copy a recent build's bases if still inside the window.
    if (reuseWindowMs > 0) {
        std::lock_guard<std::mutex> lk(revIndexCache().mu);
        auto& c = revIndexCache();
        if (c.valid && (GetTickCount() - c.builtAtTick) <= reuseWindowMs) {
            bases_ = c.bases;
            Log::Write(MagicStrings::LogCategoryScan,
                       "ReverseLookupIndex(IL2CPP): reused cached index instances=" +
                       std::to_string(bases_.size()));
            return true;
        }
    }

    if (!BuildClassRangeCache()) {
        Log::Write(MagicStrings::LogCategoryScan,
                   "ReverseLookupIndex: build aborted — class range cache unavailable");
        return false;
    }

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

    // Same region filter as FindAllInstances (RW, non-image, non-mapped,
    // non-stack) — the IL2CPP managed heap is VirtualAlloc'd MEM_PRIVATE.
    auto regions = Memory::EnumerateCommittedRegions();
    auto stacks  = Memory::CollectThreadStackRanges();
    std::vector<std::pair<const std::uint8_t*, std::size_t>> accepted;
    accepted.reserve(regions.size());
    for (const auto& r : regions) {
        if ((r.protect & kRW) == 0) continue;
        if (r.type == MEM_IMAGE || r.type == MEM_MAPPED) continue;
        const std::uint8_t* regionEnd = r.base + r.size;
        bool onStack = false;
        for (const auto& s : stacks) {
            if (s.low >= regionEnd) break;
            if (s.high > r.base && s.low < regionEnd) { onStack = true; break; }
        }
        if (onStack) continue;
        accepted.emplace_back(r.base, r.size);
    }

    // 8-aligned 1 MB chunks (qword == 8 stride → no cross-chunk straddle).
    struct Chunk { const std::uint8_t* base; std::size_t size; };
    constexpr std::size_t kChunk = 1u * 1024u * 1024u;
    std::vector<Chunk> chunks;
    for (const auto& [rb, rs] : accepted) {
        for (std::size_t off = 0; off < rs; off += kChunk) {
            chunks.push_back(Chunk{rb + off, std::min(kChunk, rs - off)});
        }
    }

    // Parallel sweep into per-thread flat lists, then concatenate + sort.
    unsigned int nthreads = Memory::ScanThreadCount();
    if (nthreads > chunks.size()) nthreads = static_cast<unsigned int>(chunks.size());
    if (nthreads < 1) nthreads = 1;

    std::vector<std::vector<std::pair<std::uintptr_t, std::uintptr_t>>> perThread(nthreads);
    std::vector<std::size_t> qScanned(nthreads, 0);
    std::atomic<std::size_t> nextChunk{0};
    auto worker = [&](unsigned int tid) {
        auto& vec = perThread[tid];
        std::size_t qs = 0;
        for (;;) {
            std::size_t idx = nextChunk.fetch_add(1, std::memory_order_relaxed);
            if (idx >= chunks.size()) break;
            SehSweepRegionFlat(chunks[idx].base, chunks[idx].size, minKlass, maxKlass,
                               classSet, vec, qs);
        }
        qScanned[tid] = qs;
    };
    if (nthreads <= 1) {
        worker(0);
    } else {
        std::vector<std::thread> pool;
        pool.reserve(nthreads);
        for (unsigned int t = 0; t < nthreads; ++t) pool.emplace_back(worker, t);
        for (auto& th : pool) th.join();
    }

    std::size_t total = 0;
    for (auto& v : perThread) total += v.size();
    bases_.reserve(total);
    for (auto& v : perThread) {
        for (auto& pr : v) bases_.push_back(Entry{pr.first, pr.second});
    }
    // Sort ascending by base. Each chunk's hits are already ascending and
    // chunks don't overlap, but per-thread interleaving across the worklist
    // means the concatenation isn't globally sorted — sort once.
    std::sort(bases_.begin(), bases_.end(),
              [](const Entry& a, const Entry& b) { return a.base < b.base; });

    std::size_t qtotal = 0;
    for (std::size_t q : qScanned) qtotal += q;
    Log::Write(MagicStrings::LogCategoryScan,
               "ReverseLookupIndex(IL2CPP): instances=" + std::to_string(bases_.size()) +
               " qwords=" + std::to_string(qtotal) +
               " threads=" + std::to_string(nthreads));

    // Publish to the reuse cache for a back-to-back aggregate fan-out.
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
    // as (maxSteps-1)*8 below it. The nearest 8-aligned object base ≤ addr
    // inside that window is the containing instance (identical to the
    // backward scan's "first class pointer found" result).
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
    // lastBase_ is PROVABLY the greatest base ≤ start (the next base up is
    // lastNextBase_ > start), so we can skip the binary search. Still subject
    // to the backward-window check (lastBase_ must be ≥ lowest).
    if (lastBase_ != 0 && start >= lastBase_ &&
        (lastNextBase_ == 0 || start < lastNextBase_)) {
        if (lastBase_ < lowest) {
            out.reason = "no_klass_in_range";
            return out;
        }
        ++localityHits_;
        ++hits_;
        ResolveInstanceLookup(addr, lastBase_, lastKlass_, out);
        return out;
    }

    // Binary search for the greatest base ≤ start.
    Entry probe{start, 0};
    auto it = std::upper_bound(bases_.begin(), bases_.end(), probe,
        [](const Entry& a, const Entry& b) { return a.base < b.base; });
    // `it` now points at the first base > start; the entry before it (if any)
    // is the greatest base ≤ start, and `it` itself is the next base above it.
    std::uintptr_t nextBase = (it != bases_.end()) ? it->base : 0;
    if (it == bases_.begin()) {
        out.reason = "no_klass_in_range";
        return out;
    }
    --it;   // greatest base ≤ start
    if (it->base < lowest) {
        // Nearest base is outside the backward window → not contained.
        out.reason = "no_klass_in_range";
        return out;
    }

    lastBase_     = it->base;
    lastKlass_    = it->klass;
    lastNextBase_ = nextBase;
    ++hits_;
    ResolveInstanceLookup(addr, it->base, it->klass, out);
    return out;
}

}  // namespace Discrete::IL2CPP
