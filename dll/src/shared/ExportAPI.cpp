// ExportAPI.cpp — implementation of the C ABI surface.

#define DISCRETE_BUILDING_DLL
#include "ExportAPI.h"

#include "AobPattern.h"
#include "BackendDetector.h"
#include "Logger.h"
#include "MagicStrings.h"
#include "MainThreadDispatcher.h"
#include "ManagedLayout.h"
#include "Memory.h"
#include "ModuleHelper.h"
#include "PipeServer.h"
#include "SnapshotSession.h"
#include "ValueScanSession.h"

#if defined(DISCRETE_BACKEND_MONO)
#  include "../mono/MonoAddressFinder.h"
#  include "../mono/MonoClassWalker.h"
#  include "../mono/MonoDomain.h"
#  include "../mono/MonoFieldXref.h"
#  include "../mono/MonoMainThreadHook.h"
#  include "../mono/MonoMethodFinder.h"
#  include "../mono/MonoMethodInvoker.h"
#  include "../mono/MonoResolver.h"
#  include "../mono/MonoThreadAttach.h"
#elif defined(DISCRETE_BACKEND_IL2CPP)
#  include "../il2cpp/IL2CPPAddressFinder.h"
#  include "../il2cpp/IL2CPPClassReferenceDiagnostic.h"
#  include "../il2cpp/IL2CPPClassWalker.h"
#  include "../il2cpp/IL2CPPDomain.h"
#  include "../il2cpp/IL2CPPFieldXref.h"
#  include "../il2cpp/IL2CPPLazyInitSlotScanner.h"
#  include "../il2cpp/IL2CPPMainThreadHook.h"
#  include "../il2cpp/IL2CPPMethodFinder.h"
#  include "../il2cpp/IL2CPPMethodInvoker.h"
#  include "../il2cpp/IL2CPPMetadataRegistration.h"
#  include "../il2cpp/IL2CPPResolver.h"
#  include "../il2cpp/IL2CPPThreadAttach.h"
#  include "../il2cpp/MetadataParser.h"
#endif

#include <nlohmann/json.hpp>

#include <windows.h>
#include <psapi.h>   // GetProcessMemoryInfo for 32-bit snapshot OOM guard

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>

using nlohmann::json;

// SEH-guarded class_get_parent for the inheritance-chain walks below.
// When the inspector drills into a field that LOOKS like an object pointer
// but isn't a real managed object (a value-type field misread as a
// reference, or a stale pointer), the runtime's class_get_parent
// dereferences garbage and access-violates — which crashes the *game* (the
// read runs on our pipe worker thread; an unhandled AV takes the process
// down). Catch it so the inheritance walk just stops. File-scope so every
// handler below (across both backends' anonymous namespaces) can see it;
// generic over IL2CPP::Il2CppClass* / Mono::MonoClass* — K is deduced.
#pragma warning(push)
#pragma warning(disable: 4509)  // SEH in a function (no C++ unwinding here)
template <typename Fn, typename K>
static K SehGetParent(Fn fn, K cur) {
    __try { return fn(cur); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// SEH-guarded class_value_size (two-arg: class + out-align). Returns the
// value-type instance size used as the inline array-element stride for the
// Dictionary walk; 0 on fault / unbound. Generic over both backends' class ptr.
template <typename Fn, typename K>
static int SehValueSize(Fn fn, K cur) {
    __try { std::uint32_t align = 0; return fn(cur, &align); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
#pragma warning(pop)

namespace {

std::atomic<bool> g_initialized{false};

// Game-process memory snapshot used both by the handshake (so UI can
// auto-clamp snapshot caps on x86 targets) and by snapshotBegin's
// pre-check that refuses to run when the game is already near the
// 4 GB user-space limit. workingSet = current physical mapping;
// privateUsage = total committed virtual (closer to real address-
// space pressure); pointerSize = sizeof(void*) inside the game
// process (4 on WOW64 / x86, 8 on x64).
struct ProcessMemorySnapshot {
    std::uint64_t workingSet   = 0;
    std::uint64_t privateUsage = 0;
    std::uint32_t pointerSize  = sizeof(void*);
};

ProcessMemorySnapshot QueryProcessMemory() noexcept {
    ProcessMemorySnapshot out;
    PROCESS_MEMORY_COUNTERS_EX pmc{};
    pmc.cb = sizeof(pmc);
    if (::GetProcessMemoryInfo(::GetCurrentProcess(),
                                reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&pmc),
                                sizeof(pmc))) {
        out.workingSet   = static_cast<std::uint64_t>(pmc.WorkingSetSize);
        out.privateUsage = static_cast<std::uint64_t>(pmc.PrivateUsage);
    }
    return out;
}

// 32-bit guard threshold. Process user-space ceiling on 32-bit
// Windows is 2 GB by default, 3 GB with /3GB-aware OS, 4 GB on
// WOW64-with-LARGEADDRESSAWARE (the common case for modern Unity
// 32-bit builds). Snapshot capture adds 300-500 MB of DLL-side
// JSON cache, so 3.0 GB current private-usage is the danger line —
// past it we're one bad allocation away from the game CTD that
// build #646 hit on 活俠傳.
constexpr std::uint64_t kX86PrivateUsageRefuseThreshold = 3ULL * 1024 * 1024 * 1024;

// Phase P1-fix — available physical RAM right now. The snapshot decode builds
// the ENTIRE snapshot in the game process before streaming, so a huge game
// (e.g. 22K classes / 800K instances / 7 GB heap) can balloon the game's
// private usage past the OS limit and CTD it. We bound the decode's added
// memory using a budget clamped to a fraction of free RAM.
std::uint64_t QueryAvailablePhysBytes() noexcept {
    MEMORYSTATUSEX ms{};
    ms.dwLength = sizeof(ms);
    if (::GlobalMemoryStatusEx(&ms)) return static_cast<std::uint64_t>(ms.ullAvailPhys);
    return 0;
}

// Default extra memory the snapshot decode may consume on top of the game's
// pre-snapshot private usage before it stops and reports truncation. Clamped
// at runtime to a fraction of currently-available physical RAM so a memory-
// constrained machine stops earlier. Overridable per-request via `memHeadroomMB`.
constexpr std::uint64_t kSnapshotDefaultHeadroomBytes = 4ULL * 1024 * 1024 * 1024;  // 4 GB
constexpr double        kSnapshotAvailPhysFraction     = 0.60;
// How often (in decoded instances) to re-sample process memory during decode.
constexpr std::size_t   kSnapshotMemCheckInterval       = 2048;

// ---------------------------------------------------------------------------
// Phase 31-2 (Phase B) — generic value-type struct member capture.
//
// Phase A (decodeField, named Unity math structs) emits x/y/z for Vector3 &
// friends by hard-coded offsets. Phase B extends this to ARBITRARY value-type
// structs (custom int/double/… structs): given the struct's runtime class +
// the inline struct base address, ReadClassInfo the class and decode each
// instance member.
//
// Offset: mono_field_get_offset / il2cpp_field_get_offset report a value
// type's member offsets HEADER-INCLUSIVE (boxed convention: e.g. Vector3.x =
// 0x10, not 0x00). An inline struct stored in a field has NO object header, so
// the member's real address is `structBase + member.offset - kObjectHeader`
// (the §3.2 / STATUS.md live-smoke formula: -0x10 on x64, -0x08 on x86).
//
// Safety: ReadClassInfo already wraps every runtime accessor in SEH and
// Memory::SafeReadBytes guards every read, so a garbage class / offset yields a
// clean error, not an AV. ProbeKlassValid rejects a junk class up front; the
// member cap + depth cap bound runaway recursion (C# forbids value-type cycles,
// so depth is naturally shallow, but cap anyway). A process-lifetime cache of
// ReadClassInfo results keeps repeated struct decode (a snapshot touches the
// same Vector3 class thousands of times) off the runtime — important for Mono
// BDWGC pressure.
using StructDecodeFieldFn = std::function<json(const std::string&, std::uintptr_t)>;

constexpr int         kStructMaxDepth         = 4;   // generic-struct nesting cap
constexpr std::size_t kStructDefaultMemberCap = 64;  // walkInstance per-struct cap

// Shared decode-site gate: should this field's decoded `entry` be expanded as a
// generic value-type struct? True only for a non-enum value type whose runtime
// class is known and whose decodeField result was a bare pointer word — i.e.
// NOT a primitive (`value`), NOT a named math struct already fanned out
// (`fields`), NOT a String, and NOT IntPtr/UIntPtr (value types that legitimately
// ARE a pointer). `typeName` is the field's declared type; `vtc` its captured
// runtime class.
template <typename ClassPtr>
bool ShouldExpandValueStruct(bool isValueType, bool isEnum, ClassPtr vtc,
                             const std::string& typeName, const json& entry) {
    return isValueType && !isEnum && vtc != nullptr &&
           typeName != "System.IntPtr" && typeName != "System.UIntPtr" &&
           entry.contains("ptr") && !entry.contains("fields") && !entry.contains("value");
}

// ── Native Dictionary<K,V> key/value walk ──────────────────────────────────
// A managed Dictionary field decodes to a bare reference pointer; left alone
// the user only sees the dict object's _buckets/_freeList/_comparer noise with
// `entries` leafed. Instead, detect the dict by type name, read its live
// `count` + `entries` (Entry[]) and decode each used entry's key/value via
// ReadClassInfo(EntryClass) — runtime offsets, never hard-coded layout. Pairs
// are emitted as child `fields` named "[<keyRepr>]" so the Inspector (which
// already renders nested `fields`) shows key→value with no UI change.

// Detect a managed Dictionary<K,V> field by type name. The metadata/IL form is
// "System.Collections.Generic.Dictionary`2<...>" (backtick-2 arity), but a LIVE
// IL2CPP walk (il2cpp_type_get_name) renders it DEMANGLED as
// "System.Collections.Generic.Dictionary<System.Int32,FieldEnemyInfo>" — no
// backtick (live-confirmed on Utawarerumono, build #1078). Accept either: the
// shared "...Generic.Dictionary" prefix followed by '`' (backtick form) or '<'
// (demangled instantiation). The char check excludes System.Collections.
// DictionaryEntry (different namespace + next char 'E') and the open generic.
inline bool IsDictionaryType(const std::string& t) {
    static constexpr char kBase[] = "System.Collections.Generic.Dictionary";
    constexpr std::size_t kBaseLen = sizeof(kBase) - 1;
    if (t.rfind(kBase, 0) != 0 || t.size() <= kBaseLen) return false;
    const char c = t[kBaseLen];
    return c == '`' || c == '<';
}

constexpr std::size_t kDictDefaultPairCap = 256;  // emitted key/value pairs cap

// Backend-agnostic hex — the file-scope helpers can't see the handler-local
// `formatHex` lambda. Matches its "0x%016llX" form exactly.
inline std::string DictFormatHex(std::uintptr_t v) {
    char buf[32];
    ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "0x%016llX",
                  static_cast<unsigned long long>(v));
    return std::string(buf);
}

// Case-insensitive field-name match tolerating one leading underscore.
// referencesource uses `entries`/`count`/`hashCode`; some corlibs prefix `_`.
inline bool DictNameIs(const std::string& s, const char* want) {
    const char* p = s.c_str();
    if (*p == '_') ++p;
    for (; *p != '\0' && *want != '\0'; ++p, ++want) {
        char a = (*p >= 'A' && *p <= 'Z') ? static_cast<char>(*p + 32) : *p;
        char b = (*want >= 'A' && *want <= 'Z') ? static_cast<char>(*want + 32) : *want;
        if (a != b) return false;
    }
    return *p == '\0' && *want == '\0';
}

// Short human-readable label from a decoded key field (string/number/bool/ptr).
inline std::string DictKeyRepr(const json& kd, std::size_t i) {
    if (kd.contains("string")) {
        const json& s = kd["string"];
        if (s.is_string()) return s.get<std::string>();
        if (s.is_null())   return "null";
    }
    if (kd.contains("value")) {
        const json& v = kd["value"];
        // NB: is_number_integer() is also true for unsigned in nlohmann, so the
        // unsigned check must come first to avoid get<long long> overflow.
        if (v.is_boolean())         return v.get<bool>() ? "true" : "false";
        if (v.is_number_unsigned()) return std::to_string(v.get<unsigned long long>());
        if (v.is_number_integer())  return std::to_string(v.get<long long>());
        if (v.is_number_float())    return std::to_string(v.get<double>());
        return v.dump();
    }
    if (kd.contains("ptr") && kd["ptr"].is_string()) return kd["ptr"].get<std::string>();
    return "#" + std::to_string(i);
}

#if defined(DISCRETE_BACKEND_MONO)
json CaptureMonoStructFields(const StructDecodeFieldFn& decodeField,
                             ::Discrete::Mono::MonoClass* sc, std::uintptr_t structBase,
                             int depthLeft, std::size_t memberCap) {
    namespace Mono = ::Discrete::Mono;
    if (sc == nullptr || depthLeft <= 0) return json::object();
    if (!Mono::ProbeKlassValid(sc)) return json{{"struct_error", "invalid struct class"}};

    // Process-lifetime cache (struct field/offset layout is stable). Mutex:
    // the interactive (browse) and bulk (snapshot) pipes can both reach here.
    static std::unordered_map<Mono::MonoClass*, Mono::ClassInfoResult> s_cache;
    static std::mutex s_mtx;
    Mono::ClassInfoResult res;
    {
        std::lock_guard<std::mutex> lk(s_mtx);
        auto it = s_cache.find(sc);
        if (it != s_cache.end()) res = it->second;
    }
    if (!res.ok) {
        res = Mono::ReadClassInfo(sc);
        if (res.ok) { std::lock_guard<std::mutex> lk(s_mtx); s_cache.emplace(sc, res); }
    }
    if (!res.ok) return json{{"struct_error", "ReadClassInfo failed"}};

    const long hdr = static_cast<long>(::Discrete::ManagedLayout::kObjectHeader);
    json members = json::array();
    std::size_t produced = 0;
    for (const auto& f : res.info.fields) {
        if (f.flags & 0x10u) continue;          // skip statics
        if (produced >= memberCap) break;
        const long relOff = static_cast<long>(f.offset) - hdr;
        std::uintptr_t memberAddr = structBase + static_cast<std::uintptr_t>(relOff);
        const std::string& decType = (f.isEnum && !f.underlyingTypeName.empty())
            ? f.underlyingTypeName : f.typeName;
        json mf = { {"name", f.name}, {"type", f.typeName}, {"offset", relOff} };
        if (f.isEnum)      mf["is_enum"]       = true;
        if (f.isValueType) mf["is_value_type"] = true;
        json d = decodeField(decType, memberAddr);
        for (auto kv = d.begin(); kv != d.end(); ++kv) mf[kv.key()] = kv.value();
        // Nested generic struct member → recurse one more level. Named math
        // structs were already fanned out by decodeField (Phase A) above, so
        // this only fires for a struct-in-struct of custom type.
        if (ShouldExpandValueStruct(f.isValueType, f.isEnum, f.valueTypeClass, f.typeName, mf)) {
            json sub = CaptureMonoStructFields(decodeField, f.valueTypeClass,
                                               memberAddr, depthLeft - 1, memberCap);
            if (sub.contains("fields")) { mf.erase("ptr"); mf["fields"] = std::move(sub["fields"]); }
        }
        members.push_back(std::move(mf));
        ++produced;
    }
    return json{{"fields", std::move(members)}};
}

// Native Dictionary<K,V> walk (Mono) — see the shared header comment above.
// `dictObj` is a live dict OBJECT pointer (reference type, has object header).
// Returns {"dict_count":N,"fields":[pairs]} on success, {"dict_error":"..."} for
// an unsupported/torn layout, or {} when it isn't a usable dict (caller keeps
// the bare ptr). depthLeft bounds nested-dict recursion; pairCap bounds pairs.
json CaptureMonoDictionary(const StructDecodeFieldFn& decodeField,
                           std::uintptr_t dictObj, int depthLeft,
                           std::size_t pairCap) {
    namespace Mono = ::Discrete::Mono;
    namespace ML   = ::Discrete::ManagedLayout;
    if (dictObj == 0 || depthLeft <= 0) return json::object();

    const auto& r = Mono::GetResolver();
    if (r.class_get_element_class == nullptr || r.class_value_size == nullptr)
        return json::object();  // runtime too old → keep bare ptr

    auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
        // Full qualification: the `namespace Memory = ...` alias is declared
        // later in this TU (~line 1217), after these early helpers.
        return ::Discrete::Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
    };

    // 1. live dict class (SEH-guarded object_get_class) — fields are set up.
    Mono::MonoClass* dictClass = Mono::ClassOfObject(dictObj);
    if (dictClass == nullptr || !Mono::ProbeKlassValid(dictClass)) return json::object();
    Mono::ClassInfoResult dr = Mono::ReadClassInfo(dictClass);
    if (!dr.ok) return json::object();

    // 2. locate `entries` (Entry[]) + `count` (Int32) by name. Missing → an
    //    unsupported corlib layout (e.g. old mcs parallel-array dictionary).
    const Mono::FieldEntry* entriesF = nullptr;
    const Mono::FieldEntry* countF   = nullptr;
    for (const auto& f : dr.info.fields) {
        if (f.flags & 0x10u) continue;  // skip statics
        if (entriesF == nullptr && DictNameIs(f.name, "entries")) entriesF = &f;
        else if (countF == nullptr && DictNameIs(f.name, "count")) countF = &f;
    }
    if (entriesF == nullptr || countF == nullptr)
        return json{{"dict_error", "unsupported dict layout"}};

    // 3. read count + entries pointer. The dict is a reference object, so its
    //    field offsets are header-inclusive → no -header here.
    std::int32_t count = 0;
    if (!rd(dictObj + static_cast<std::uintptr_t>(countF->offset), &count, 4))
        return json{{"dict_error", "count unreadable"}};
    if (count <= 0) return json{{"dict_count", 0}, {"fields", json::array()}};
    if (count > (1 << 20)) count = (1 << 20);  // sanity clamp on torn data
    std::uintptr_t entriesPtr = 0;
    if (!rd(dictObj + static_cast<std::uintptr_t>(entriesF->offset), &entriesPtr,
            sizeof(std::uintptr_t)) || entriesPtr == 0)
        return json{{"dict_count", 0}, {"fields", json::array()}};
    // clamp count to the entries array's real length.
    std::uintptr_t arrLen = 0;
    if (rd(entriesPtr + ML::kArrayMaxLengthOffset, &arrLen, sizeof(std::uintptr_t)) &&
        arrLen < static_cast<std::uintptr_t>(count))
        count = static_cast<std::int32_t>(arrLen);

    // 4. Entry element class → stride + member offsets (all SEH-guarded).
    Mono::MonoClass* arrClass = Mono::ClassOfObject(entriesPtr);
    if (arrClass == nullptr) return json{{"dict_error", "entries class unresolved"}};
    Mono::MonoClass* entryClass = SehGetParent(r.class_get_element_class, arrClass);
    if (entryClass == nullptr || !Mono::ProbeKlassValid(entryClass))
        return json{{"dict_error", "entry class unresolved"}};
    const int stride = SehValueSize(r.class_value_size, entryClass);
    if (stride <= 0) return json{{"dict_error", "entry stride unknown"}};
    Mono::ClassInfoResult er = Mono::ReadClassInfo(entryClass);
    if (!er.ok) return json{{"dict_error", "entry layout unreadable"}};

    const Mono::FieldEntry* keyF  = nullptr;
    const Mono::FieldEntry* valF  = nullptr;
    const Mono::FieldEntry* hashF = nullptr;
    const Mono::FieldEntry* nextF = nullptr;
    for (const auto& f : er.info.fields) {
        if (f.flags & 0x10u) continue;
        if      (keyF  == nullptr && DictNameIs(f.name, "key"))      keyF  = &f;
        else if (valF  == nullptr && DictNameIs(f.name, "value"))    valF  = &f;
        else if (hashF == nullptr && DictNameIs(f.name, "hashCode")) hashF = &f;
        else if (nextF == nullptr && DictNameIs(f.name, "next"))     nextF = &f;
    }
    if (keyF == nullptr || valF == nullptr)
        return json{{"dict_error", "entry key/value not found"}};

    const long hdr = static_cast<long>(ML::kObjectHeader);
    const std::uintptr_t entriesData = entriesPtr + ML::kArrayDataOffset;
    const std::string& keyDecType = (keyF->isEnum && !keyF->underlyingTypeName.empty())
        ? keyF->underlyingTypeName : keyF->typeName;
    const std::string& valDecType = (valF->isEnum && !valF->underlyingTypeName.empty())
        ? valF->underlyingTypeName : valF->typeName;

    json pairs = json::array();
    std::size_t emitted = 0;
    bool truncated = false;
    for (std::int32_t i = 0; i < count; ++i) {
        if (emitted >= pairCap) { truncated = true; break; }
        const std::uintptr_t elemBase =
            entriesData + static_cast<std::uintptr_t>(i) * static_cast<std::uintptr_t>(stride);
        // Skip freed entries. referencesource marks a freed Entry with
        // hashCode == -1 (the live hash is masked to 0x7FFFFFFF, never -1);
        // CoreCLR marks it with next < -1 (next is a free-list link, StartOfFreeList
        // = -3). Check both so the walk is correct across corlib variants without
        // hard-coding which the target uses.
        bool freed = false;
        if (hashF != nullptr) {
            std::int32_t hc = 0;
            const std::uintptr_t hcAddr =
                elemBase + static_cast<std::uintptr_t>(static_cast<long>(hashF->offset) - hdr);
            if (rd(hcAddr, &hc, 4) && hc == -1) freed = true;
        }
        if (!freed && nextF != nullptr) {
            std::int32_t nx = 0;
            const std::uintptr_t nxAddr =
                elemBase + static_cast<std::uintptr_t>(static_cast<long>(nextF->offset) - hdr);
            if (rd(nxAddr, &nx, 4) && nx < -1) freed = true;
        }
        if (freed) continue;
        const std::uintptr_t keyAddr =
            elemBase + static_cast<std::uintptr_t>(static_cast<long>(keyF->offset) - hdr);
        const std::uintptr_t valAddr =
            elemBase + static_cast<std::uintptr_t>(static_cast<long>(valF->offset) - hdr);
        json kd = decodeField(keyDecType, keyAddr);
        json vd = decodeField(valDecType, valAddr);
        const std::string keyRepr = DictKeyRepr(kd, static_cast<std::size_t>(i));

        json pair = {
            {"name",     "[" + keyRepr + "]"},
            {"type",     valF->typeName},
            {"offset",   static_cast<long>(valF->offset) - hdr},
            {"address",  DictFormatHex(valAddr)},
            {"index",    i},
            {"dict_key", keyRepr},
        };
        if (valF->isEnum)      pair["is_enum"]       = true;
        if (valF->isValueType) pair["is_value_type"] = true;
        for (auto kv = vd.begin(); kv != vd.end(); ++kv) pair[kv.key()] = kv.value();

        // value expansion: inline value-struct → fan out; nested dict → recurse.
        if (ShouldExpandValueStruct(valF->isValueType, valF->isEnum, valF->valueTypeClass,
                                    valF->typeName, pair)) {
            json sub = CaptureMonoStructFields(decodeField, valF->valueTypeClass,
                                               valAddr, depthLeft - 1, kStructDefaultMemberCap);
            if (sub.contains("fields")) { pair.erase("ptr"); pair["fields"] = std::move(sub["fields"]); }
        } else if (IsDictionaryType(valF->typeName) && pair.contains("ptr")) {
            std::uintptr_t nested = 0;
            if (rd(valAddr, &nested, sizeof(std::uintptr_t)) && nested != 0) {
                json nd = CaptureMonoDictionary(decodeField, nested, depthLeft - 1, pairCap);
                if (nd.contains("fields")) {
                    pair.erase("ptr");
                    pair["dict"]                = true;
                    pair["dict_count"]          = nd.value("dict_count", 0);
                    pair["dict_entries_offset"] = nd.value("dict_entries_offset", 0);
                    pair["dict_data_offset"]    = nd.value("dict_data_offset", 0);
                    pair["dict_stride"]         = nd.value("dict_stride", 0);
                    if (nd.value("dict_truncated", false)) pair["dict_truncated"] = true;
                    pair["fields"] = std::move(nd["fields"]);
                }
            }
        }
        pairs.push_back(std::move(pair));
        ++emitted;
    }
    // dict_entries_offset/data_offset/stride let the CE-XML exporter rebuild the
    // relative deref chain: dict field → +entries_offset deref → +data_offset +
    // index*stride + per-pair value offset. entries_offset is the raw (header-
    // inclusive) offset of `entries` within the dict object; data_offset is the
    // SZARRAY data start; stride is the Entry value-type size.
    json out = {
        {"dict_count",          emitted},
        {"dict_entries_offset", static_cast<long>(entriesF->offset)},
        {"dict_data_offset",    static_cast<long>(ML::kArrayDataOffset)},
        {"dict_stride",         stride},
        {"fields",              std::move(pairs)},
    };
    if (truncated) out["dict_truncated"] = true;
    return out;
}
#endif  // DISCRETE_BACKEND_MONO

#if defined(DISCRETE_BACKEND_IL2CPP)
// Namespace aliases so the decoder (defined here, before the handler-block
// aliases at ~line 662) can use the short `Snapshot::` / `IL2CPP::` forms. Both
// are re-aliased to the same target later — re-aliasing to an identical target
// is well-formed, so this doesn't conflict.
namespace Snapshot = ::Discrete::Snapshot;
namespace IL2CPP   = ::Discrete::IL2CPP;

// Phase 31-2 (Phase B) — IL2CPP twin of CaptureMonoStructFields (see the Mono
// definition above for the full rationale + offset formula). Same header-strip
// (`offset - kObjectHeader`), same caps + SEH safety via ReadClassInfo /
// ProbeKlassValid / Memory::SafeReadBytes. NOTE: the -header convention is
// live-proven on Mono; IL2CPP applies the same convention (its walkInstance
// value-type drill, Phase 31-6a, already does) but still wants its own
// live-smoke to confirm byte-for-byte.
json CaptureIl2cppStructFields(const StructDecodeFieldFn& decodeField,
                               IL2CPP::Il2CppClass* sc, std::uintptr_t structBase,
                               int depthLeft, std::size_t memberCap) {
    if (sc == nullptr || depthLeft <= 0) return json::object();
    if (!IL2CPP::ProbeKlassValid(sc)) return json{{"struct_error", "invalid struct class"}};

    static std::unordered_map<IL2CPP::Il2CppClass*, IL2CPP::ClassInfoResult> s_cache;
    static std::mutex s_mtx;
    IL2CPP::ClassInfoResult res;
    {
        std::lock_guard<std::mutex> lk(s_mtx);
        auto it = s_cache.find(sc);
        if (it != s_cache.end()) res = it->second;
    }
    if (!res.ok) {
        res = IL2CPP::ReadClassInfo(sc);
        if (res.ok) { std::lock_guard<std::mutex> lk(s_mtx); s_cache.emplace(sc, res); }
    }
    if (!res.ok) return json{{"struct_error", "ReadClassInfo failed"}};

    const long hdr = static_cast<long>(::Discrete::ManagedLayout::kObjectHeader);
    json members = json::array();
    std::size_t produced = 0;
    for (const auto& f : res.info.fields) {
        if (f.flags & 0x10u) continue;          // skip statics
        if (produced >= memberCap) break;
        const long relOff = static_cast<long>(f.offset) - hdr;
        std::uintptr_t memberAddr = structBase + static_cast<std::uintptr_t>(relOff);
        const std::string& decType = (f.isEnum && !f.underlyingTypeName.empty())
            ? f.underlyingTypeName : f.typeName;
        json mf = { {"name", f.name}, {"type", f.typeName}, {"offset", relOff} };
        if (f.isEnum)      mf["is_enum"]       = true;
        if (f.isValueType) mf["is_value_type"] = true;
        json d = decodeField(decType, memberAddr);
        for (auto kv = d.begin(); kv != d.end(); ++kv) mf[kv.key()] = kv.value();
        if (ShouldExpandValueStruct(f.isValueType, f.isEnum, f.valueTypeClass, f.typeName, mf)) {
            json sub = CaptureIl2cppStructFields(decodeField, f.valueTypeClass,
                                                 memberAddr, depthLeft - 1, memberCap);
            if (sub.contains("fields")) { mf.erase("ptr"); mf["fields"] = std::move(sub["fields"]); }
        }
        members.push_back(std::move(mf));
        ++produced;
    }
    return json{{"fields", std::move(members)}};
}

// Native Dictionary<K,V> walk (IL2CPP) — twin of CaptureMonoDictionary (see it
// for the full rationale + the layout facts). Same algorithm with IL2CPP types:
// object_get_class → ReadClassInfo for `entries`/`count`, element class for the
// Entry stride + key/value/hashCode offsets, emit "[key]" child fields. IL2CPP
// is x64-only today; the -kObjectHeader inline-element math is the same.
json CaptureIl2cppDictionary(const StructDecodeFieldFn& decodeField,
                             std::uintptr_t dictObj, int depthLeft,
                             std::size_t pairCap) {
    namespace ML = ::Discrete::ManagedLayout;
    if (dictObj == 0 || depthLeft <= 0) return json::object();

    const auto& r = IL2CPP::GetResolver();
    if (r.class_get_element_class == nullptr || r.class_value_size == nullptr)
        return json::object();

    auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
        // Full qualification: the `namespace Memory = ...` alias is declared
        // later in this TU (~line 1217), after these early helpers.
        return ::Discrete::Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
    };

    IL2CPP::Il2CppClass* dictClass = IL2CPP::ClassOfObject(dictObj);
    if (dictClass == nullptr || !IL2CPP::ProbeKlassValid(dictClass)) return json::object();
    IL2CPP::ClassInfoResult dr = IL2CPP::ReadClassInfo(dictClass);
    if (!dr.ok) return json::object();

    const IL2CPP::FieldEntry* entriesF = nullptr;
    const IL2CPP::FieldEntry* countF   = nullptr;
    for (const auto& f : dr.info.fields) {
        if (f.flags & 0x10u) continue;
        if (entriesF == nullptr && DictNameIs(f.name, "entries")) entriesF = &f;
        else if (countF == nullptr && DictNameIs(f.name, "count")) countF = &f;
    }
    if (entriesF == nullptr || countF == nullptr)
        return json{{"dict_error", "unsupported dict layout"}};

    std::int32_t count = 0;
    if (!rd(dictObj + static_cast<std::uintptr_t>(countF->offset), &count, 4))
        return json{{"dict_error", "count unreadable"}};
    if (count <= 0) return json{{"dict_count", 0}, {"fields", json::array()}};
    if (count > (1 << 20)) count = (1 << 20);
    std::uintptr_t entriesPtr = 0;
    if (!rd(dictObj + static_cast<std::uintptr_t>(entriesF->offset), &entriesPtr,
            sizeof(std::uintptr_t)) || entriesPtr == 0)
        return json{{"dict_count", 0}, {"fields", json::array()}};
    std::uintptr_t arrLen = 0;
    if (rd(entriesPtr + ML::kArrayMaxLengthOffset, &arrLen, sizeof(std::uintptr_t)) &&
        arrLen < static_cast<std::uintptr_t>(count))
        count = static_cast<std::int32_t>(arrLen);

    IL2CPP::Il2CppClass* arrClass = IL2CPP::ClassOfObject(entriesPtr);
    if (arrClass == nullptr) return json{{"dict_error", "entries class unresolved"}};
    IL2CPP::Il2CppClass* entryClass = SehGetParent(r.class_get_element_class, arrClass);
    if (entryClass == nullptr || !IL2CPP::ProbeKlassValid(entryClass))
        return json{{"dict_error", "entry class unresolved"}};
    const int stride = SehValueSize(r.class_value_size, entryClass);
    if (stride <= 0) return json{{"dict_error", "entry stride unknown"}};
    IL2CPP::ClassInfoResult er = IL2CPP::ReadClassInfo(entryClass);
    if (!er.ok) return json{{"dict_error", "entry layout unreadable"}};

    const IL2CPP::FieldEntry* keyF  = nullptr;
    const IL2CPP::FieldEntry* valF  = nullptr;
    const IL2CPP::FieldEntry* hashF = nullptr;
    const IL2CPP::FieldEntry* nextF = nullptr;
    for (const auto& f : er.info.fields) {
        if (f.flags & 0x10u) continue;
        if      (keyF  == nullptr && DictNameIs(f.name, "key"))      keyF  = &f;
        else if (valF  == nullptr && DictNameIs(f.name, "value"))    valF  = &f;
        else if (hashF == nullptr && DictNameIs(f.name, "hashCode")) hashF = &f;
        else if (nextF == nullptr && DictNameIs(f.name, "next"))     nextF = &f;
    }
    if (keyF == nullptr || valF == nullptr)
        return json{{"dict_error", "entry key/value not found"}};

    const long hdr = static_cast<long>(ML::kObjectHeader);
    const std::uintptr_t entriesData = entriesPtr + ML::kArrayDataOffset;
    const std::string& keyDecType = (keyF->isEnum && !keyF->underlyingTypeName.empty())
        ? keyF->underlyingTypeName : keyF->typeName;
    const std::string& valDecType = (valF->isEnum && !valF->underlyingTypeName.empty())
        ? valF->underlyingTypeName : valF->typeName;

    json pairs = json::array();
    std::size_t emitted = 0;
    bool truncated = false;
    for (std::int32_t i = 0; i < count; ++i) {
        if (emitted >= pairCap) { truncated = true; break; }
        const std::uintptr_t elemBase =
            entriesData + static_cast<std::uintptr_t>(i) * static_cast<std::uintptr_t>(stride);
        // Skip freed entries. referencesource marks freed with hashCode == -1
        // (live hash masked to 0x7FFFFFFF); CoreCLR marks freed with next < -1
        // (free-list link). Check both → correct across corlib variants.
        bool freed = false;
        if (hashF != nullptr) {
            std::int32_t hc = 0;
            const std::uintptr_t hcAddr =
                elemBase + static_cast<std::uintptr_t>(static_cast<long>(hashF->offset) - hdr);
            if (rd(hcAddr, &hc, 4) && hc == -1) freed = true;
        }
        if (!freed && nextF != nullptr) {
            std::int32_t nx = 0;
            const std::uintptr_t nxAddr =
                elemBase + static_cast<std::uintptr_t>(static_cast<long>(nextF->offset) - hdr);
            if (rd(nxAddr, &nx, 4) && nx < -1) freed = true;
        }
        if (freed) continue;
        const std::uintptr_t keyAddr =
            elemBase + static_cast<std::uintptr_t>(static_cast<long>(keyF->offset) - hdr);
        const std::uintptr_t valAddr =
            elemBase + static_cast<std::uintptr_t>(static_cast<long>(valF->offset) - hdr);
        json kd = decodeField(keyDecType, keyAddr);
        json vd = decodeField(valDecType, valAddr);
        const std::string keyRepr = DictKeyRepr(kd, static_cast<std::size_t>(i));

        json pair = {
            {"name",     "[" + keyRepr + "]"},
            {"type",     valF->typeName},
            {"offset",   static_cast<long>(valF->offset) - hdr},
            {"address",  DictFormatHex(valAddr)},
            {"index",    i},
            {"dict_key", keyRepr},
        };
        if (valF->isEnum)      pair["is_enum"]       = true;
        if (valF->isValueType) pair["is_value_type"] = true;
        for (auto kv = vd.begin(); kv != vd.end(); ++kv) pair[kv.key()] = kv.value();

        if (ShouldExpandValueStruct(valF->isValueType, valF->isEnum, valF->valueTypeClass,
                                    valF->typeName, pair)) {
            json sub = CaptureIl2cppStructFields(decodeField, valF->valueTypeClass,
                                                 valAddr, depthLeft - 1, kStructDefaultMemberCap);
            if (sub.contains("fields")) { pair.erase("ptr"); pair["fields"] = std::move(sub["fields"]); }
        } else if (IsDictionaryType(valF->typeName) && pair.contains("ptr")) {
            std::uintptr_t nested = 0;
            if (rd(valAddr, &nested, sizeof(std::uintptr_t)) && nested != 0) {
                json nd = CaptureIl2cppDictionary(decodeField, nested, depthLeft - 1, pairCap);
                if (nd.contains("fields")) {
                    pair.erase("ptr");
                    pair["dict"]                = true;
                    pair["dict_count"]          = nd.value("dict_count", 0);
                    pair["dict_entries_offset"] = nd.value("dict_entries_offset", 0);
                    pair["dict_data_offset"]    = nd.value("dict_data_offset", 0);
                    pair["dict_stride"]         = nd.value("dict_stride", 0);
                    if (nd.value("dict_truncated", false)) pair["dict_truncated"] = true;
                    pair["fields"] = std::move(nd["fields"]);
                }
            }
        }
        pairs.push_back(std::move(pair));
        ++emitted;
    }
    // See CaptureMonoDictionary for the chain-offset rationale.
    json out = {
        {"dict_count",          emitted},
        {"dict_entries_offset", static_cast<long>(entriesF->offset)},
        {"dict_data_offset",    static_cast<long>(ML::kArrayDataOffset)},
        {"dict_stride",         stride},
        {"fields",              std::move(pairs)},
    };
    if (truncated) out["dict_truncated"] = true;
    return out;
}

// Phase 30b — IL2CPP game-side streaming decoder. Owns the heap-walk result +
// all the decode context snapshotBegin used to hold as locals, so snapshotChunk
// can decode plan rows ON DEMAND and free each chunk's payload after it ships —
// bounding the game-process transient peak to ~one chunk + the plan + the
// ClassInfo cache instead of the whole decoded snapshot (~4.5 GB on
// Utawarerumono). The body of DecodeOneClass / element capture / static decode
// is a faithful transplant of the old decode-in-Begin loop; the only behavioural
// change is WHEN it runs (per chunk) and that each chunk opens its OWN
// ThreadAttachGuard — safe on IL2CPP, which has no BDWGC finalizer-accumulation
// hazard (that's why this is IL2CPP-only; Mono keeps decode-in-Begin).
//
// The shared json helpers (formatHex / decodeField / walkArrayElementsInline)
// are function-local lambdas in RegisterCoreHandlers, so they're handed in as
// std::function copies at construction.
class Il2cppStreamDecoder final : public Snapshot::ISnapshotStreamDecoder {
public:
    using FormatHexFn  = std::function<std::string(std::uintptr_t)>;
    using DecodeFieldFn = std::function<json(const std::string&, std::uintptr_t)>;
    using WalkArrayFn  = std::function<json(std::uintptr_t, const std::string&, std::size_t,
                                            const std::function<json(std::uintptr_t)>&)>;

    Il2cppStreamDecoder(IL2CPP::Il2CppDomain* domain,
                        std::vector<Snapshot::ClassPlanItem> plan,
                        std::unordered_map<IL2CPP::Il2CppClass*, char> knownIlKlasses,
                        std::size_t arrayCap,
                        std::size_t instanceDecodeBudget,
                        std::size_t elementFieldCap,
                        std::size_t elementCaptureBudget,
                        std::vector<std::string> arrayElementTargets,
                        bool arrayElementFields,
                        bool captureStaticValues,
                        bool valueStructFields,
                        std::size_t structMemberCap,
                        std::uint64_t memBudgetBytes,
                        std::uint64_t initialPrivateUsage,
                        FormatHexFn formatHex,
                        DecodeFieldFn decodeField,
                        WalkArrayFn walkArrayElementsInline)
        : domain_(domain),
          plan_(std::move(plan)),
          knownIlKlasses_(std::move(knownIlKlasses)),
          arrayCap_(arrayCap),
          instanceDecodeBudget_(instanceDecodeBudget),
          elementFieldCap_(elementFieldCap),
          elementCaptureBudget_(elementCaptureBudget),
          arrayElementTargets_(std::move(arrayElementTargets)),
          arrayElementFields_(arrayElementFields),
          captureStaticValues_(captureStaticValues),
          valueStructFields_(valueStructFields),
          structMemberCap_(structMemberCap),
          memBudgetBytes_(memBudgetBytes),
          peakPrivate_(initialPrivateUsage),
          formatHex_(std::move(formatHex)),
          decodeField_(std::move(decodeField)),
          walkArrayElementsInline_(std::move(walkArrayElementsInline)) {}

    std::size_t PlanSize() const override { return plan_.size(); }
    bool TruncatedByMemory() const override { return memBudgetHit_; }
    bool TruncatedByCount()  const override { return countBudgetHit_; }
    std::uint64_t PeakPrivateUsage() const override { return peakPrivate_; }

    json DecodeStats() const override {
        return json{
            {"decodedClasses",           decodedClasses_},
            {"decodedInstances",         decodedInstances_},
            {"decodedArrays",            decodedArrays_},
            {"skippedOpenGenericArrays", skippedOpenGenericArrays_},
            {"classInfoCacheHits",       classInfoCacheHits_},
            {"classInfoCacheMisses",     classInfoCacheMisses_},
            {"decodedElementSets",       decodedElementSets_},
            {"elementBudgetHit",         elementBudgetHit_},
            {"truncatedByMemory",        memBudgetHit_},
            {"truncatedByCount",         countBudgetHit_},
            {"instanceDecodeBudget",     instanceDecodeBudget_},
            {"memBudgetBytes",           memBudgetBytes_},
            {"peakPrivateUsage",         peakPrivate_},
        };
    }

    std::size_t DecodeRange(std::size_t cursor, std::size_t limit, json& out) override {
        if (cursor >= plan_.size()) return plan_.size();
        const std::size_t end = (std::min)(plan_.size(), cursor + limit);

        // One ThreadAttachGuard per chunk. Safe on IL2CPP (no BDWGC finalizer
        // accumulation) — this is the trade that lets decode live in Chunk.
        IL2CPP::ThreadAttachGuard guard(domain_);
        if (!guard.Attached()) {
            for (std::size_t i = cursor; i < end; ++i) {
                out.push_back(Snapshot::ClassPayload{
                    {"assembly", plan_[i].assembly},
                    {"namespace", plan_[i].ns},
                    {"name", plan_[i].name},
                    {"error", "il2cpp_thread_attach failed"},
                });
            }
            return end;
        }

        for (std::size_t i = cursor; i < end; ++i) {
            out.push_back(DecodeOneClass(plan_[i]));
        }

        // Last chunk → post-decode GC drain (mirror of the old Begin tail).
        // IL2CPP GC doesn't have Mono BDWGC's catch-up behaviour, but keep the
        // architecture symmetric. The 500ms "breathe" the old Begin did after
        // the WHOLE decode now lands once, on the final chunk.
        if (end >= plan_.size() && !drained_) {
            drained_ = true;
            const auto& r = IL2CPP::GetResolver();
            if (r.gc_collect != nullptr) {
                auto drainStart = std::chrono::steady_clock::now();
                r.gc_collect(0);
                auto drainMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - drainStart).count();
                Discrete::Log::Write(Discrete::MagicStrings::LogCategoryScan,
                    "snapshotChunk(IL2CPP) final-chunk drain " + std::to_string(drainMs) + " ms");
            }
            ::Sleep(500);
        }
        return end;
    }

private:
    const IL2CPP::ClassInfoResult& GetClassInfo(IL2CPP::Il2CppClass* c) {
        auto it = globalClassInfo_.find(c);
        if (it != globalClassInfo_.end()) {
            ++classInfoCacheHits_;
            return it->second;
        }
        ++classInfoCacheMisses_;
        auto inserted = globalClassInfo_.emplace(c, IL2CPP::ReadClassInfo(c));
        return inserted.first->second;
    }

    void CheckMemBudget() {
        const std::uint64_t pu = QueryProcessMemory().privateUsage;
        if (pu > peakPrivate_) peakPrivate_ = pu;
        if (pu >= memBudgetBytes_) memBudgetHit_ = true;
    }

    static bool IsCapturableArrayType(const std::string& tn) {
        if (tn.size() < 4) return false;
        if (tn[tn.size() - 1] != ']' || tn[tn.size() - 2] != '[') return false;
        return tn.find('.') != std::string::npos ||
               tn.find('/') != std::string::npos;
    }

    bool ShouldCaptureInner(const std::string& fqn, const std::string& fieldName) {
        if (!arrayElementFields_) return false;
        if (arrayElementTargets_.empty()) return true;   // blanket (x64)
        const std::string full = fqn + "." + fieldName;
        for (const auto& t : arrayElementTargets_)
            if (t == fqn || t == full) return true;
        return false;
    }

    // Phase 29f (Layer B) — reference-array element inner-field capture.
    // Returns {"fields":[…]} or {"element_class_error":"…"}. Budget caps
    // ATTEMPTS (not just captures) — bounds JSON memory AND object_get_class
    // volume on a large game.
    json CaptureElementFields(std::uintptr_t elemPtr) {
        if (elementsProcessed_ >= elementCaptureBudget_) {
            elementBudgetHit_ = true;
            return json::object();
        }
        ++elementsProcessed_;
        IL2CPP::Il2CppClass* ec = IL2CPP::ClassOfObject(elemPtr);
        if (ec == nullptr)
            return json{{"element_class_error", "object_get_class_failed"}};
        if (knownIlKlasses_.find(ec) == knownIlKlasses_.end())
            return json{{"element_class_error", "class_out_of_scope"}};
        const auto& r = IL2CPP::GetResolver();
        std::vector<IL2CPP::Il2CppClass*> echain;
        for (IL2CPP::Il2CppClass* cur = ec; cur != nullptr && echain.size() < 16; ) {
            echain.push_back(cur);
            if (r.class_get_parent == nullptr) break;
            cur = SehGetParent(r.class_get_parent, cur);
        }
        json efields = json::array();
        std::size_t produced = 0;
        const std::size_t fcap = elementFieldCap_;
        for (std::size_t ci = echain.size(); ci > 0 && produced < fcap; --ci) {
            const auto& pr = GetClassInfo(echain[ci - 1]);
            if (!pr.ok) continue;
            for (const auto& f : pr.info.fields) {
                if (produced >= fcap) break;
                if (f.flags & 0x10u) continue;   // skip statics
                std::uintptr_t fieldAddr = elemPtr + static_cast<std::uintptr_t>(f.offset);
                json fe = {
                    {"name",   f.name},
                    {"type",   f.typeName},
                    {"offset", f.offset},
                    {"from",   pr.info.self.name},
                };
                if (f.isEnum)      fe["is_enum"]       = true;
                if (f.isValueType) fe["is_value_type"] = true;
                const std::string& decodeType = (f.isEnum && !f.underlyingTypeName.empty())
                    ? f.underlyingTypeName : f.typeName;
                json d = decodeField_(decodeType, fieldAddr);
                for (auto kv = d.begin(); kv != d.end(); ++kv) fe[kv.key()] = kv.value();
                // Phase 31-2 (Phase B) — value-type struct field on an array
                // element fans out (one extra level). See the Mono twin.
                if (valueStructFields_ &&
                    ShouldExpandValueStruct(f.isValueType, f.isEnum,
                        f.valueTypeClass, f.typeName, fe)) {
                    json sub = CaptureIl2cppStructFields(decodeField_,
                        f.valueTypeClass, fieldAddr, 2, structMemberCap_);
                    if (sub.contains("fields")) {
                        fe.erase("ptr");
                        fe["fields"] = std::move(sub["fields"]);
                    }
                }
                efields.push_back(std::move(fe));
                ++produced;
            }
        }
        ++decodedElementSets_;
        return json{{"fields", std::move(efields)}};
    }

    Snapshot::ClassPayload DecodeOneClass(Snapshot::ClassPlanItem& item) {
        IL2CPP::Il2CppClass* k = static_cast<IL2CPP::Il2CppClass*>(item.klass);
        if (k == nullptr) {
            return Snapshot::ClassPayload{
                {"assembly", item.assembly},
                {"namespace", item.ns},
                {"name", item.name},
                {"error", "klass null"},
            };
        }
        // Budget tripped (memory watchdog or total-instance budget) — emit
        // class metadata only so the snapshot still lists it.
        if (memBudgetHit_ || countBudgetHit_) {
            return Snapshot::ClassPayload{
                {"assembly", item.assembly},
                {"namespace", item.ns},
                {"name", item.name},
                {"truncated", memBudgetHit_ ? "memory_budget" : "instance_budget"},
            };
        }

        const auto& r = IL2CPP::GetResolver();
        Snapshot::ClassPayload classEntry = {
            {"assembly",  item.assembly},
            {"namespace", item.ns},
            {"name",      item.name},
        };

        std::vector<IL2CPP::Il2CppClass*> chain;
        for (IL2CPP::Il2CppClass* cur = k; cur != nullptr && chain.size() < 16; ) {
            chain.push_back(cur);
            if (r.class_get_parent == nullptr) break;
            cur = SehGetParent(r.class_get_parent, cur);
        }
        std::vector<const IL2CPP::ClassInfoResult*> chainInfo;
        chainInfo.reserve(chain.size());
        for (auto* c : chain) chainInfo.push_back(&GetClassInfo(c));

        const auto& res = *chainInfo[0];
        if (!res.ok) {
            classEntry["error"] = "ReadClassInfo failed";
            return classEntry;
        }
        ++decodedClasses_;
        if (!res.info.parent.name.empty()) {
            classEntry["parent"] = Snapshot::ClassPayload{
                {"namespace", res.info.parent.ns},
                {"name",      res.info.parent.name},
            };
        }

        // Static fields — ONE ResolveStaticBaseAddress call per class, then
        // per-field is offset arithmetic. Static VALUE capture is opt-in.
        Snapshot::ClassPayload statics = Snapshot::ClassPayload::array();
        void* staticBase = nullptr;
        bool staticBaseResolved = false;
        for (const auto& f : res.info.fields) {
            if ((f.flags & 0x10u) == 0u) continue;
            Snapshot::ClassPayload srow = {
                {"name",   f.name},
                {"type",   f.typeName},
                {"offset", f.offset},
            };
            if (!captureStaticValues_) {
                srow["error"] = "static_value_not_captured";
            } else {
                if (!staticBaseResolved) {
                    staticBase = IL2CPP::ResolveStaticBaseAddress(k);
                    staticBaseResolved = true;
                }
                if (staticBase == nullptr) {
                    srow["error"] = "static_unresolved";
                } else {
                    std::uintptr_t fa = reinterpret_cast<std::uintptr_t>(staticBase)
                                        + static_cast<std::uintptr_t>(f.offset);
                    srow["address"] = formatHex_(fa);
                    json d = decodeField_(f.typeName, fa);
                    for (auto kv = d.begin(); kv != d.end(); ++kv) {
                        srow[kv.key()] = kv.value();
                    }
                }
            }
            statics.push_back(std::move(srow));
        }
        if (!statics.empty()) classEntry["statics"] = std::move(statics);

        // Phase 30a — build the instance field SCHEMA once per class (sorted
        // by offset), emit it once, then per instance emit only the value
        // array aligned to this order.
        struct FieldDesc {
            const std::string*   name;
            const std::string*   type;
            const std::string*   from;
            const std::string*   decodeType;
            std::size_t          offset;
            bool                 isEnum;
            bool                 isValueType;
            bool                 isArrayShape;
            IL2CPP::Il2CppClass* valueTypeClass;  // Phase 31-2 (Phase B)
        };
        std::vector<FieldDesc> fieldDescs;
        for (std::size_t ci = chain.size(); ci > 0; --ci) {
            const auto& pr = *chainInfo[ci - 1];
            if (!pr.ok) continue;
            for (const auto& f : pr.info.fields) {
                if (f.flags & 0x10u) continue;   // skip statics
                if (fieldDescs.size() >= 4096) break;
                FieldDesc fd;
                fd.name        = &f.name;
                fd.type        = &f.typeName;
                fd.from        = &pr.info.self.name;
                fd.decodeType  = (f.isEnum && !f.underlyingTypeName.empty())
                                    ? &f.underlyingTypeName : &f.typeName;
                fd.offset      = f.offset;
                fd.isEnum      = f.isEnum;
                fd.isValueType = f.isValueType;
                fd.isArrayShape = f.typeName.size() >= 2 &&
                    f.typeName[f.typeName.size() - 1] == ']' &&
                    f.typeName[f.typeName.size() - 2] == '[';
                fd.valueTypeClass = f.valueTypeClass;
                fieldDescs.push_back(fd);
            }
            if (fieldDescs.size() >= 4096) break;
        }
        std::sort(fieldDescs.begin(), fieldDescs.end(),
            [](const FieldDesc& a, const FieldDesc& b) { return a.offset < b.offset; });

        Snapshot::ClassPayload schemaArr = Snapshot::ClassPayload::array();
        for (const auto& fd : fieldDescs) {
            Snapshot::ClassPayload s = {
                {"name",   *fd.name},
                {"type",   *fd.type},
                {"offset", fd.offset},
                {"from",   *fd.from},
            };
            if (fd.isEnum)      s["is_enum"]       = true;
            if (fd.isValueType) s["is_value_type"] = true;
            schemaArr.push_back(std::move(s));
        }
        if (!fieldDescs.empty() && !item.instances.empty())
            classEntry["schema"] = std::move(schemaArr);

        const std::string classFqn =
            item.ns.empty() ? item.name : item.ns + "." + item.name;

        Snapshot::ClassPayload instArr = Snapshot::ClassPayload::array();
        for (std::uintptr_t instAddr : item.instances) {
            if (instanceDecodeBudget_ > 0 && decodedInstances_ >= instanceDecodeBudget_)
                countBudgetHit_ = true;
            if (++memCheckCounter_ >= kSnapshotMemCheckInterval) {
                memCheckCounter_ = 0;
                CheckMemBudget();
            }
            if (memBudgetHit_ || countBudgetHit_) break;

            Snapshot::ClassPayload values = Snapshot::ClassPayload::array();
            for (const auto& fd : fieldDescs) {
                std::uintptr_t fieldAddr = instAddr + static_cast<std::uintptr_t>(fd.offset);
                Snapshot::ClassPayload entry = Snapshot::ClassPayload::object();
                json d = decodeField_(*fd.decodeType, fieldAddr);
                for (auto kv = d.begin(); kv != d.end(); ++kv) entry[kv.key()] = kv.value();

                if (fd.isArrayShape) {
                    if (!IsCapturableArrayType(*fd.type)) {
                        ++skippedOpenGenericArrays_;
                    } else if (entry.contains("ptr") && entry["ptr"].is_string()) {
                        std::uintptr_t arrPtr = 0;
                        const std::string& ps = entry["ptr"].get_ref<const std::string&>();
                        if (ps.size() > 2 && ps[0] == '0' && (ps[1] == 'x' || ps[1] == 'X')) {
                            arrPtr = std::strtoull(ps.c_str() + 2, nullptr, 16);
                        }
                        if (arrPtr != 0) {
                            json elems = walkArrayElementsInline_(
                                arrPtr, *fd.type, arrayCap_,
                                ShouldCaptureInner(classFqn, *fd.name)
                                    ? std::function<json(std::uintptr_t)>(
                                          [this](std::uintptr_t p) { return CaptureElementFields(p); })
                                    : std::function<json(std::uintptr_t)>{});
                            if (elems.contains("error")) {
                                entry["arrayError"] = elems.value("error", std::string{"unknown"});
                                if (elems.contains("length")) {
                                    entry["arrayErrorLength"] = elems["length"];
                                }
                            } else {
                                entry["arrayElements"] = std::move(elems);
                                ++decodedArrays_;
                            }
                        }
                    }
                } else if (valueStructFields_ &&
                           ShouldExpandValueStruct(fd.isValueType, fd.isEnum,
                               fd.valueTypeClass, *fd.type, entry)) {
                    // Phase 31-2 (Phase B) — generic value-type struct member
                    // fan-out (gated). Named math structs already done in decodeField.
                    json sub = CaptureIl2cppStructFields(decodeField_,
                        fd.valueTypeClass, fieldAddr, kStructMaxDepth, structMemberCap_);
                    if (sub.contains("fields")) {
                        entry.erase("ptr");
                        entry["fields"] = std::move(sub["fields"]);
                    }
                }
                values.push_back(std::move(entry));
            }
            Snapshot::ClassPayload instEntry = { {"address", formatHex_(instAddr)} };
            instEntry["v"] = std::move(values);
            instArr.push_back(std::move(instEntry));
            ++decodedInstances_;
        }
        if (!instArr.empty()) classEntry["instances"] = std::move(instArr);

        return classEntry;
    }

    IL2CPP::Il2CppDomain* domain_;
    std::vector<Snapshot::ClassPlanItem> plan_;
    std::unordered_map<IL2CPP::Il2CppClass*, char> knownIlKlasses_;
    std::size_t arrayCap_;
    std::size_t instanceDecodeBudget_;
    std::size_t elementFieldCap_;
    std::size_t elementCaptureBudget_;
    std::vector<std::string> arrayElementTargets_;
    bool arrayElementFields_;
    bool captureStaticValues_;
    bool valueStructFields_;
    std::size_t structMemberCap_;
    std::uint64_t memBudgetBytes_;

    FormatHexFn   formatHex_;
    DecodeFieldFn decodeField_;
    WalkArrayFn   walkArrayElementsInline_;

    std::unordered_map<IL2CPP::Il2CppClass*, IL2CPP::ClassInfoResult> globalClassInfo_;

    std::size_t decodedClasses_           = 0;
    std::size_t decodedInstances_         = 0;
    std::size_t decodedArrays_            = 0;
    std::size_t skippedOpenGenericArrays_ = 0;
    std::size_t classInfoCacheHits_       = 0;
    std::size_t classInfoCacheMisses_     = 0;
    std::size_t elementsProcessed_        = 0;
    std::size_t decodedElementSets_       = 0;
    bool        elementBudgetHit_         = false;

    bool          memBudgetHit_    = false;
    bool          countBudgetHit_  = false;
    std::uint64_t peakPrivate_     = 0;
    std::size_t   memCheckCounter_ = 0;
    bool          drained_         = false;
};
#endif  // DISCRETE_BACKEND_IL2CPP

constexpr DiscreteBackend BackendOfBuild() {
#if defined(DISCRETE_BACKEND_MONO)
    return DiscreteBackend_Mono;
#elif defined(DISCRETE_BACKEND_IL2CPP)
    return DiscreteBackend_IL2CPP;
#else
    return DiscreteBackend_Unknown;
#endif
}

constexpr const char* BackendName(DiscreteBackend b) {
    switch (b) {
        case DiscreteBackend_Mono:   return "Mono";
        case DiscreteBackend_IL2CPP: return "IL2CPP";
        default: return "Unknown";
    }
}

// Phase 25 — ASCII case-insensitive substring search. Field / method
// names are always ASCII in C# Mono / IL2CPP, so this avoids dragging
// in <locale> or Unicode-folded comparison logic.
bool ICaseContainsAscii(std::string_view haystack, std::string_view needle) {
    if (needle.empty()) return true;
    if (needle.size() > haystack.size()) return false;
    auto fold = [](unsigned char c) -> unsigned char {
        return (c >= 'A' && c <= 'Z') ? static_cast<unsigned char>(c | 0x20) : c;
    };
    for (std::size_t i = 0; i + needle.size() <= haystack.size(); ++i) {
        bool match = true;
        for (std::size_t j = 0; j < needle.size(); ++j) {
            if (fold(static_cast<unsigned char>(haystack[i + j])) !=
                fold(static_cast<unsigned char>(needle[j]))) {
                match = false;
                break;
            }
        }
        if (match) return true;
    }
    return false;
}

// Phase 29a-v3 — default-exclude for the snapshot "全抓" scope. Live-smoke
// on 活俠傳 (Mono x86) showed that capturing every assembly grabs 15,935
// classes — ~80% of which are mscorlib / System.* / UnityEngine.* /
// Unity.* / Fungus noise that the user almost certainly doesn't care
// about as game state. Same pattern across all Unity Mono targets:
// system + engine assemblies dominate the class count, drowning the
// user-game assemblies in the diff results.
//
// The user-facing rule: an empty `assemblies` arg means "every USER
// assembly" — exclude well-known system/engine prefixes by default.
// Caller can pass `includeSystemAssemblies: true` to keep everything.
//
// Match shape: exact prefix match where the next character (if any) is
// '.' or ' '. Catches:
//   "mscorlib"            → excluded
//   "System"              → excluded (exact)
//   "System.Xml"          → excluded (System. prefix)
//   "SystemException"     → NOT excluded (no '.')
//   "UnityEngine"         → excluded (older Unity monolithic)
//   "UnityEngine.CoreModule" → excluded
//   "Unity.InputSystem"   → excluded
//   "Mortal.Core"         → NOT excluded (user game)
//   "Assembly-CSharp"     → NOT excluded (default user assembly)
bool IsSystemOrEngineAssembly(const std::string& name) {
    static const char* const kSystemPrefixes[] = {
        "mscorlib",
        "System",
        "Microsoft",
        "netstandard",
        "Mono",
        "UnityEngine",
        "Unity",
        // Common large support libraries that ship with many Unity games
        // and that aren't gameplay state. Add more as new targets reveal
        // similar bloat.
        "Fungus",
    };
    for (const char* p : kSystemPrefixes) {
        const std::size_t pl = std::strlen(p);
        if (name.size() < pl) continue;
        if (name.compare(0, pl, p) != 0) continue;
        // Strict suffix: exact match OR followed by '.' / ' '. Rejects
        // false-matches like "SystemException".
        if (name.size() == pl || name[pl] == '.' || name[pl] == ' ') {
            return true;
        }
    }
    return false;
}

// Phase 27a — shared helpers for the scanFieldValue handler (both backends).
// `Memory::` below resolves to Discrete::Memory:: via this using; the
// alternative is to fully qualify each occurrence, which makes the helper
// signatures unreadable. Phase 27b adds the `ValueScan` alias for the
// session container — same rationale.
namespace Memory    = ::Discrete::Memory;
namespace ValueScan = ::Discrete::ValueScan;
namespace Snapshot  = ::Discrete::Snapshot;

// Wire dataType string → ValueDataType enum. Returns false on unknown string.
bool ParseValueDataType(std::string_view s, Memory::ValueDataType& out) {
    if (s == "int8"   || s == "sbyte"  || s == "System.SByte")   { out = Memory::ValueDataType::Int8;   return true; }
    if (s == "uint8"  || s == "byte"   || s == "System.Byte")    { out = Memory::ValueDataType::UInt8;  return true; }
    if (s == "int16"  || s == "short"  || s == "System.Int16")   { out = Memory::ValueDataType::Int16;  return true; }
    if (s == "uint16" || s == "ushort" || s == "System.UInt16")  { out = Memory::ValueDataType::UInt16; return true; }
    if (s == "int32"  || s == "int"    || s == "System.Int32")   { out = Memory::ValueDataType::Int32;  return true; }
    if (s == "uint32" || s == "uint"   || s == "System.UInt32")  { out = Memory::ValueDataType::UInt32; return true; }
    if (s == "int64"  || s == "long"   || s == "System.Int64")   { out = Memory::ValueDataType::Int64;  return true; }
    if (s == "uint64" || s == "ulong"  || s == "System.UInt64")  { out = Memory::ValueDataType::UInt64; return true; }
    if (s == "float"  || s == "single" || s == "System.Single")  { out = Memory::ValueDataType::Float;  return true; }
    if (s == "double" || s == "System.Double")                   { out = Memory::ValueDataType::Double; return true; }
    return false;
}

// Map ValueDataType → canonical CLR type-name string used by decodeField /
// walkInstance. Used to filter whatIsAt-resolved field types when the
// caller requests strict type matching.
const char* ValueDataTypeClrName(Memory::ValueDataType dt) {
    switch (dt) {
        case Memory::ValueDataType::Int8:   return "System.SByte";
        case Memory::ValueDataType::UInt8:  return "System.Byte";
        case Memory::ValueDataType::Int16:  return "System.Int16";
        case Memory::ValueDataType::UInt16: return "System.UInt16";
        case Memory::ValueDataType::Int32:  return "System.Int32";
        case Memory::ValueDataType::UInt32: return "System.UInt32";
        case Memory::ValueDataType::Int64:  return "System.Int64";
        case Memory::ValueDataType::UInt64: return "System.UInt64";
        case Memory::ValueDataType::Float:  return "System.Single";
        case Memory::ValueDataType::Double: return "System.Double";
    }
    return "System.Object";
}

// JSON value → packed 8-byte buffer per the requested dataType. The wire
// allows: number (int / float), hex string ("0x...") for unsigned-64 path,
// or a string-encoded number for clients that don't trust JSON precision
// on 64-bit ints. Returns false if the value is out of range or wrong kind.
bool PackJsonValueToBytes(const json& v, Memory::ValueDataType dt, std::uint8_t out[8]) {
    std::memset(out, 0, 8);
    // For signed ints, allow negative JSON numbers; for unsigned-64 we may
    // need to accept hex strings to cover the > 2^53 range that JSON
    // doubles can't represent exactly.
    auto asI64 = [&](std::int64_t& iv) -> bool {
        if (v.is_number_integer())        { iv = v.get<std::int64_t>(); return true; }
        if (v.is_number_unsigned())       { iv = static_cast<std::int64_t>(v.get<std::uint64_t>()); return true; }
        if (v.is_number_float())          { iv = static_cast<std::int64_t>(v.get<double>()); return true; }
        if (v.is_string()) {
            const std::string& s = v.get_ref<const std::string&>();
            try {
                if (s.size() > 2 && (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))) {
                    iv = static_cast<std::int64_t>(std::stoll(s.substr(2), nullptr, 16));
                } else {
                    iv = std::stoll(s, nullptr, 10);
                }
                return true;
            } catch (...) { return false; }
        }
        return false;
    };
    auto asU64 = [&](std::uint64_t& uv) -> bool {
        if (v.is_number_unsigned())       { uv = v.get<std::uint64_t>(); return true; }
        if (v.is_number_integer())        { auto s = v.get<std::int64_t>(); if (s < 0) return false; uv = static_cast<std::uint64_t>(s); return true; }
        if (v.is_number_float())          { auto d = v.get<double>(); if (d < 0.0) return false; uv = static_cast<std::uint64_t>(d); return true; }
        if (v.is_string()) {
            const std::string& s = v.get_ref<const std::string&>();
            try {
                if (s.size() > 2 && (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))) {
                    uv = std::stoull(s.substr(2), nullptr, 16);
                } else {
                    uv = std::stoull(s, nullptr, 10);
                }
                return true;
            } catch (...) { return false; }
        }
        return false;
    };
    auto asF64 = [&](double& dv) -> bool {
        if (v.is_number())                { dv = v.get<double>(); return true; }
        if (v.is_string()) {
            const std::string& s = v.get_ref<const std::string&>();
            try { dv = std::stod(s); return true; } catch (...) { return false; }
        }
        return false;
    };

    switch (dt) {
        case Memory::ValueDataType::Int8: {
            std::int64_t iv; if (!asI64(iv)) return false;
            if (iv < INT8_MIN || iv > INT8_MAX) return false;
            std::int8_t t = static_cast<std::int8_t>(iv); std::memcpy(out, &t, 1); return true;
        }
        case Memory::ValueDataType::UInt8: {
            std::uint64_t uv; if (!asU64(uv)) return false;
            if (uv > UINT8_MAX) return false;
            std::uint8_t t = static_cast<std::uint8_t>(uv); std::memcpy(out, &t, 1); return true;
        }
        case Memory::ValueDataType::Int16: {
            std::int64_t iv; if (!asI64(iv)) return false;
            if (iv < INT16_MIN || iv > INT16_MAX) return false;
            std::int16_t t = static_cast<std::int16_t>(iv); std::memcpy(out, &t, 2); return true;
        }
        case Memory::ValueDataType::UInt16: {
            std::uint64_t uv; if (!asU64(uv)) return false;
            if (uv > UINT16_MAX) return false;
            std::uint16_t t = static_cast<std::uint16_t>(uv); std::memcpy(out, &t, 2); return true;
        }
        case Memory::ValueDataType::Int32: {
            std::int64_t iv; if (!asI64(iv)) return false;
            if (iv < INT32_MIN || iv > INT32_MAX) return false;
            std::int32_t t = static_cast<std::int32_t>(iv); std::memcpy(out, &t, 4); return true;
        }
        case Memory::ValueDataType::UInt32: {
            std::uint64_t uv; if (!asU64(uv)) return false;
            if (uv > UINT32_MAX) return false;
            std::uint32_t t = static_cast<std::uint32_t>(uv); std::memcpy(out, &t, 4); return true;
        }
        case Memory::ValueDataType::Int64: {
            std::int64_t iv; if (!asI64(iv)) return false;
            std::memcpy(out, &iv, 8); return true;
        }
        case Memory::ValueDataType::UInt64: {
            std::uint64_t uv; if (!asU64(uv)) return false;
            std::memcpy(out, &uv, 8); return true;
        }
        case Memory::ValueDataType::Float: {
            double dv; if (!asF64(dv)) return false;
            float t = static_cast<float>(dv); std::memcpy(out, &t, 4); return true;
        }
        case Memory::ValueDataType::Double: {
            double dv; if (!asF64(dv)) return false;
            std::memcpy(out, &dv, 8); return true;
        }
    }
    return false;
}

// Decode the raw value bytes back to a JSON-friendly representation for
// the scanFieldValue response's `currentValue` field. Uses the same wire
// shape as decodeField's primitive paths.
json DecodeValueBytesToJson(const std::uint8_t bytes[8], Memory::ValueDataType dt) {
    switch (dt) {
        case Memory::ValueDataType::Int8:   { std::int8_t   v; std::memcpy(&v, bytes, 1); return static_cast<int>(v); }
        case Memory::ValueDataType::UInt8:  { std::uint8_t  v; std::memcpy(&v, bytes, 1); return static_cast<unsigned>(v); }
        case Memory::ValueDataType::Int16:  { std::int16_t  v; std::memcpy(&v, bytes, 2); return v; }
        case Memory::ValueDataType::UInt16: { std::uint16_t v; std::memcpy(&v, bytes, 2); return v; }
        case Memory::ValueDataType::Int32:  { std::int32_t  v; std::memcpy(&v, bytes, 4); return v; }
        case Memory::ValueDataType::UInt32: { std::uint32_t v; std::memcpy(&v, bytes, 4); return v; }
        case Memory::ValueDataType::Int64:  { std::int64_t  v; std::memcpy(&v, bytes, 8); return v; }
        case Memory::ValueDataType::UInt64: { std::uint64_t v; std::memcpy(&v, bytes, 8); return v; }
        case Memory::ValueDataType::Float:  { float         v; std::memcpy(&v, bytes, 4); return v; }
        case Memory::ValueDataType::Double: { double        v; std::memcpy(&v, bytes, 8); return v; }
    }
    return nullptr;
}

// Phase 27b-4 — wire encoding string → StringEncoding enum.
bool ParseStringEncoding(std::string_view s, Memory::StringEncoding& out) {
    if (s == "utf16" || s == "utf-16" || s == "utf16le" || s.empty()) {
        out = Memory::StringEncoding::Utf16Le; return true;
    }
    if (s == "utf8" || s == "utf-8") {
        out = Memory::StringEncoding::Utf8;    return true;
    }
    if (s == "ascii") {
        out = Memory::StringEncoding::Ascii;   return true;
    }
    return false;
}

bool ParseStringMatchMode(std::string_view s, Memory::StringMatchMode& out) {
    if (s == "contains" || s.empty()) { out = Memory::StringMatchMode::Contains; return true; }
    if (s == "exact")                 { out = Memory::StringMatchMode::Exact;    return true; }
    return false;
}

// Encode a UTF-8 wire string into the requested target encoding (bytes).
// Returns empty on failure (caller maps to wire error).
std::vector<std::uint8_t> EncodeNeedleForString(std::string_view utf8In,
                                                  Memory::StringEncoding enc) {
    std::vector<std::uint8_t> out;
    if (utf8In.empty()) return out;
    switch (enc) {
        case Memory::StringEncoding::Ascii: {
            out.reserve(utf8In.size());
            for (unsigned char ch : utf8In) {
                if (ch >= 0x80) return {};   // non-ASCII in ASCII mode
                out.push_back(ch);
            }
            return out;
        }
        case Memory::StringEncoding::Utf8: {
            out.assign(utf8In.begin(), utf8In.end());
            return out;
        }
        case Memory::StringEncoding::Utf16Le: {
            // UTF-8 → UTF-16LE via Windows API. CP_UTF8 = 65001.
            int wc = ::MultiByteToWideChar(65001, 0, utf8In.data(),
                                            static_cast<int>(utf8In.size()),
                                            nullptr, 0);
            if (wc <= 0) return {};
            std::vector<wchar_t> wbuf(static_cast<std::size_t>(wc));
            ::MultiByteToWideChar(65001, 0, utf8In.data(),
                                  static_cast<int>(utf8In.size()),
                                  wbuf.data(), wc);
            out.assign(reinterpret_cast<const std::uint8_t*>(wbuf.data()),
                       reinterpret_cast<const std::uint8_t*>(wbuf.data() + wc));
            return out;
        }
    }
    return out;
}

// Decode an arbitrary byte sequence into a UTF-8 display string for the
// scanFieldValue response's `currentValue` field on string hits. The
// sequence is the matched needle bytes — what we displayed at scan time.
std::string DecodeMatchedNeedleToUtf8(const std::vector<std::uint8_t>& bytes,
                                       Memory::StringEncoding enc) {
    if (bytes.empty()) return {};
    switch (enc) {
        case Memory::StringEncoding::Ascii:
        case Memory::StringEncoding::Utf8:
            return std::string(bytes.begin(), bytes.end());
        case Memory::StringEncoding::Utf16Le: {
            const wchar_t* w = reinterpret_cast<const wchar_t*>(bytes.data());
            int wcLen = static_cast<int>(bytes.size() / sizeof(wchar_t));
            int u8 = ::WideCharToMultiByte(65001, 0, w, wcLen, nullptr, 0, nullptr, nullptr);
            if (u8 <= 0) return {};
            std::string out(static_cast<std::size_t>(u8), '\0');
            ::WideCharToMultiByte(65001, 0, w, wcLen, out.data(), u8, nullptr, nullptr);
            return out;
        }
    }
    return {};
}

// Wire scanType string → ValueScanType enum (Memory-level: Exact / Range
// are the only modes the raw memory scan supports). The session-level
// modes (changed / unchanged / increased / decreased / newValue) are
// handled in `CompareCandidateForRefine` below.
bool ParseValueScanType(std::string_view s, Memory::ValueScanType& out) {
    if (s == "exact" || s.empty()) { out = Memory::ValueScanType::Exact; return true; }
    if (s == "range")              { out = Memory::ValueScanType::Range; return true; }
    return false;
}

// Phase 27b — session-level refine comparator. Returns true (keep) or
// false (drop) for one candidate. When kept, `prev` is updated to `cur`
// so the next refine round compares against THIS round's value.
//
// scanType values:
//   exact      : cur == arg.value (range collapsed to single point)
//   range      : arg.value <= cur <= arg.valueTo (typed)
//   newValue   : cur == arg.value (alias for exact — semantic distinction
//                only matters in the UI: "I now want a different value")
//   changed    : cur != prev
//   unchanged  : cur == prev
//   increased  : cur > prev (typed)
//   decreased  : cur < prev (typed)
// `argLo` / `argHi` are 8-byte payloads packed by PackJsonValueToBytes
// (only meaningful for exact / range / newValue).
//
// `cur` is read from process memory before this is called; if the read
// failed, the caller should drop the candidate without invoking this.
bool CompareCandidateForRefine(Memory::ValueDataType dt,
                                std::string_view      scanType,
                                const std::uint8_t    cur[8],
                                std::uint8_t          prev[8],
                                const std::uint8_t    argLo[8],
                                const std::uint8_t    argHi[8],
                                double                tolerance) {
    auto updatePrev = [&](bool keep) {
        if (keep) std::memcpy(prev, cur, Memory::ValueDataTypeSize(dt));
        return keep;
    };

    #define LOAD(T, name, src) T name; std::memcpy(&name, src, sizeof(T))
    #define BODY_INT(T) {                                                         \
        LOAD(T, vCur, cur);  LOAD(T, vPrev, prev);                                \
        if (scanType == "exact" || scanType == "newValue") {                      \
            LOAD(T, vArg, argLo); return updatePrev(vCur == vArg);                \
        }                                                                          \
        if (scanType == "range") {                                                \
            LOAD(T, vLo, argLo); LOAD(T, vHi, argHi);                             \
            return updatePrev(vCur >= vLo && vCur <= vHi);                        \
        }                                                                          \
        if (scanType == "changed")    return updatePrev(vCur != vPrev);           \
        if (scanType == "unchanged")  return updatePrev(vCur == vPrev);           \
        if (scanType == "increased")  return updatePrev(vCur >  vPrev);           \
        if (scanType == "decreased")  return updatePrev(vCur <  vPrev);           \
        return false;                                                              \
    }
    #define BODY_FLT(T) {                                                         \
        LOAD(T, vCur, cur);  LOAD(T, vPrev, prev);                                \
        if (!std::isfinite(static_cast<double>(vCur))) return false;              \
        if (scanType == "exact" || scanType == "newValue") {                      \
            LOAD(T, vArg, argLo);                                                  \
            bool ok = std::fabs(static_cast<double>(vCur)                          \
                              - static_cast<double>(vArg)) <= tolerance;           \
            return updatePrev(ok);                                                 \
        }                                                                          \
        if (scanType == "range") {                                                \
            LOAD(T, vLo, argLo); LOAD(T, vHi, argHi);                             \
            return updatePrev(vCur >= vLo && vCur <= vHi);                        \
        }                                                                          \
        if (scanType == "changed")    return updatePrev(vCur != vPrev);           \
        if (scanType == "unchanged")  return updatePrev(vCur == vPrev);           \
        if (scanType == "increased")  return updatePrev(vCur >  vPrev);           \
        if (scanType == "decreased")  return updatePrev(vCur <  vPrev);           \
        return false;                                                              \
    }

    switch (dt) {
        case Memory::ValueDataType::Int8:   BODY_INT(std::int8_t);
        case Memory::ValueDataType::UInt8:  BODY_INT(std::uint8_t);
        case Memory::ValueDataType::Int16:  BODY_INT(std::int16_t);
        case Memory::ValueDataType::UInt16: BODY_INT(std::uint16_t);
        case Memory::ValueDataType::Int32:  BODY_INT(std::int32_t);
        case Memory::ValueDataType::UInt32: BODY_INT(std::uint32_t);
        case Memory::ValueDataType::Int64:  BODY_INT(std::int64_t);
        case Memory::ValueDataType::UInt64: BODY_INT(std::uint64_t);
        case Memory::ValueDataType::Float:  BODY_FLT(float);
        case Memory::ValueDataType::Double: BODY_FLT(double);
    }
    #undef BODY_INT
    #undef BODY_FLT
    #undef LOAD
    return false;
}

// Phase 27 hardening (2026-05-20 night) — real-game scans get dominated
// by large static arrays (UIElements.IPointerEvent / compiler-generated
// `<PrivateImplementationDetails>/__StaticArrayInitTypeSize=N`) when the
// search value happens to appear in literal init data. One array can
// flood thousands of array-element hits and starve the candidate budget
// before the actual gameplay field is reached.
//
// Two filters mitigate this:
//   1. Per-instance cap — at most kPerInstanceCap hits from the same
//      managed instance. Stops one big array from monopolising results.
//   2. Compiler-generated container filter — drop any class whose name
//      starts with '<' (PrivateImplementationDetails, <>c__DisplayClass,
//      generic anonymous types). These are never user-modifiable state.
//
// Both filters apply post-whatIsAt, so non-managed memory still gets
// `droppedNonManaged`. Diversity drops counted separately.
constexpr int kPerInstanceCap = 3;

// Phase 30 P2 perf-gate — only build the reverse-lookup interval-tree when the
// raw candidate count is large enough to amortize the one-shot heap walk. The
// index trades a fixed full-heap walk for O(log N) per-candidate lookups; for a
// scan that returns FEW candidates (e.g. a specific value like 10160 → ~150
// hits) the per-candidate backward scan (WhatIsAt) is far cheaper than the
// build. This matters most on Mono, whose index build is single-threaded by
// design (the parallel walk risks the 活俠傳 BDWGC freeze) — measured ~48s to
// walk a ~2 GB heap, which dwarfed a 152-candidate scan (the 51s regression).
// Below the threshold both backends fall back to per-candidate WhatIsAt.
constexpr std::size_t kRevIndexMinCandidates = 5000;

bool IsCompilerGeneratedClassName(std::string_view name) noexcept {
    if (name.empty()) return false;
    if (name.front() == '<') return true;
    // C# compiler sometimes prefixes with the namespace, e.g.
    // "MyNamespace.<PrivateImplementationDetails>". Check for '<' after
    // the last namespace separator.
    auto pos = name.rfind('.');
    if (pos != std::string_view::npos && pos + 1 < name.size() && name[pos + 1] == '<') {
        return true;
    }
    return false;
}

// Common: emit a Refine candidate as JSON row. Same shape as the global
// scanFieldValue response so UI / Lua bridge code can render either with
// the same template.
json EmitCandidateRow(const ValueScan::Candidate& c,
                      Memory::ValueDataType dt,
                      const std::function<std::string(std::uintptr_t)>& fmtHex) {
    json row = {
        {"addr",         fmtHex(c.addr)},
        {"instAddr",     fmtHex(c.instAddr)},
        {"asm",          c.asmName},
        {"ns",           c.ns},
        {"class",        c.className},
        {"fieldOffset",  c.fieldOffset},
        {"fieldKind",    c.fieldKind},
        {"currentValue", DecodeValueBytesToJson(c.prevValue, dt)},
    };
    if (!c.field.empty())     row["field"]     = c.field;
    if (!c.fieldType.empty()) row["fieldType"] = c.fieldType;
    if (!c.nearestFieldName.empty()) {
        row["nearestField"] = json{
            {"name",  c.nearestFieldName},
            {"delta", c.nearestFieldDelta},
        };
    }
    return row;
}

std::string Utf8FromWide(const std::wstring& w) {
    if (w.empty()) return {};
    int n = ::WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()),
                                  nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(static_cast<std::size_t>(n), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()),
                          out.data(), n, nullptr, nullptr);
    return out;
}

json BuildHandshakeJson() {
    using namespace Discrete;
    auto d = BackendDetector::Detect();

    json j;
    j["engine"]       = "Unity";
    j["backend"]      = BackendDetector::BackendName(d.backend);
    j["dll_variant"]  = BackendName(BackendOfBuild());
    j["game_module"]  = Utf8FromWide(d.gameModule);
    j["unity_version"]    = d.unityVersion ? json(*d.unityVersion) : json(nullptr);
    j["metadata_version"] = d.metadataVersion ? json(*d.metadataVersion) : json(nullptr);

    std::uint32_t assembliesCount = 0;
#if defined(DISCRETE_BACKEND_MONO)
    if (d.backend == BackendDetector::Backend::Mono && Mono::IsResolved()) {
        assembliesCount = static_cast<std::uint32_t>(Mono::EnumerateAssemblies().size());
    }
#elif defined(DISCRETE_BACKEND_IL2CPP)
    if (d.backend == BackendDetector::Backend::IL2CPP && IL2CPP::IsResolved()) {
        assembliesCount = static_cast<std::uint32_t>(IL2CPP::EnumerateAssemblies().size());
    }
#endif
    j["assemblies_count"] = assembliesCount;
    j["build"]    = static_cast<std::uint32_t>(DISCRETE_BUILD_NUMBER);
    j["protocol"] = 1;
    // Phase 29b — compute the same gameBuildId hash that snapshotBegin
    // emits so the UI can detect "we reconnected to a different game"
    // without taking a snapshot first. Same fingerprint scheme
    // (FNV-1a over module path + file size + last-write-time).
#if defined(DISCRETE_BACKEND_MONO)
    j["game_build_id"] = Snapshot::ComputeGameBuildId(MagicStrings::ModuleMonoBdwgc);
    if (j["game_build_id"].get<std::string>().empty()) {
        j["game_build_id"] = Snapshot::ComputeGameBuildId(MagicStrings::ModuleMonoLegacy);
    }
#elif defined(DISCRETE_BACKEND_IL2CPP)
    j["game_build_id"] = Snapshot::ComputeGameBuildId(MagicStrings::ModuleIL2CPP);
#endif
    // Game-process memory + bitness so UI can auto-clamp snapshot caps
    // on x86 targets (where DLL-side JSON cache can push past 4 GB
    // user space). pointer_size is sizeof(void*) in the game's
    // address space — 4 on WOW64/x86, 8 on x64. workingSet and
    // privateUsage are sampled fresh per handshake call so the UI
    // sees current pressure (handshake fires on every reconnect).
    const ProcessMemorySnapshot mem = QueryProcessMemory();
    j["game_pointer_size"] = mem.pointerSize;
    j["game_working_set"]  = mem.workingSet;
    j["game_private_usage"] = mem.privateUsage;
    return j;
}

// Phase 20-MT — idempotent lazy install of the main-thread dispatcher +
// runtime_invoke hook. Called from handshake/scan; safe to call repeatedly
// because both Init() and Install() are guarded by atomic compare-exchange.
//
// Lazy install lets us inject the DLL even before Unity has finished
// starting up — resolver might not be bound yet on first attach, but by
// the time the UI's handshake / scan RPC arrives the Mono / IL2CPP
// runtime is live and runtime_invoke is bindable. After install, the
// detour piggybacks on Unity's per-frame runtime_invoke dispatch to drain
// our queue + record main-thread tick freshness for `IsHealthy()`.
//
// j is filled in by the caller (handshake/scan response); we add a
// "main_thread" sub-object showing dispatcher state so the UI can surface
// it (e.g. "queued: 0, last tick 8 ms ago, hook installed").
void TryInstallMainThreadHookAndReport(json& j) {
    using namespace Discrete;
    using namespace Discrete::MainThreadDispatcher;

    if (!IsInitialized()) {
        (void)Init();
    }
#if defined(DISCRETE_BACKEND_MONO)
    if (IsInitialized() && Mono::IsResolved() && !Mono::MainThreadHook::IsInstalled()) {
        (void)Mono::MainThreadHook::Install();
    }
    const bool hookInstalled = Mono::MainThreadHook::IsInstalled();
#elif defined(DISCRETE_BACKEND_IL2CPP)
    if (IsInitialized() && IL2CPP::IsResolved() && !IL2CPP::MainThreadHook::IsInstalled()) {
        (void)IL2CPP::MainThreadHook::Install();
    }
    const bool hookInstalled = IL2CPP::MainThreadHook::IsInstalled();
#else
    const bool hookInstalled = false;
#endif

    Stats s = GetStats();
    json mt = {
        {"initialized",     s.initialized},
        {"hook_installed",  hookInstalled},
        {"main_thread_id",  s.main_thread_id},
        {"tick_count",      s.tick_count},
        {"last_tick_age_ms", s.last_tick_age_ms},
        {"queue_depth",     s.queue_depth},
        {"is_healthy",      IsHealthy()},
    };
    j["main_thread"] = std::move(mt);
}

void RegisterCoreHandlers() {
    using namespace Discrete;
    PipeServer::RegisterHandler("ping", [](const json&) -> json {
        return json{{"pong", true}};
    });
    PipeServer::RegisterHandler(MagicStrings::CmdHandshake, [](const json&) -> json {
        json j = BuildHandshakeJson();
        // Phase 20-MT: piggyback hook install on handshake so the dispatcher
        // is live by the time the user's first callMethod arrives. Adds a
        // `main_thread` sub-object to the response for UI visibility.
        TryInstallMainThreadHookAndReport(j);
        return j;
    });
    // Phase 2: `scan` re-runs backend detection and returns the same shape as handshake.
    // Phase 17a follow-up: also drops the address-finder class-pointer cache so
    // newly-loaded assemblies get picked up by the next whatIsAt call.
    PipeServer::RegisterHandler(MagicStrings::CmdScan, [](const json&) -> json {
#if defined(DISCRETE_BACKEND_MONO)
        Mono::InvalidateClassRangeCache();
#elif defined(DISCRETE_BACKEND_IL2CPP)
        IL2CPP::InvalidateClassRangeCache();
#endif
        json j = BuildHandshakeJson();
        TryInstallMainThreadHookAndReport(j);
        return j;
    });

    // Phase 20-MT diagnostics — read-only snapshot of the dispatcher's
    // internal counters. Useful for the stress smoke (`Test-MainThreadBurst.ps1`)
    // and for the UI to show whether the hook is wired + healthy.
    PipeServer::RegisterHandler(MagicStrings::CmdGetDispatcherStats, [](const json&) -> json {
        using namespace Discrete::MainThreadDispatcher;
        Stats s = GetStats();
#if defined(DISCRETE_BACKEND_MONO)
        const bool hookInstalled = Mono::MainThreadHook::IsInstalled();
#elif defined(DISCRETE_BACKEND_IL2CPP)
        const bool hookInstalled = IL2CPP::MainThreadHook::IsInstalled();
#else
        const bool hookInstalled = false;
#endif
        return json{
            {"initialized",      s.initialized},
            {"hook_installed",   hookInstalled},
            {"main_thread_id",   s.main_thread_id},
            {"tick_count",       s.tick_count},
            {"submit_count",     s.submit_count},
            {"drain_count",      s.drain_count},
            {"timeout_count",    s.timeout_count},
            {"reject_count",     s.reject_count},
            {"last_tick_age_ms", s.last_tick_age_ms},
            {"queue_depth",      s.queue_depth},
            {"is_healthy",       IsHealthy()},
        };
    });

    // Phase 27b — refineValueScan + endValueScan. Backend-agnostic: refine
    // walks the session's candidate vector and re-reads each candidate's
    // value via SafeReadBytes (no Mono/IL2CPP runtime APIs), end drops the
    // session. Both handlers registered in RegisterCoreHandlers so they
    // exist in both DLL flavors without duplication. `beginValueScan` IS
    // backend-specific (uses Mono::WhatIsAt or IL2CPP::WhatIsAt) so it
    // stays inside the #if blocks.
    auto formatHexCore = [](std::uintptr_t v) -> std::string {
        char buf[32];
        std::snprintf(buf, sizeof(buf), "0x%016llX", static_cast<unsigned long long>(v));
        return std::string(buf);
    };
    PipeServer::RegisterHandler(MagicStrings::CmdRefineValueScan,
        [formatHexCore](const json& args) -> json {
        if (!args.contains("sessionId")) return json{{"error", "missing 'sessionId'"}};
        std::uint64_t sessionId = 0;
        try {
            sessionId = std::stoull(args["sessionId"].get<std::string>());
        } catch (...) { return json{{"error", "invalid 'sessionId'"}}; }

        std::string stStr = args.value("scanType", std::string{"unchanged"});
        static const std::array<std::string_view, 7> kValidScanTypes = {
            "exact", "range", "newValue", "changed", "unchanged", "increased", "decreased"
        };
        bool stOk = false;
        for (const auto& v : kValidScanTypes) if (stStr == v) { stOk = true; break; }
        if (!stOk) return json{{"error", "unknown 'scanType'"}};

        std::size_t emitLimit = static_cast<std::size_t>(args.value("limit", 5000));
        if (emitLimit == 0 || emitLimit > 50000) emitLimit = 5000;
        double tolerance = args.value("tolerance", 0.0001);

        json rows = json::array();
        std::size_t totalCands = 0, removed = 0;
        std::string errorOut;
        std::string dtStrOut;

        auto fmtHex = std::function<std::string(std::uintptr_t)>(formatHexCore);
        bool ok = ValueScan::SessionManager::Instance().RefineWith(sessionId,
            [&](Memory::ValueDataType dt, std::vector<ValueScan::Candidate>& cands) {
            const std::size_t vSize = Memory::ValueDataTypeSize(dt);
            std::uint8_t argLo[8] = {};
            std::uint8_t argHi[8] = {};
            const bool needsArg = (stStr == "exact" || stStr == "range" || stStr == "newValue");
            if (needsArg) {
                if (!args.contains("value")) { errorOut = "missing 'value' for scanType=" + stStr; return; }
                if (!PackJsonValueToBytes(args["value"], dt, argLo)) {
                    errorOut = "'value' out of range for session dataType"; return;
                }
                if (stStr == "range") {
                    if (!args.contains("valueTo")) { errorOut = "scanType=range requires 'valueTo'"; return; }
                    if (!PackJsonValueToBytes(args["valueTo"], dt, argHi)) {
                        errorOut = "'valueTo' out of range"; return;
                    }
                }
            }

            // Erase-remove with a writer index — O(N) prune in place.
            std::size_t writer = 0;
            for (std::size_t reader = 0; reader < cands.size(); ++reader) {
                auto& c = cands[reader];
                std::uint8_t curBytes[8] = {};
                if (!Memory::SafeReadBytes(reinterpret_cast<void*>(c.addr), curBytes, vSize)) {
                    continue;
                }
                if (!CompareCandidateForRefine(dt, stStr, curBytes, c.prevValue, argLo, argHi, tolerance)) {
                    continue;
                }
                if (reader != writer) cands[writer] = std::move(c);
                ++writer;
            }
            removed = cands.size() - writer;
            cands.resize(writer);
            totalCands = cands.size();

            std::size_t toShow = std::min(totalCands, emitLimit);
            rows = json::array();
            for (std::size_t i = 0; i < toShow; ++i) {
                rows.push_back(EmitCandidateRow(cands[i], dt, fmtHex));
            }
            dtStrOut = std::string(ValueDataTypeClrName(dt));
        });

        if (!ok)               return json{{"error", "session_not_found"}};
        if (!errorOut.empty()) return json{{"error", errorOut}};

        {
            char line[256];
            std::snprintf(line, sizeof(line),
                "refineValueScan sess=%llu type=%s total=%zu removed=%zu",
                static_cast<unsigned long long>(sessionId), stStr.c_str(),
                totalCands, removed);
            Discrete::Log::Write(MagicStrings::LogCategoryScan, line);
        }

        return json{
            {"ok",              true},
            {"sessionId",       std::to_string(sessionId)},
            {"scanType",        stStr},
            {"dataType",        dtStrOut},
            {"count",           rows.size()},
            {"totalCandidates", totalCands},
            {"removed",         removed},
            {"truncated",       totalCands > rows.size()},
            {"candidates",      std::move(rows)},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdEndValueScan,
        [](const json& args) -> json {
        if (!args.contains("sessionId")) return json{{"error", "missing 'sessionId'"}};
        std::uint64_t sessionId = 0;
        try {
            sessionId = std::stoull(args["sessionId"].get<std::string>());
        } catch (...) { return json{{"error", "invalid 'sessionId'"}}; }
        bool dropped = ValueScan::SessionManager::Instance().End(sessionId);
        return json{
            {"ok",        true},
            {"sessionId", std::to_string(sessionId)},
            {"dropped",   dropped},
        };
    });

    // ----------------------------------------------------------------------
    // Phase 6 — backend-agnostic memory ops.
    // readMemory / writeMemory / resolvePointer act on the host process's
    // address space. They use SEH-protected primitives in Memory.cpp so a
    // bad pointer returns an error envelope instead of crashing the game.
    // ----------------------------------------------------------------------

    auto parseAddr = [](const json& v, std::uintptr_t& out) -> bool {
        if (v.is_string()) {
            const std::string s = v.get<std::string>();
            const char* p = s.c_str();
            if (s.size() >= 2 && p[0] == '0' && (p[1] == 'x' || p[1] == 'X')) p += 2;
            char* end = nullptr;
            unsigned long long val = ::_strtoui64(p, &end, 16);
            if (end == p) return false;
            out = static_cast<std::uintptr_t>(val);
            return true;
        }
        if (v.is_number_unsigned()) {
            out = static_cast<std::uintptr_t>(v.get<std::uint64_t>());
            return true;
        }
        if (v.is_number_integer()) {
            out = static_cast<std::uintptr_t>(v.get<std::int64_t>());
            return true;
        }
        return false;
    };

    auto bytesToHex = [](const std::uint8_t* data, std::size_t n) {
        static const char* kHex = "0123456789ABCDEF";
        std::string out;
        out.reserve(n * 2);
        for (std::size_t i = 0; i < n; ++i) {
            out.push_back(kHex[data[i] >> 4]);
            out.push_back(kHex[data[i] & 0xF]);
        }
        return out;
    };

    auto hexToBytes = [](const std::string& hex, std::vector<std::uint8_t>& out) -> bool {
        std::string clean;
        clean.reserve(hex.size());
        for (char c : hex) {
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == ',') continue;
            clean.push_back(c);
        }
        if (clean.size() % 2 != 0) return false;
        out.clear();
        out.reserve(clean.size() / 2);
        auto hexNibble = [](char c, std::uint8_t& n) {
            if (c >= '0' && c <= '9') { n = c - '0';      return true; }
            if (c >= 'a' && c <= 'f') { n = 10 + c - 'a'; return true; }
            if (c >= 'A' && c <= 'F') { n = 10 + c - 'A'; return true; }
            return false;
        };
        for (std::size_t i = 0; i + 1 < clean.size(); i += 2) {
            std::uint8_t hi = 0, lo = 0;
            if (!hexNibble(clean[i], hi) || !hexNibble(clean[i + 1], lo)) return false;
            out.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
        }
        return true;
    };

    auto formatHex = [](std::uintptr_t v) {
        char buf[32];
        ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "0x%016llX",
                      static_cast<unsigned long long>(v));
        return std::string(buf);
    };

    // Decode one field's value given its CLR type name and starting address.
    // Returns a JSON object whose keys are merged into the per-field entry by
    // the caller. On unreadable memory, returns {"error": "unreadable"}.
    //
    // The .NET String layout is identical between Mono (Unity bdwgc) and IL2CPP
    // — header is 16 bytes (vtable + sync), then int32 length at +0x10, then
    // UTF-16 chars at +0x14. So this decoder is backend-agnostic.
    auto decodeField = [formatHex, bytesToHex](const std::string& typeName,
                                               std::uintptr_t addr) -> json {
        json out;
        auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
            return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
        };

        if (typeName == "System.Boolean") {
            std::uint8_t v = 0;
            if (rd(addr, &v, 1)) out["value"] = (v != 0); else out["error"] = "unreadable";
        } else if (typeName == "System.Byte") {
            std::uint8_t v = 0;
            if (rd(addr, &v, 1)) out["value"] = static_cast<unsigned>(v); else out["error"] = "unreadable";
        } else if (typeName == "System.SByte") {
            std::int8_t v = 0;
            if (rd(addr, &v, 1)) out["value"] = static_cast<int>(v); else out["error"] = "unreadable";
        } else if (typeName == "System.Int16") {
            std::int16_t v = 0;
            if (rd(addr, &v, 2)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.UInt16" || typeName == "System.Char") {
            std::uint16_t v = 0;
            if (rd(addr, &v, 2)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Int32") {
            std::int32_t v = 0;
            if (rd(addr, &v, 4)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.UInt32") {
            std::uint32_t v = 0;
            if (rd(addr, &v, 4)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Int64") {
            std::int64_t v = 0;
            if (rd(addr, &v, 8)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.UInt64") {
            std::uint64_t v = 0;
            if (rd(addr, &v, 8)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Single") {
            float v = 0.f;
            if (rd(addr, &v, 4)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Double") {
            double v = 0.0;
            if (rd(addr, &v, 8)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.IntPtr" || typeName == "System.UIntPtr") {
            std::uintptr_t v = 0;
            if (rd(addr, &v, sizeof(std::uintptr_t))) out["ptr"] = formatHex(v); else out["error"] = "unreadable";
        } else if (typeName == "System.String") {
            // Reference to MonoString / Il2CppString. Follow the pointer if non-null
            // and decode the inline UTF-16 buffer to UTF-8.
            std::uintptr_t sptr = 0;
            if (!rd(addr, &sptr, sizeof(std::uintptr_t))) { out["error"] = "unreadable"; return out; }
            out["ptr"] = formatHex(sptr);
            if (sptr == 0) { out["string"] = nullptr; return out; }
            std::int32_t len = 0;
            if (!rd(sptr + ManagedLayout::kStringLengthOffset, &len, 4)) { out["string_error"] = "header unreadable"; return out; }
            if (len < 0 || len > 65536)    { out["string_error"] = "implausible length"; out["string_length"] = len; return out; }
            std::vector<std::uint16_t> chars(static_cast<std::size_t>(len));
            if (len > 0 && !rd(sptr + ManagedLayout::kStringCharsOffset, chars.data(),
                               static_cast<std::size_t>(len) * 2u)) {
                out["string_error"] = "chars unreadable";
                return out;
            }
            // UTF-16 → UTF-8 with U+FFFD on lone surrogates. Track "not text"
            // signals along the way: an embedded NUL, C0 control chars, or
            // lone surrogates. A real managed string used as a game field
            // (name / version / path) has none of these; when a "String"
            // field's pointer actually points at a non-string object (a
            // mis-typed/stale reference), the bytes decode to exactly that
            // noise — so we flag it instead of dumping a wall of mojibake.
            // CJK-safe: ordinary CJK code points are >= 0x20 and not surrogates.
            std::size_t nulCount = 0, ctrlCount = 0, replCount = 0;
            std::string utf8;
            utf8.reserve(static_cast<std::size_t>(len));
            auto pushCp = [&utf8](std::uint32_t cp) {
                if (cp < 0x80u) utf8.push_back(static_cast<char>(cp));
                else if (cp < 0x800u) {
                    utf8.push_back(static_cast<char>(0xC0u | (cp >> 6)));
                    utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                } else if (cp < 0x10000u) {
                    utf8.push_back(static_cast<char>(0xE0u | (cp >> 12)));
                    utf8.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
                    utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                } else {
                    utf8.push_back(static_cast<char>(0xF0u | (cp >> 18)));
                    utf8.push_back(static_cast<char>(0x80u | ((cp >> 12) & 0x3Fu)));
                    utf8.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
                    utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                }
            };
            for (std::size_t i = 0; i < chars.size(); ++i) {
                std::uint16_t raw = chars[i];
                if (raw == 0) ++nulCount;
                else if (raw < 0x20u && raw != 0x09u && raw != 0x0Au && raw != 0x0Du) ++ctrlCount;
                std::uint32_t cp = raw;
                if (cp >= 0xD800u && cp <= 0xDBFFu && i + 1 < chars.size()) {
                    std::uint32_t lo = chars[i + 1];
                    if (lo >= 0xDC00u && lo <= 0xDFFFu) {
                        cp = 0x10000u + ((cp - 0xD800u) << 10) + (lo - 0xDC00u);
                        ++i;
                    } else {
                        cp = 0xFFFDu; ++replCount;
                    }
                } else if (cp >= 0xDC00u && cp <= 0xDFFFu) {
                    cp = 0xFFFDu; ++replCount;
                }
                pushCp(cp);
            }
            // "Not text" guard — see the comment above. Embedded NUL is a hard
            // signal; otherwise require >= 25% control/lone-surrogate chars on
            // a string long enough to judge.
            std::size_t suspicious = ctrlCount + replCount;
            if (len >= 2 && (nulCount > 0 || suspicious * 4 >= static_cast<std::size_t>(len))) {
                out["string_error"]  = "not text (pointer is not a real string)";
                out["string_length"] = len;
                return out;
            }
            out["string"] = std::move(utf8);
            out["string_length"] = len;
        } else {
            // Phase 31-1 (Phase A) — known Unity math value-structs are plain
            // N-float / N-byte blobs stored inline (the field storage IS the
            // value; no header, no deref). Emit their members as a `fields`
            // sub-list so snapshot / SPC / pivot can see x/y/z instead of a
            // meaningless pointer word. Pure offset reads → zero CTD risk.
            // Custom structs (int/double members) are handled generically in
            // walkInstance (Phase 31-2 / Phase B); they fall through here.
            struct VsMember { const char* name; const char* type; int offset; int width; };
            static const std::unordered_map<std::string, std::vector<VsMember>> kVs = {
                {"UnityEngine.Vector2",    {{"x","System.Single",0,4},{"y","System.Single",4,4}}},
                {"UnityEngine.Vector3",    {{"x","System.Single",0,4},{"y","System.Single",4,4},{"z","System.Single",8,4}}},
                {"UnityEngine.Vector4",    {{"x","System.Single",0,4},{"y","System.Single",4,4},{"z","System.Single",8,4},{"w","System.Single",12,4}}},
                {"UnityEngine.Quaternion", {{"x","System.Single",0,4},{"y","System.Single",4,4},{"z","System.Single",8,4},{"w","System.Single",12,4}}},
                {"UnityEngine.Color",      {{"r","System.Single",0,4},{"g","System.Single",4,4},{"b","System.Single",8,4},{"a","System.Single",12,4}}},
                {"UnityEngine.Color32",    {{"r","System.Byte",0,1},{"g","System.Byte",1,1},{"b","System.Byte",2,1},{"a","System.Byte",3,1}}},
                {"UnityEngine.Vector2Int", {{"x","System.Int32",0,40},{"y","System.Int32",4,40}}},
                {"UnityEngine.Vector3Int", {{"x","System.Int32",0,40},{"y","System.Int32",4,40},{"z","System.Int32",8,40}}},
                {"UnityEngine.Rect",       {{"x","System.Single",0,4},{"y","System.Single",4,4},{"width","System.Single",8,4},{"height","System.Single",12,4}}},
            };
            auto vit = kVs.find(typeName);
            if (vit != kVs.end()) {
                json members = json::array();
                for (const auto& m : vit->second) {
                    json mf = { {"name", m.name}, {"type", m.type}, {"offset", m.offset} };
                    std::uintptr_t maddr = addr + static_cast<std::uintptr_t>(m.offset);
                    if (m.width == 4) {            // float
                        float v = 0.f;
                        if (rd(maddr, &v, 4)) mf["value"] = v; else mf["error"] = "unreadable";
                    } else if (m.width == 40) {    // int32 (Vector2Int/Vector3Int)
                        std::int32_t v = 0;
                        if (rd(maddr, &v, 4)) mf["value"] = v; else mf["error"] = "unreadable";
                    } else {                        // byte (Color32)
                        std::uint8_t v = 0;
                        if (rd(maddr, &v, 1)) mf["value"] = static_cast<unsigned>(v); else mf["error"] = "unreadable";
                    }
                    members.push_back(std::move(mf));
                }
                out["fields"] = std::move(members);
            } else {
                // Reference / unknown value type: report the raw pointer-sized word.
                // Caller can resolvePointer or walkInstance again on it.
                std::uintptr_t v = 0;
                if (rd(addr, &v, sizeof(std::uintptr_t))) out["ptr"] = formatHex(v); else out["error"] = "unreadable";
            }
        }
        (void)bytesToHex;  // reserved for future raw-bytes inclusion
        return out;
    };

    // ------------------------------------------------------------------------
    // Phase 29 — inline array-element capture used by the snapshot handlers.
    // Given a non-null managed array pointer + the element type name + a cap,
    // reads the array header and emits up to `cap` elements with their values
    // decoded by the same `decodeField` lambda used by walkInstance. Backend-
    // agnostic — relies on ManagedLayout's pointer-sized offsets for header
    // layout (16-byte object header + length at +0x18 + data at +0x20 on x64;
    // half that on x86).
    //
    // Element-size resolution is the same primitive-name table CmdWalkArray
    // uses. Value-type-struct arrays (`Vector3[]`, custom user struct) fall
    // through to the "unknown element width" branch and emit a raw hex blob
    // of `elemSize` bytes per slot when the caller supplied a width hint;
    // otherwise we default to 8 bytes per element (pointer-sized — matches
    // reference-type arrays).
    //
    // Bundled Phase 27c live smoke: this is where the `T[]` element decode
    // path gets exercised on real game targets. Any bug we find here also
    // gets fixed in the parallel CmdWalkArray path so the array-element
    // contract stays consistent across walkInstance / Snapshot / whatIsAt.
    // ------------------------------------------------------------------------
    auto walkArrayElementsInline = [formatHex, decodeField, bytesToHex](
        std::uintptr_t arrayPtr, const std::string& elementTypeRaw, std::size_t cap,
        const std::function<json(std::uintptr_t)>& elementFieldCapturer) -> json {
        if (arrayPtr == 0 || cap == 0) {
            return json{{"length", 0}, {"count", 0}, {"elements", json::array()}};
        }

        auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
            return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
        };

        std::uintptr_t bounds = 0;
        std::uintptr_t length = 0;
        if (!rd(arrayPtr + ManagedLayout::kArrayBoundsOffset,    &bounds, sizeof(std::uintptr_t)) ||
            !rd(arrayPtr + ManagedLayout::kArrayMaxLengthOffset, &length, sizeof(std::uintptr_t))) {
            return json{{"error", "header_unreadable"}};
        }
        if (length > 16u * 1024u * 1024u) {
            return json{{"error", "implausible_length"}, {"length", length}};
        }

        // Element type comes in as "System.Int32[]" / "MainGame.PLY_WORK[]"
        // etc. — strip the trailing "[]" so the primitive table + decodeField
        // see the bare element type. We tolerate `[][]` (jagged) by stripping
        // one level only; the inner array reads as a pointer.
        std::string elementType = elementTypeRaw;
        if (elementType.size() >= 2 &&
            elementType[elementType.size() - 1] == ']' &&
            elementType[elementType.size() - 2] == '[') {
            elementType.resize(elementType.size() - 2);
        }

        auto knownElemSize = [](const std::string& t) -> std::size_t {
            if (t == "System.Boolean" || t == "System.Byte" || t == "System.SByte") return 1;
            if (t == "System.Int16"   || t == "System.UInt16" || t == "System.Char") return 2;
            if (t == "System.Int32"   || t == "System.UInt32" || t == "System.Single") return 4;
            if (t == "System.Int64"   || t == "System.UInt64" || t == "System.Double") return 8;
            if (t == "System.IntPtr"  || t == "System.UIntPtr") return 8;
            return 0;
        };

        const std::size_t elemSize  = knownElemSize(elementType);
        const bool        isPointer = (elemSize == 0);   // ref-type or struct → pointer-sized slot default
        const std::size_t effectiveElemSize = isPointer ? sizeof(std::uintptr_t) : elemSize;

        std::uintptr_t dataAddr = arrayPtr + ManagedLayout::kArrayDataOffset;
        std::size_t    n = (length < cap) ? static_cast<std::size_t>(length) : cap;

        json elements = json::array();
        for (std::size_t i = 0; i < n; ++i) {
            std::uintptr_t slotAddr = dataAddr + i * effectiveElemSize;
            json e = { {"index", i} };
            if (elementType == "System.String") {
                // Decode via decodeField's String branch (follows the pointer
                // and inlines UTF-8) so element rows carry actual text.
                json d = decodeField(elementType, slotAddr);
                for (auto kv = d.begin(); kv != d.end(); ++kv) e[kv.key()] = kv.value();
            } else if (!isPointer) {
                json d = decodeField(elementType, slotAddr);
                for (auto kv = d.begin(); kv != d.end(); ++kv) e[kv.key()] = kv.value();
            } else {
                std::uintptr_t v = 0;
                if (rd(slotAddr, &v, sizeof(std::uintptr_t))) {
                    e["ptr"] = formatHex(v);
                    // Phase 29f (Layer B): chase the element object one level
                    // deeper to capture its own fields when a capturer is
                    // supplied. The snapshot handler passes one only when
                    // arrayElementFields is on AND the per-target gate allows
                    // this class.field — otherwise the capturer is empty and
                    // we keep the legacy ptr-only behaviour. The capturer
                    // returns {"fields":[…]} or {"element_class_error":"…"}.
                    if (elementFieldCapturer && v != 0) {
                        json inner = elementFieldCapturer(v);
                        if (inner.is_object())
                            for (auto kv = inner.begin(); kv != inner.end(); ++kv) e[kv.key()] = kv.value();
                    }
                } else {
                    e["error"] = "unreadable";
                }
            }
            elements.push_back(std::move(e));
        }

        (void)bytesToHex;  // reserved for future raw-bytes inclusion of struct slots
        return json{
            {"length",       length},
            {"count",        n},
            {"element_type", elementType},
            {"element_size", effectiveElemSize},
            {"is_pointer",   isPointer},
            {"elements",     std::move(elements)},
        };
    };

    PipeServer::RegisterHandler(MagicStrings::CmdReadMemory,
        [parseAddr, bytesToHex, formatHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address' (hex string or integer)"}};
        }
        std::size_t size = 0;
        if (args.contains("size") && args["size"].is_number_unsigned()) {
            size = args["size"].get<std::size_t>();
        }
        if (size == 0 || size > 1 * 1024 * 1024) {
            return json{{"error", "'size' must be in [1, 1048576]"}};
        }
        std::vector<std::uint8_t> buf(size);
        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(addr), buf.data(), size)) {
            return json{
                {"error", "memory not readable"},
                {"address", formatHex(addr)},
                {"size", size},
            };
        }
        return json{
            {"address", formatHex(addr)},
            {"size", size},
            {"hex", bytesToHex(buf.data(), buf.size())},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdWriteMemory,
        [parseAddr, hexToBytes, formatHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        if (!args.contains("hex") || !args["hex"].is_string()) {
            return json{{"error", "missing 'hex' (compact hex string)"}};
        }
        std::vector<std::uint8_t> bytes;
        if (!hexToBytes(args["hex"].get<std::string>(), bytes)) {
            return json{{"error", "'hex' is not valid hex"}};
        }
        if (bytes.empty() || bytes.size() > 1 * 1024 * 1024) {
            return json{{"error", "byte length must be in [1, 1048576]"}};
        }
        if (!Memory::SafeWriteBytes(reinterpret_cast<void*>(addr), bytes.data(), bytes.size())) {
            return json{
                {"error", "memory not writable"},
                {"address", formatHex(addr)},
                {"size", bytes.size()},
            };
        }
        return json{
            {"address", formatHex(addr)},
            {"written", bytes.size()},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdResolvePointer,
        [parseAddr, bytesToHex, formatHex](const json& args) -> json {
        std::uintptr_t base = 0;
        if (!args.contains("base") || !parseAddr(args["base"], base)) {
            return json{{"error", "missing/invalid 'base'"}};
        }
        std::vector<std::int64_t> offsets;
        if (args.contains("offsets") && args["offsets"].is_array()) {
            for (const auto& o : args["offsets"]) {
                if      (o.is_number_integer())  offsets.push_back(o.get<std::int64_t>());
                else if (o.is_number_unsigned()) offsets.push_back(static_cast<std::int64_t>(o.get<std::uint64_t>()));
                else return json{{"error", "offsets[] must be integers"}};
            }
        }
        // Resolve: at each step except the last, dereference (base + offset) as
        // a pointer; the final offset is added but NOT dereferenced. This is
        // the standard Cheat Engine pointer-chain semantics.
        std::vector<std::string> trace;
        trace.reserve(offsets.size() + 1);
        std::uintptr_t cur = base;
        trace.push_back(formatHex(cur));
        for (std::size_t i = 0; i < offsets.size(); ++i) {
            std::uintptr_t addr = static_cast<std::uintptr_t>(
                static_cast<std::int64_t>(cur) + offsets[i]);
            if (i + 1 < offsets.size()) {
                auto deref = Memory::SafeRead<std::uintptr_t>(reinterpret_cast<void*>(addr));
                if (!deref) {
                    return json{
                        {"error", "deref failed at step"},
                        {"step", i},
                        {"address", formatHex(addr)},
                        {"trace", trace},
                    };
                }
                cur = *deref;
            } else {
                cur = addr;  // last offset is just added, not dereferenced
            }
            trace.push_back(formatHex(cur));
        }
        json result{
            {"final_address", formatHex(cur)},
            {"trace", trace},
        };
        if (args.contains("size") && args["size"].is_number_unsigned()) {
            std::size_t size = args["size"].get<std::size_t>();
            if (size > 0 && size <= 4096) {
                std::vector<std::uint8_t> buf(size);
                if (Memory::SafeReadBytes(reinterpret_cast<void*>(cur), buf.data(), size)) {
                    result["size"] = size;
                    result["hex"] = bytesToHex(buf.data(), buf.size());
                }
            }
        }
        return result;
    });

    // ------------------------------------------------------------------------
    // Phase 10 follow-up — walkArray (Both backends).
    // Reads a Mono / IL2CPP managed array (T[]) header and returns up to
    // `limit` element addresses. Decodes primitive elements inline; for
    // reference-type arrays (8-byte slots) returns the dereferenced object
    // pointer per slot so callers can chain into walkInstance.
    //
    // Array layout (identical between Mono bdwgc and IL2CPP, x64):
    //   +0x00 vtable*   |   +0x10 bounds*    (NULL for SZARRAY)
    //   +0x08 sync      |   +0x18 max_length (uintptr_t — total length)
    //                       +0x20 vector[0]  (first element)
    //
    // List<T> handling stays client-side — caller readMemory()s the
    // List._items pointer at offset 0x10 first, then walkArray()s that.
    //
    // Phase 10d follow-up: when the caller supplies an
    // `elementAssembly`/`elementNamespace`/`elementName` triple, the active
    // backend resolves the element class and fills element_size +
    // is_pointer + element_class_* from class_value_size /
    // class_is_valuetype. Lets the UI walk struct arrays (Vector3[] etc.)
    // correctly without needing to hand-hint the stride.
    // ------------------------------------------------------------------------
    PipeServer::RegisterHandler(MagicStrings::CmdWalkArray,
        [parseAddr, formatHex, decodeField, bytesToHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::string elementType = args.value("elementType", std::string{});
        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = args["limit"].get<std::size_t>();
        }
        if (limit == 0)    limit = 256;
        if (limit > 4096)  limit = 4096;

        // Element width: well-known primitive table first, caller hint as
        // override / fallback for struct arrays we can't introspect from
        // here.
        auto knownElemSize = [](const std::string& t) -> std::size_t {
            if (t == "System.Boolean" || t == "System.Byte" || t == "System.SByte") return 1;
            if (t == "System.Int16"   || t == "System.UInt16" || t == "System.Char") return 2;
            if (t == "System.Int32"   || t == "System.UInt32" || t == "System.Single") return 4;
            if (t == "System.Int64"   || t == "System.UInt64" || t == "System.Double") return 8;
            if (t == "System.IntPtr"  || t == "System.UIntPtr") return 8;
            return 0;  // unknown / reference type
        };

        std::size_t elemSize = knownElemSize(elementType);
        bool        isPointer = false;
        bool        elemClassResolved = false;
        std::string elemClassNs, elemClassName;
        bool        elemIsValueType = false;

        // Optional: caller-supplied element-class spec for runtime lookup.
        // The active backend (this DLL is built for one or the other) drives
        // the resolution. Falls through cleanly when not supplied or when
        // the resolver doesn't have the optional exports bound.
        std::string elemAsm = args.value("elementAssembly",  std::string{});
        std::string elemNs  = args.value("elementNamespace", std::string{});
        std::string elemNm  = args.value("elementName",      std::string{});
        if (!elemAsm.empty() && !elemNm.empty()) {
#if defined(DISCRETE_BACKEND_IL2CPP)
            const auto& r = IL2CPP::GetResolver();
            if (r.resolved) {
                const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(elemAsm);
                IL2CPP::Il2CppClass* k = (img != nullptr) ? IL2CPP::FindClass(img, elemNs, elemNm) : nullptr;
                if (k != nullptr) {
                    elemClassResolved = true;
                    if (r.class_get_name      != nullptr) elemClassName = r.class_get_name(k)      ? r.class_get_name(k)      : "";
                    if (r.class_get_namespace != nullptr) elemClassNs   = r.class_get_namespace(k) ? r.class_get_namespace(k) : "";
                    if (r.class_is_valuetype != nullptr) elemIsValueType = r.class_is_valuetype(k) != 0;
                    if (r.class_value_size != nullptr) {
                        std::uint32_t align = 0;
                        int sz = r.class_value_size(k, &align);
                        if (sz > 0) elemSize = static_cast<std::size_t>(sz);
                    }
                }
            }
#elif defined(DISCRETE_BACKEND_MONO)
            const auto& r = Mono::GetResolver();
            if (r.resolved) {
                // Phase 14d: Mono::Find* helpers do their own attach/detach,
                // so call them OUTSIDE of any guard (avoids the nested
                // attach pattern noted in build #54's BongoCat regression).
                // The bare runtime calls below (class_get_name, etc.) ARE
                // direct resolver invocations on a thread Mono never
                // created, so they need their own attach scope. We open
                // one guard for the whole batch — single attach, not four.
                Mono::MonoImage* img = Mono::FindImageByName(elemAsm);
                Mono::MonoClass* k = (img != nullptr) ? Mono::FindClass(img, elemNs, elemNm) : nullptr;
                if (k != nullptr) {
                    Mono::MonoDomain* domain = Mono::GetRootDomain();
                    Mono::ThreadAttachGuard guard(domain);
                    if (guard.Attached()) {
                        elemClassResolved = true;
                        if (r.class_get_name      != nullptr) elemClassName = r.class_get_name(k)      ? r.class_get_name(k)      : "";
                        if (r.class_get_namespace != nullptr) elemClassNs   = r.class_get_namespace(k) ? r.class_get_namespace(k) : "";
                        if (r.class_is_valuetype != nullptr) elemIsValueType = r.class_is_valuetype(k) != 0;
                        if (r.class_value_size != nullptr) {
                            std::uint32_t align = 0;
                            int sz = r.class_value_size(k, &align);
                            if (sz > 0) elemSize = static_cast<std::size_t>(sz);
                        }
                    }
                }
            }
#endif
            // If resolver bound + class found + we got a value-type, leave
            // is_pointer=false. Otherwise treat as ref by default below.
            if (elemClassResolved) {
                isPointer = !elemIsValueType;
                if (elementType.empty()) {
                    elementType = elemClassNs.empty()
                        ? elemClassName
                        : (elemClassNs + "." + elemClassName);
                }
            }
        }

        // If element-class lookup didn't fire / failed, fall back to the
        // primitive table + caller hints / pointer default. A reference-type
        // (pointer) slot is sizeof(void*) — 4 on x86 / WOW64, 8 on x64 — NOT a
        // hardcoded 8, otherwise x86 arrays step by 8 and every slot past [0]
        // reads the wrong address (observed on 活俠傳 Mono x86: String[] slots
        // landed mid-pointer). ManagedLayout::kPointerSize is sizeof(void*) of
        // this DLL build, which matches the target arch (x86 DLL ↔ x86 game).
        if (elemSize == 0) {
            if (args.contains("elementSize") && args["elementSize"].is_number_unsigned()) {
                elemSize = args["elementSize"].get<std::size_t>();
            } else {
                elemSize = ManagedLayout::kPointerSize;
                isPointer = true;
            }
        }

        // Read array header — bounds (validate SZARRAY) + max_length. Both
        // fields are pointer-sized: 8 bytes on x64, 4 on x86. Offsets come
        // from ManagedLayout so the same source serves Mono x64 / Mono x86 /
        // IL2CPP x64 without a per-arch branch.
        auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
            return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
        };
        std::uintptr_t bounds = 0;
        std::uintptr_t length = 0;
        if (!rd(addr + ManagedLayout::kArrayBoundsOffset,    &bounds, sizeof(std::uintptr_t)) ||
            !rd(addr + ManagedLayout::kArrayMaxLengthOffset, &length, sizeof(std::uintptr_t))) {
            return json{{"error", "header unreadable"}, {"address", formatHex(addr)}};
        }
        // Sanity: a real managed array has length below ~16M for our use
        // (anything bigger is almost certainly a bad pointer / not an array).
        if (length > 16u * 1024u * 1024u) {
            return json{
                {"error",   "implausible length — not a managed array?"},
                {"address", formatHex(addr)},
                {"length",  length},
            };
        }

        std::uintptr_t dataAddr = addr + ManagedLayout::kArrayDataOffset;
        std::size_t    n = (length < limit) ? static_cast<std::size_t>(length) : limit;

        json elements = json::array();
        for (std::size_t i = 0; i < n; ++i) {
            std::uintptr_t slotAddr = dataAddr + i * elemSize;
            json e = {
                {"index",   i},
                {"address", formatHex(slotAddr)},
            };
            if (isPointer) {
                std::uintptr_t v = 0;
                if (rd(slotAddr, &v, sizeof(std::uintptr_t))) e["ptr"] = formatHex(v);
                else                                          e["error"] = "unreadable";
            } else if (!elementType.empty()) {
                // Decode primitive inline using the existing field decoder.
                json decoded = decodeField(elementType, slotAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv)
                    e[kv.key()] = kv.value();
            } else {
                // Caller gave us a struct width but no name — return raw bytes.
                std::vector<std::uint8_t> buf(elemSize);
                if (rd(slotAddr, buf.data(), elemSize))
                    e["hex"] = bytesToHex(buf.data(), buf.size());
                else
                    e["error"] = "unreadable";
            }
            elements.push_back(std::move(e));
        }

        json out = {
            {"address",      formatHex(addr)},
            {"data_address", formatHex(dataAddr)},
            {"length",       length},
            {"limit",        limit},
            {"element_type", elementType},
            {"element_size", elemSize},
            {"is_pointer",   isPointer},
            {"count",        n},
            {"elements",     std::move(elements)},
        };
        if (elemClassResolved) {
            out["element_class_namespace"] = elemClassNs;
            out["element_class_name"]      = elemClassName;
            out["element_is_value_type"]   = elemIsValueType;
        }
        return out;
    });

    // ------------------------------------------------------------------------
    // Phase 6 segment D — symbol registry (CE-style named pointers).
    // Backend-agnostic in-memory map. Survives until the DLL unloads. Useful
    // for stashing addresses returned by getStaticFieldData / findInstances /
    // walkInstance and recalling them by a friendly name from later commands.
    // ------------------------------------------------------------------------
    static std::mutex g_symbolMutex;
    static std::map<std::string, std::uintptr_t> g_symbolMap;

    PipeServer::RegisterHandler(MagicStrings::CmdRegisterSymbol,
        [parseAddr, formatHex](const json& args) -> json {
        std::string name = args.value("name", std::string{});
        if (name.empty()) return json{{"error", "missing 'name'"}};
        // Action: "set" (default), "delete", "get". Symmetric with CE.
        std::string action = args.value("action", std::string{"set"});
        std::lock_guard<std::mutex> lk(g_symbolMutex);
        if (action == "delete") {
            std::size_t n = g_symbolMap.erase(name);
            return json{{"name", name}, {"deleted", n != 0}};
        }
        if (action == "get") {
            auto it = g_symbolMap.find(name);
            if (it == g_symbolMap.end()) return json{{"error", "symbol not found"}, {"name", name}};
            return json{{"name", name}, {"address", formatHex(it->second)}};
        }
        // Default: set.
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        g_symbolMap[name] = addr;
        return json{{"name", name}, {"address", formatHex(addr)}, {"set", true}};
    });

    PipeServer::RegisterHandler("lookupSymbol",
        [formatHex](const json& args) -> json {
        std::string name = args.value("name", std::string{});
        if (name.empty()) return json{{"error", "missing 'name'"}};
        std::lock_guard<std::mutex> lk(g_symbolMutex);
        auto it = g_symbolMap.find(name);
        if (it == g_symbolMap.end()) return json{{"error", "symbol not found"}, {"name", name}};
        return json{{"name", name}, {"address", formatHex(it->second)}};
    });

    PipeServer::RegisterHandler("listSymbols",
        [formatHex](const json&) -> json {
        std::lock_guard<std::mutex> lk(g_symbolMutex);
        json arr = json::array();
        for (const auto& [k, v] : g_symbolMap) {
            arr.push_back({{"name", k}, {"address", formatHex(v)}});
        }
        return json{{"count", arr.size()}, {"symbols", std::move(arr)}};
    });

    // ------------------------------------------------------------------------
    // Phase 5 segment 3 — stripped / obfuscated metadata heuristic.
    // Backend-agnostic helpers; each backend block below registers its own
    // `getMetadataStats` handler that feeds the shared collectors.
    // Heuristic: count single-letter / two-letter type names (excluding the
    // C# compiler-generated `<...>` types) as a fraction of all "user"
    // names. Vanilla Unity sits well under 5%; Beebyte / Eazfuscator builds
    // routinely exceed 30%.
    // ------------------------------------------------------------------------
    struct NameStats {
        std::size_t total            = 0;
        std::size_t with_namespace   = 0;
        std::size_t single_char      = 0;
        std::size_t short_two_char   = 0;
        std::size_t compiler_gen     = 0;
        std::size_t numeric_lead     = 0;
        std::uint64_t total_name_len = 0;
        std::size_t min_name_len     = static_cast<std::size_t>(-1);
        std::size_t max_name_len     = 0;
    };
    auto updateStats = [](NameStats& s, const std::string& ns,
                           const std::string& name) {
        s.total++;
        if (!ns.empty()) s.with_namespace++;
        if (name.empty()) return;
        if (name[0] == '<') { s.compiler_gen++; return; }  // <PrivateImpl> etc.
        if (name[0] >= '0' && name[0] <= '9') s.numeric_lead++;
        if      (name.size() == 1) s.single_char++;
        else if (name.size() == 2) s.short_two_char++;
        s.total_name_len += name.size();
        if (name.size() < s.min_name_len) s.min_name_len = name.size();
        if (name.size() > s.max_name_len) s.max_name_len = name.size();
    };
    auto buildStatsJson = [](const NameStats& s) -> json {
        json out = {
            {"total_classes",      s.total},
            {"with_namespace",     s.with_namespace},
            {"single_char_names",  s.single_char},
            {"short_names_2",      s.short_two_char},
            {"compiler_generated", s.compiler_gen},
            {"numeric_lead",       s.numeric_lead},
        };
        if (s.total > 0) {
            // Average / min / max are reported over non-compiler-generated
            // names since `total_name_len` excludes those by construction.
            std::size_t denom = (s.total > s.compiler_gen) ?
                                (s.total - s.compiler_gen) : 0;
            if (denom > 0) {
                out["avg_name_length"] =
                    static_cast<double>(s.total_name_len) / static_cast<double>(denom);
            }
        }
        if (s.min_name_len != static_cast<std::size_t>(-1)) {
            out["min_name_length"] = s.min_name_len;
        }
        out["max_name_length"] = s.max_name_len;

        // Verdict: ratio of "stripped-shape" names over user (non compiler-
        // generated) types. Thresholds calibrated against vanilla Unity
        // (~1-2%) and typical commercial obfuscators (>30%).
        std::size_t denom = (s.total > s.compiler_gen) ?
                            (s.total - s.compiler_gen) : 0;
        if (denom > 0) {
            double ratio = static_cast<double>(s.single_char + s.short_two_char) /
                           static_cast<double>(denom);
            out["strip_ratio"] = ratio;
            std::string verdict;
            if      (ratio > 0.30) verdict = "heavy_strip";
            else if (ratio > 0.10) verdict = "partial_strip";
            else                   verdict = "clean";
            out["verdict"] = verdict;
        }
        return out;
    };

    // ----------------------------------------------------------------------
    // Phase 18a-3 — backend-agnostic AOB pattern generator. Reads N bytes
    // (default 32, capped 128) from the supplied address, walks them with a
    // minimal x64 length disassembler, returns a CE-style pattern string
    // with rel8/rel32 immediates and RIP-relative ModRM disp32 fields
    // wildcarded. Also resolves the containing module name + offset so the
    // UI can emit `aobscanmodule(name, GameAssembly.dll, ...)` directly.
    // ----------------------------------------------------------------------
    PipeServer::RegisterHandler(MagicStrings::CmdGetMethodAobPattern,
        [parseAddr, bytesToHex, formatHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::size_t length = 32;
        if (args.contains("length") && args["length"].is_number_unsigned()) {
            length = args["length"].get<std::size_t>();
        }
        if (length == 0)   length = 32;
        if (length > 128)  length = 128;

        std::vector<std::uint8_t> buf(length);
        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(addr),
                                   buf.data(), buf.size())) {
            return json{
                {"error",   "address unreadable"},
                {"address", formatHex(addr)},
                {"length",  length},
            };
        }

        auto pr = Aob::BuildPattern(buf.data(), buf.size());

        // Resolve containing module — needed for the AA script's
        // aobscanmodule(name, MODULE, AOB) form.
        std::string moduleName;
        std::uintptr_t moduleBase = 0;
        std::size_t    moduleSize = 0;
        for (const auto& mi : ModuleHelper::EnumerateModules()) {
            const auto base = reinterpret_cast<std::uintptr_t>(mi.baseAddress);
            if (base == 0 || mi.sizeOfImage == 0) continue;
            if (addr >= base && addr < base + mi.sizeOfImage) {
                moduleBase = base;
                moduleSize = mi.sizeOfImage;
                int n = ::WideCharToMultiByte(CP_UTF8, 0, mi.name.data(),
                                              static_cast<int>(mi.name.size()),
                                              nullptr, 0, nullptr, nullptr);
                if (n > 0) {
                    moduleName.assign(static_cast<std::size_t>(n), '\0');
                    ::WideCharToMultiByte(CP_UTF8, 0, mi.name.data(),
                                          static_cast<int>(mi.name.size()),
                                          moduleName.data(), n, nullptr, nullptr);
                }
                break;
            }
        }

        std::size_t wildcards = 0;
        for (bool b : pr.wildcardMask) if (b) ++wildcards;

        json result = {
            {"address",      formatHex(addr)},
            {"length",       pr.length},
            {"pattern",      pr.pattern},
            {"bytes",        bytesToHex(pr.bytes.data(), pr.bytes.size())},
            {"moduleName",   moduleName},
            {"moduleBase",   formatHex(moduleBase)},
            {"moduleOffset", formatHex(moduleBase ? (addr - moduleBase) : 0)},
            {"moduleSize",   moduleSize},
            {"stats", json{
                {"instructions", pr.instructionCount},
                {"wildcards",    wildcards},
                {"relBranches",  pr.relBranchCount},
                {"ripRel",       pr.ripRelCount},
            }},
            {"decoderBailed", pr.decoderBailed},
        };
        if (pr.decoderBailed) {
            result["bailedAt"] = pr.bailedAt;
        }
        return result;
    });

    // Phase 21a — setInstanceField. Inverse of walkInstance's per-field
    // read: given instance + cumulative offset + type + value, write
    // sizeof(type) bytes at instance+offset. Backend-agnostic — just
    // SEH-protected SafeWriteBytes; the field offset is already known
    // from the prior walkInstance response, so no runtime reflection.
    //
    // v1 scope: primitives (bool / signed + unsigned 8/16/32/64 / float /
    // double / char) + raw address. Reference-type writes (System.String,
    // object pointer) deferred to v2 — they need il2cpp_string_new /
    // mono_string_new plus proper GC-root marshaling, mirroring Phase 19c.
    //
    // Type tags accept both the wire-short form ("int32") and the CLR
    // type name ("System.Int32") — matches the taxonomy used by
    // callMethod arg builders and walkInstance field decode so callers
    // can plumb a single string through both sides.
    PipeServer::RegisterHandler(MagicStrings::CmdSetInstanceField,
        [parseAddr, formatHex](const json& args) -> json {
        std::uintptr_t instance = 0;
        if (!args.contains("address") || !parseAddr(args["address"], instance)) {
            return json{{"error", "missing/invalid 'address' (hex string or integer)"}};
        }
        std::int64_t fieldOffset = 0;
        if (args.contains("fieldOffset")) {
            if (args["fieldOffset"].is_number_integer()) {
                fieldOffset = args["fieldOffset"].get<std::int64_t>();
            } else if (args["fieldOffset"].is_number_unsigned()) {
                fieldOffset = static_cast<std::int64_t>(args["fieldOffset"].get<std::uint64_t>());
            } else {
                return json{{"error", "'fieldOffset' must be an integer"}};
            }
        }
        // Cap offset to a sane upper bound (matches listFieldValuesAcrossInstances).
        if (fieldOffset < 0 || fieldOffset > (1 << 24)) {
            return json{{"error", "'fieldOffset' out of range"}};
        }
        if (!args.contains("type") || !args["type"].is_string()) {
            return json{{"error", "missing 'type'"}};
        }
        const std::string ty = args["type"].get<std::string>();
        if (!args.contains("value")) {
            return json{{"error", "missing 'value'"}};
        }
        const json& jv = args["value"];

        std::uintptr_t fieldAddr = instance + static_cast<std::uintptr_t>(fieldOffset);

        // Marshal value into a stack buffer. The byte layout is identical
        // to MonoObject + offset / Il2CppObject + offset since the field
        // storage is just raw bytes at the supplied address.
        std::uint8_t buf[8] = {0};
        std::size_t size = 0;

        auto asI64 = [&](std::int64_t& out) -> bool {
            if (jv.is_number_integer())      { out = jv.get<std::int64_t>(); return true; }
            if (jv.is_number_unsigned())     { out = static_cast<std::int64_t>(jv.get<std::uint64_t>()); return true; }
            if (jv.is_number_float())        { out = static_cast<std::int64_t>(jv.get<double>()); return true; }
            if (jv.is_boolean())             { out = jv.get<bool>() ? 1 : 0; return true; }
            return false;
        };
        auto asU64 = [&](std::uint64_t& out) -> bool {
            if (jv.is_number_unsigned())     { out = jv.get<std::uint64_t>(); return true; }
            if (jv.is_number_integer())      { out = static_cast<std::uint64_t>(jv.get<std::int64_t>()); return true; }
            if (jv.is_number_float())        { out = static_cast<std::uint64_t>(jv.get<double>()); return true; }
            if (jv.is_boolean())             { out = jv.get<bool>() ? 1 : 0; return true; }
            return false;
        };

        if (ty == "bool" || ty == "boolean" || ty == "System.Boolean") {
            std::uint8_t v = 0;
            if      (jv.is_boolean())          v = jv.get<bool>() ? 1 : 0;
            else if (jv.is_number_integer())   v = jv.get<std::int64_t>() != 0 ? 1 : 0;
            else if (jv.is_number_unsigned())  v = jv.get<std::uint64_t>() != 0 ? 1 : 0;
            else return json{{"error", "'value' not a bool"}};
            buf[0] = v; size = 1;
        } else if (ty == "sbyte" || ty == "int8" || ty == "System.SByte") {
            std::int64_t v = 0;
            if (!asI64(v)) return json{{"error", "'value' not a number"}};
            if (v < INT8_MIN || v > INT8_MAX) return json{{"error", "'value' out of range for int8"}};
            std::int8_t w = static_cast<std::int8_t>(v);
            std::memcpy(buf, &w, 1); size = 1;
        } else if (ty == "byte" || ty == "uint8" || ty == "System.Byte") {
            std::uint64_t v = 0;
            if (!asU64(v)) return json{{"error", "'value' not a number"}};
            if (v > UINT8_MAX) return json{{"error", "'value' out of range for uint8"}};
            buf[0] = static_cast<std::uint8_t>(v); size = 1;
        } else if (ty == "int16" || ty == "short" || ty == "System.Int16") {
            std::int64_t v = 0;
            if (!asI64(v)) return json{{"error", "'value' not a number"}};
            if (v < INT16_MIN || v > INT16_MAX) return json{{"error", "'value' out of range for int16"}};
            std::int16_t w = static_cast<std::int16_t>(v);
            std::memcpy(buf, &w, 2); size = 2;
        } else if (ty == "uint16" || ty == "ushort" || ty == "char" ||
                   ty == "System.UInt16" || ty == "System.Char") {
            std::uint64_t v = 0;
            if (ty == "char" || ty == "System.Char") {
                if (jv.is_string() && !jv.get<std::string>().empty()) {
                    v = static_cast<std::uint8_t>(jv.get<std::string>()[0]);
                } else if (!asU64(v)) {
                    return json{{"error", "'value' not a char or number"}};
                }
            } else if (!asU64(v)) {
                return json{{"error", "'value' not a number"}};
            }
            if (v > UINT16_MAX) return json{{"error", "'value' out of range for uint16"}};
            std::uint16_t w = static_cast<std::uint16_t>(v);
            std::memcpy(buf, &w, 2); size = 2;
        } else if (ty == "int32" || ty == "int" || ty == "System.Int32") {
            std::int64_t v = 0;
            if (!asI64(v)) return json{{"error", "'value' not a number"}};
            if (v < INT32_MIN || v > INT32_MAX) return json{{"error", "'value' out of range for int32"}};
            std::int32_t w = static_cast<std::int32_t>(v);
            std::memcpy(buf, &w, 4); size = 4;
        } else if (ty == "uint32" || ty == "uint" || ty == "System.UInt32") {
            std::uint64_t v = 0;
            if (!asU64(v)) return json{{"error", "'value' not a number"}};
            if (v > UINT32_MAX) return json{{"error", "'value' out of range for uint32"}};
            std::uint32_t w = static_cast<std::uint32_t>(v);
            std::memcpy(buf, &w, 4); size = 4;
        } else if (ty == "int64" || ty == "long" || ty == "System.Int64") {
            std::int64_t v = 0;
            if (!asI64(v)) return json{{"error", "'value' not a number"}};
            std::memcpy(buf, &v, 8); size = 8;
        } else if (ty == "uint64" || ty == "ulong" || ty == "System.UInt64") {
            std::uint64_t v = 0;
            if (!asU64(v)) return json{{"error", "'value' not a number"}};
            std::memcpy(buf, &v, 8); size = 8;
        } else if (ty == "float" || ty == "single" || ty == "System.Single") {
            double d = 0.0;
            if      (jv.is_number())          d = jv.get<double>();
            else if (jv.is_number_integer())  d = static_cast<double>(jv.get<std::int64_t>());
            else if (jv.is_number_unsigned()) d = static_cast<double>(jv.get<std::uint64_t>());
            else return json{{"error", "'value' not a number"}};
            float w = static_cast<float>(d);
            std::memcpy(buf, &w, 4); size = 4;
        } else if (ty == "double" || ty == "System.Double") {
            double w = 0.0;
            if      (jv.is_number())          w = jv.get<double>();
            else if (jv.is_number_integer())  w = static_cast<double>(jv.get<std::int64_t>());
            else if (jv.is_number_unsigned()) w = static_cast<double>(jv.get<std::uint64_t>());
            else return json{{"error", "'value' not a number"}};
            std::memcpy(buf, &w, 8); size = 8;
        } else if (ty == "address" || ty == "intptr" || ty == "uintptr" || ty == "ptr" ||
                   ty == "System.IntPtr" || ty == "System.UIntPtr") {
            std::uintptr_t v = 0;
            if (!parseAddr(jv, v)) return json{{"error", "'value' not a valid address"}};
            std::memcpy(buf, &v, sizeof(std::uintptr_t));
            size = sizeof(std::uintptr_t);
        } else {
            return json{
                {"error", std::string{"unsupported type '"} + ty + "'"},
                {"hint",  "v1 supports primitives + address; reference-type writes (string, object) deferred to v2"},
            };
        }

        if (!Memory::SafeWriteBytes(reinterpret_cast<void*>(fieldAddr), buf, size)) {
            return json{
                {"error",        "memory not writable"},
                {"address",      formatHex(instance)},
                {"fieldOffset",  fieldOffset},
                {"fieldAddress", formatHex(fieldAddr)},
                {"type",         ty},
                {"size",         size},
            };
        }
        return json{
            {"ok",           true},
            {"address",      formatHex(instance)},
            {"fieldOffset",  fieldOffset},
            {"fieldAddress", formatHex(fieldAddr)},
            {"type",         ty},
            {"written",      size},
        };
    });

    // ---- Phase 32 — generic post-scan instance filter (backend-agnostic) -----
    // findInstances is a raw "first qword == class header" heap scan, so its
    // matches mix real instances with noise (transient stack slots, runtime
    // bookkeeping) AND with semantically-uninteresting live siblings (pooled /
    // template / prefab instances of the same class). This filter trims both,
    // and needs no Mono/IL2CPP types — pure SEH-guarded memory reads — so one
    // implementation serves all three backends (Mono x86/x64, IL2CPP x64).
    //
    // Layers (all opt-in except `validate`):
    //   validate (default true): re-read qword @ +0; require == expectedHeader
    //       (MonoVTable* on Mono, Il2CppClass* on IL2CPP). Drops a slot whose
    //       value changed since the scan (transient stack/register spill).
    //   aliveOnly (default false): for UnityEngine.Object-derived classes,
    //       require m_CachedPtr (@ +cachedPtrOffset, default = object header
    //       size: 0x10 on x64 / 0x8 on x86) != 0. Drops destroyed Unity shells.
    //   fieldFilter (default none): array of {offset,type,op,value} predicates,
    //       AND-combined. GENERIC engine — the caller supplies the per-class
    //       predicate (e.g. {offset:0x5FC,type:"System.Int32",op:"==",value:1}).
    //       Nothing here is class- or game-specific.
    //
    // NB: `limit` bounds the RAW scan; filtering happens after, so a heavily
    // filtered query may return far fewer than `limit`. Raise `limit` if the
    // target can sit past the first `limit` raw hits.
    auto evalPredicate = [](std::uintptr_t base, const json& p) -> bool {
        if (!p.is_object() || !p.contains("offset") || !p["offset"].is_number()) {
            return true;  // malformed predicate → don't drop the candidate
        }
        std::uint64_t off = p["offset"].get<std::uint64_t>();
        if (off > (1u << 24)) return false;  // implausible offset
        std::uintptr_t addr = base + static_cast<std::uintptr_t>(off);
        const std::string type = p.value("type", std::string{"System.Int32"});
        const std::string op   = p.value("op",   std::string{"nonzero"});
        const bool hasVal = p.contains("value");

        auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
            return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
        };

        // --- managed string: ==, !=, contains, nonempty/empty ---------------
        if (type == "string" || type == "System.String") {
            std::uintptr_t sptr = 0;
            if (!rd(addr, &sptr, sizeof(sptr))) return false;
            std::string s; bool decoded = false;
            if (sptr != 0) {
                std::int32_t len = 0;
                if (rd(sptr + ManagedLayout::kStringLengthOffset, &len, 4) && len >= 0 && len <= 65536) {
                    std::vector<std::uint16_t> chars(static_cast<std::size_t>(len));
                    if (len == 0 || rd(sptr + ManagedLayout::kStringCharsOffset, chars.data(),
                                       static_cast<std::size_t>(len) * 2u)) {
                        decoded = true;
                        for (std::uint16_t c : chars) s.push_back(c < 0x80 ? static_cast<char>(c) : '?');
                    }
                }
            }
            const std::string want = hasVal && p["value"].is_string() ? p["value"].get<std::string>() : std::string{};
            if (op == "nonempty" || op == "nonnull") return !s.empty();
            if (op == "empty"    || op == "null")    return s.empty();
            if (op == "==")       return s == want;
            if (op == "!=")       return s != want;
            if (op == "contains") return s.find(want) != std::string::npos;
            return decoded;  // unknown op → presence test
        }

        // --- pointer / reference: nonnull/null, ==, != ----------------------
        if (type == "ptr" || type == "ref" || type == "object" ||
            type == "System.IntPtr" || type == "System.UIntPtr") {
            std::uintptr_t v = 0;
            if (!rd(addr, &v, sizeof(v))) return false;
            std::uint64_t want = 0;
            if (hasVal) {
                if (p["value"].is_string()) {
                    std::string hv = p["value"].get<std::string>();
                    std::size_t pos = hv.rfind("0x");
                    try { want = std::stoull(pos != std::string::npos ? hv.substr(pos + 2) : hv, nullptr, 16); }
                    catch (...) { want = 0; }
                } else if (p["value"].is_number()) {
                    want = p["value"].get<std::uint64_t>();
                }
            }
            if (op == "==")  return v == want;
            if (op == "!=")  return v != want;
            if (op == "null" || op == "zero")     return v == 0;
            return v != 0;  // nonnull / nonzero / default
        }

        // --- numeric: read as declared type into a double -------------------
        double d = 0.0; bool ok = false;
        if      (type == "System.Boolean" || type == "System.Byte") { std::uint8_t  v = 0; ok = rd(addr, &v, 1); d = v; }
        else if (type == "System.SByte")                            { std::int8_t   v = 0; ok = rd(addr, &v, 1); d = v; }
        else if (type == "System.Int16")                            { std::int16_t  v = 0; ok = rd(addr, &v, 2); d = v; }
        else if (type == "System.UInt16" || type == "System.Char")  { std::uint16_t v = 0; ok = rd(addr, &v, 2); d = v; }
        else if (type == "System.Int32")                            { std::int32_t  v = 0; ok = rd(addr, &v, 4); d = v; }
        else if (type == "System.UInt32")                           { std::uint32_t v = 0; ok = rd(addr, &v, 4); d = static_cast<double>(v); }
        else if (type == "System.Int64")                            { std::int64_t  v = 0; ok = rd(addr, &v, 8); d = static_cast<double>(v); }
        else if (type == "System.UInt64")                           { std::uint64_t v = 0; ok = rd(addr, &v, 8); d = static_cast<double>(v); }
        else if (type == "System.Single")                           { float         v = 0; ok = rd(addr, &v, 4); d = v; }
        else if (type == "System.Double")                           { double        v = 0; ok = rd(addr, &v, 8); d = v; }
        else { return true; }  // unknown type → don't drop
        if (!ok) return false;

        double want = 0.0;
        if (hasVal) {
            if      (p["value"].is_boolean()) want = p["value"].get<bool>() ? 1.0 : 0.0;
            else if (p["value"].is_number())  want = p["value"].get<double>();
        }
        if (op == "==")      return d == want;
        if (op == "!=")      return d != want;
        if (op == "<")       return d <  want;
        if (op == "<=")      return d <= want;
        if (op == ">")       return d >  want;
        if (op == ">=")      return d >= want;
        if (op == "zero")    return d == 0.0;
        return d != 0.0;  // nonzero / default
    };

    // `refOffsets` = byte offsets of this class's MANAGED reference-typed
    // instance fields (non-static, non-value-type), built by the caller from
    // class metadata. Used only by the `sane` layer.
    // `boolOffsets` = byte offsets of this class's System.Boolean instance
    // fields. A live object stores a managed bool as exactly 0 or 1; any other
    // byte value means the slot is stale / torn / not really an instance of
    // this class. Cheap 1-byte probe, complements the reference-field check.
    auto filterMatches = [evalPredicate](std::vector<const void*>& matches,
                                         std::uintptr_t expectedHeader,
                                         const json& args,
                                         const std::vector<std::uint64_t>& refOffsets,
                                         const std::vector<std::uint64_t>& boolOffsets) -> json {
        const bool validate  = args.value("validate", true);
        const bool aliveOnly = args.value("aliveOnly", false);
        const bool sane      = args.value("sane", false)
                               && (!refOffsets.empty() || !boolOffsets.empty());
        const std::uint64_t cachedOff = args.value("cachedPtrOffset",
            static_cast<std::uint64_t>(ManagedLayout::kObjectHeader));

        json filters = json::array();
        if (args.contains("fieldFilter") && args["fieldFilter"].is_array()) {
            filters = args["fieldFilter"];
        }
        const bool active = validate || aliveOnly || sane || !filters.empty();

        const std::size_t raw = matches.size();
        std::size_t dropValidate = 0, dropAlive = 0, dropField = 0, dropSane = 0;
        if (active) {
            std::vector<const void*> kept;
            kept.reserve(matches.size());
            for (const void* p : matches) {
                const std::uintptr_t b = reinterpret_cast<std::uintptr_t>(p);
                if (validate) {
                    std::uintptr_t hv = 0;
                    if (!Memory::SafeReadBytes(p, &hv, sizeof(hv)) || hv != expectedHeader) { ++dropValidate; continue; }
                }
                if (aliveOnly) {
                    std::uintptr_t cp = 0;
                    if (!Memory::SafeReadBytes(reinterpret_cast<void*>(b + cachedOff), &cp, sizeof(cp)) || cp == 0) { ++dropAlive; continue; }
                }
                // sane: every managed reference field must be null OR point to
                // readable committed memory. A live object satisfies this; a
                // stale/freed/torn slot has garbage (e.g. 0x8800D300C75AB77B) in
                // some reference field. Generic — needs no per-class predicate.
                if (sane) {
                    bool ok = true;
                    for (std::uint64_t off : refOffsets) {
                        std::uintptr_t v = 0;
                        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(b + off), &v, sizeof(v))) { ok = false; break; }
                        if (v == 0) continue;                       // null reference is fine
                        if (v < 0x10000) { ok = false; break; }     // null-page region → garbage
                        std::uintptr_t probe = 0;                   // must be a readable object
                        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(v), &probe, sizeof(probe))) { ok = false; break; }
                    }
                    // System.Boolean fields must hold exactly 0 or 1. A torn /
                    // mis-typed slot puts arbitrary bytes here.
                    if (ok) {
                        for (std::uint64_t off : boolOffsets) {
                            std::uint8_t bv = 0;
                            if (!Memory::SafeReadBytes(reinterpret_cast<void*>(b + off), &bv, sizeof(bv))) { ok = false; break; }
                            if (bv > 1) { ok = false; break; }
                        }
                    }
                    if (!ok) { ++dropSane; continue; }
                }
                bool pass = true;
                for (const auto& pred : filters) { if (!evalPredicate(b, pred)) { pass = false; break; } }
                if (!pass) { ++dropField; continue; }
                kept.push_back(p);
            }
            matches.swap(kept);
        }
        json out{
            {"applied",          active},
            {"raw_count",        raw},
            {"validate_dropped", dropValidate},
            {"alive_dropped",    dropAlive},
            {"field_dropped",    dropField},
            {"kept",             matches.size()},
        };
        if (args.value("sane", false)) {
            out["sane_dropped"] = dropSane;
            out["sane_ref_fields"]  = refOffsets.size();   // 0 + 0 bool ⇒ sane skipped (no metadata)
            out["sane_bool_fields"] = boolOffsets.size();
        }
        return out;
    };

#if defined(DISCRETE_BACKEND_MONO)
    // Phase 3 segment 1: list every Mono-loaded assembly.
    PipeServer::RegisterHandler(MagicStrings::CmdGetAssemblies, [](const json&) -> json {
        if (!Mono::IsResolved()) {
            return json{
                {"error", "mono runtime not resolved"},
                {"assemblies", json::array()},
                {"count", 0},
            };
        }
        auto vec = Mono::EnumerateAssemblies();
        json arr = json::array();
        for (const auto& a : vec) {
            arr.push_back({{"name", a.name}, {"path", a.path}});
        }
        return json{
            {"assemblies", std::move(arr)},
            {"count", vec.size()},
        };
    });

    // Phase 3 segment 2: classes / class info / field info / method info.
    PipeServer::RegisterHandler(MagicStrings::CmdGetClasses, [](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string assemblyName = args.value("assembly", std::string{});
        if (assemblyName.empty()) return json{{"error", "missing 'assembly' arg"}};
        Mono::MonoImage* img = Mono::FindImageByName(assemblyName);
        if (img == nullptr) return json{{"error", "assembly not found: " + assemblyName}};

        auto classes = Mono::EnumerateClasses(img);
        json arr = json::array();
        for (const auto& c : classes) {
            arr.push_back({
                {"namespace", c.ns},
                {"name", c.name},
                {"token", c.token},
            });
        }
        return json{
            {"assembly", assemblyName},
            {"count", classes.size()},
            {"classes", std::move(arr)},
        };
    });

    auto resolveClassFromArgs = [parseAddr](const json& args, std::string& errOut) -> Mono::MonoClass* {
        // Header path (Inspector drilldown): resolve the LIVE runtime class from
        // the object's own vtable via mono_object_get_class. That class is
        // always field-set-up because a real object of it exists — unlike a
        // metadata class returned by mono_class_from_name for a type only ever
        // reached via a field-type reference (e.g. Mortal.Core.StatConvertData
        // drilled through StatConvertCollectionData._list), whose field table is
        // empty so the walk returned only the inherited m_CachedPtr. Mirrors the
        // IL2CPP klass_from_header path + how Cheat Engine resolves drilldown
        // classes. Falls through to name resolution when no header address is
        // supplied or the read faults.
        std::uintptr_t hdrAddr = 0;
        if (args.contains("klass_from_header_addr") &&
            parseAddr(args["klass_from_header_addr"], hdrAddr) && hdrAddr != 0) {
            if (Mono::MonoClass* hk = Mono::ClassOfObject(hdrAddr)) return hk;
            // object_get_class unbound / read faulted → try name below.
        }
        std::string assemblyName = args.value("assembly", std::string{});
        std::string ns           = args.value("namespace", std::string{});
        std::string name         = args.value("name", std::string{});
        if (assemblyName.empty() || name.empty()) {
            errOut = "missing 'assembly' or 'name' arg";
            return nullptr;
        }
        Mono::MonoImage* img = Mono::FindImageByName(assemblyName);
        if (img == nullptr) { errOut = "assembly not found: " + assemblyName; return nullptr; }
        Mono::MonoClass* k = Mono::FindClass(img, ns, name);
        if (k == nullptr) { errOut = "class not found: " + ns + "." + name; return nullptr; }
        return k;
    };

    PipeServer::RegisterHandler(MagicStrings::CmdGetClassInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = Mono::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};

        json fields = json::array();
        for (const auto& f : res.info.fields) {
            fields.push_back({
                {"name", f.name},
                {"type", f.typeName},
                {"offset", f.offset},
                {"flags", f.flags},
                {"static", (f.flags & 0x10u) != 0},
            });
        }
        json methods = json::array();
        for (const auto& m : res.info.methods) {
            methods.push_back({
                {"name", m.name},
                {"token", m.token},
                {"flags", m.flags},
                {"static", (m.flags & 0x10u) != 0},
            });
        }
        json parent = nullptr;
        if (!res.info.parent.name.empty()) {
            parent = json{
                {"namespace", res.info.parent.ns},
                {"name", res.info.parent.name},
            };
        }
        return json{
            {"namespace", res.info.self.ns},
            {"name",      res.info.self.name},
            {"parent",    parent},
            {"fields",    std::move(fields)},
            {"methods",   std::move(methods)},
        };
    });

    // Phase C follow-up (Mono mirror of the IL2CPP handler ~line 3593) —
    // bulk class-info dump in one RPC, prevents the per-class
    // ThreadAttachGuard accumulation pattern documented in
    // lessons-learned.md "Phase C follow-up — IL2CPP equivalent". Symmetric
    // with the IL2CPP handler so the UI consumer can stay backend-
    // agnostic; the only payload difference is Mono methods carry no
    // `address` field (JIT addresses don't survive restart; on-demand
    // monoCompileMethod is the right answer when an address is needed).
    PipeServer::RegisterHandler(MagicStrings::CmdDumpClassInfo,
        [](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        std::vector<std::string> assemblyNames;
        if (args.contains("assemblies") && args["assemblies"].is_array()) {
            for (const auto& a : args["assemblies"]) {
                if (!a.is_string()) continue;
                const auto& s = a.get_ref<const std::string&>();
                if (!s.empty()) assemblyNames.push_back(s);
            }
        }
        if (assemblyNames.empty()) {
            return json{{"error", "missing or empty 'assemblies' arg"}};
        }

        std::int64_t limit = args.value("limit", static_cast<std::int64_t>(0));
        if (limit < 0) limit = 0;
        constexpr std::size_t kHardCap = 1'000'000u;
        const std::size_t effectiveCap = (limit == 0)
            ? kHardCap
            : std::min<std::size_t>(static_cast<std::size_t>(limit), kHardCap);

        json rows = json::array();
        std::size_t scanned = 0;
        bool truncated = false;
        std::vector<std::string> missingAssemblies;

        for (const auto& assemblyName : assemblyNames) {
            if (truncated) break;
            Mono::MonoImage* img = Mono::FindImageByName(assemblyName);
            if (img == nullptr) {
                missingAssemblies.push_back(assemblyName);
                continue;
            }
            auto classes = Mono::EnumerateClasses(img);
            for (const auto& ce : classes) {
                ++scanned;

                json row = {
                    {"assembly",  assemblyName},
                    {"namespace", ce.ns},
                    {"name",      ce.name},
                };

                Mono::MonoClass* k = Mono::FindClass(img, ce.ns, ce.name);
                if (k == nullptr) {
                    row["error"] = "FindClass failed";
                    rows.push_back(std::move(row));
                    if (rows.size() >= effectiveCap) { truncated = true; break; }
                    continue;
                }

                auto res = Mono::ReadClassInfo(k);
                if (!res.ok) {
                    row["error"] = "ReadClassInfo failed";
                    rows.push_back(std::move(row));
                    if (rows.size() >= effectiveCap) { truncated = true; break; }
                    continue;
                }

                json fields = json::array();
                for (const auto& f : res.info.fields) {
                    fields.push_back({
                        {"name", f.name}, {"type", f.typeName},
                        {"offset", f.offset}, {"flags", f.flags},
                        {"static", (f.flags & 0x10u) != 0},
                    });
                }
                json methods = json::array();
                for (const auto& m : res.info.methods) {
                    // Mono path: NO `address` field — JIT addresses
                    // require monoCompileMethod on demand and don't
                    // survive a game restart. UI handles null.
                    methods.push_back({
                        {"name", m.name}, {"token", m.token}, {"flags", m.flags},
                        {"static", (m.flags & 0x10u) != 0},
                    });
                }
                json parent = nullptr;
                if (!res.info.parent.name.empty()) {
                    parent = json{
                        {"namespace", res.info.parent.ns},
                        {"name",      res.info.parent.name},
                    };
                }
                row["parent"]  = parent;
                row["fields"]  = std::move(fields);
                row["methods"] = std::move(methods);
                rows.push_back(std::move(row));
                if (rows.size() >= effectiveCap) { truncated = true; break; }
            }
        }

        const std::size_t rowCount = rows.size();
        json response = {
            {"rows",      std::move(rows)},
            {"count",     rowCount},
            {"scanned",   scanned},
            {"truncated", truncated},
        };
        if (!missingAssemblies.empty()) {
            response["missing_assemblies"] = std::move(missingAssemblies);
        }
        return response;
    });

    // ------------------------------------------------------------------------
    // Phase 29a-v2 — snapshotBegin / snapshotChunk / snapshotEnd (Mono).
    //
    // Architecture lesson from build #597 live-smoke on 活俠傳 (Mono x86 /
    // Unity 2020.3): the original design did per-class decode work inside
    // the Chunk handler under a fresh ThreadAttachGuard per chunk. With
    // 17K classes / 297K instances → 542 chunks → 542 attach/detach cycles
    // → BDWGC finalizer queue accumulation → game-thread freeze partway
    // through the snapshot AND a residual freeze after the snapshot ended.
    // Same root cause as the 44s shutdown stall documented in
    // lessons-learned.md "Reentrant ThreadAttachGuard".
    //
    // Fix: do ALL decode work in `snapshotBegin` under ONE outer attach
    // guard. The decoded JSON for each class is pre-built and stored in
    // the session. `snapshotChunk` then becomes pure slicing — zero Mono
    // runtime calls per chunk, zero further attach cycles. Trade-off:
    // Begin takes longer (everything happens up-front) instead of being
    // amortised across chunks. Total time is unchanged; the user-visible
    // game freeze risk goes from "definitely freezes mid-snapshot on
    // legacy 32-bit Mono" to "doesn't freeze".
    //
    // End: drops the session. 5-min idle auto-expiry covers crashes.
    // ------------------------------------------------------------------------
    PipeServer::RegisterHandler(MagicStrings::CmdSnapshotBegin,
        [formatHex, decodeField, walkArrayElementsInline](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        // Parse args ----------------------------------------------------------
        // Hard-caps raised 2026-05-27 — old 1024/256 ceiling forced the
        // UI to under-cap on dense classes (活俠傳: 139 GameStat instances
        // with cap=100 missed positions 100..139 entirely; user wanted
        // headroom to push past 1024 for genuinely-large populations).
        // New 8192/4096 stays inside the per-class memory budget the
        // address-finder layer was already designed for (FindAllInstances
        // internal cap raised in lockstep).
        std::size_t instanceCap = static_cast<std::size_t>(args.value("instanceCap", 100));
        if (instanceCap == 0)    instanceCap = 100;
        if (instanceCap > 8192)  instanceCap = 8192;

        std::size_t arrayCap = static_cast<std::size_t>(args.value("arrayCap", 50));
        if (arrayCap > 4096)     arrayCap = 4096;
        // Total decoded-instance budget (mirror of IL2CPP) — bounds decode
        // time + memory; combined with the ascending-population plan sort it
        // drops high-population noise classes first. 0 = unlimited.
        std::size_t instanceDecodeBudget =
            static_cast<std::size_t>(args.value("instanceDecodeBudget", 50000));
        if (instanceDecodeBudget > 2000000) instanceDecodeBudget = 2000000;

        // Phase 29f (Layer B) — reference-array inner-field capture opt-in.
        // arrayElementFields turns it on; elementFieldCap bounds per-element
        // field count; arrayElementTargets (optional) limits capture to
        // matching "FQN" / "FQN.fieldName" entries. The x86 blanket gate is
        // applied below once the game pointer size is known.
        const bool reqArrayElementFields = args.value("arrayElementFields", false);
        const bool reqCaptureStaticValues = args.value("captureStaticValues", false);
        std::size_t elementFieldCap = static_cast<std::size_t>(args.value("elementFieldCap", 16));
        if (elementFieldCap == 0)  elementFieldCap = 16;
        if (elementFieldCap > 64)  elementFieldCap = 64;
        std::size_t elementCaptureBudget = static_cast<std::size_t>(args.value("elementCaptureBudget", 20000));
        if (elementCaptureBudget == 0)       elementCaptureBudget = 20000;
        if (elementCaptureBudget > 2000000)  elementCaptureBudget = 2000000;
        std::vector<std::string> arrayElementTargets;
        if (args.contains("arrayElementTargets") && args["arrayElementTargets"].is_array()) {
            for (const auto& t : args["arrayElementTargets"]) {
                if (t.is_string() && !t.get_ref<const std::string&>().empty())
                    arrayElementTargets.push_back(t.get_ref<const std::string&>());
            }
        }

        // 32-bit OOM guard — refuse before doing any work if game is x86
        // and already near the 4 GB user-space ceiling. Build #646 live-
        // smoke on 活俠傳 (Mono x86) crashed snap #2 when the DLL-side
        // JSON cache pushed past 4 GB; refusing here prevents the CTD
        // at the cost of asking the user to lower caps + retry.
        const ProcessMemorySnapshot memBefore = QueryProcessMemory();
        if (memBefore.pointerSize == 4 &&
            memBefore.privateUsage >= kX86PrivateUsageRefuseThreshold) {
            const double gb = memBefore.privateUsage / (1024.0 * 1024.0 * 1024.0);
            return json{
                {"error",
                 "Game process at " + std::to_string(static_cast<int>(gb * 100) / 100.0).substr(0, 4)
                 + " GB private memory — refusing snapshot to avoid OOM crash on this 32-bit Mono target. "
                   "Lower InstanceCap (try 128) and ArrayCap (try 32) then retry, "
                   "or restart the game to free memory before snapshotting."},
                {"gamePointerSize", memBefore.pointerSize},
                {"gameWorkingSet",  memBefore.workingSet},
                {"gamePrivateUsage", memBefore.privateUsage},
            };
        }

        // Snapshot memory budget (mirror of the IL2CPP handler) — stop the
        // decode before the game OOMs. budget = pre-snapshot private usage +
        // headroom, headroom clamped to a fraction of free RAM.
        std::uint64_t memHeadroomMono =
            static_cast<std::uint64_t>(args.value("memHeadroomMB", 0)) * 1024ull * 1024ull;
        if (memHeadroomMono == 0) memHeadroomMono = kSnapshotDefaultHeadroomBytes;
        if (const std::uint64_t availMono = QueryAvailablePhysBytes(); availMono > 0) {
            const auto cap = static_cast<std::uint64_t>(availMono * kSnapshotAvailPhysFraction);
            if (memHeadroomMono > cap) memHeadroomMono = cap;
        }
        const std::uint64_t memBudgetBytesMono = memBefore.privateUsage + memHeadroomMono;

        bool includeSystemAsm = args.value("includeSystemAssemblies", false);
        std::vector<std::string> requestedAssemblies;
        bool explicitScope = false;
        if (args.contains("assemblies") && args["assemblies"].is_array()) {
            for (const auto& a : args["assemblies"]) {
                if (!a.is_string()) continue;
                const auto& s = a.get_ref<const std::string&>();
                if (!s.empty()) requestedAssemblies.push_back(s);
            }
            explicitScope = !requestedAssemblies.empty();
        }
        // Default "全抓" mode: every assembly the runtime reports, EXCEPT
        // well-known system / engine / large-support-library noise. The
        // user-game assemblies (Assembly-CSharp / Mortal.Core / ...) get
        // through; mscorlib / System.* / UnityEngine.* / Fungus get
        // filtered. Caller can set includeSystemAssemblies=true to keep
        // everything (matches the original Phase 29 plan §"全抓 default,
        // opt-in narrow" semantics).
        std::size_t excludedSystemCount = 0;
        if (!explicitScope) {
            for (const auto& a : Mono::EnumerateAssemblies()) {
                if (!includeSystemAsm && IsSystemOrEngineAssembly(a.name)) {
                    ++excludedSystemCount;
                    continue;
                }
                requestedAssemblies.push_back(a.name);
            }
        }
        if (requestedAssemblies.empty()) {
            return json{{"error", "no assemblies visible (default exclude removed every assembly; try includeSystemAssemblies=true)"}};
        }

        // Wide heap walk ------------------------------------------------------
        auto bulk = Mono::FindAllInstances(instanceCap);

        // Build klass → (assembly, ns, name) index by enumerating each
        // requested assembly's classes ONCE — much cheaper than per-instance
        // resolver calls.
        struct ClassDesc {
            std::string  assembly;
            std::string  ns;
            std::string  name;
            Mono::MonoClass* klass = nullptr;
        };
        std::vector<ClassDesc> descs;
        std::unordered_map<Mono::MonoClass*, std::size_t> klassToDescIdx;
        for (const auto& assemblyName : requestedAssemblies) {
            Mono::MonoImage* img = Mono::FindImageByName(assemblyName);
            if (img == nullptr) continue;
            auto classes = Mono::EnumerateClasses(img);
            for (const auto& ce : classes) {
                if (ce.klass == nullptr) continue;
                ClassDesc d;
                d.assembly = assemblyName;
                d.ns       = ce.ns;
                d.name     = ce.name;
                d.klass    = ce.klass;
                klassToDescIdx[ce.klass] = descs.size();
                descs.push_back(std::move(d));
            }
        }

        // Build the plan: one row per class. Statics are always captured
        // (cheap, even classes with no live instances may have interesting
        // global values like `MAX_HP`). Instances are attached from the
        // wide-scan result.
        auto session = std::make_unique<Snapshot::Session>();
        session->capturedAt    = Snapshot::CapturedAtUtcNow();
        session->gameBuildId   = Snapshot::ComputeGameBuildId(MagicStrings::ModuleMonoBdwgc);
        // Fallback to legacy mono.dll for Unity < 5.4 builds.
        if (session->gameBuildId.empty()) {
            session->gameBuildId = Snapshot::ComputeGameBuildId(MagicStrings::ModuleMonoLegacy);
        }
        session->gameSessionId = Snapshot::SessionManager::GameSessionId();
        session->instanceCap   = instanceCap;
        session->arrayCap      = arrayCap;
        // Phase 29f (Layer B) — x86 blanket gate: 32-bit targets only get
        // inner-field capture when an explicit target set is supplied
        // (blanket on x86 risks the OOM the cap-clamp/refusal gate guards
        // against). x64 leaves targets empty for blanket capture.
        session->elementFieldCap     = elementFieldCap;
        session->elementCaptureBudget = elementCaptureBudget;
        session->captureStaticValues  = reqCaptureStaticValues;
        session->arrayElementTargets = std::move(arrayElementTargets);
        session->arrayElementFields  = reqArrayElementFields &&
            !(memBefore.pointerSize == 4 && session->arrayElementTargets.empty());
        // Effective state echoed in the Begin response so the UI knows when
        // the x86 blanket gate silently disabled a requested capture.
        const bool effArrayElementFields = session->arrayElementFields;
        // Phase 31-2 (Phase B) — generic value-type struct fan-out. Default on;
        // off restores the pre-31-2 single-ptr-word behaviour. memberCap mirrors
        // elementFieldCap as a per-struct bound. Captured into locals because the
        // session is std::move'd into the SessionManager before the Begin
        // response is built (echoing session->… there would be use-after-move).
        const bool        effValueStructFields = args.value("valueStructFields", true);
        const std::size_t effStructMemberCap   = args.value("structMemberCap",
                                                            static_cast<std::size_t>(32));
        session->valueStructFields = effValueStructFields;
        session->structMemberCap   = effStructMemberCap;
        session->plan.reserve(descs.size());

        std::unordered_map<std::string, Snapshot::PlanAssemblyEntry> planByAsm;
        for (const auto& asmName : requestedAssemblies) {
            planByAsm[asmName].assembly = asmName;
        }

        for (const auto& d : descs) {
            Snapshot::ClassPlanItem item;
            item.assembly = d.assembly;
            item.ns       = d.ns;
            item.name     = d.name;
            item.klass    = static_cast<void*>(d.klass);

            auto it = bulk.byClass.find(d.klass);
            if (it != bulk.byClass.end()) {
                item.instances = std::move(it->second);
            }
            // Skip classes that have neither statics nor instances — pure
            // type definitions we'd just emit empty for. Cheap heuristic:
            // we don't know yet which classes have statics, so include
            // every class for now (chunk emit decides whether to skip
            // empty-output rows).
            planByAsm[d.assembly].classCount++;
            planByAsm[d.assembly].instanceCount += item.instances.size();
            session->totalInstances += item.instances.size();
            session->plan.push_back(std::move(item));
        }

        // Decode low-population classes first (mirror of IL2CPP) so budget /
        // memory truncation drops the high-population noise tail and keeps the
        // low-population game-state classes.
        std::stable_sort(session->plan.begin(), session->plan.end(),
            [](const Snapshot::ClassPlanItem& a, const Snapshot::ClassPlanItem& b) {
                return a.instances.size() < b.instances.size();
            });

        // ---- Decode ALL classes under ONE outer attach guard --------------
        // Phase 29a-v2 fix: one outer ThreadAttachGuard for the whole
        // decode loop (was per-chunk in v1, accumulated BDWGC finalizers).
        // Phase 29a-v3 fix: GLOBAL ClassInfo cache across the entire
        // decode loop. Without this, System.Object's class info was
        // read 15,000+ times on 活俠傳 (every class's parent chain root)
        // and ResolveStaticFieldAddress was calling mono_class_vtable
        // per static field. Walk-log evidence: 23K `EnumerateAssemblies`
        // / `vtable_get_static_field_data` log lines for one snapshot →
        // game freeze. New shape: one ReadClassInfo per unique class
        // (cache hit on shared ancestors), one ResolveStaticBaseAddress
        // per class with statics, per-static-field becomes pure offset
        // arithmetic on the cached base.
        Mono::MonoDomain* domain = Mono::GetRootDomain();
        session->classes.reserve(session->plan.size());
        std::size_t decodedClasses = 0;
        std::size_t decodedInstances = 0;
        std::size_t decodedArrays = 0;
        std::size_t skippedOpenGenericArrays = 0;
        std::size_t classInfoCacheHits = 0;
        std::size_t classInfoCacheMisses = 0;
        // Phase 29f (Layer B) — shared across the whole decode loop so the
        // element-capture budget bounds total memory + object_get_class volume
        // (elementsProcessed caps ATTEMPTS, not just captures).
        std::size_t elementsProcessed = 0;
        std::size_t decodedElementSets = 0;
        bool         elementBudgetHit  = false;
        std::unordered_map<Mono::MonoClass*, Mono::ClassInfoResult> globalClassInfo;

        // Phase P1-fix — process-memory watchdog (mirror of the IL2CPP path).
        bool          memBudgetHitMono = false;
        bool          countBudgetHitMono = false;
        std::uint64_t peakPrivateMono  = memBefore.privateUsage;
        std::size_t   memCheckCounterMono = 0;
        auto checkMemBudgetMono = [&]() {
            const std::uint64_t pu = QueryProcessMemory().privateUsage;
            if (pu > peakPrivateMono) peakPrivateMono = pu;
            if (pu >= memBudgetBytesMono) memBudgetHitMono = true;
        };

        // Open-generic array detection: real array types are namespaced
        // ("System.Int32[]") or nested with slash ("MainGame/Foo[]"). Open
        // generic placeholders like "T[]" / "TKey[]" have neither separator
        // and reading them as managed arrays leads to garbage at random
        // pointer offsets (live-smoke build #597 reported length=1.9e12).
        auto isCapturableArrayType = [](const std::string& tn) -> bool {
            if (tn.size() < 4) return false;
            if (tn[tn.size() - 1] != ']' || tn[tn.size() - 2] != '[') return false;
            return tn.find('.') != std::string::npos ||
                   tn.find('/') != std::string::npos;
        };

        if (domain != nullptr) {
            // Phase 29a-v4 round 3 — one big attach (reverted from
            // batch-attach). Build #631 batch-attach attempt regressed
            // BongoCat (Mono x64) — game hangs after snapshot, but
            // worked fine in earlier one-big-attach builds. v4 yields
            // moved into the heap-walk path (where most game-thread
            // bandwidth pressure actually originates) and the
            // post-decode GC drain + Sleep(500) breathe.
            auto getClassInfo = [&](Mono::MonoClass* c)
                -> const Mono::ClassInfoResult& {
                auto it = globalClassInfo.find(c);
                if (it != globalClassInfo.end()) {
                    ++classInfoCacheHits;
                    return it->second;
                }
                ++classInfoCacheMisses;
                auto inserted = globalClassInfo.emplace(c, Mono::ReadClassInfo(c));
                return inserted.first->second;
            };

            // Phase 29f (Layer B) — given a reference-array element object
            // pointer, resolve its runtime class and walk its own fields one
            // level deep, reusing the cached getClassInfo + shared decodeField
            // exactly like the top-level instance walk. Returns
            // {"fields":[…]} or {"element_class_error":"…"}.
            auto captureMonoElementFields = [&](std::uintptr_t elemPtr) -> json {
                // Budget caps ATTEMPTS (not just captures): bounds both JSON
                // memory and per-element object_get_class volume so a blanket
                // sweep over a large game can't OOM / over-call the runtime.
                if (elementsProcessed >= session->elementCaptureBudget) {
                    elementBudgetHit = true;
                    return json::object();   // no fields → element keeps ptr= only
                }
                ++elementsProcessed;
                Mono::MonoClass* ec = Mono::ClassOfObject(elemPtr);
                if (ec == nullptr)
                    return json{{"element_class_error", "object_get_class_failed"}};
                // Only walk classes we enumerated in-scope. object_get_class on
                // a false-positive array slot can return a readable-but-garbage
                // class; the known-class set is exact (no runtime call) and keeps
                // a garbage class out of the class_get_parent / field walk.
                if (klassToDescIdx.find(ec) == klassToDescIdx.end())
                    return json{{"element_class_error", "class_out_of_scope"}};
                std::vector<Mono::MonoClass*> echain;
                for (Mono::MonoClass* cur = ec; cur != nullptr && echain.size() < 16; ) {
                    echain.push_back(cur);
                    if (Mono::GetResolver().class_get_parent == nullptr) break;
                    cur = SehGetParent(Mono::GetResolver().class_get_parent, cur);
                }
                json efields = json::array();
                std::size_t produced = 0;
                const std::size_t fcap = session->elementFieldCap;
                for (std::size_t ci = echain.size(); ci > 0 && produced < fcap; --ci) {
                    const auto& pr = getClassInfo(echain[ci - 1]);
                    if (!pr.ok) continue;
                    for (const auto& f : pr.info.fields) {
                        if (produced >= fcap) break;
                        if (f.flags & 0x10u) continue;   // skip statics
                        std::uintptr_t fieldAddr = elemPtr + static_cast<std::uintptr_t>(f.offset);
                        json fe = {
                            {"name",   f.name},
                            {"type",   f.typeName},
                            {"offset", f.offset},
                            {"from",   pr.info.self.name},
                        };
                        if (f.isEnum)      fe["is_enum"]       = true;
                        if (f.isValueType) fe["is_value_type"] = true;
                        const std::string& decodeType = (f.isEnum && !f.underlyingTypeName.empty())
                            ? f.underlyingTypeName : f.typeName;
                        json d = decodeField(decodeType, fieldAddr);
                        for (auto kv = d.begin(); kv != d.end(); ++kv) fe[kv.key()] = kv.value();
                        // Phase 31-2 (Phase B) — a value-type struct field on an
                        // array element fans out too (one extra level), so e.g.
                        // an array of objects each carrying a custom struct shows
                        // its members. Gated by valueStructFields; capped shallow.
                        if (session->valueStructFields &&
                            ShouldExpandValueStruct(f.isValueType, f.isEnum,
                                f.valueTypeClass, f.typeName, fe)) {
                            json sub = CaptureMonoStructFields(decodeField,
                                f.valueTypeClass, fieldAddr, 2, session->structMemberCap);
                            if (sub.contains("fields")) {
                                fe.erase("ptr");
                                fe["fields"] = std::move(sub["fields"]);
                            }
                        }
                        // Layer B is one level deep — nested arrays inside the
                        // element stay as their decodeField ptr, not walked.
                        efields.push_back(std::move(fe));
                        ++produced;
                    }
                }
                ++decodedElementSets;
                return json{{"fields", std::move(efields)}};
            };
            auto shouldCaptureInner = [&](const std::string& fqn, const std::string& fieldName) -> bool {
                if (!session->arrayElementFields) return false;
                if (session->arrayElementTargets.empty()) return true;   // blanket (x64)
                const std::string full = fqn + "." + fieldName;
                for (const auto& t : session->arrayElementTargets)
                    if (t == fqn || t == full) return true;
                return false;
            };

            auto decodeOneClass = [&](Snapshot::ClassPlanItem& item) {
                // (entire per-class decode body — moved from the inline
                // for-loop into a lambda so the outer batch loop can
                // re-enter attach state between batches without
                // duplicating code.)
                Mono::MonoClass* k = static_cast<Mono::MonoClass*>(item.klass);
                if (k == nullptr) {
                    session->classes.push_back(Snapshot::ClassPayload{
                        {"assembly", item.assembly},
                        {"namespace", item.ns},
                        {"name", item.name},
                        {"error", "klass null"},
                    });
                    return;
                }
                // Budget tripped (memory watchdog or total-instance budget) —
                // emit class metadata only.
                if (memBudgetHitMono || countBudgetHitMono) {
                    session->classes.push_back(Snapshot::ClassPayload{
                        {"assembly", item.assembly},
                        {"namespace", item.ns},
                        {"name", item.name},
                        {"truncated", memBudgetHitMono ? "memory_budget" : "instance_budget"},
                    });
                    return;
                }

                Snapshot::ClassPayload classEntry = {
                    {"assembly",  item.assembly},
                    {"namespace", item.ns},
                    {"name",      item.name},
                };

                // Build parent chain + cache ClassInfo for every entry up
                // front. chainInfo[0] is k itself, so the previously-
                // duplicate outer ReadClassInfo(k) call is gone.
                std::vector<Mono::MonoClass*> chain;
                for (Mono::MonoClass* cur = k; cur != nullptr && chain.size() < 16; ) {
                    chain.push_back(cur);
                    if (Mono::GetResolver().class_get_parent == nullptr) break;
                    cur = SehGetParent(Mono::GetResolver().class_get_parent, cur);
                }
                std::vector<const Mono::ClassInfoResult*> chainInfo;
                chainInfo.reserve(chain.size());
                for (auto* c : chain) chainInfo.push_back(&getClassInfo(c));

                const auto& res = *chainInfo[0];
                if (!res.ok) {
                    classEntry["error"] = "ReadClassInfo failed";
                    session->classes.push_back(std::move(classEntry));
                    return;
                }
                ++decodedClasses;
                if (!res.info.parent.name.empty()) {
                    classEntry["parent"] = Snapshot::ClassPayload{
                        {"namespace", res.info.parent.ns},
                        {"name",      res.info.parent.name},
                    };
                }

                // Static field METADATA only (Mono). Live-smoke build
                // #633 on BongoCat + 活俠傳: game freezes AFTER our RPC
                // returns; BGM keeps playing (audio thread fine), only
                // game main thread wedges. Pattern matches BDWGC
                // catching up on `mono_class_vtable` allocations we
                // triggered via ResolveStaticBaseAddress (one per
                // class with statics, then VTable for the class gets
                // created in managed heap). Game's main thread runs
                // GC catch-up on next allocation → freeze.
                //
                // Trade-off: snapshot captures static field NAME / TYPE /
                // OFFSET but not VALUE. Users who want static values
                // can use the Browser tab's static-field path on demand
                // (which goes through mono_class_vtable only for the
                // specific class they're viewing — single call, no
                // bulk GC pressure). Diff workflow loses static value
                // tracking on Mono but instance fields still work.
                Snapshot::ClassPayload statics = Snapshot::ClassPayload::array();
                for (const auto& f : res.info.fields) {
                    if ((f.flags & 0x10u) == 0u) continue;
                    statics.push_back(Snapshot::ClassPayload{
                        {"name",   f.name},
                        {"type",   f.typeName},
                        {"offset", f.offset},
                        {"error",  "static_value_skipped_mono"},
                    });
                }
                if (!statics.empty()) classEntry["statics"] = std::move(statics);

                // Phase 30a — build the instance field SCHEMA once per class
                // (sorted by offset; mirror of the IL2CPP path), emit it once,
                // then per instance emit only the value array aligned to it.
                // Eliminates the per-instance repetition of name/type/offset/from
                // that dominated the decode tree. Field metadata strings are
                // referenced (not copied) from the cached ClassInfoResult, whose
                // map nodes are stable for this decode.
                struct FieldDesc {
                    const std::string* name;
                    const std::string* type;
                    const std::string* from;
                    const std::string* decodeType;
                    std::size_t        offset;
                    bool               isEnum;
                    bool               isValueType;
                    bool               isArrayShape;
                    Mono::MonoClass*   valueTypeClass;  // Phase 31-2 (Phase B)
                };
                std::vector<FieldDesc> fieldDescs;
                for (std::size_t ci = chain.size(); ci > 0; --ci) {
                    const auto& pr = *chainInfo[ci - 1];
                    if (!pr.ok) continue;
                    for (const auto& f : pr.info.fields) {
                        if (f.flags & 0x10u) continue;   // skip statics
                        if (fieldDescs.size() >= 4096) break;
                        FieldDesc fd;
                        fd.name        = &f.name;
                        fd.type        = &f.typeName;
                        fd.from        = &pr.info.self.name;
                        fd.decodeType  = (f.isEnum && !f.underlyingTypeName.empty())
                                            ? &f.underlyingTypeName : &f.typeName;
                        fd.offset      = f.offset;
                        fd.isEnum      = f.isEnum;
                        fd.isValueType = f.isValueType;
                        fd.isArrayShape = f.typeName.size() >= 2 &&
                            f.typeName[f.typeName.size() - 1] == ']' &&
                            f.typeName[f.typeName.size() - 2] == '[';
                        fd.valueTypeClass = f.valueTypeClass;
                        fieldDescs.push_back(fd);
                    }
                    if (fieldDescs.size() >= 4096) break;
                }
                std::sort(fieldDescs.begin(), fieldDescs.end(),
                    [](const FieldDesc& a, const FieldDesc& b) { return a.offset < b.offset; });

                Snapshot::ClassPayload schemaArr = Snapshot::ClassPayload::array();
                for (const auto& fd : fieldDescs) {
                    Snapshot::ClassPayload s = {
                        {"name",   *fd.name},
                        {"type",   *fd.type},
                        {"offset", fd.offset},
                        {"from",   *fd.from},
                    };
                    if (fd.isEnum)      s["is_enum"]       = true;
                    if (fd.isValueType) s["is_value_type"] = true;
                    schemaArr.push_back(std::move(s));
                }
                if (!fieldDescs.empty() && !item.instances.empty())
                    classEntry["schema"] = std::move(schemaArr);

                const std::string classFqn =
                    item.ns.empty() ? item.name : item.ns + "." + item.name;

                // Instance walk with array element capture — uses cached
                // chainInfo + fieldDescs, no Mono calls inside the loop.
                Snapshot::ClassPayload instArr = Snapshot::ClassPayload::array();
                for (std::uintptr_t instAddr : item.instances) {
                    // Total-instance budget + process-memory watchdog —
                    // stop adding instances once either trips.
                    if (instanceDecodeBudget > 0 && decodedInstances >= instanceDecodeBudget)
                        countBudgetHitMono = true;
                    if (++memCheckCounterMono >= kSnapshotMemCheckInterval) {
                        memCheckCounterMono = 0;
                        checkMemBudgetMono();
                    }
                    if (memBudgetHitMono || countBudgetHitMono) break;

                    // Value-only entries aligned to schema order (no per-field
                    // name/type/offset/from — the UI re-expands from schema).
                    Snapshot::ClassPayload values = Snapshot::ClassPayload::array();
                    for (const auto& fd : fieldDescs) {
                        std::uintptr_t fieldAddr = instAddr + static_cast<std::uintptr_t>(fd.offset);
                        Snapshot::ClassPayload entry = Snapshot::ClassPayload::object();
                        json d = decodeField(*fd.decodeType, fieldAddr);
                        for (auto kv = d.begin(); kv != d.end(); ++kv) entry[kv.key()] = kv.value();

                        if (fd.isArrayShape) {
                            if (!isCapturableArrayType(*fd.type)) {
                                ++skippedOpenGenericArrays;
                            } else if (entry.contains("ptr") && entry["ptr"].is_string()) {
                                std::uintptr_t arrPtr = 0;
                                const std::string& ps = entry["ptr"].get_ref<const std::string&>();
                                if (ps.size() > 2 && ps[0] == '0' && (ps[1] == 'x' || ps[1] == 'X')) {
                                    arrPtr = std::strtoull(ps.c_str() + 2, nullptr, 16);
                                }
                                if (arrPtr != 0) {
                                    json elems = walkArrayElementsInline(
                                        arrPtr, *fd.type, arrayCap,
                                        shouldCaptureInner(classFqn, *fd.name)
                                            ? captureMonoElementFields
                                            : std::function<json(std::uintptr_t)>{});
                                    // Only record successful captures as
                                    // "arrayElements"; error-path responses get
                                    // arrayError so the UI can distinguish.
                                    if (elems.contains("error")) {
                                        entry["arrayError"] = elems.value("error", std::string{"unknown"});
                                        if (elems.contains("length")) {
                                            entry["arrayErrorLength"] = elems["length"];
                                        }
                                    } else {
                                        entry["arrayElements"] = std::move(elems);
                                        ++decodedArrays;
                                    }
                                }
                            }
                        } else if (session->valueStructFields &&
                                   ShouldExpandValueStruct(fd.isValueType, fd.isEnum,
                                       fd.valueTypeClass, *fd.type, entry)) {
                            // Phase 31-2 (Phase B) — generic value-type struct
                            // member fan-out (gated by valueStructFields). Named
                            // math structs already fanned out inside decodeField.
                            json sub = CaptureMonoStructFields(decodeField,
                                fd.valueTypeClass, fieldAddr, kStructMaxDepth,
                                session->structMemberCap);
                            if (sub.contains("fields")) {
                                entry.erase("ptr");
                                entry["fields"] = std::move(sub["fields"]);
                            }
                        }
                        values.push_back(std::move(entry));
                    }
                    Snapshot::ClassPayload instEntry = { {"address", formatHex(instAddr)} };
                    instEntry["v"] = std::move(values);
                    instArr.push_back(std::move(instEntry));
                    ++decodedInstances;
                }
                if (!instArr.empty()) classEntry["instances"] = std::move(instArr);

                session->classes.push_back(std::move(classEntry));
            };  // end decodeOneClass lambda

            // Phase 29a-v4 round 3 — REVERTED to one outer
            // ThreadAttachGuard for the whole decode loop. Build #631
            // live-smoke on BongoCat (Mono x64, 1,112 classes) hung
            // the game after snapshot, but build #602 with the v2
            // one-big-attach + memoised ClassInfo design ran cleanly.
            // The batch attach + per-batch Sleep(0) yields added
            // attach-cycle overhead that turned out worse than the
            // benefit. Reverting; 活俠傳 (Mono x86) still has a
            // separate freeze that v4's yields are addressing via the
            // heap-walk yield path.
            Mono::ThreadAttachGuard guard(domain);
            if (guard.Attached()) {
                for (auto& item : session->plan) {
                    decodeOneClass(item);
                }
            } else {
                for (auto& item : session->plan) {
                    session->classes.push_back(Snapshot::ClassPayload{
                        {"assembly", item.assembly},
                        {"namespace", item.ns},
                        {"name", item.name},
                        {"error", "mono_thread_attach failed"},
                    });
                }
            }

            // (Phase 29a-v4 round 4 — gc_collect drain + 500ms Sleep
            // both REMOVED on Mono. The drain runs on our worker
            // thread but doesn't actually drain the queue that affects
            // the game's main thread; the 500ms sleep just delayed
            // the inevitable wedge. Real fix landed above: skip
            // ResolveStaticBaseAddress to avoid mono_class_vtable
            // allocations entirely. If wedge persists, more drastic
            // changes needed.)
        }

        // Preserve assembly order in the response.
        for (const auto& asmName : requestedAssemblies) {
            session->planByAssembly.push_back(planByAsm[asmName]);
        }
        const std::size_t totalClasses = session->plan.size();
        const std::size_t totalInst    = session->totalInstances;
        const std::string capturedAt   = session->capturedAt;
        const std::string buildId      = session->gameBuildId;
        const std::string sessionGuid  = session->gameSessionId;

        json planArr = json::array();
        for (const auto& pe : session->planByAssembly) {
            planArr.push_back({
                {"assembly",      pe.assembly},
                {"classCount",    pe.classCount},
                {"instanceCount", pe.instanceCount},
            });
        }

        std::uint64_t sid = Snapshot::SessionManager::Instance().Begin(std::move(session));

        json scanStats = {
            {"regionsTotal",     bulk.regionsTotal},
            {"regionsScanned",   bulk.regionsScanned},
            {"regionsSkipImage", bulk.regionsSkipImage},
            {"regionsSkipStack", bulk.regionsSkipStack},
            {"bytesScanned",     bulk.bytesScanned},
            {"vtablesResolved",  bulk.vtablesResolved},
        };
        json decodeStats = {
            {"decodedClasses",           decodedClasses},
            {"decodedInstances",         decodedInstances},
            {"decodedArrays",            decodedArrays},
            {"skippedOpenGenericArrays", skippedOpenGenericArrays},
            {"classInfoCacheHits",       classInfoCacheHits},
            {"classInfoCacheMisses",     classInfoCacheMisses},
            {"decodedElementSets",       decodedElementSets},
            {"elementBudgetHit",         elementBudgetHit},
            {"truncatedByMemory",        memBudgetHitMono},
            {"truncatedByCount",         countBudgetHitMono},
            {"instanceDecodeBudget",     instanceDecodeBudget},
            {"memBudgetBytes",           memBudgetBytesMono},
            {"peakPrivateUsage",         peakPrivateMono},
        };

        // Post-decode memory snapshot for the response. UI surfaces this
        // alongside its own WorkingSet so the user sees how much the
        // game grew during capture (the 32-bit OOM danger metric).
        const ProcessMemorySnapshot memAfter = QueryProcessMemory();

        Discrete::Log::Write(MagicStrings::LogCategoryScan,
            "snapshotBegin(Mono) id=" + std::to_string(sid) +
            " classes=" + std::to_string(totalClasses) +
            " instances=" + std::to_string(totalInst) +
            " decoded=" + std::to_string(decodedClasses) +
            " decodedInst=" + std::to_string(decodedInstances) +
            " arrays=" + std::to_string(decodedArrays) +
            " elemSets=" + std::to_string(decodedElementSets) +
            (elementBudgetHit ? " elemBudgetHit=1" : "") +
            (memBudgetHitMono ? " MEM_TRUNCATED=1" : "") +
            (countBudgetHitMono ? " COUNT_TRUNCATED=1" : "") +
            " instBudget=" + std::to_string(instanceDecodeBudget) +
            " memBudget=" + std::to_string(memBudgetBytesMono) +
            " peakPriv=" + std::to_string(peakPrivateMono) +
            " buildId=" + buildId +
            " gameWorkingSet=" + std::to_string(memAfter.workingSet) +
            " gamePrivateUsage=" + std::to_string(memAfter.privateUsage));

        return json{
            {"ok",                      true},
            {"snapshotId",              std::to_string(sid)},
            {"capturedAt",              capturedAt},
            {"gameBuildId",             buildId},
            {"gameSessionId",           sessionGuid},
            {"instanceCap",             instanceCap},
            {"arrayCap",                arrayCap},
            {"arrayElementFields",      effArrayElementFields},
            {"valueStructFields",       effValueStructFields},
            {"structMemberCap",         effStructMemberCap},
            {"elementFieldCap",         elementFieldCap},
            {"totalClasses",            totalClasses},
            {"totalInstances",          totalInst},
            {"excludedSystemAssemblies", excludedSystemCount},
            {"includeSystemAssemblies",  includeSystemAsm},
            {"plan",                    std::move(planArr)},
            {"scanStats",               std::move(scanStats)},
            {"decodeStats",             std::move(decodeStats)},
            {"truncatedByMemory",       memBudgetHitMono},
            {"truncatedByCount",        countBudgetHitMono},
            {"gamePointerSize",         memAfter.pointerSize},
            {"gameWorkingSet",          memAfter.workingSet},
            {"gamePrivateUsage",        memAfter.privateUsage},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdSnapshotChunk,
        [](const json& args) -> json {
        // Phase 29a-v2: pure slicer. All decode work happened in Begin
        // under one outer attach guard. Chunk just hands back the next
        // window of pre-built class payloads. Zero Mono runtime calls
        // here — that's the rule that keeps 活俠傳 from freezing.
        std::string sidStr = args.value("snapshotId", std::string{});
        if (sidStr.empty()) return json{{"error", "missing 'snapshotId'"}};
        std::uint64_t sid = 0;
        try { sid = std::stoull(sidStr); } catch (...) {
            return json{{"error", "bad 'snapshotId'"}};
        }

        std::int64_t cursorIn = args.value("cursor", static_cast<std::int64_t>(-1));
        std::size_t  limit    = static_cast<std::size_t>(args.value("limit", 32));
        if (limit == 0)   limit = 32;
        if (limit > 200)  limit = 200;

        json classes = json::array();
        std::size_t totalPlan   = 0;
        std::size_t startCursor = 0;
        bool found = Snapshot::SessionManager::Instance().View(sid,
            [&](Snapshot::Session& s) {
                totalPlan = s.classes.size();
                startCursor = (cursorIn < 0) ? 0 : static_cast<std::size_t>(cursorIn);
                if (startCursor > totalPlan) startCursor = totalPlan;
                std::size_t end = std::min(totalPlan, startCursor + limit);
                for (std::size_t i = startCursor; i < end; ++i) {
                    classes.push_back(s.classes[i]);   // json copy — small per entry
                }
            });
        if (!found) {
            return json{{"error", "session_not_found"}, {"snapshotId", sidStr}};
        }
        std::size_t nextCursor = startCursor + static_cast<std::size_t>(classes.size());
        bool done = (nextCursor >= totalPlan);
        return json{
            {"ok",         true},
            {"snapshotId", sidStr},
            {"cursor",     startCursor},
            {"nextCursor", nextCursor},
            {"done",       done},
            {"classes",    std::move(classes)},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdSnapshotEnd,
        [](const json& args) -> json {
        std::string sidStr = args.value("snapshotId", std::string{});
        if (sidStr.empty()) return json{{"error", "missing 'snapshotId'"}};
        std::uint64_t sid = 0;
        try { sid = std::stoull(sidStr); } catch (...) {
            return json{{"error", "bad 'snapshotId'"}};
        }
        bool dropped = Snapshot::SessionManager::Instance().End(sid);
        return json{
            {"ok",         true},
            {"snapshotId", sidStr},
            {"dropped",    dropped},
        };
    });

    // Phase 25 — cross-class symbol finder (Mono). Walks every class in the
    // given assembly (or array of assemblies), iterates fields + methods,
    // returns rows whose names match a case-insensitive substring pattern.
    // Same surface as the IL2CPP handler below; both emit the same wire
    // shape so the UI VM is backend-agnostic.
    //
    // Args: { assembly?: string, assemblies?: string[], pattern: string,
    //         kinds?: string[], limit?: int }
    //   - Exactly one of `assembly` (legacy single) or `assemblies` (multi)
    //     must be present. Multi path applies a single global `limit` across
    //     the union of all walked assemblies.
    PipeServer::RegisterHandler(MagicStrings::CmdSearchSymbols,
        [](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string pattern = args.value("pattern", std::string{});
        // `staticOnly` lets callers list every static member without supplying
        // a name pattern (Symbol Finder "Static only" toggle). Pattern is
        // still required for the legacy name-substring flow.
        bool staticOnly = args.value("staticOnly", false);
        if (pattern.empty() && !staticOnly) {
            return json{{"error", "missing 'pattern' arg (or pass staticOnly=true)"}};
        }

        // Resolve target assembly list — accept legacy single `assembly` OR
        // new `assemblies` array. Order in `assemblies` is preserved.
        std::vector<std::string> assemblyNames;
        if (args.contains("assemblies") && args["assemblies"].is_array()) {
            for (const auto& a : args["assemblies"]) {
                if (!a.is_string()) continue;
                const auto& s = a.get_ref<const std::string&>();
                if (!s.empty()) assemblyNames.push_back(s);
            }
            if (assemblyNames.empty()) {
                return json{{"error", "'assemblies' array is empty"}};
            }
        } else {
            std::string single = args.value("assembly", std::string{});
            if (single.empty()) {
                return json{{"error", "missing 'assembly' or 'assemblies' arg"}};
            }
            assemblyNames.push_back(std::move(single));
        }

        bool wantFields  = true;
        bool wantMethods = true;
        if (args.contains("kinds") && args["kinds"].is_array()) {
            wantFields = false; wantMethods = false;
            for (const auto& k : args["kinds"]) {
                if (!k.is_string()) continue;
                const auto& s = k.get_ref<const std::string&>();
                if (s == "field")  wantFields  = true;
                if (s == "method") wantMethods = true;
            }
            if (!wantFields && !wantMethods) {
                return json{{"error", "kinds must include 'field' or 'method'"}};
            }
        }

        int limit = args.value("limit", 200);
        if (limit <= 0)      limit = 200;
        if (limit > 5000)    limit = 5000;

        // Build #547 follow-up — per-assembly limit in multi-asm mode.
        // Pre-fix bug: when caller passed `assemblies: [WAAPI, Assembly-CSharp,
        // ...]` the global limit drained on the alphabetically-first hit
        // (WAAPI / Audio middleware with hundreds of static methods) and the
        // outer loop broke before reaching Assembly-CSharp. User saw "Search
        // all user assemblies" appearing to ignore everything but the first.
        // Fix: in multi-asm mode each assembly gets its OWN limit budget;
        // a separate global hard cap (5x limit, ceiling 25k) prevents pathological
        // runaway when the user keeps the default 200 limit + scans 100 assemblies.
        const bool multiAsm = assemblyNames.size() > 1;
        const std::size_t perAsmLimit  = static_cast<std::size_t>(limit);
        const std::size_t globalCap    = multiAsm
            ? std::min<std::size_t>(25000, perAsmLimit * 5 * assemblyNames.size())
            : perAsmLimit;  // single-asm: behave exactly as before

        json matches  = json::array();
        bool truncated = false;          // GLOBAL truncation — caller hit overall ceiling
        std::size_t scanned = 0;
        std::vector<std::string> missingAssemblies;
        std::vector<std::string> truncatedAssemblies;  // per-asm budgets that filled

        for (const auto& assemblyName : assemblyNames) {
            if (matches.size() >= globalCap) { truncated = true; break; }
            Mono::MonoImage* img = Mono::FindImageByName(assemblyName);
            if (img == nullptr) {
                if (assemblyNames.size() == 1) {
                    return json{{"error", "assembly not found: " + assemblyName}};
                }
                missingAssemblies.push_back(assemblyName);
                continue;
            }

            std::size_t startCount = matches.size();
            bool thisAsmCapped = false;
            auto isAsmCapped = [&]() {
                return matches.size() - startCount >= perAsmLimit;
            };

            auto classes = Mono::EnumerateClasses(img);
            for (const auto& ce : classes) {
                if (ce.klass == nullptr) continue;
                if (thisAsmCapped) break;
                if (matches.size() >= globalCap) { truncated = true; break; }
                ++scanned;
                auto res = Mono::ReadClassInfo(ce.klass);
                if (!res.ok) continue;

                if (wantFields) {
                    for (const auto& f : res.info.fields) {
                        bool isStatic = (f.flags & 0x10u) != 0;
                        if (staticOnly && !isStatic) continue;
                        if (!pattern.empty() && !ICaseContainsAscii(f.name, pattern)) continue;
                        matches.push_back({
                            {"namespace", ce.ns},
                            {"class",     ce.name},
                            {"assembly",  assemblyName},
                            {"name",      f.name},
                            {"kind",      "field"},
                            {"type",      f.typeName},
                            {"offset",    f.offset},
                            {"static",    isStatic},
                        });
                        if (isAsmCapped())              { thisAsmCapped = true; break; }
                        if (matches.size() >= globalCap){ truncated = true;     break; }
                    }
                    if (thisAsmCapped || truncated) break;
                }
                if (wantMethods) {
                    for (const auto& m : res.info.methods) {
                        bool isStatic = (m.flags & 0x10u) != 0;
                        if (staticOnly && !isStatic) continue;
                        if (!pattern.empty() && !ICaseContainsAscii(m.name, pattern)) continue;
                        matches.push_back({
                            {"namespace", ce.ns},
                            {"class",     ce.name},
                            {"assembly",  assemblyName},
                            {"name",      m.name},
                            {"kind",      "method"},
                            {"token",     m.token},
                            {"static",    isStatic},
                        });
                        if (isAsmCapped())              { thisAsmCapped = true; break; }
                        if (matches.size() >= globalCap){ truncated = true;     break; }
                    }
                    if (thisAsmCapped || truncated) break;
                }
            }

            if (thisAsmCapped) truncatedAssemblies.push_back(assemblyName);
            if (truncated) break;  // global ceiling — abandon remaining assemblies
        }
        json response = {
            {"matches",         std::move(matches)},
            {"scanned_classes", scanned},
            {"truncated",       truncated},
        };
        if (!missingAssemblies.empty()) {
            response["missing_assemblies"] = std::move(missingAssemblies);
        }
        if (!truncatedAssemblies.empty()) {
            response["truncated_assemblies"] = std::move(truncatedAssemblies);
        }
        return response;
    });

    // Phase 28a — findFieldReferences (Mono). Walks every method in the
    // target assembly's IL bytecode and returns every method that touches
    // the named field via ldfld / stfld / ldsfld / stsfld / ldflda /
    // ldsflda. Exact match — IL operand tokens uniquely identify the
    // field. Scope: same-assembly references only (cross-assembly
    // MemberRef tokens deferred to v2).
    PipeServer::RegisterHandler(MagicStrings::CmdFindFieldReferences,
        [formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        std::string assembly = args.value("assembly", std::string{});
        if (assembly.empty()) return json{{"error", "missing 'assembly' arg"}};
        std::string ns       = args.value("ns",       std::string{});
        std::string klassNm  = args.value("class",    std::string{});
        std::string fieldNm  = args.value("fieldName", std::string{});
        if (klassNm.empty()) return json{{"error", "missing 'class' arg"}};
        if (fieldNm.empty()) return json{{"error", "missing 'fieldName' arg"}};

        std::string kindStr = args.value("kind", std::string{"any"});
        Mono::FieldRefKind kind = Mono::FieldRefKind::Any;
        if      (kindStr == "read")  kind = Mono::FieldRefKind::Read;
        else if (kindStr == "write") kind = Mono::FieldRefKind::Write;
        else if (kindStr != "any")
            return json{{"error", "invalid 'kind' (expected 'any' / 'read' / 'write')"}};

        int limit = args.value("limit", 200);
        if (limit <= 0)   limit = 200;
        if (limit > 5000) limit = 5000;

        Mono::MonoImage* img = Mono::FindImageByName(assembly);
        if (img == nullptr) return json{{"error", "assembly not found: " + assembly}};

        auto fr = Mono::FindFieldReferences(img, ns, klassNm, fieldNm, kind,
                                            static_cast<std::size_t>(limit));
        if (!fr.ok && !fr.error.empty()) {
            return json{{"error", fr.error}};
        }

        // Resolve declaring-field metadata for the response header. Re-walk
        // the class once to grab offset / type / static-flag; cheap relative
        // to the IL scan we just finished.
        json fieldInfo = json::object();
        fieldInfo["name"]    = fieldNm;
        fieldInfo["token"]   = fr.fieldToken;
        // Walk the resolved class once more for the headline metadata —
        // ReadClassInfo is O(num_fields_on_class), trivially fast.
        if (Mono::MonoClass* targetK = Mono::FindClass(img, ns, klassNm)) {
            auto info = Mono::ReadClassInfo(targetK);
            if (info.ok) {
                for (const auto& f : info.info.fields) {
                    if (f.name == fieldNm) {
                        fieldInfo["offset"]   = f.offset;
                        fieldInfo["type"]     = f.typeName;
                        fieldInfo["isStatic"] = (f.flags & 0x10u) != 0;
                        break;
                    }
                }
            }
        }

        json refs = json::array();
        for (const auto& m : fr.matches) {
            refs.push_back({
                {"assembly",       assembly},
                {"namespace",      m.ns},
                {"class",          m.className},
                {"method",         m.methodName},
                {"methodToken",    m.methodToken},
                {"methodFlags",    m.methodFlags},
                {"isMethodStatic", m.isMethodStatic},
                {"isStatic",       m.isStatic},
                {"kind",           m.refKind},
                {"confidence",     m.confidence},
                {"ilOffset",       m.ilOffset},
                // address null on Mono — JIT deferred to user-driven
                // findMethodInstances / callMethod flow.
                {"address",        nullptr},
            });
        }

        return json{
            {"ok",              true},
            {"field",           std::move(fieldInfo)},
            {"references",     std::move(refs)},
            {"scanned_methods", fr.scannedMethods},
            {"truncated",       fr.truncated},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetFieldInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = Mono::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        for (const auto& f : res.info.fields) {
            if (f.name == fieldName) {
                return json{
                    {"name", f.name},
                    {"type", f.typeName},
                    {"offset", f.offset},
                    {"flags", f.flags},
                    {"static", (f.flags & 0x10u) != 0},
                };
            }
        }
        return json{{"error", "field not found: " + fieldName}};
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetMethodInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = Mono::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        json matches = json::array();
        for (const auto& m : res.info.methods) {
            if (m.name == methodName) {
                matches.push_back({
                    {"name", m.name},
                    {"token", m.token},
                    {"flags", m.flags},
                    {"static", (m.flags & 0x10u) != 0},
                });
            }
        }
        if (matches.empty()) return json{{"error", "method not found: " + methodName}};
        return json{{"matches", std::move(matches)}};
    });

    // Phase 3 segment 3: static field address + JIT method native address.
    // (formatHex is the shared lambda declared in the backend-agnostic block above.)

    PipeServer::RegisterHandler(MagicStrings::CmdGetStaticFieldData,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto sa = Mono::ResolveStaticFieldAddress(k, fieldName);
        if (!sa.ok) return json{{"error", "static field not resolvable: " + fieldName}};
        return json{
            {"field",   fieldName},
            {"type",    sa.typeName},
            {"offset",  sa.offset},
            {"flags",   sa.flags},
            {"address", formatHex(reinterpret_cast<std::uintptr_t>(sa.address))},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdMonoCompileMethod,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto cm = Mono::CompileMethodByName(k, methodName);
        if (!cm.ok) return json{{"error", "mono_compile_method failed for: " + methodName}};
        return json{
            {"method",  cm.name},
            {"token",   cm.token},
            {"address", formatHex(reinterpret_cast<std::uintptr_t>(cm.address))},
        };
    });

    // Phase 6 segment C — findInstances (Mono).
    // Each MonoObject's first qword is its MonoVTable*. We resolve the class's
    // VTable for the root domain and scan committed RW pages for that pointer.
    PipeServer::RegisterHandler(MagicStrings::CmdFindInstances,
        [resolveClassFromArgs, formatHex, filterMatches](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        const auto& r = Mono::GetResolver();
        if (r.class_vtable == nullptr) return json{{"error", "mono_class_vtable not exported"}};

        Mono::MonoDomain* domain = Mono::GetRootDomain();
        if (domain == nullptr) return json{{"error", "no mono root domain"}};

        Mono::MonoVTable* vtable = nullptr;
        {
            Mono::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
            vtable = r.class_vtable(domain, k);
        }
        if (vtable == nullptr) return json{{"error", "mono_class_vtable returned null"}};

        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = std::min<std::size_t>(args["limit"].get<std::size_t>(), 4096);
        }
        // Phase 32 — opt-in `liveOnly`: enumerate ONLY live managed objects via
        // mono_gc_walk_heap (stops the world, visits each live object + its
        // MonoClass*). This skips dead/uncollected shells and runtime-bookkeeping
        // false positives that the raw pointer scan picks up. Falls back to the
        // raw scan when the export is missing (non-Unity / legacy Mono).
        const bool liveOnly = args.value("liveOnly", false);
        Memory::ScanStats stats{};
        std::vector<const void*> matches;
        bool usedLiveWalk = false;
        if (liveOnly && r.gc_walk_heap != nullptr) {
            struct WalkCtx { Mono::MonoClass* target; std::vector<const void*>* out; std::size_t limit; };
            WalkCtx ctx{ k, &matches, limit };
            Mono::fn_gc_references_cb cb =
                [](Mono::MonoObject* obj, Mono::MonoClass* klass, std::uintptr_t, std::uintptr_t,
                   Mono::MonoObject**, std::uintptr_t*, void* data) -> int {
                    auto* c = static_cast<WalkCtx*>(data);
                    if (klass == c->target && c->out->size() < c->limit) {
                        c->out->push_back(reinterpret_cast<const void*>(obj));
                    }
                    return c->out->size() >= c->limit ? 1 : 0;  // stop the walk once full
                };
            Mono::ThreadAttachGuard guard(domain);
            if (guard.Attached()) {
                r.gc_walk_heap(0, cb, &ctx);
                usedLiveWalk = true;
            }
        }
        // mono_gc_walk_heap is a no-op STUB on Unity's bdwgc (Boehm) builds —
        // the export exists but visits nothing (verified on Core Keeper: 0
        // objects, 7ms). The functional live-enumeration path on bdwgc is the
        // unity-liveness API (deferred — version-sensitive struct ABI). Treat an
        // empty walk as "unsupported" and fall back to the raw scan so liveOnly
        // is never WORSE than the default, instead of returning a misleading 0.
        bool liveWalkEmpty = false;
        if (usedLiveWalk && matches.empty()) { usedLiveWalk = false; liveWalkEmpty = true; }
        if (!usedLiveWalk) {
            matches = Memory::ScanForPointer(reinterpret_cast<std::uintptr_t>(vtable), limit, &stats);
        }
        // Phase 32 — `sane` layer needs this class's managed reference-field
        // offsets (non-static, non-value-type). Built from class metadata only
        // when requested. Mirrors walkInstance's chain walk + ReadClassInfo.
        std::vector<std::uint64_t> refOffsets;
        std::vector<std::uint64_t> boolOffsets;
        if (args.value("sane", false)) {
            std::vector<Mono::MonoClass*> chain;
            {
                Mono::ThreadAttachGuard guard(domain);
                if (guard.Attached()) {
                    for (Mono::MonoClass* cur = k; cur != nullptr && chain.size() < 16; ) {
                        chain.push_back(cur);
                        if (r.class_get_parent == nullptr) break;
                        cur = SehGetParent(r.class_get_parent, cur);
                    }
                }
            }
            if (!chain.empty() && chain.size() < 16) {
                for (auto* c : chain) {
                    Mono::ClassInfoResult res = Mono::ReadClassInfo(c);
                    if (!res.ok) continue;
                    for (const auto& f : res.info.fields) {
                        if (f.flags & 0x10u) continue;   // static
                        // System.Boolean is a value type — capture its offset for
                        // the 0/1 sanity probe BEFORE the value-type skip below.
                        if (f.typeName == "System.Boolean") { boolOffsets.push_back(static_cast<std::uint64_t>(f.offset)); continue; }
                        if (f.isValueType) continue;     // value type (incl IntPtr / struct / enum)
                        refOffsets.push_back(static_cast<std::uint64_t>(f.offset));
                    }
                }
            }
        }
        // Phase 32 — generic post-scan filter (validate / aliveOnly / sane / fieldFilter).
        // expectedHeader = MonoVTable* (the first qword of a real Mono object).
        json filterStats = filterMatches(matches, reinterpret_cast<std::uintptr_t>(vtable), args, refOffsets, boolOffsets);
        filterStats["live_walk"] = usedLiveWalk;
        if (liveOnly && liveWalkEmpty)        filterStats["live_walk_unsupported"] = true;
        else if (liveOnly && !usedLiveWalk)   filterStats["live_walk_unavailable"] = true;
        json arr = json::array();
        for (const void* p : matches) {
            arr.push_back(formatHex(reinterpret_cast<std::uintptr_t>(p)));
        }
        {
            char line[256];
            std::snprintf(line, sizeof(line),
                "findInstances(Mono) class=%p vtable=%p matches=%zu "
                "regions total=%zu scanned=%zu skipImage=%zu skipStack=%zu bytes=%zu stacks=%zu",
                static_cast<const void*>(k), static_cast<const void*>(vtable),
                matches.size(), stats.regionsTotal, stats.regionsScanned,
                stats.regionsSkipImage, stats.regionsSkipStack,
                stats.bytesScanned, stats.threadStacks);
            Discrete::Log::Write(MagicStrings::LogCategoryScan, line);
        }
        return json{
            // `class_ptr` mirrors the IL2CPP findInstances shape so cross-backend
            // consumers (e.g. a first-qword instance validator) can read
            // the same field name. On Mono the FIRST qword of a real instance
            // is actually MonoVTable* (not MonoClass*), so for validators that
            // compare against the instance's first qword, use `vtable`.
            // `class_ptr` is provided for class-identity purposes.
            {"class_ptr", formatHex(reinterpret_cast<std::uintptr_t>(k))},
            {"vtable",   formatHex(reinterpret_cast<std::uintptr_t>(vtable))},
            {"count",    arr.size()},
            {"limit",    limit},
            {"matches",  std::move(arr)},
            {"filter",   std::move(filterStats)},
            {"scan_stats", json{
                {"regions_total",      stats.regionsTotal},
                {"regions_scanned",    stats.regionsScanned},
                {"regions_skip_image", stats.regionsSkipImage},
                {"regions_skip_stack", stats.regionsSkipStack},
                {"bytes_scanned",      stats.bytesScanned},
                {"thread_stacks",      stats.threadStacks},
            }},
        };
    });

    // Phase 21a — listFieldValuesAcrossInstances (Mono).
    // Inverse of walkInstance: given a class spec + a fixed cumulative byte
    // offset from instance base + the field's managed type, find every live
    // instance of the class and decode the field at instance+offset for each.
    // Reuses the same heap scan as findInstances and the same per-type decoder
    // as walkInstance, so primitives come back as `value`, strings as `ptr`
    // + `string`, references as `ptr`, unreadable rows as `error: unreadable`.
    PipeServer::RegisterHandler(MagicStrings::CmdListFieldValuesAcrossInstances,
        [resolveClassFromArgs, formatHex, decodeField](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        if (!args.contains("fieldOffset") || !args["fieldOffset"].is_number()) {
            return json{{"error", "missing/invalid 'fieldOffset' (int)"}};
        }
        std::int64_t fieldOffset = args["fieldOffset"].get<std::int64_t>();
        if (fieldOffset < 0 || fieldOffset > (1 << 24)) {
            return json{{"error", "'fieldOffset' out of range"}};
        }
        std::string fieldType = args.value("fieldType", std::string{"System.Object"});

        const auto& r = Mono::GetResolver();
        if (r.class_vtable == nullptr) return json{{"error", "mono_class_vtable not exported"}};
        Mono::MonoDomain* domain = Mono::GetRootDomain();
        if (domain == nullptr) return json{{"error", "no mono root domain"}};

        Mono::MonoVTable* vtable = nullptr;
        {
            Mono::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
            vtable = r.class_vtable(domain, k);
        }
        if (vtable == nullptr) return json{{"error", "mono_class_vtable returned null"}};

        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = std::min<std::size_t>(args["limit"].get<std::size_t>(), 4096);
        }
        auto matches = Memory::ScanForPointer(reinterpret_cast<std::uintptr_t>(vtable), limit, nullptr);

        json rows = json::array();
        for (const void* p : matches) {
            std::uintptr_t inst = reinterpret_cast<std::uintptr_t>(p);
            std::uintptr_t fieldAddr = inst + static_cast<std::uintptr_t>(fieldOffset);
            json row = {
                {"instance",     formatHex(inst)},
                {"fieldAddress", formatHex(fieldAddr)},
            };
            json decoded = decodeField(fieldType, fieldAddr);
            for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                row[kv.key()] = kv.value();
            }
            rows.push_back(std::move(row));
        }
        return json{
            {"class_ptr",   formatHex(reinterpret_cast<std::uintptr_t>(k))},
            {"vtable",      formatHex(reinterpret_cast<std::uintptr_t>(vtable))},
            {"count",       rows.size()},
            {"limit",       limit},
            {"fieldOffset", fieldOffset},
            {"fieldType",   fieldType},
            {"rows",        std::move(rows)},
        };
    });

    // Phase 27a — scanFieldValue (Mono).
    // CE-style value search filtered to Unity managed instances. Two modes:
    //   * Global (default): walks every committed RW region with the same
    //     MEM_IMAGE + thread-stack filter `findInstances` uses, then feeds
    //     each value-match through `Mono::WhatIsAt` to drop non-managed
    //     hits. Result rows are populated with the resolved instance +
    //     class + field — exactly the kind of context CE alone cannot
    //     provide.
    //   * Class-scoped (request `classScope: true`): skips the region walk
    //     entirely. Uses findInstances' Memory::ScanForPointer to enumerate
    //     live instances of one class, then per-instance walks declared
    //     fields whose typeName matches the wire dataType, SafeReads each
    //     field, and emits matches inline. Orders of magnitude faster
    //     because we never touch non-managed memory.
    PipeServer::RegisterHandler(MagicStrings::CmdScanFieldValue,
        [resolveClassFromArgs, formatHex, decodeField](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        // --- Parse wire shape ---
        std::string dtStr = args.value("dataType", std::string{"int32"});

        // Phase 27b-4 — string scan path. Different request shape:
        //   value     : string (the search string in UTF-8 wire encoding)
        //   encoding  : "utf16"|"utf8"|"ascii" (default utf16, matches managed)
        //   matchMode : "exact"|"contains" (default contains)
        //   limit / maxBackwardScan : same as numeric path
        if (dtStr == "string") {
            std::string encStr   = args.value("encoding",  std::string{"utf16"});
            std::string mmStr    = args.value("matchMode", std::string{"contains"});
            Memory::StringEncoding enc{};
            Memory::StringMatchMode mm{};
            if (!ParseStringEncoding(encStr, enc))     return json{{"error", "unknown 'encoding'"}};
            if (!ParseStringMatchMode(mmStr, mm))      return json{{"error", "unknown 'matchMode'"}};
            if (!args.contains("value") || !args["value"].is_string()) {
                return json{{"error", "string scan requires 'value' (string)"}};
            }
            std::string needleUtf8 = args["value"].get<std::string>();
            auto needleBytes = EncodeNeedleForString(needleUtf8, enc);
            if (needleBytes.empty()) return json{{"error", "needle encoding failed (e.g. non-ASCII chars in ASCII mode)"}};

            Memory::StringScanRequest sreq{};
            sreq.encoding  = enc;
            sreq.matchMode = mm;
            sreq.needle    = std::move(needleBytes);
            sreq.limit     = static_cast<std::size_t>(args.value("limit", 5000));
            if (sreq.limit == 0 || sreq.limit > 50000) sreq.limit = 5000;

            std::size_t maxBackwardScan = static_cast<std::size_t>(args.value("maxBackwardScan", 16384));
            if (maxBackwardScan > 16384) maxBackwardScan = 16384;
            if (maxBackwardScan == 0)    maxBackwardScan = 4096;

            // Phase 30 P2 (Mono port) — one outer attach for the whole batch
            // (the index Lookup + BuildClassRangeCache we call). Mirrors the
            // value scan handler; makes inner per-candidate guards no-ops and
            // satisfies ResolveInstanceLookup's attach precondition.
            Mono::ThreadAttachGuard scanGuard(Mono::GetRootDomain());

            if (!Mono::BuildClassRangeCache()) {
                return json{{"error", "address-finder class cache build failed"}};
            }

            Memory::ValueScanStats sstats{};
            auto candidates = Memory::ScanForString(sreq, &sstats);

            json rows = json::array();
            std::size_t droppedNonManaged = 0;
            std::string matchedDisplay = DecodeMatchedNeedleToUtf8(sreq.needle, enc);

            // Phase 30 P2 — reverse-lookup index for the string-scan candidate
            // loop (build once, O(log N) per candidate, WhatIsAt fallback).
            // Perf-gate: only worth the heap walk when candidates are plentiful.
            Mono::ReverseLookupIndex strIndex;
            bool strIndexOk = candidates.size() >= kRevIndexMinCandidates && strIndex.Build();
            for (const auto& c : candidates) {
                std::uintptr_t addr = reinterpret_cast<std::uintptr_t>(c.addr);
                Mono::AddressLookup lookup = strIndexOk
                    ? strIndex.Lookup(addr, maxBackwardScan)
                    : Mono::WhatIsAt(addr, maxBackwardScan);
                if (!lookup.found) { droppedNonManaged++; continue; }
                json row = {
                    {"addr",            formatHex(addr)},
                    {"instAddr",        formatHex(lookup.instance)},
                    {"asm",             lookup.assembly},
                    {"ns",              lookup.ns},
                    {"class",           lookup.className},
                    {"fieldOffset",     static_cast<std::int64_t>(lookup.fieldOffset)},
                    {"currentValue",    matchedDisplay},
                    {"matchedLength",   c.length},
                };
                if (lookup.field.present) {
                    row["field"]     = lookup.field.name;
                    row["fieldType"] = lookup.field.typeName;
                    row["fieldKind"] = lookup.field.kind;
                } else if (lookup.nearestField.present) {
                    row["nearestField"] = json{
                        {"name",  lookup.nearestField.name},
                        {"delta", static_cast<std::int64_t>(lookup.nearestFieldDelta)},
                    };
                    row["fieldKind"] = "near";
                } else {
                    row["fieldKind"] = "interior";
                }
                rows.push_back(std::move(row));
            }
            return json{
                {"ok",                true},
                {"mode",              "global"},
                {"dataType",          "string"},
                {"encoding",          encStr},
                {"matchMode",         mmStr},
                {"count",             rows.size()},
                {"droppedNonManaged", droppedNonManaged},
                {"truncated",         candidates.size() >= sreq.limit},
                {"scannedBytes",      sstats.bytesScanned},
                {"readFailureBytes",  sstats.readFailureBytes},
                {"candidates",        std::move(rows)},
            };
        }

        Memory::ValueDataType dt{};
        if (!ParseValueDataType(dtStr, dt)) {
            return json{{"error", "unknown 'dataType' (try int32/uint32/float/double/...)"}};
        }
        std::string stStr = args.value("scanType", std::string{"exact"});
        Memory::ValueScanType st{};
        if (!ParseValueScanType(stStr, st)) {
            return json{{"error", "unknown 'scanType' (expected exact|range)"}};
        }
        if (!args.contains("value")) return json{{"error", "missing 'value'"}};

        Memory::ValueScanRequest req{};
        req.dataType  = dt;
        req.scanType  = st;
        req.tolerance = args.value("tolerance", 0.0001);
        req.alignment = static_cast<std::size_t>(args.value("alignment", 0));
        req.limit     = static_cast<std::size_t>(args.value("limit", 50000));
        if (req.limit == 0)        req.limit = 50000;
        if (req.limit > 50000)     req.limit = 50000;

        if (!PackJsonValueToBytes(args["value"], dt, req.lo)) {
            return json{{"error", "'value' out of range or not numeric for dataType"}};
        }
        if (st == Memory::ValueScanType::Range) {
            if (!args.contains("valueTo")) {
                return json{{"error", "scanType=range requires 'valueTo'"}};
            }
            if (!PackJsonValueToBytes(args["valueTo"], dt, req.hi)) {
                return json{{"error", "'valueTo' out of range or not numeric for dataType"}};
            }
        }

        std::size_t maxBackwardScan = static_cast<std::size_t>(
            args.value("maxBackwardScan", 16384));
        if (maxBackwardScan > 16384) maxBackwardScan = 16384;
        if (maxBackwardScan == 0)    maxBackwardScan = 4096;

        const bool classScope = args.value("classScope", false);

        // --- Build response shell ---
        const char* expectedClrName = ValueDataTypeClrName(dt);

        // -------- Class-scoped mode --------
        if (classScope) {
            std::string err;
            Mono::MonoClass* k = resolveClassFromArgs(args, err);
            if (k == nullptr) return json{{"error", err}};

            const auto& r = Mono::GetResolver();
            if (r.class_vtable == nullptr) return json{{"error", "mono_class_vtable not exported"}};
            Mono::MonoDomain* domain = Mono::GetRootDomain();
            if (domain == nullptr) return json{{"error", "no mono root domain"}};

            Mono::MonoVTable* vtable = nullptr;
            {
                Mono::ThreadAttachGuard guard(domain);
                if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
                vtable = r.class_vtable(domain, k);
            }
            if (vtable == nullptr) return json{{"error", "mono_class_vtable returned null"}};

            // Enumerate live instances (cap at limit * 4 — most fields won't
            // match, but we don't want to scan more than necessary).
            const std::size_t instanceLimit = std::min<std::size_t>(req.limit * 4, 4096);
            auto matches = Memory::ScanForPointer(
                reinterpret_cast<std::uintptr_t>(vtable), instanceLimit, nullptr);

            // For each instance, walk declared fields of matching type and
            // SafeRead+compare each field value. ReadClassInfo gives us the
            // declared field list with offsets + type names — same path
            // walkInstance uses.
            //
            // IMPORTANT: ReadClassInfo creates its own ThreadAttachGuard
            // internally. Wrapping THIS call in an outer guard would nest
            // attach/detach on the pipe worker thread — the same hazard
            // the Phase 20-MT investigation flagged for Mono (build #54
            // BongoCat-killer). Don't add an outer guard here.
            Mono::ClassInfoResult infoResult = Mono::ReadClassInfo(k);
            if (!infoResult.ok) return json{{"error", "ReadClassInfo failed"}};
            const Mono::ClassInfo& info = infoResult.info;
            // Echo assembly back from request (resolver matched it; we don't
            // re-derive it from the runtime).
            std::string asmName = args.value("assembly", std::string{});
            // ECMA-335 FieldAttributes.STATIC = 0x10.
            constexpr std::uint32_t kFieldAttrStatic = 0x10;

            json rows = json::array();
            std::size_t fieldsConsidered = 0;
            const std::size_t valueSize   = Memory::ValueDataTypeSize(dt);
            const bool        isFloat     = (dt == Memory::ValueDataType::Float || dt == Memory::ValueDataType::Double);
            const bool        isRange     = (st == Memory::ValueScanType::Range);

            // Reuse Memory::ScanForValue's per-type predicate by routing one
            // value through it: load the candidate as 8 bytes, call a one-
            // off comparator. To keep the handler tight we use a small
            // typed lambda — exact comparators for each dt.
            auto matchesValue = [&](const std::uint8_t* candidate) -> bool {
                std::uint8_t v[8] = {};
                std::memcpy(v, candidate, valueSize);
                // Use ScanForValue's comparison logic by stashing through a
                // 1-byte fake region — overkill. Inline the typed compare:
                #define CMP_INT(T) do { \
                    T cur; std::memcpy(&cur, v, sizeof(T)); \
                    T lo;  std::memcpy(&lo, req.lo, sizeof(T)); \
                    if (isRange) { T hi; std::memcpy(&hi, req.hi, sizeof(T)); return cur >= lo && cur <= hi; } \
                    return cur == lo; \
                } while (0)
                #define CMP_FLT(T) do { \
                    T cur; std::memcpy(&cur, v, sizeof(T)); \
                    T lo;  std::memcpy(&lo, req.lo, sizeof(T)); \
                    if (!std::isfinite(static_cast<double>(cur))) return false; \
                    if (isRange) { T hi; std::memcpy(&hi, req.hi, sizeof(T)); return cur >= lo && cur <= hi; } \
                    return std::fabs(static_cast<double>(cur) - static_cast<double>(lo)) <= req.tolerance; \
                } while (0)
                switch (dt) {
                    case Memory::ValueDataType::Int8:   CMP_INT(std::int8_t);
                    case Memory::ValueDataType::UInt8:  CMP_INT(std::uint8_t);
                    case Memory::ValueDataType::Int16:  CMP_INT(std::int16_t);
                    case Memory::ValueDataType::UInt16: CMP_INT(std::uint16_t);
                    case Memory::ValueDataType::Int32:  CMP_INT(std::int32_t);
                    case Memory::ValueDataType::UInt32: CMP_INT(std::uint32_t);
                    case Memory::ValueDataType::Int64:  CMP_INT(std::int64_t);
                    case Memory::ValueDataType::UInt64: CMP_INT(std::uint64_t);
                    case Memory::ValueDataType::Float:  CMP_FLT(float);
                    case Memory::ValueDataType::Double: CMP_FLT(double);
                }
                #undef CMP_INT
                #undef CMP_FLT
                (void)isFloat;
                return false;
            };

            for (const void* p : matches) {
                if (rows.size() >= req.limit) break;
                std::uintptr_t inst = reinterpret_cast<std::uintptr_t>(p);
                for (const auto& f : info.fields) {
                    if ((f.flags & kFieldAttrStatic) != 0) continue;
                    if (f.typeName != expectedClrName) continue;
                    fieldsConsidered++;
                    std::uintptr_t fieldAddr = inst + static_cast<std::uintptr_t>(f.offset);
                    std::uint8_t   buf[8] = {};
                    if (!Memory::SafeReadBytes(reinterpret_cast<void*>(fieldAddr), buf, valueSize)) {
                        continue;
                    }
                    if (!matchesValue(buf)) continue;

                    json row = {
                        {"addr",          formatHex(fieldAddr)},
                        {"instAddr",      formatHex(inst)},
                        {"asm",           asmName},
                        {"ns",            info.self.ns},
                        {"class",         info.self.name},
                        {"field",         f.name},
                        {"fieldOffset",   f.offset},
                        {"fieldType",     f.typeName},
                        {"fieldKind",     "leaf"},
                        {"currentValue",  DecodeValueBytesToJson(buf, dt)},
                    };
                    rows.push_back(std::move(row));
                    if (rows.size() >= req.limit) break;
                }
            }

            return json{
                {"ok",                true},
                {"mode",              "class-scoped"},
                {"dataType",          dtStr},
                {"scanType",          stStr},
                {"count",             rows.size()},
                {"truncated",         rows.size() >= req.limit},
                {"instancesScanned",  matches.size()},
                {"fieldsConsidered",  fieldsConsidered},
                {"droppedNonManaged", 0},
                {"scannedBytes",      static_cast<std::uint64_t>(matches.size()) * valueSize},
                {"candidates",        std::move(rows)},
            };
        }

        // -------- Global mode --------
        // Make sure the address-finder's class-range cache is built; first call
        // is a one-shot ~tens-of-ms pass and subsequent whatIsAt calls are sub-ms.
        if (!Mono::BuildClassRangeCache()) {
            return json{{"error", "address-finder class cache build failed"}};
        }

        // Phase 27 hardening: decouple raw memory-scan budget from the UI
        // emit budget. Always scan up to kRawScanCap raw value matches so
        // that one big static array can't starve the result set when the
        // user's value happens to appear in literal init data. The UI's
        // `limit` only caps the final post-filter rows.
        const std::size_t emitLimit = req.limit;
        constexpr std::size_t kRawScanCap = 50000;
        Memory::ValueScanRequest scanReq = req;
        scanReq.limit = kRawScanCap;

        Memory::ValueScanStats stats{};
        auto candidates = Memory::ScanForValue(scanReq, &stats);

        // Phase 27 follow-up — open ONE outer ThreadAttachGuard for the
        // entire WhatIsAt batch. Inner guards inside WhatIsAt become
        // reentrant no-ops (depth > 0), so we go from N attach/detach
        // cycles to 1. Closes the 44s-game-shutdown bug where 4K+
        // attach cycles left finalizable refs that BDWGC ran on exit.
        Mono::ThreadAttachGuard scanGuard(Mono::GetRootDomain());

        // Phase 30 P2 (Mono port) — build the reverse-lookup index once, then
        // resolve each candidate via O(log N) binary search instead of a
        // per-candidate backward scan. Fall back to WhatIsAt if Build failed
        // or the candidate count is below the amortization threshold.
        Mono::ReverseLookupIndex revIndex;
        bool revIndexOk = candidates.size() >= kRevIndexMinCandidates && revIndex.Build();

        json rows = json::array();
        std::size_t droppedNonManaged = 0, droppedDiversity = 0, droppedNoise = 0;
        std::unordered_map<std::uintptr_t, int> perInstanceCount;
        for (const auto& c : candidates) {
            if (rows.size() >= emitLimit) break;
            std::uintptr_t addr = reinterpret_cast<std::uintptr_t>(c.addr);
            Mono::AddressLookup lookup = revIndexOk
                ? revIndex.Lookup(addr, maxBackwardScan)
                : Mono::WhatIsAt(addr, maxBackwardScan);
            if (!lookup.found) { droppedNonManaged++; continue; }
            if (IsCompilerGeneratedClassName(lookup.className)) { droppedNoise++; continue; }
            if (++perInstanceCount[lookup.instance] > kPerInstanceCap) { droppedDiversity++; continue; }

            json row = {
                {"addr",            formatHex(addr)},
                {"instAddr",        formatHex(lookup.instance)},
                {"asm",             lookup.assembly},
                {"ns",              lookup.ns},
                {"class",           lookup.className},
                {"fieldOffset",     static_cast<std::int64_t>(lookup.fieldOffset)},
                {"currentValue",    DecodeValueBytesToJson(c.value, dt)},
            };
            if (lookup.field.present) {
                row["field"]     = lookup.field.name;
                row["fieldType"] = lookup.field.typeName;
                row["fieldKind"] = lookup.field.kind;
            } else if (lookup.nearestField.present) {
                row["nearestField"] = json{
                    {"name",      lookup.nearestField.name},
                    {"typeName",  lookup.nearestField.typeName},
                    {"delta",     static_cast<std::int64_t>(lookup.nearestFieldDelta)},
                };
                row["fieldKind"] = "near";
            } else {
                row["fieldKind"] = "interior";
            }
            rows.push_back(std::move(row));
        }

        {
            char line[320];
            std::snprintf(line, sizeof(line),
                "scanFieldValue(Mono) dt=%s raw=%zu rows=%zu dropped non=%zu noise=%zu div=%zu bytes=%zu "
                "revIndex=%d idxInst=%zu idxHits=%zu idxLocal=%zu",
                dtStr.c_str(), candidates.size(), rows.size(),
                droppedNonManaged, droppedNoise, droppedDiversity, stats.bytesScanned,
                revIndexOk ? 1 : 0, revIndex.InstanceCount(), revIndex.HitCount(), revIndex.LocalityHits());
            Discrete::Log::Write(MagicStrings::LogCategoryScan, line);
        }

        return json{
            {"ok",                true},
            {"mode",              "global"},
            {"dataType",          dtStr},
            {"scanType",          stStr},
            {"count",             rows.size()},
            {"droppedNonManaged", droppedNonManaged},
            {"droppedNoise",      droppedNoise},
            {"droppedDiversity",  droppedDiversity},
            {"truncated",         candidates.size() >= kRawScanCap || rows.size() >= emitLimit},
            {"scannedBytes",      stats.bytesScanned},
            {"readFailureBytes",  stats.readFailureBytes},
            {"candidates",        std::move(rows)},
            {"scan_stats", json{
                {"regions_total",      stats.regionsTotal},
                {"regions_scanned",    stats.regionsScanned},
                {"regions_skip_image", stats.regionsSkipImage},
                {"regions_skip_stack", stats.regionsSkipStack},
                {"thread_stacks",      stats.threadStacks},
            }},
        };
    });

    // Phase 27b — beginValueScan (Mono). Identical inputs to scanFieldValue's
    // global mode (no class-scoped support in v1 — class-scoped is already
    // fast enough as a one-shot; session value is in the refine workflow).
    // Difference: stores the candidate set in ValueScan::SessionManager so
    // the client can later `refineValueScan(sessionId, scanType)`.
    PipeServer::RegisterHandler(MagicStrings::CmdBeginValueScan,
        [formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        std::string dtStr = args.value("dataType", std::string{"int32"});
        Memory::ValueDataType dt{};
        if (!ParseValueDataType(dtStr, dt)) {
            return json{{"error", "unknown 'dataType'"}};
        }
        std::string stStr = args.value("scanType", std::string{"exact"});
        Memory::ValueScanType st{};
        if (!ParseValueScanType(stStr, st)) {
            return json{{"error", "scanType must be exact|range for beginValueScan (refine modes go through refineValueScan)"}};
        }
        if (!args.contains("value")) return json{{"error", "missing 'value'"}};

        Memory::ValueScanRequest req{};
        req.dataType  = dt;
        req.scanType  = st;
        req.tolerance = args.value("tolerance", 0.0001);
        req.alignment = static_cast<std::size_t>(args.value("alignment", 0));
        req.limit     = static_cast<std::size_t>(args.value("limit", 50000));
        if (req.limit == 0 || req.limit > 50000) req.limit = 50000;
        if (!PackJsonValueToBytes(args["value"], dt, req.lo)) {
            return json{{"error", "'value' out of range / not numeric for dataType"}};
        }
        if (st == Memory::ValueScanType::Range) {
            if (!args.contains("valueTo")) return json{{"error", "scanType=range requires 'valueTo'"}};
            if (!PackJsonValueToBytes(args["valueTo"], dt, req.hi)) {
                return json{{"error", "'valueTo' out of range / not numeric for dataType"}};
            }
        }
        std::size_t maxBackwardScan = static_cast<std::size_t>(args.value("maxBackwardScan", 16384));
        if (maxBackwardScan > 16384) maxBackwardScan = 16384;
        if (maxBackwardScan == 0)    maxBackwardScan = 4096;

        if (!Mono::BuildClassRangeCache()) {
            return json{{"error", "address-finder class cache build failed"}};
        }

        // Phase 27 hardening — same decoupled-budget + diversity-cap +
        // noise-filter pattern as scanFieldValue.
        const std::size_t emitLimit = req.limit;
        constexpr std::size_t kRawScanCap = 50000;
        Memory::ValueScanRequest scanReq = req;
        scanReq.limit = kRawScanCap;

        Memory::ValueScanStats stats{};
        auto rawCandidates = Memory::ScanForValue(scanReq, &stats);

        // Phase 27 follow-up — outer ThreadAttachGuard for the WhatIsAt
        // batch. See scanFieldValue handler above for the rationale.
        Mono::ThreadAttachGuard scanGuard(Mono::GetRootDomain());

        // Phase 30 P2 (Mono port) — reverse-lookup index, same as
        // scanFieldValue. Build once, Lookup per candidate, WhatIsAt fallback.
        // Gated on candidate count (begin uses rawCandidates).
        // reuseIndexMs > 0 lets the aggregate "any numeric width" fan-out share
        // one index build across its N back-to-back scans (the ~48s heap walk
        // is identical per width). Single scans omit it → always a fresh walk.
        std::uint32_t reuseIndexMs =
            static_cast<std::uint32_t>(args.value("reuseIndexMs", 0));
        Mono::ReverseLookupIndex revIndex;
        bool revIndexOk = rawCandidates.size() >= kRevIndexMinCandidates && revIndex.Build(reuseIndexMs);

        std::vector<ValueScan::Candidate> sessionCands;
        sessionCands.reserve(std::min<std::size_t>(emitLimit, 4096));
        std::size_t droppedNonManaged = 0, droppedDiversity = 0, droppedNoise = 0;
        std::unordered_map<std::uintptr_t, int> perInstanceCount;
        const std::size_t vSize = Memory::ValueDataTypeSize(dt);
        for (const auto& c : rawCandidates) {
            if (sessionCands.size() >= emitLimit) break;
            std::uintptr_t addr = reinterpret_cast<std::uintptr_t>(c.addr);
            Mono::AddressLookup lookup = revIndexOk
                ? revIndex.Lookup(addr, maxBackwardScan)
                : Mono::WhatIsAt(addr, maxBackwardScan);
            if (!lookup.found) { droppedNonManaged++; continue; }
            if (IsCompilerGeneratedClassName(lookup.className)) { droppedNoise++; continue; }
            if (++perInstanceCount[lookup.instance] > kPerInstanceCap) { droppedDiversity++; continue; }

            ValueScan::Candidate sc{};
            sc.addr        = addr;
            sc.instAddr    = lookup.instance;
            sc.fieldOffset = static_cast<std::int64_t>(lookup.fieldOffset);
            sc.asmName     = lookup.assembly;
            sc.ns          = lookup.ns;
            sc.className   = lookup.className;
            if (lookup.field.present) {
                sc.field     = lookup.field.name;
                sc.fieldType = lookup.field.typeName;
                sc.fieldKind = lookup.field.kind;
            } else if (lookup.nearestField.present) {
                sc.fieldKind         = "near";
                sc.nearestFieldName  = lookup.nearestField.name;
                sc.nearestFieldDelta = static_cast<std::int64_t>(lookup.nearestFieldDelta);
            } else {
                sc.fieldKind = "interior";
            }
            std::memcpy(sc.prevValue, c.value, vSize);
            sessionCands.push_back(std::move(sc));
        }

        std::uint64_t sessionId = ValueScan::SessionManager::Instance().Begin(dt, std::move(sessionCands));

        // Re-render the same rows for immediate display — caller doesn't have
        // to make a second RPC to see the initial set. ViewWith locks the
        // session briefly (a few ms for 50K rows).
        json rows = json::array();
        std::size_t kept = 0;
        auto fmtHex = std::function<std::string(std::uintptr_t)>(formatHex);
        ValueScan::SessionManager::Instance().ViewWith(sessionId,
            [&](Memory::ValueDataType d, const std::vector<ValueScan::Candidate>& cands) {
            kept = cands.size();
            for (const auto& sc : cands) rows.push_back(EmitCandidateRow(sc, d, fmtHex));
        });

        {
            char line[320];
            std::snprintf(line, sizeof(line),
                "beginValueScan(Mono) sess=%llu dt=%s raw=%zu kept=%zu non=%zu noise=%zu div=%zu bytes=%zu "
                "revIndex=%d idxInst=%zu idxHits=%zu idxLocal=%zu",
                static_cast<unsigned long long>(sessionId), dtStr.c_str(),
                rawCandidates.size(), kept,
                droppedNonManaged, droppedNoise, droppedDiversity, stats.bytesScanned,
                revIndexOk ? 1 : 0, revIndex.InstanceCount(), revIndex.HitCount(), revIndex.LocalityHits());
            Discrete::Log::Write(MagicStrings::LogCategoryScan, line);
        }

        return json{
            {"ok",                true},
            {"sessionId",         std::to_string(sessionId)},
            {"mode",              "global"},
            {"dataType",          dtStr},
            {"scanType",          stStr},
            {"count",             kept},
            {"totalCandidates",   kept},
            {"droppedNonManaged", droppedNonManaged},
            {"droppedNoise",      droppedNoise},
            {"droppedDiversity",  droppedDiversity},
            {"truncated",         rawCandidates.size() >= kRawScanCap || kept >= emitLimit},
            {"scannedBytes",      stats.bytesScanned},
            {"readFailureBytes",  stats.readFailureBytes},
            {"candidates",        std::move(rows)},
        };
    });

    // Phase 6 segment B — walkInstance (Mono).
    // Given an instance address + class spec, walk the inheritance chain and
    // decode every instance field's value. Each MonoObject's first 16 bytes
    // are the header (vtable + sync); field offsets returned by
    // mono_field_get_offset are absolute from the object start, so
    // address + offset is ready to read.
    PipeServer::RegisterHandler(MagicStrings::CmdWalkInstance,
        [parseAddr, formatHex, decodeField, resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        // Optional cap on emitted Dictionary<K,V> key/value pairs (default 256).
        std::size_t dictPairCap = kDictDefaultPairCap;
        if (args.contains("dictLimit") && args["dictLimit"].is_number()) {
            const long long dl = args["dictLimit"].get<long long>();
            if (dl > 0) dictPairCap = static_cast<std::size_t>(dl);
        }
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        // Phase 31-6 — reject a garbage class early. The header path
        // (klass_from_header_addr → ClassOfObject) can hand back a bogus class
        // when a deep Copy-CE-XML drilldown dereferences a stale / mis-typed
        // pointer (now reachable because Phase 31 walks value-struct members).
        // ProbeKlassValid is SEH-guarded (mono_class_get_name must yield a
        // sane name); a garbage class fails it → clean error instead of an AV
        // in the unguarded field-offset/name reads below. By-name walks (real
        // FindClass result, incl. value structs) pass trivially.
        if (!Mono::ProbeKlassValid(k))
            return json{{"error", "address does not resolve to a valid managed class"}};

        // Phase 31-6 diag — per-step flush trace (mirrors IL2CPP). Each Write
        // flushes, so the LAST line in the walk log pinpoints the class/field
        // being processed when a CTD hits. Localizes the deep Copy-CE-XML
        // drilldown crash that leaves no WER dump (Mono BDWGC hang/AV).
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "walkInstance(Mono): addr=" + formatHex(addr) +
            " klass=" + formatHex(reinterpret_cast<std::uintptr_t>(k)));

        const auto& r = Mono::GetResolver();
        Mono::MonoDomain* domain = Mono::GetRootDomain();
        if (domain == nullptr) return json{{"error", "no mono root domain"}};

        // Phase 31-6 (a) — value-type struct walk (the Inspector "expand"
        // drill on an inline Vector3 / Quaternion / custom struct). The field
        // storage IS the value, so there is NO object header at `addr`. But
        // mono_field_get_offset reports a value type's instance-field offsets
        // header-INCLUSIVE (boxed convention: Vector3.x = 0x10, not 0x00).
        // Reading at addr+0x10 lands one header past the struct → the wrong
        // (shifted) members — confirmed by live-smoke (Core Keeper: srOffSetPos
        // shown as defaultSROffSetPos's bytes). Subtract the header for value
        // types so member addresses + reported offsets are struct-relative.
        // Reference-type walks: `addr` is the object base (incl header) and
        // offsets are header-inclusive, so addr+offset is already correct → 0.
        const long structHeaderAdjust =
            (r.class_is_valuetype != nullptr && r.class_is_valuetype(k) != 0)
                ? static_cast<long>(ManagedLayout::kObjectHeader) : 0;

        // Walk parent chain (cap at 16 levels — defends against pathological loops).
        std::vector<Mono::MonoClass*> chain;
        {
            Mono::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
            for (Mono::MonoClass* cur = k; cur != nullptr && chain.size() < 16; ) {
                chain.push_back(cur);
                if (r.class_get_parent == nullptr) break;
                cur = SehGetParent(r.class_get_parent, cur);
            }
        }

        // Phase 31-6 — runaway-chain bail (mirror the IL2CPP handler). A real
        // managed class's inheritance chain terminates at System.Object within
        // a handful of levels. Hitting the 16-cap means class_get_parent kept
        // returning non-null GARBAGE — the address wasn't a real class (a stale
        // pointer deref'd during a deep Copy-CE-XML drilldown, or a value-type
        // field misread as a reference). Do NOT ReadClassInfo those garbage
        // class pointers: mono_class_get_fields then hands back garbage
        // MonoClassField*s whose (non-SEH) field_get_offset/name reads AV the
        // game. Bail with a clean error instead. (Previously Mono lacked this
        // bail — see plan.md backlog; deeper struct-member recursion enabled by
        // Phase 31 made it reachable.)
        if (chain.size() >= 16) {
            return json{{"error",
                "address does not resolve to a managed object "
                "(runaway class chain — likely a value-type field or stale pointer)"}};
        }

        // Phase 31-6 — process-lifetime ClassInfo cache for the walk path.
        // Inspector / Copy-CE-XML drilldown re-walks the same classes over and
        // over (a depth-5 export touches recurring types — many Vector3s,
        // shared component types). Without a cache, each node re-ran
        // ReadClassInfo = mono_class_get_fields + get_methods iteration (+ a
        // possible one-shot mono_class_vtable force-setup) from the pipe worker
        // thread — wasteful and steady BDWGC pressure that can wedge/crash the
        // game (Mono). Field/method layout is stable for the session, so
        // memoize by MonoClass*; the dangerous vtable force-setup now happens at
        // most once per class ever. Mutex: the interactive (browse) and bulk
        // (snapshot) pipes can call concurrently. Mirrors the snapshot path's
        // globalClassInfo cache.
        static std::unordered_map<Mono::MonoClass*, Mono::ClassInfoResult> s_walkClassCache;
        static std::mutex s_walkClassCacheMtx;

        // Walk base-class first so derived fields appear last (typical layout).
        json fields = json::array();
        std::string selfNs, selfName;
        for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
            Mono::ClassInfoResult res;
            bool cacheHit = false;
            {
                std::lock_guard<std::mutex> lk(s_walkClassCacheMtx);
                auto cit = s_walkClassCache.find(*it);
                if (cit != s_walkClassCache.end()) { res = cit->second; cacheHit = true; }
            }
            Discrete::Log::Write(MagicStrings::LogCategoryWalk,
                "walkInstance: class=" + formatHex(reinterpret_cast<std::uintptr_t>(*it)) +
                (cacheHit ? " (cache)" : " ReadClassInfo"));
            if (!res.ok) {
                res = Mono::ReadClassInfo(*it);
                if (res.ok) {
                    std::lock_guard<std::mutex> lk(s_walkClassCacheMtx);
                    s_walkClassCache.emplace(*it, res);
                }
            }
            if (!res.ok) continue;
            const bool isSelf = (*it == k);
            if (isSelf) { selfNs = res.info.self.ns; selfName = res.info.self.name; }
            for (const auto& f : res.info.fields) {
                if (f.flags & 0x10u) continue;  // skip static
                // Struct-relative offset for value-type walks (Phase 31-6 a).
                const long relOffset = static_cast<long>(f.offset) - structHeaderAdjust;
                std::uintptr_t fieldAddr = addr + static_cast<std::uintptr_t>(relOffset);
                json entry = {
                    {"name",    f.name},
                    {"type",    f.typeName},
                    {"offset",  relOffset},
                    {"from",    res.info.self.name},
                    {"address", formatHex(fieldAddr)},
                };
                if (f.isEnum)      entry["is_enum"]       = true;
                if (f.isValueType) entry["is_value_type"] = true;
                if (!f.underlyingTypeName.empty())
                    entry["underlying_type"] = f.underlyingTypeName;
                // For enums, decode using the underlying integer type so the
                // value comes back as an int instead of the catch-all 8-byte
                // ptr. Falls back to the original typeName when the underlying
                // wasn't detected (older runtimes without class_enum_basetype).
                const std::string& decodeType = (f.isEnum && !f.underlyingTypeName.empty())
                    ? f.underlyingTypeName : f.typeName;
                json decoded = decodeField(decodeType, fieldAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                    entry[kv.key()] = kv.value();
                }
                // Phase 31-2 (Phase B) — inline generic value-type struct: fan
                // out its members (custom int/double structs etc). Always-on in
                // the interactive walk (single, on-demand; named math structs
                // were already handled inside decodeField).
                if (ShouldExpandValueStruct(f.isValueType, f.isEnum,
                                            f.valueTypeClass, f.typeName, entry)) {
                    json sub = CaptureMonoStructFields(decodeField, f.valueTypeClass,
                        fieldAddr, kStructMaxDepth, kStructDefaultMemberCap);
                    if (sub.contains("fields")) {
                        entry.erase("ptr");
                        entry["fields"] = std::move(sub["fields"]);
                    } else if (sub.contains("struct_error")) {
                        entry["struct_error"] = sub["struct_error"];
                    }
                }
                // Dictionary<K,V> — emit key→value pairs as child fields.
                else if (IsDictionaryType(f.typeName) && entry.contains("ptr")) {
                    std::uintptr_t dptr = 0;
                    if (Memory::SafeReadBytes(reinterpret_cast<void*>(fieldAddr), &dptr,
                                              sizeof(dptr)) && dptr != 0) {
                        json d = CaptureMonoDictionary(decodeField, dptr,
                                                       kStructMaxDepth, dictPairCap);
                        if (d.contains("fields")) {
                            entry.erase("ptr");
                            entry["dict"]                = true;
                            entry["dict_count"]          = d.value("dict_count", 0);
                            entry["dict_entries_offset"] = d.value("dict_entries_offset", 0);
                            entry["dict_data_offset"]    = d.value("dict_data_offset", 0);
                            entry["dict_stride"]         = d.value("dict_stride", 0);
                            if (d.value("dict_truncated", false)) entry["dict_truncated"] = true;
                            entry["fields"] = std::move(d["fields"]);
                        } else if (d.contains("dict_error")) {
                            entry["dict_error"] = d["dict_error"];
                        }
                    }
                }
                fields.push_back(std::move(entry));
                if (fields.size() >= 4096) break;
            }
            if (fields.size() >= 4096) break;
        }
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "walkInstance: done count=" + std::to_string(fields.size()));

        // Sort by offset so the inspector grid displays fields in memory
        // order regardless of metadata declaration order or parent-chain
        // traversal direction. Inherited fields naturally fall before the
        // class's own fields because base-class layout always precedes
        // derived-class layout in .NET.
        std::sort(fields.begin(), fields.end(),
                  [](const json& a, const json& b) {
                      return a.value("offset", 0) < b.value("offset", 0);
                  });

        return json{
            {"address",      formatHex(addr)},
            {"namespace",    selfNs},
            {"name",         selfName},
            {"depth",        chain.size()},
            {"count",        fields.size()},
            // Authoritative game-process pointer size (4 = x86/WOW64, 8 = x64).
            // The live CE-XML exporter uses this for managed-String header
            // offsets + List<T>._items deref instead of relying on the UI's
            // handshake-cached GamePointerSize (which can be stale / 0 at
            // export time → wrong x64 offsets on a 32-bit target).
            {"pointer_size", static_cast<int>(Discrete::ManagedLayout::kPointerSize)},
            {"fields",       std::move(fields)},
        };
    });

    // Phase 6 segment D — monoGetVTable.
    // Resolves the MonoVTable* for a class in the root domain. The same call
    // findInstances uses internally, exposed for callers that want to do their
    // own pointer scans or to feed registerSymbol.
    PipeServer::RegisterHandler(MagicStrings::CmdMonoGetVTable,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        const auto& r = Mono::GetResolver();
        if (r.class_vtable == nullptr) return json{{"error", "mono_class_vtable not exported"}};
        Mono::MonoDomain* domain = Mono::GetRootDomain();
        if (domain == nullptr) return json{{"error", "no mono root domain"}};

        Mono::MonoVTable* vt = nullptr;
        {
            Mono::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
            vt = r.class_vtable(domain, k);
        }
        if (vt == nullptr) return json{{"error", "mono_class_vtable returned null"}};
        return json{
            {"vtable", formatHex(reinterpret_cast<std::uintptr_t>(vt))},
        };
    });

    // Phase 5 segment 3 — getMetadataStats (Mono).
    // Walks every class across every loaded assembly. Each Mono helper does
    // its own thread attach, so we deliberately don't hold an outer guard
    // (nested attach/detach destroyed BongoCat in build #54).
    PipeServer::RegisterHandler("getMetadataStats",
        [updateStats, buildStatsJson](const json&) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        NameStats s;
        auto assemblies = Mono::EnumerateAssemblies();
        std::size_t scanned = 0;
        for (const auto& a : assemblies) {
            Mono::MonoImage* img = Mono::FindImageByName(a.name);
            if (img == nullptr) continue;
            auto classes = Mono::EnumerateClasses(img);
            for (const auto& c : classes) updateStats(s, c.ns, c.name);
            ++scanned;
        }
        json out = buildStatsJson(s);
        out["scope"]              = "all_assemblies";
        out["assemblies_scanned"] = scanned;
        return out;
    });

    // Phase 5 follow-up — getEnumValues (Mono).
    // Mono stores const literal defaults as raw little-endian bytes (no
    // type / length prefix and no compressed-int trickery — that's an
    // IL2CPP-specific encoding). The width is determined by the enum's
    // underlying type, which we read from the runtime `value__` field
    // (same trick getEnumValues uses on IL2CPP).
    PipeServer::RegisterHandler(MagicStrings::CmdGetEnumValues,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        const auto& r = Mono::GetResolver();
        if (r.field_get_data == nullptr) {
            return json{
                {"error", "mono_field_get_data not exported in this Mono build"},
                {"hint",  "rebuild against a Unity Mono with the standard public API"},
            };
        }
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto info = Mono::ReadClassInfo(k);
        if (!info.ok) return json{{"error", "ReadClassInfo failed"}};
        if (info.info.parent.name != "Enum") {
            return json{
                {"error", "class is not an enum"},
                {"parent", info.info.parent.name},
            };
        }

        // Underlying integer width comes from the value__ field's type name.
        std::string underlying = "System.Int32";
        for (const auto& f : info.info.fields) {
            if (f.name == "value__") { underlying = f.typeName; break; }
        }

        // Per-type raw-LE decoder.
        //
        // mono_field_get_data returns a pointer that includes the blob's
        // ECMA-335 §II.23.2 compressed length prefix (1, 2, or 4 bytes).
        // Empirical: DayOfWeek.Sunday came back as 4 instead of 0 because we
        // were reading the prefix byte (0x04 — the length, not the value)
        // as the first byte of the int32. Skipping the prefix puts us on
        // the actual raw LE value bytes.
        auto decodeRaw = [](const std::string& typeName, const std::uint8_t* raw,
                            json& out) -> bool {
            if (raw == nullptr) { out["error"] = "no default data"; return false; }
            std::size_t prefix = 0;
            std::uint8_t b0 = raw[0];
            if      ((b0 & 0x80u) == 0u)    prefix = 1;
            else if ((b0 & 0xC0u) == 0x80u) prefix = 2;
            else if ((b0 & 0xE0u) == 0xC0u) prefix = 4;
            else { out["error"] = "invalid blob length prefix"; return false; }
            out["blob_prefix_bytes"] = prefix;
            const std::uint8_t* p = raw + prefix;

            auto put = [&](auto v, std::size_t bytes) {
                out["value"] = v;
                out["enc_bytes"] = bytes;
            };
            if (typeName == "System.SByte")   { put(static_cast<std::int64_t>(static_cast<std::int8_t>(*p)),   1); return true; }
            if (typeName == "System.Byte")    { put(static_cast<std::uint64_t>(*p),                            1); return true; }
            if (typeName == "System.Boolean") { out["value"] = (*p != 0); out["enc_bytes"] = 1;                   return true; }
            if (typeName == "System.Int16")   { std::int16_t v = 0;  std::memcpy(&v, p, 2); put(static_cast<std::int64_t>(v),  2); return true; }
            if (typeName == "System.UInt16" ||
                typeName == "System.Char")    { std::uint16_t v = 0; std::memcpy(&v, p, 2); put(static_cast<std::uint64_t>(v), 2); return true; }
            if (typeName == "System.Int32")   { std::int32_t v = 0;  std::memcpy(&v, p, 4); put(static_cast<std::int64_t>(v),  4); return true; }
            if (typeName == "System.UInt32")  { std::uint32_t v = 0; std::memcpy(&v, p, 4); put(static_cast<std::uint64_t>(v), 4); return true; }
            if (typeName == "System.Int64")   { std::int64_t v = 0;  std::memcpy(&v, p, 8); put(v, 8);                             return true; }
            if (typeName == "System.UInt64")  { std::uint64_t v = 0; std::memcpy(&v, p, 8); put(v, 8);                             return true; }
            if (typeName == "System.Single")  { float  v = 0.f;      std::memcpy(&v, p, 4); put(v, 4);                             return true; }
            if (typeName == "System.Double")  { double v = 0.0;      std::memcpy(&v, p, 8); put(v, 8);                             return true; }
            return false;
        };

        static const char* kHex = "0123456789ABCDEF";
        json values = json::array();
        auto literals = Mono::EnumerateLiteralFields(k);
        for (const auto& lit : literals) {
            if (lit.name == "value__") continue;  // storage field, not a member
            json entry = {
                {"name", lit.name},
                {"type", lit.typeName},
            };
            // Diagnostic: show the first ~16 bytes of the blob (length prefix
            // + value) so the encoding is auditable from the pipe response.
            if (lit.data != nullptr) {
                std::string hex;
                auto* bp = reinterpret_cast<const std::uint8_t*>(lit.data);
                for (std::size_t i = 0; i < 16; ++i) {
                    hex.push_back(kHex[bp[i] >> 4]);
                    hex.push_back(kHex[bp[i] & 0xF]);
                }
                entry["raw_hex"] = hex;
            }
            if (!decodeRaw(underlying,
                           reinterpret_cast<const std::uint8_t*>(lit.data),
                           entry)) {
                entry.erase("error");
                entry["decode_error"] = "unsupported underlying type: " + underlying;
            }
            values.push_back(std::move(entry));
        }
        return json{
            {"namespace",       info.info.self.ns},
            {"name",            info.info.self.name},
            {"underlying_type", underlying},
            {"count",           values.size()},
            {"values",          std::move(values)},
        };
    });

    // Phase 17a — address finder (Mono); 17b adds field-offset resolution.
    // Backward 8-byte stride scan from the queried address; matches MonoVTable*
    // candidates against a cache built lazily on first call. The cache walks
    // every assembly's classes once and resolves a vtable per class via
    // mono_class_vtable, then maps vtable→class for the lookup. The 17b
    // pass walks the parent chain inside the same attach scope and resolves
    // the queried byte offset back to a declared field (or the nearest one
    // below it).
    PipeServer::RegisterHandler(MagicStrings::CmdWhatIsAt,
        [parseAddr, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::size_t maxBackward = 4096;
        if (args.contains("maxBackwardScan") && args["maxBackwardScan"].is_number_unsigned()) {
            maxBackward = args["maxBackwardScan"].get<std::size_t>();
        }
        auto hit = Mono::WhatIsAt(addr, maxBackward);
        if (!hit.found) {
            return json{
                {"found",   false},
                {"reason",  hit.reason},
                {"address", formatHex(hit.queriedAddr)},
                {"scannedRange", json{
                    {"from", formatHex(hit.scannedFrom)},
                    {"to",   formatHex(hit.scannedTo)},
                }},
                {"scannedSteps", hit.scannedSteps},
            };
        }
        // Phase 17b — render FieldHit (shared shape across backends).
        auto fieldToJson = [](const Mono::FieldHit& f) -> json {
            json out = {
                {"name",         f.name},
                {"type",         f.typeName},
                {"offset",       f.offset},
                {"size",         f.size},
                {"isStatic",     f.isStatic},
                {"isValueType",  f.isValueType},
                {"isEnum",       f.isEnum},
                {"declaredIn",   f.declaredIn},
                {"kind",         f.kind},
            };
            if (!f.underlyingTypeName.empty())
                out["underlying_type"] = f.underlyingTypeName;
            if (f.kind == "interior")
                out["interiorOffset"] = static_cast<std::int64_t>(f.interiorOffset);
            return out;
        };
        json result = {
            {"found",       true},
            {"address",     formatHex(hit.queriedAddr)},
            {"instance",    formatHex(hit.instance)},
            {"vtable_ptr",  formatHex(hit.vtablePtr)},
            {"class_ptr",   formatHex(hit.klassPtr)},
            {"assembly",    hit.assembly},
            {"namespace",   hit.ns},
            {"class",       hit.className},
            {"fieldOffset", static_cast<std::int64_t>(hit.fieldOffset)},
            {"scannedRange", json{
                {"from", formatHex(hit.scannedFrom)},
                {"to",   formatHex(hit.scannedTo)},
            }},
            {"scannedSteps", hit.scannedSteps},
        };
        result["field"]        = hit.field.present        ? fieldToJson(hit.field)        : json(nullptr);
        result["nearestField"] = hit.nearestField.present ? fieldToJson(hit.nearestField) : json(nullptr);
        if (hit.nearestField.present) {
            result["nearestFieldDelta"] = static_cast<std::int64_t>(hit.nearestFieldDelta);
        }
        return result;
    });

    // Phase 17a follow-up: diagnostics for the class-pointer cache. Lets the
    // user / smoke test confirm the cache is populated and inspect its bounds
    // before issuing a whatIsAt query.
    PipeServer::RegisterHandler(MagicStrings::CmdGetClassRangeStats,
        [formatHex](const json& args) -> json {
        if (args.value("rebuild", false)) Mono::InvalidateClassRangeCache();
        Mono::BuildClassRangeCache();
        auto s = Mono::GetClassRangeStats();
        return json{
            {"backend",      "Mono"},
            {"built",        s.built},
            {"class_count",  s.classCount},
            {"vtable_count", s.vtableCount},
            {"min_klass",    formatHex(s.minKlass)},
            {"max_klass",    formatHex(s.maxKlass)},
            {"min_vtable",   formatHex(s.minVtable)},
            {"max_vtable",   formatHex(s.maxVtable)},
        };
    });

    // Phase 18 — find method instances (Mono). Walks the supplied class's
    // declared methods, returns every overload of `method` with native
    // address (post-JIT) + decoded signature + symbol-safe humanSuffix.
    PipeServer::RegisterHandler(MagicStrings::CmdFindMethodInstances,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch = args.value("signatureMatch", std::string{});
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto fr = Mono::FindMethodInstances(k, methodName, sigMatch);
        if (!fr.ok) return json{{"error", fr.error}};

        json matches = json::array();
        for (const auto& ov : fr.matches) {
            json names = json::array();
            for (const auto& s : ov.paramNames) names.push_back(s);
            json types = json::array();
            for (const auto& s : ov.paramTypes) types.push_back(s);
            matches.push_back({
                {"name",          ov.name},
                {"token",         ov.token},
                {"flags",         ov.flags},
                {"iflags",        ov.iflags},
                {"isStatic",      ov.isStatic},
                {"isVirtual",     ov.isVirtual},
                {"isAbstract",    ov.isAbstract},
                {"compiled",      ov.compiled},
                {"address",       formatHex(reinterpret_cast<std::uintptr_t>(ov.address))},
                {"paramTypes",    std::move(types)},
                {"paramNames",    std::move(names)},
                {"returnType",    ov.returnType},
                {"declaringClass",     ov.declaringClass},
                {"declaringNamespace", ov.declaringNamespace},
                {"signature",     ov.signatureString},
                {"humanSuffix",   ov.humanSuffix},
            });
        }
        return json{
            {"method",  methodName},
            {"count",   matches.size()},
            {"matches", std::move(matches)},
        };
    });

    // Phase 19b — callMethod (Mono, static-only). Mirror of the IL2CPP
    // handler in the IL2CPP block below: same wire shape, same arg-marshal
    // semantics, mono_runtime_invoke instead of il2cpp_runtime_invoke. 19c
    // will lift the static-only / no-string restrictions; 19b stays tight
    // so the wire shape matches IL2CPP exactly.
    PipeServer::RegisterHandler(MagicStrings::CmdCallMethod,
        [resolveClassFromArgs, parseAddr, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch   = args.value("signatureMatch", std::string{});

        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        // 19b: instance must be absent / null. Parse for forward-compat; the
        // invoker rejects non-zero with a distinct error code.
        std::uintptr_t instance = 0;
        if (args.contains("instance") && !args["instance"].is_null()) {
            if (!parseAddr(args["instance"], instance)) {
                return json{{"error", "invalid 'instance' (hex string or integer)"}};
            }
        }

        // Decode each {type, value} into a typed CallArg. Identical wire
        // contract to the IL2CPP handler — same `type` tags, same JSON
        // encodings. Spec: docs/lua-invoke.md.
        using Mono::CallArg;
        using Mono::CallArgKind;
        std::vector<CallArg> typedArgs;

        const json& argsArr = args.contains("args") && args["args"].is_array()
                                ? args["args"] : json::array();
        typedArgs.reserve(argsArr.size());
        for (std::size_t i = 0; i < argsArr.size(); ++i) {
            const json& a = argsArr[i];
            if (!a.is_object()) {
                return json{{"error", std::string{"args["} + std::to_string(i) + "] not an object"}};
            }
            std::string ty = a.value("type", std::string{});
            CallArg out;

            if (ty == "null") {
                out.kind = CallArgKind::Null;
            } else if (ty == "bool" || ty == "boolean") {
                out.kind = CallArgKind::Bool;
                if (a.contains("value")) {
                    if      (a["value"].is_boolean())          out.i64 = a["value"].get<bool>() ? 1 : 0;
                    else if (a["value"].is_number_integer())    out.i64 = a["value"].get<std::int64_t>();
                    else if (a["value"].is_number_unsigned())   out.u64 = a["value"].get<std::uint64_t>();
                }
            } else if (ty == "byte"   || ty == "uint8")  { out.kind = CallArgKind::Byte;   out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "sbyte"  || ty == "int8")   { out.kind = CallArgKind::SByte;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "int16"  || ty == "short")  { out.kind = CallArgKind::Int16;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint16" || ty == "ushort") { out.kind = CallArgKind::UInt16; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int32"  || ty == "int")    { out.kind = CallArgKind::Int32;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint32" || ty == "uint")   { out.kind = CallArgKind::UInt32; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int64"  || ty == "long")   { out.kind = CallArgKind::Int64;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint64" || ty == "ulong")  { out.kind = CallArgKind::UInt64; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "float"  || ty == "single") { out.kind = CallArgKind::Float;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "double") { out.kind = CallArgKind::Double;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "char")   { out.kind = CallArgKind::Char;
                  if (a.contains("value")) {
                      if      (a["value"].is_string() && !a["value"].get<std::string>().empty())
                                                          out.u64 = static_cast<std::uint8_t>(a["value"].get<std::string>()[0]);
                      else if (a["value"].is_number())     out.u64 = a["value"].get<std::uint64_t>();
                  }
              }
              else if (ty == "string") {
                  out.kind = CallArgKind::String;          // rejected by invoker in 19b
                  out.s = a.value("value", std::string{});
              }
              else if (ty == "address" || ty == "intptr" || ty == "uintptr" || ty == "ptr") {
                  out.kind = CallArgKind::Address;
                  if (a.contains("value")) {
                      std::uintptr_t addr = 0;
                      if (parseAddr(a["value"], addr)) {
                          out.u64 = addr;
                      } else {
                          return json{{"error", std::string{"args["} + std::to_string(i) + "].value not a valid address"}};
                      }
                  }
              }
              else {
                  return json{{"error", std::string{"args["} + std::to_string(i) + "] unknown type '" + ty + "'"}};
              }
            typedArgs.push_back(std::move(out));
        }

        // Phase 20-MT: optional main-thread marshaling. When `main_thread`
        // is true (defaults false to keep Phase 19 callers fast), the
        // CallMethod invocation runs inside MainThreadDispatcher::SubmitAndWait
        // so the runtime_invoke fires on the Unity main thread instead of
        // the worker thread. Required for any method that touches Unity
        // scene-graph / lifecycle (Transform.parent, GameObject.name, etc.).
        const bool mainThread = args.value("main_thread", false);
        const std::int32_t mtTimeoutMs = args.value("main_thread_timeout_ms",
                                                    std::int32_t{5000});

        Mono::CallMethodResult cr;
        std::string dispatchPath = "worker_direct";
        if (mainThread) {
            using namespace Discrete::MainThreadDispatcher;
            if (!IsInitialized()) {
                // Lazy install: handshake usually arms this, but if a Lua
                // bridge or test hits callMethod first we kick it here.
                (void)Init();
                if (Mono::IsResolved() && !Mono::MainThreadHook::IsInstalled()) {
                    (void)Mono::MainThreadHook::Install();
                }
            }
            DispatchResult disp = SubmitAndWait(
                [&]() { cr = Mono::CallMethod(k, methodName, sigMatch, instance, typedArgs); },
                mtTimeoutMs);
            dispatchPath = disp.diagnostic_path;
            if (!disp.ok) {
                json result;
                result["ok"]    = false;
                result["error"] = disp.error;
                result["dispatch"] = json{{"main_thread", true}, {"path", disp.diagnostic_path},
                                          {"timed_out",   disp.timed_out},
                                          {"rejected",    disp.rejected}};
                return result;
            }
        } else {
            cr = Mono::CallMethod(k, methodName, sigMatch, instance, typedArgs);
        }

        json returnValue = nullptr;
        switch (cr.ret.kind) {
        case Mono::CallReturn::Kind::Void:        returnValue = nullptr; break;
        case Mono::CallReturn::Kind::SignedInt:   returnValue = cr.ret.i64; break;
        case Mono::CallReturn::Kind::UnsignedInt: returnValue = cr.ret.u64; break;
        case Mono::CallReturn::Kind::Float:       returnValue = cr.ret.f; break;
        case Mono::CallReturn::Kind::Address:     returnValue = formatHex(static_cast<std::uintptr_t>(cr.ret.u64)); break;
        case Mono::CallReturn::Kind::String:      returnValue = cr.ret.s; break;
        case Mono::CallReturn::Kind::Unsupported: returnValue = nullptr; break;
        }

        json result;
        if (!cr.error.empty()) {
            result["ok"]    = false;
            result["error"] = cr.error;
            return result;
        }
        result["ok"]          = cr.ok;
        result["returnType"]  = cr.ret.typeName;
        result["returnValue"] = std::move(returnValue);
        result["exception"]   = cr.exception.empty() ? json(nullptr) : json(cr.exception);
        result["elapsedMs"]   = cr.elapsedMs;
        result["method"] = json{
            {"address",     formatHex(reinterpret_cast<std::uintptr_t>(cr.addressCalled))},
            {"signature",   cr.methodSignature},
            {"humanSuffix", cr.humanSuffix},
        };
        result["dispatch"] = json{{"main_thread", mainThread}, {"path", std::move(dispatchPath)}};
        if (cr.ret.kind == Mono::CallReturn::Kind::Unsupported) {
            result["note"] = "value-type-by-value returns are not decoded in 19b; pass an out-buffer in v2 or read the address";
        }
        return result;
    });
#elif defined(DISCRETE_BACKEND_IL2CPP)
    // Phase 4: IL2CPP API path. Same command names as the Mono variant.
    // (formatHex is the shared lambda declared in the backend-agnostic block above.)

    // Phase 29h — shared helper for instance heap scans (findInstances and
    // listFieldValuesAcrossInstances). Computes the Il2CppClass* metadata-arena
    // address span so class self-references (element_class/castClass) and
    // FieldInfo[].parent slots — which legitimately hold the class pointer but
    // aren't heap objects — can be excluded from the scan. Without it the scan
    // fills its `limit` quota with low-address arena residue and never reaches
    // the GC heap regions where genuine objects live. The bound is widened to
    // the committed regions holding minKlass/maxKlass because the metadata
    // allocator places FieldInfo/method arrays just past the class structs.
    // Capture-less so handlers can copy it by value safely. Yields [0,0) (no
    // exclusion) when disabled or the cache is unavailable.
    auto computeArenaExclude = [](bool excludeArena,
                                  std::uintptr_t& exLow, std::uintptr_t& exHigh) {
        exLow = 0; exHigh = 0;
        if (!excludeArena || !IL2CPP::BuildClassRangeCache()) return;
        auto crs = IL2CPP::GetClassRangeStats();
        if (!(crs.built && crs.classCount > 0 && crs.maxKlass >= crs.minKlass)) return;
        exLow  = crs.minKlass;
        exHigh = crs.maxKlass;
        MEMORY_BASIC_INFORMATION mbi{};
        if (::VirtualQuery(reinterpret_cast<const void*>(crs.maxKlass), &mbi, sizeof(mbi)) == sizeof(mbi)) {
            auto end = reinterpret_cast<std::uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
            if (end > exHigh) exHigh = end;
        }
        if (::VirtualQuery(reinterpret_cast<const void*>(crs.minKlass), &mbi, sizeof(mbi)) == sizeof(mbi)) {
            auto base = reinterpret_cast<std::uintptr_t>(mbi.BaseAddress);
            if (base < exLow) exLow = base;
        }
    };

    PipeServer::RegisterHandler(MagicStrings::CmdGetAssemblies, [](const json&) -> json {
        if (!IL2CPP::IsResolved()) {
            return json{
                {"error", "il2cpp runtime not resolved"},
                {"assemblies", json::array()},
                {"count", 0},
            };
        }
        auto vec = IL2CPP::EnumerateAssemblies();
        json arr = json::array();
        for (const auto& a : vec) {
            arr.push_back({{"name", a.name}, {"path", a.path}});
        }
        return json{
            {"assemblies", std::move(arr)},
            {"count", vec.size()},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetClasses, [](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string assemblyName = args.value("assembly", std::string{});
        if (assemblyName.empty()) return json{{"error", "missing 'assembly' arg"}};
        const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
        if (img == nullptr) return json{{"error", "assembly not found: " + assemblyName}};

        auto classes = IL2CPP::EnumerateClasses(img);
        json arr = json::array();
        for (const auto& c : classes) {
            arr.push_back({{"namespace", c.ns}, {"name", c.name}});
        }
        return json{
            {"assembly", assemblyName},
            {"count", classes.size()},
            {"classes", std::move(arr)},
        };
    });

    auto resolveClassFromArgs = [parseAddr](const json& args, std::string& errOut) -> IL2CPP::Il2CppClass* {
        // Header-based path: caller hands us the address of an Il2CppObject
        // whose first qword is the runtime Il2CppClass*. Used by the inspector
        // to expand pointer-to-class fields whose declared type is a generic
        // INSTANTIATION (e.g. ClassAry<CARRYITEM>) — those aren't registered
        // by name in the image's class table, so il2cpp_class_from_name can't
        // find them. The runtime always stamps the concrete instantiation's
        // class into the object header, so reading it bypasses the name
        // lookup entirely.
        if (args.contains("klass_from_header_addr")) {
            std::uintptr_t hdrAddr = 0;
            if (!parseAddr(args["klass_from_header_addr"], hdrAddr)) {
                errOut = "invalid 'klass_from_header_addr'"; return nullptr;
            }
            std::uintptr_t klassPtr = 0;
            if (!Memory::SafeReadBytes(reinterpret_cast<void*>(hdrAddr),
                                       &klassPtr, sizeof(klassPtr))) {
                errOut = "object header unreadable"; return nullptr;
            }
            if (klassPtr == 0) { errOut = "object header klass is null"; return nullptr; }
            auto* k = reinterpret_cast<IL2CPP::Il2CppClass*>(klassPtr);
            if (!IL2CPP::ProbeKlassValid(k)) {
                errOut = "klass pointer in header looks invalid";
                return nullptr;
            }
            return k;
        }

        // Direct class-pointer path: caller already knows the Il2CppClass*
        // (e.g. cached from an earlier walk). Same validation as above.
        if (args.contains("class_ptr")) {
            std::uintptr_t klassPtr = 0;
            if (!parseAddr(args["class_ptr"], klassPtr) || klassPtr == 0) {
                errOut = "invalid 'class_ptr'"; return nullptr;
            }
            auto* k = reinterpret_cast<IL2CPP::Il2CppClass*>(klassPtr);
            if (!IL2CPP::ProbeKlassValid(k)) {
                errOut = "class_ptr does not look valid";
                return nullptr;
            }
            return k;
        }

        std::string assemblyName = args.value("assembly", std::string{});
        std::string ns           = args.value("namespace", std::string{});
        std::string name         = args.value("name", std::string{});
        if (assemblyName.empty() || name.empty()) {
            errOut = "missing 'assembly' or 'name' arg"; return nullptr;
        }
        const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
        if (img == nullptr) { errOut = "assembly not found: " + assemblyName; return nullptr; }
        IL2CPP::Il2CppClass* k = IL2CPP::FindClass(img, ns, name);
        if (k == nullptr) { errOut = "class not found: " + ns + "." + name; return nullptr; }
        return k;
    };

    PipeServer::RegisterHandler(MagicStrings::CmdGetClassInfo,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = IL2CPP::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};

        json fields = json::array();
        for (const auto& f : res.info.fields) {
            fields.push_back({
                {"name", f.name}, {"type", f.typeName},
                {"offset", f.offset}, {"flags", f.flags},
                {"static", (f.flags & 0x10u) != 0},
            });
        }
        json methods = json::array();
        for (const auto& m : res.info.methods) {
            methods.push_back({
                {"name", m.name}, {"token", m.token}, {"flags", m.flags},
                {"static", (m.flags & 0x10u) != 0},
                {"address", formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
            });
        }
        json parent = nullptr;
        if (!res.info.parent.name.empty()) {
            parent = json{{"namespace", res.info.parent.ns}, {"name", res.info.parent.name}};
        }
        return json{
            {"namespace", res.info.self.ns}, {"name", res.info.self.name},
            {"parent", parent},
            {"fields", std::move(fields)}, {"methods", std::move(methods)},
        };
    });

    // Phase 25 — cross-class symbol finder (IL2CPP). Mirror of the Mono
    // handler above; same wire shape + same single-or-multi assembly arg
    // semantics. IL2CPP method entries additionally carry the AOT pointer
    // (Mono needs mono_compile_method to JIT — too expensive for a bulk
    // scan, so we omit address there).
    PipeServer::RegisterHandler(MagicStrings::CmdSearchSymbols,
        [formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string pattern = args.value("pattern", std::string{});
        // `staticOnly` lets callers list every static member without supplying
        // a name pattern (Symbol Finder "Static only" toggle). Pattern is
        // still required for the legacy name-substring flow.
        bool staticOnly = args.value("staticOnly", false);
        if (pattern.empty() && !staticOnly) {
            return json{{"error", "missing 'pattern' arg (or pass staticOnly=true)"}};
        }

        std::vector<std::string> assemblyNames;
        if (args.contains("assemblies") && args["assemblies"].is_array()) {
            for (const auto& a : args["assemblies"]) {
                if (!a.is_string()) continue;
                const auto& s = a.get_ref<const std::string&>();
                if (!s.empty()) assemblyNames.push_back(s);
            }
            if (assemblyNames.empty()) {
                return json{{"error", "'assemblies' array is empty"}};
            }
        } else {
            std::string single = args.value("assembly", std::string{});
            if (single.empty()) {
                return json{{"error", "missing 'assembly' or 'assemblies' arg"}};
            }
            assemblyNames.push_back(std::move(single));
        }

        bool wantFields  = true;
        bool wantMethods = true;
        if (args.contains("kinds") && args["kinds"].is_array()) {
            wantFields = false; wantMethods = false;
            for (const auto& k : args["kinds"]) {
                if (!k.is_string()) continue;
                const auto& s = k.get_ref<const std::string&>();
                if (s == "field")  wantFields  = true;
                if (s == "method") wantMethods = true;
            }
            if (!wantFields && !wantMethods) {
                return json{{"error", "kinds must include 'field' or 'method'"}};
            }
        }

        int limit = args.value("limit", 200);
        if (limit <= 0)   limit = 200;
        if (limit > 5000) limit = 5000;

        // Build #547 follow-up — per-assembly limit in multi-asm mode.
        // Mirror of Mono path; see that handler for the bug-fix rationale.
        const bool multiAsm = assemblyNames.size() > 1;
        const std::size_t perAsmLimit = static_cast<std::size_t>(limit);
        const std::size_t globalCap   = multiAsm
            ? std::min<std::size_t>(25000, perAsmLimit * 5 * assemblyNames.size())
            : perAsmLimit;

        json matches   = json::array();
        bool truncated = false;
        std::size_t scanned = 0;
        std::vector<std::string> missingAssemblies;
        std::vector<std::string> truncatedAssemblies;

        for (const auto& assemblyName : assemblyNames) {
            if (matches.size() >= globalCap) { truncated = true; break; }
            const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
            if (img == nullptr) {
                if (assemblyNames.size() == 1) {
                    return json{{"error", "assembly not found: " + assemblyName}};
                }
                missingAssemblies.push_back(assemblyName);
                continue;
            }

            std::size_t startCount = matches.size();
            bool thisAsmCapped = false;
            auto isAsmCapped = [&]() {
                return matches.size() - startCount >= perAsmLimit;
            };

            auto classes = IL2CPP::EnumerateClasses(img);
            for (const auto& ce : classes) {
                if (ce.klass == nullptr) continue;
                if (thisAsmCapped) break;
                if (matches.size() >= globalCap) { truncated = true; break; }
                ++scanned;
                auto res = IL2CPP::ReadClassInfo(ce.klass);
                if (!res.ok) continue;

                if (wantFields) {
                    for (const auto& f : res.info.fields) {
                        bool isStatic = (f.flags & 0x10u) != 0;
                        if (staticOnly && !isStatic) continue;
                        if (!pattern.empty() && !ICaseContainsAscii(f.name, pattern)) continue;
                        matches.push_back({
                            {"namespace", ce.ns},
                            {"class",     ce.name},
                            {"assembly",  assemblyName},
                            {"name",      f.name},
                            {"kind",      "field"},
                            {"type",      f.typeName},
                            {"offset",    f.offset},
                            {"static",    isStatic},
                        });
                        if (isAsmCapped())              { thisAsmCapped = true; break; }
                        if (matches.size() >= globalCap){ truncated = true;     break; }
                    }
                    if (thisAsmCapped || truncated) break;
                }
                if (wantMethods) {
                    for (const auto& m : res.info.methods) {
                        bool isStatic = (m.flags & 0x10u) != 0;
                        if (staticOnly && !isStatic) continue;
                        if (!pattern.empty() && !ICaseContainsAscii(m.name, pattern)) continue;
                        matches.push_back({
                            {"namespace", ce.ns},
                            {"class",     ce.name},
                            {"assembly",  assemblyName},
                            {"name",      m.name},
                            {"kind",      "method"},
                            {"token",     m.token},
                            {"static",    isStatic},
                            {"address",   formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
                        });
                        if (isAsmCapped())              { thisAsmCapped = true; break; }
                        if (matches.size() >= globalCap){ truncated = true;     break; }
                    }
                    if (thisAsmCapped || truncated) break;
                }
            }

            if (thisAsmCapped) truncatedAssemblies.push_back(assemblyName);
            if (truncated) break;
        }
        json response = {
            {"matches",         std::move(matches)},
            {"scanned_classes", scanned},
            {"truncated",       truncated},
        };
        if (!missingAssemblies.empty()) {
            response["missing_assemblies"] = std::move(missingAssemblies);
        }
        if (!truncatedAssemblies.empty()) {
            response["truncated_assemblies"] = std::move(truncatedAssemblies);
        }
        return response;
    });

    // Phase 28b — findFieldReferences (IL2CPP). Dispatches on static vs
    // instance:
    //   * STATIC → FindStaticFieldReferences: resolve static-storage address
    //     via il2cpp_class_get_static_data + offset, scan GameAssembly.dll's
    //     .text for `mov/lea reg, [rip+disp32]` (+ chain/lazy-init fallbacks)
    //     whose absolute target hits the field. confidence="exact".
    //   * INSTANCE → FindInstanceFieldReferences: Zydis-decode the declaring
    //     class's own methods for `[gpReg + fieldOffset]` accesses (base !=
    //     rip/rsp/rbp). confidence="heuristic" (displacement match, no taint).
    // Each hit is attributed to its enclosing method via the sorted method-
    // pointer index built from the target image.
    PipeServer::RegisterHandler(MagicStrings::CmdFindFieldReferences,
        [formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::string assembly = args.value("assembly", std::string{});
        if (assembly.empty()) return json{{"error", "missing 'assembly' arg"}};
        std::string ns       = args.value("ns",       std::string{});
        std::string klassNm  = args.value("class",    std::string{});
        std::string fieldNm  = args.value("fieldName", std::string{});
        if (klassNm.empty()) return json{{"error", "missing 'class' arg"}};
        if (fieldNm.empty()) return json{{"error", "missing 'fieldName' arg"}};

        std::string kindStr = args.value("kind", std::string{"any"});
        IL2CPP::FieldRefKind kind = IL2CPP::FieldRefKind::Any;
        if      (kindStr == "read")  kind = IL2CPP::FieldRefKind::Read;
        else if (kindStr == "write") kind = IL2CPP::FieldRefKind::Write;
        else if (kindStr != "any")
            return json{{"error", "invalid 'kind' (expected 'any' / 'read' / 'write')"}};

        int limit = args.value("limit", 200);
        if (limit <= 0)   limit = 200;
        if (limit > 5000) limit = 5000;

        const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assembly);
        if (img == nullptr) return json{{"error", "assembly not found: " + assembly}};

        // Phase 28b — resolve the field's static-vs-instance flag up front so
        // we dispatch to the right scanner. Static fields → RIP/chain scan
        // (exact); instance fields → declaring-class AOB scan (heuristic).
        IL2CPP::Il2CppClass* targetClassForFlags = IL2CPP::FindClass(img, ns, klassNm);
        if (targetClassForFlags == nullptr) {
            return json{{"error", "class not found: " +
                         (ns.empty() ? klassNm : ns + "." + klassNm)}};
        }
        bool fieldIsStatic = false;
        bool fieldFound     = false;
        {
            auto info = IL2CPP::ReadClassInfo(targetClassForFlags);
            if (info.ok) {
                for (const auto& f : info.info.fields) {
                    if (f.name == fieldNm) {
                        fieldIsStatic = (f.flags & 0x10u) != 0;
                        fieldFound    = true;
                        break;
                    }
                }
            }
        }
        if (!fieldFound) {
            return json{{"error", "field not found: " + fieldNm}};
        }

        auto fr = fieldIsStatic
            ? IL2CPP::FindStaticFieldReferences(img, ns, klassNm, fieldNm, kind,
                                                static_cast<std::size_t>(limit))
            : IL2CPP::FindInstanceFieldReferences(img, ns, klassNm, fieldNm, kind,
                                                  static_cast<std::size_t>(limit));
        if (!fr.ok && !fr.error.empty()) {
            return json{{"error", fr.error}};
        }

        // Resolve field metadata for the response header.
        json fieldInfo = json::object();
        fieldInfo["name"]     = fieldNm;
        fieldInfo["isStatic"] = fieldIsStatic;
        // staticAddress is meaningful only for the static path — instance
        // fields have no single storage address (per-object on the GC heap).
        if (fieldIsStatic) {
            fieldInfo["staticAddress"] =
                formatHex(reinterpret_cast<std::uintptr_t>(fr.staticAddress));
        }
        {
            auto info = IL2CPP::ReadClassInfo(targetClassForFlags);
            if (info.ok) {
                for (const auto& f : info.info.fields) {
                    if (f.name == fieldNm) {
                        fieldInfo["offset"]   = f.offset;
                        fieldInfo["type"]     = f.typeName;
                        break;
                    }
                }
            }
        }

        json refs = json::array();
        for (const auto& m : fr.matches) {
            refs.push_back({
                {"assembly",       assembly},
                {"namespace",      m.ns},
                {"class",          m.className},
                {"method",         m.methodName},
                {"methodToken",    m.methodToken},
                {"methodFlags",    m.methodFlags},
                {"isMethodStatic", m.isMethodStatic},
                {"isStatic",       m.isStatic},
                {"kind",           m.refKind},
                {"confidence",     m.confidence},
                {"address",        formatHex(reinterpret_cast<std::uintptr_t>(m.methodAddress))},
                {"hitAddress",     formatHex(reinterpret_cast<std::uintptr_t>(m.hitAddress))},
            });
        }

        json response = {
            {"ok",              true},
            {"field",           std::move(fieldInfo)},
            {"references",     std::move(refs)},
            {"scanned_methods", fr.scannedMethods},
            {"truncated",       fr.truncated},
        };
        // Build #555 — surface the structural-limit advisory when the
        // scanner couldn't possibly see the references (runtime-heap
        // static_fields). UI shows this prominently when matches=0.
        if (!fr.note.empty()) response["note"] = fr.note;
        return response;
    });

    // Phase 28b-v3 investigation — diagnoseClassReferences. Given a class
    // FQN, reports every storage slot (GameAssembly.dll non-exec sections
    // + committed RW heap) holding the Il2CppClass* and its
    // static_fields ptr, with 128 bytes of surrounding context per hit.
    // Used to figure out how compiler-generated `<>c` classes are
    // referenced from AOT code when the v2 ClassPointerScanner returns
    // 0 module slots (so we can design the v3 chain detector).
    PipeServer::RegisterHandler(MagicStrings::CmdDiagnoseClassReferences,
        [formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::string assembly = args.value("assembly", std::string{});
        if (assembly.empty()) return json{{"error", "missing 'assembly' arg"}};
        std::string ns       = args.value("ns",       std::string{});
        std::string klassNm  = args.value("class",    std::string{});
        if (klassNm.empty()) return json{{"error", "missing 'class' arg"}};
        std::string fieldNm  = args.value("fieldName", std::string{});

        bool heapScan = args.value("heapScan", true);
        int  heapLimit = args.value("heapLimit", 64);
        if (heapLimit <= 0)   heapLimit = 64;
        if (heapLimit > 1024) heapLimit = 1024;

        const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assembly);
        if (img == nullptr) return json{{"error", "assembly not found: " + assembly}};

        IL2CPP::Il2CppDomain* domain = IL2CPP::GetDomain();
        if (domain == nullptr) return json{{"error", "il2cpp domain unavailable"}};
        IL2CPP::ThreadAttachGuard guard(domain);
        if (!guard.Attached()) return json{{"error", "il2cpp thread attach failed"}};

        IL2CPP::Il2CppClass* klass = IL2CPP::FindClass(img, ns, klassNm);
        if (klass == nullptr) {
            return json{{"error", "class not found: " +
                         (ns.empty() ? klassNm : ns + "." + klassNm)}};
        }

        auto d = IL2CPP::DiagnoseClassReferences(
            klass, fieldNm, heapScan, static_cast<std::size_t>(heapLimit));
        if (!d.ok) return json{{"error", d.error}};

        // Hex-encode byte buffers for the wire (no base64 — keeps the
        // response copy-pasteable into a debugger watch window).
        auto bytesToHex = [](const std::vector<std::uint8_t>& bytes) {
            std::string out;
            out.reserve(bytes.size() * 2);
            static constexpr char kHex[] = "0123456789ABCDEF";
            for (auto b : bytes) {
                out.push_back(kHex[(b >> 4) & 0xF]);
                out.push_back(kHex[b & 0xF]);
            }
            return out;
        };

        auto slotToJson = [&](const IL2CPP::DiagnosticSlot& s) {
            json j = {
                {"address",          formatHex(s.address)},
                {"section",          s.sectionName},
                {"contextHex",       bytesToHex(s.context)},
                {"contextSlotOffset", s.contextSlotOffset},
            };
            if (s.regionBase != 0) {
                j["regionBase"]    = formatHex(s.regionBase);
                j["regionSize"]    = s.regionSize;
                j["regionProtect"] = s.regionProtect;
                j["regionType"]    = s.regionType;
            }
            return j;
        };

        json sectionsJson = json::array();
        for (const auto& sec : d.sections) {
            sectionsJson.push_back({
                {"name",         sec.name},
                {"va",           formatHex(sec.va)},
                {"size",         sec.size},
                {"isExecutable", sec.isExecutable},
                {"isWritable",   sec.isWritable},
            });
        }

        auto slotListToJson = [&](const std::vector<IL2CPP::DiagnosticSlot>& v) {
            json out = json::array();
            out.get_ptr<json::array_t*>()->reserve(v.size());
            for (const auto& s : v) out.push_back(slotToJson(s));
            return out;
        };

        json navJson = json::array();
        for (const auto& c : d.navigationCandidates) {
            navJson.push_back({
                {"methodPointer", formatHex(c.methodPointer)},
                {"slotAddress",   formatHex(c.slotAddress)},
            });
        }

        json response = {
            {"ok",                        true},
            {"classFqn",                  ns.empty() ? klassNm : ns + "." + klassNm},
            {"classPtr",                  formatHex(d.classPtr)},
            {"staticFieldsPtr",           formatHex(d.staticFieldsPtr)},
            {"staticFieldsOffsetInClass", d.staticFieldsOffsetInClass},
            {"targetFieldName",           d.targetFieldName},
            {"targetFieldInfoPtr",        formatHex(d.targetFieldInfoPtr)},
            {"targetFieldOffset",         d.targetFieldOffset},
            {"module", {
                {"base",     formatHex(d.moduleBase)},
                {"size",     d.moduleSize},
                {"sections", std::move(sectionsJson)},
            }},
            {"classStructHex",            bytesToHex(d.classStructDump)},
            {"moduleClassPtrSlots",       slotListToJson(d.moduleClassPtrSlots)},
            {"moduleStaticFieldsSlots",   slotListToJson(d.moduleStaticFieldsSlots)},
            {"moduleFieldInfoSlots",      slotListToJson(d.moduleFieldInfoSlots)},
            {"heapClassPtrSlots",         slotListToJson(d.heapClassPtrSlots)},
            {"heapStaticFieldsSlots",     slotListToJson(d.heapStaticFieldsSlots)},
            {"heapFieldInfoSlots",        slotListToJson(d.heapFieldInfoSlots)},
            {"navigationCandidates",      std::move(navJson)},
            {"stats", {
                {"moduleBytesScanned", d.moduleBytesScanned},
                {"heapRegionsScanned", d.heapRegionsScanned},
                {"heapBytesScanned",   d.heapBytesScanned},
                {"heapRegionsSkipped", d.heapRegionsSkipped},
                {"heapCapHit",         d.heapCapHit},
            }},
        };
        return response;
    });

    // Phase 28b-v3.1 step 1 — dumpLazyInitSlots.
    // Returns a sample of the lazy-init slot map (cached after first
    // call to EnumerateLazyInitSlots), with raw bytes captured around
    // each slot so we can determine the IL2CPP MetadataInitializerEntry
    // struct layout empirically. v3.1 needs the typeIndex offset within
    // the entry to map slot → class statically (= without depending on
    // the AOT method to have executed in the current session).
    PipeServer::RegisterHandler(MagicStrings::CmdDumpLazyInitSlots,
        [formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        int  limit       = args.value("limit",       32);
        int  bytesBefore = args.value("bytesBefore", 16);
        int  bytesAfter  = args.value("bytesAfter",  48);
        bool onlyNonZero = args.value("onlyNonZero", false);
        if (limit <= 0)       limit = 32;
        if (limit > 1024)     limit = 1024;
        if (bytesBefore < 0)  bytesBefore = 0;
        if (bytesBefore > 128) bytesBefore = 128;
        if (bytesAfter <= 0)  bytesAfter = 48;
        if (bytesAfter > 256) bytesAfter = 256;

        auto bytesToHex = [](const std::vector<std::uint8_t>& bytes) {
            std::string out;
            out.reserve(bytes.size() * 2);
            static constexpr char kHex[] = "0123456789ABCDEF";
            for (auto b : bytes) {
                out.push_back(kHex[(b >> 4) & 0xF]);
                out.push_back(kHex[b & 0xF]);
            }
            return out;
        };

        // v3.1 step 1 — keep this handler RUTHLESSLY safe. Earlier
        // iteration crashed the host: the v3 lea+call scanner pattern
        // over-matches (every `lea reg,[data]; call rel32` in `.text`
        // matches, including thousands of non-init calls), so many
        // slots hold things that AREN'T `Il2CppClass*` (string literals,
        // MethodInfo*, plain globals, etc). Calling `class_get_name` on
        // those AVs the runtime in places SEH can't always intercept.
        // Resolution: this handler now ONLY does SEH-protected raw
        // reads. The caller correlates `currentValue` with class
        // pointers from `diagnoseClassReferences` manually.
        const auto& allSlots = IL2CPP::EnumerateLazyInitSlots();

        // Module bounds — used to classify currentValue as "in
        // GameAssembly.dll module" vs "heap-managed" vs "null".
        void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
        const std::uintptr_t gaLo = reinterpret_cast<std::uintptr_t>(gaBase);
        const std::uintptr_t gaSize = ModuleHelper::GetModuleSize(MagicStrings::ModuleIL2CPP);
        const std::uintptr_t gaHi = gaLo + gaSize;

        json slotsJson = json::array();
        std::size_t emitted = 0;
        std::size_t nonZeroCount = 0;
        std::size_t pointerLikeCount = 0;  // value > 0x10000 + 8-byte aligned
        for (const auto& s : allSlots) {
            // Read slot value (SEH-protected).
            std::uintptr_t curVal = 0;
            bool readOk = Memory::SafeReadBytes(
                reinterpret_cast<const void*>(s.slotAddress),
                &curVal, sizeof(curVal));
            const bool isNonZero    = (readOk && curVal != 0);
            const bool isPointerLike =
                isNonZero && (curVal & 7u) == 0 && curVal > 0x10000;
            if (isNonZero)     ++nonZeroCount;
            if (isPointerLike) ++pointerLikeCount;
            if (onlyNonZero && !isNonZero) continue;
            if (emitted >= static_cast<std::size_t>(limit)) continue;

            // Context window (SEH-protected).
            const std::size_t totalCtx =
                static_cast<std::size_t>(bytesBefore + bytesAfter);
            std::vector<std::uint8_t> ctx(totalCtx);
            std::size_t actualCtxStart = static_cast<std::size_t>(bytesBefore);
            if (s.slotAddress > static_cast<std::uintptr_t>(bytesBefore)) {
                const std::uintptr_t readAddr = s.slotAddress - bytesBefore;
                if (!Memory::SafeReadBytes(
                        reinterpret_cast<const void*>(readAddr),
                        ctx.data(), totalCtx)) {
                    // Partial — try just the after window.
                    ctx.assign(static_cast<std::size_t>(bytesAfter), 0);
                    actualCtxStart = 0;
                    if (!Memory::SafeReadBytes(
                            reinterpret_cast<const void*>(s.slotAddress),
                            ctx.data(), bytesAfter)) {
                        ctx.clear();
                    }
                }
            } else {
                ctx.clear();
            }

            // Classify currentValue location heuristically. No class
            // name lookup — that's what crashed the previous iteration.
            std::string valueLocation = "null";
            if (isNonZero) {
                if (curVal >= gaLo && curVal < gaHi) {
                    valueLocation = "in-module";  // probably .rdata string or similar
                } else if (isPointerLike) {
                    valueLocation = "heap";       // could be Il2CppClass* / Il2CppType* / etc
                } else {
                    valueLocation = "non-pointer-like";  // small uint64
                }
            }

            // v3.1 — token decode + (best-effort) static class resolution.
            // Token info is captured at scan time so this is non-crashy:
            // it's just looking up a typeIndex in an already-validated array.
            json tokenJson = nullptr;
            if (s.tokenKind != 0) {
                static const char* kKindNames[] = {
                    "Invalid", "TypeInfo", "Il2CppType",
                    "MethodDef", "FieldInfo", "StringLiteral", "MethodRef"
                };
                const char* kindName =
                    (s.tokenKind < (sizeof(kKindNames)/sizeof(kKindNames[0])))
                        ? kKindNames[s.tokenKind] : "Unknown";
                tokenJson = {
                    {"raw",      formatHex(s.tokenAtScan)},
                    {"kind",     s.tokenKind},
                    {"kindName", kindName},
                    {"index",    s.tokenIndex},
                };
                if (s.tokenKind == static_cast<std::uint8_t>(
                        IL2CPP::MetadataUsageKind::TypeInfo)) {
                    if (IL2CPP::Il2CppClass* k =
                            IL2CPP::ResolveTypeInfoSlotClass(s)) {
                        tokenJson["resolvedClassPtr"] =
                            formatHex(reinterpret_cast<std::uintptr_t>(k));
                    }
                }
            }

            slotsJson.push_back({
                {"slotAddress",       formatHex(s.slotAddress)},
                {"leaIP",             formatHex(s.leaIP)},
                {"callIP",            formatHex(s.callIP)},
                {"callTarget",        formatHex(s.callTarget)},
                {"currentValue",      formatHex(curVal)},
                {"valueLocation",     valueLocation},
                {"token",             std::move(tokenJson)},
                {"contextHex",        bytesToHex(ctx)},
                {"contextSlotOffset", actualCtxStart},
            });
            ++emitted;
        }

        return json{
            {"ok",                true},
            {"totalSlots",        allSlots.size()},
            {"nonZeroSlots",      nonZeroCount},
            {"pointerLikeSlots",  pointerLikeCount},
            {"returned",          emitted},
            {"slots",             std::move(slotsJson)},
        };
    });

    // Debugger symbol dump (IL2CPP only). Walks every requested assembly,
    // emits one row per class (Il2CppClass* in .data) and/or per compiled
    // method (AOT methodPointer in .text of GameAssembly.dll). The UI's
    // SymbolExporter renders these into x64dbg .dd64 / Ghidra label /
    // IDA .idc payloads.
    PipeServer::RegisterHandler(MagicStrings::CmdDumpIl2cppSymbols,
        [formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::vector<std::string> assemblyNames;
        if (args.contains("assemblies") && args["assemblies"].is_array()) {
            for (const auto& a : args["assemblies"]) {
                if (!a.is_string()) continue;
                const auto& s = a.get_ref<const std::string&>();
                if (!s.empty()) assemblyNames.push_back(s);
            }
        }
        if (assemblyNames.empty()) {
            return json{{"error", "missing or empty 'assemblies' arg"}};
        }

        bool wantClasses = true;
        bool wantMethods = true;
        if (args.contains("kinds") && args["kinds"].is_array()) {
            wantClasses = false; wantMethods = false;
            for (const auto& k : args["kinds"]) {
                if (!k.is_string()) continue;
                const auto& s = k.get_ref<const std::string&>();
                if (s == "class")  wantClasses = true;
                if (s == "method") wantMethods = true;
            }
            if (!wantClasses && !wantMethods) {
                return json{{"error", "kinds must include 'class' or 'method'"}};
            }
        }

        const bool wantHumanSuffix = args.value("computeHumanSuffix", true);

        // limit=0 (default) = unlimited up to the safety cap. Negative values
        // also degrade to unlimited rather than an error — same forgiving
        // shape as other handlers.
        std::int64_t limit = args.value("limit", static_cast<std::int64_t>(0));
        if (limit < 0) limit = 0;
        constexpr std::size_t kHardCap = 1'000'000u;
        const std::size_t effectiveCap = (limit == 0)
            ? kHardCap
            : std::min<std::size_t>(static_cast<std::size_t>(limit), kHardCap);

        // GameAssembly.dll is the only candidate for IL2CPP — every AOT
        // method pointer + every Il2CppClass* lives there.
        std::string    moduleName;
        std::uintptr_t moduleBase = 0;
        std::size_t    moduleSize = 0;
        if (void* base = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP)) {
            moduleBase = reinterpret_cast<std::uintptr_t>(base);
            moduleSize = ModuleHelper::GetModuleSize(MagicStrings::ModuleIL2CPP);
            moduleName = "GameAssembly.dll";
        }

        json rows = json::array();
        std::size_t scanned = 0;
        bool truncated = false;
        std::vector<std::string> missingAssemblies;

        for (const auto& assemblyName : assemblyNames) {
            if (truncated) break;
            const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
            if (img == nullptr) {
                missingAssemblies.push_back(assemblyName);
                continue;
            }
            auto classes = IL2CPP::EnumerateClasses(img);
            for (const auto& ce : classes) {
                if (ce.klass == nullptr) continue;
                ++scanned;

                if (wantClasses) {
                    rows.push_back({
                        {"kind",      "class"},
                        {"assembly",  assemblyName},
                        {"namespace", ce.ns},
                        {"class",     ce.name},
                        {"address",   formatHex(reinterpret_cast<std::uintptr_t>(ce.klass))},
                    });
                    if (rows.size() >= effectiveCap) { truncated = true; break; }
                }

                if (wantMethods) {
                    if (wantHumanSuffix) {
                        auto methods = IL2CPP::EnumerateMethodSignatures(ce.klass);
                        for (const auto& m : methods) {
                            if (m.pointer == nullptr) continue;
                            rows.push_back({
                                {"kind",        "method"},
                                {"assembly",    assemblyName},
                                {"namespace",   ce.ns},
                                {"class",       ce.name},
                                {"name",        m.name},
                                {"token",       m.token},
                                {"static",      (m.flags & 0x10u) != 0},
                                {"address",     formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
                                {"humanSuffix", m.humanSuffix},
                            });
                            if (rows.size() >= effectiveCap) { truncated = true; break; }
                        }
                    } else {
                        // Light path — skip the per-param walk.
                        auto info = IL2CPP::ReadClassInfo(ce.klass);
                        if (info.ok) {
                            for (const auto& m : info.info.methods) {
                                if (m.pointer == nullptr) continue;
                                rows.push_back({
                                    {"kind",      "method"},
                                    {"assembly",  assemblyName},
                                    {"namespace", ce.ns},
                                    {"class",     ce.name},
                                    {"name",      m.name},
                                    {"token",     m.token},
                                    {"static",    (m.flags & 0x10u) != 0},
                                    {"address",   formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
                                });
                                if (rows.size() >= effectiveCap) { truncated = true; break; }
                            }
                        }
                    }
                    if (truncated) break;
                }
            }
        }

        const std::size_t rowCount = rows.size();
        json response = {
            {"moduleName",      moduleName},
            {"moduleBase",      formatHex(moduleBase)},
            {"moduleSize",      moduleSize},
            {"rows",            std::move(rows)},
            {"count",           rowCount},
            {"scanned_classes", scanned},
            {"truncated",       truncated},
        };
        if (!missingAssemblies.empty()) {
            response["missing_assemblies"] = std::move(missingAssemblies);
        }
        return response;
    });

    // Phase C follow-up — bulk class-info dump. Walks every class in
    // every requested assembly server-side under ONE thread context,
    // returns the same payload the per-class `getClassInfo` would emit
    // but aggregated. Replaces the UI's per-class loop and fixes the
    // SaGa F2R post-export slow-quit (see lessons-learned.md "Phase C
    // follow-up — IL2CPP equivalent"). Wire shape mirrors the per-
    // class handler at line ~3288 so the UI can reuse the same
    // projection code on the receiving side.
    PipeServer::RegisterHandler(MagicStrings::CmdDumpClassInfo,
        [formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::vector<std::string> assemblyNames;
        if (args.contains("assemblies") && args["assemblies"].is_array()) {
            for (const auto& a : args["assemblies"]) {
                if (!a.is_string()) continue;
                const auto& s = a.get_ref<const std::string&>();
                if (!s.empty()) assemblyNames.push_back(s);
            }
        }
        if (assemblyNames.empty()) {
            return json{{"error", "missing or empty 'assemblies' arg"}};
        }

        // Same hard cap shape as dumpIl2cppSymbols — defends against a
        // brand new game with an absurd type count flipping the heap.
        std::int64_t limit = args.value("limit", static_cast<std::int64_t>(0));
        if (limit < 0) limit = 0;
        constexpr std::size_t kHardCap = 1'000'000u;
        const std::size_t effectiveCap = (limit == 0)
            ? kHardCap
            : std::min<std::size_t>(static_cast<std::size_t>(limit), kHardCap);

        json rows = json::array();
        std::size_t scanned = 0;
        bool truncated = false;
        std::vector<std::string> missingAssemblies;

        for (const auto& assemblyName : assemblyNames) {
            if (truncated) break;
            const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
            if (img == nullptr) {
                missingAssemblies.push_back(assemblyName);
                continue;
            }
            auto classes = IL2CPP::EnumerateClasses(img);
            for (const auto& ce : classes) {
                if (ce.klass == nullptr) continue;
                ++scanned;

                json row = {
                    {"assembly",  assemblyName},
                    {"namespace", ce.ns},
                    {"name",      ce.name},
                };

                auto res = IL2CPP::ReadClassInfo(ce.klass);
                if (!res.ok) {
                    // Surface per-class failures inline rather than dropping
                    // them silently — matches the // SKIPPED behaviour the
                    // existing UI flow uses to flag fail-loud cases.
                    row["error"] = "ReadClassInfo failed";
                    rows.push_back(std::move(row));
                    if (rows.size() >= effectiveCap) { truncated = true; break; }
                    continue;
                }

                json fields = json::array();
                for (const auto& f : res.info.fields) {
                    fields.push_back({
                        {"name", f.name}, {"type", f.typeName},
                        {"offset", f.offset}, {"flags", f.flags},
                        {"static", (f.flags & 0x10u) != 0},
                    });
                }
                json methods = json::array();
                for (const auto& m : res.info.methods) {
                    methods.push_back({
                        {"name", m.name}, {"token", m.token}, {"flags", m.flags},
                        {"static", (m.flags & 0x10u) != 0},
                        {"address", formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
                    });
                }
                json parent = nullptr;
                if (!res.info.parent.name.empty()) {
                    parent = json{
                        {"namespace", res.info.parent.ns},
                        {"name",      res.info.parent.name},
                    };
                }
                row["parent"]  = parent;
                row["fields"]  = std::move(fields);
                row["methods"] = std::move(methods);
                rows.push_back(std::move(row));
                if (rows.size() >= effectiveCap) { truncated = true; break; }
            }
        }

        const std::size_t rowCount = rows.size();
        json response = {
            {"rows",      std::move(rows)},
            {"count",     rowCount},
            {"scanned",   scanned},
            {"truncated", truncated},
        };
        if (!missingAssemblies.empty()) {
            response["missing_assemblies"] = std::move(missingAssemblies);
        }
        return response;
    });

    // ------------------------------------------------------------------------
    // Phase 29a-v2 — snapshotBegin / snapshotChunk / snapshotEnd (IL2CPP).
    // Mirror of the Mono handlers above — see that block for the decode-
    // in-Begin design rationale (lesson from build #597 live-smoke on
    // 活俠傳). IL2CPP differences: wide heap walk uses direct class-ptr
    // membership (no vtable indirection); build-id source module is
    // GameAssembly.dll; no BDWGC finalizer accumulation but uniform
    // architecture keeps the codebase symmetric.
    // ------------------------------------------------------------------------
    PipeServer::RegisterHandler(MagicStrings::CmdSnapshotBegin,
        [formatHex, decodeField, walkArrayElementsInline](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        // Hard-caps mirror the Mono handler — see 2026-05-27 raise note above.
        std::size_t instanceCap = static_cast<std::size_t>(args.value("instanceCap", 100));
        if (instanceCap == 0)    instanceCap = 100;
        if (instanceCap > 8192)  instanceCap = 8192;
        std::size_t arrayCap = static_cast<std::size_t>(args.value("arrayCap", 50));
        if (arrayCap > 4096)     arrayCap = 4096;
        // Total decoded-instance budget — bounds decode TIME + memory on big
        // games regardless of free RAM. 0 = unlimited. Combined with the
        // ascending-population plan sort below, truncation drops the
        // high-population "noise" classes first and keeps the low-population
        // game-state classes. Overridable via instanceDecodeBudget.
        std::size_t instanceDecodeBudget =
            static_cast<std::size_t>(args.value("instanceDecodeBudget", 50000));
        if (instanceDecodeBudget > 2000000) instanceDecodeBudget = 2000000;

        // Phase 29f (Layer B) — reference-array inner-field capture opt-in
        // (mirror of the Mono handler).
        const bool reqArrayElementFields = args.value("arrayElementFields", false);
        const bool reqCaptureStaticValues = args.value("captureStaticValues", false);
        std::size_t elementFieldCap = static_cast<std::size_t>(args.value("elementFieldCap", 16));
        if (elementFieldCap == 0)  elementFieldCap = 16;
        if (elementFieldCap > 64)  elementFieldCap = 64;
        std::size_t elementCaptureBudget = static_cast<std::size_t>(args.value("elementCaptureBudget", 20000));
        if (elementCaptureBudget == 0)       elementCaptureBudget = 20000;
        if (elementCaptureBudget > 2000000)  elementCaptureBudget = 2000000;
        std::vector<std::string> arrayElementTargets;
        if (args.contains("arrayElementTargets") && args["arrayElementTargets"].is_array()) {
            for (const auto& t : args["arrayElementTargets"]) {
                if (t.is_string() && !t.get_ref<const std::string&>().empty())
                    arrayElementTargets.push_back(t.get_ref<const std::string&>());
            }
        }

        // 32-bit OOM guard — mirrors the Mono handler. IL2CPP x86 isn't
        // a shipping target today (the UI rejects it at inject time)
        // but the guard stays symmetric in case a future IL2CPP x86
        // build appears.
        const ProcessMemorySnapshot memBeforeIl = QueryProcessMemory();
        if (memBeforeIl.pointerSize == 4 &&
            memBeforeIl.privateUsage >= kX86PrivateUsageRefuseThreshold) {
            const double gb = memBeforeIl.privateUsage / (1024.0 * 1024.0 * 1024.0);
            return json{
                {"error",
                 "Game process at " + std::to_string(static_cast<int>(gb * 100) / 100.0).substr(0, 4)
                 + " GB private memory — refusing snapshot to avoid OOM crash on this 32-bit IL2CPP target. "
                   "Lower InstanceCap and ArrayCap then retry, "
                   "or restart the game to free memory before snapshotting."},
                {"gamePointerSize", memBeforeIl.pointerSize},
                {"gameWorkingSet",  memBeforeIl.workingSet},
                {"gamePrivateUsage", memBeforeIl.privateUsage},
            };
        }

        // Snapshot memory budget — stop the decode before the game OOMs.
        // budget = pre-snapshot private usage + headroom, headroom clamped to
        // a fraction of free RAM. When the game's private usage crosses it the
        // decode stops and the response reports truncatedByMemory.
        std::uint64_t memHeadroomIl =
            static_cast<std::uint64_t>(args.value("memHeadroomMB", 0)) * 1024ull * 1024ull;
        if (memHeadroomIl == 0) memHeadroomIl = kSnapshotDefaultHeadroomBytes;
        if (const std::uint64_t availIl = QueryAvailablePhysBytes(); availIl > 0) {
            const auto cap = static_cast<std::uint64_t>(availIl * kSnapshotAvailPhysFraction);
            if (memHeadroomIl > cap) memHeadroomIl = cap;
        }
        const std::uint64_t memBudgetBytesIl = memBeforeIl.privateUsage + memHeadroomIl;

        bool includeSystemAsm = args.value("includeSystemAssemblies", false);
        std::vector<std::string> requestedAssemblies;
        bool explicitScope = false;
        if (args.contains("assemblies") && args["assemblies"].is_array()) {
            for (const auto& a : args["assemblies"]) {
                if (!a.is_string()) continue;
                const auto& s = a.get_ref<const std::string&>();
                if (!s.empty()) requestedAssemblies.push_back(s);
            }
            explicitScope = !requestedAssemblies.empty();
        }
        std::size_t excludedSystemCount = 0;
        if (!explicitScope) {
            for (const auto& a : IL2CPP::EnumerateAssemblies()) {
                if (!includeSystemAsm && IsSystemOrEngineAssembly(a.name)) {
                    ++excludedSystemCount;
                    continue;
                }
                requestedAssemblies.push_back(a.name);
            }
        }
        if (requestedAssemblies.empty()) {
            return json{{"error", "no assemblies visible (default exclude removed every assembly; try includeSystemAssemblies=true)"}};
        }

        // Wide heap walk first — groups instances by Il2CppClass*.
        auto bulk = IL2CPP::FindAllInstances(instanceCap);

        struct ClassDesc {
            std::string         assembly;
            std::string         ns;
            std::string         name;
            IL2CPP::Il2CppClass* klass = nullptr;
        };
        std::vector<ClassDesc> descs;
        // Phase 29f (Layer B) — set of every enumerated in-scope class. The
        // element-field capturer only deep-walks an element whose runtime
        // class is in here, so a garbage/out-of-scope class that
        // object_get_class hands back for a false-positive array slot can't
        // reach the field walk and crash the game (the x64 IL2CPP CTD).
        std::unordered_map<IL2CPP::Il2CppClass*, char> knownIlKlasses;
        for (const auto& assemblyName : requestedAssemblies) {
            const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
            if (img == nullptr) continue;
            auto classes = IL2CPP::EnumerateClasses(img);
            for (const auto& ce : classes) {
                if (ce.klass == nullptr) continue;
                ClassDesc d;
                d.assembly = assemblyName;
                d.ns       = ce.ns;
                d.name     = ce.name;
                d.klass    = ce.klass;
                knownIlKlasses[ce.klass] = 1;
                descs.push_back(std::move(d));
            }
        }

        auto session = std::make_unique<Snapshot::Session>();
        session->capturedAt    = Snapshot::CapturedAtUtcNow();
        session->gameBuildId   = Snapshot::ComputeGameBuildId(MagicStrings::ModuleIL2CPP);
        session->gameSessionId = Snapshot::SessionManager::GameSessionId();
        session->instanceCap   = instanceCap;
        session->arrayCap      = arrayCap;
        // Phase 29f (Layer B) — x86 blanket gate (mirror of Mono).
        session->elementFieldCap     = elementFieldCap;
        session->elementCaptureBudget = elementCaptureBudget;
        session->captureStaticValues  = reqCaptureStaticValues;
        session->arrayElementTargets = std::move(arrayElementTargets);
        session->arrayElementFields  = reqArrayElementFields &&
            !(memBeforeIl.pointerSize == 4 && session->arrayElementTargets.empty());
        const bool effArrayElementFields = session->arrayElementFields;
        // Phase 31-2 (Phase B) — generic value-type struct fan-out (see Mono path).
        // Locals because session is moved before the Begin response is built.
        const bool        effValueStructFields = args.value("valueStructFields", true);
        const std::size_t effStructMemberCap   = args.value("structMemberCap",
                                                            static_cast<std::size_t>(32));
        session->valueStructFields = effValueStructFields;
        session->structMemberCap   = effStructMemberCap;
        session->plan.reserve(descs.size());

        std::unordered_map<std::string, Snapshot::PlanAssemblyEntry> planByAsm;
        for (const auto& asmName : requestedAssemblies) {
            planByAsm[asmName].assembly = asmName;
        }

        for (const auto& d : descs) {
            Snapshot::ClassPlanItem item;
            item.assembly = d.assembly;
            item.ns       = d.ns;
            item.name     = d.name;
            item.klass    = static_cast<void*>(d.klass);

            auto it = bulk.byClass.find(d.klass);
            if (it != bulk.byClass.end()) {
                item.instances = std::move(it->second);
            }
            planByAsm[d.assembly].classCount++;
            planByAsm[d.assembly].instanceCount += item.instances.size();
            session->totalInstances += item.instances.size();
            session->plan.push_back(std::move(item));
        }

        // Decode low-population classes first. When the instance budget or the
        // memory watchdog truncates, it drops the high-population tail (Node /
        // Entry / small container types — usually noise) and preserves the
        // low-population game-state classes the user actually monitors. Stable
        // sort keeps enumeration order among equal-population classes.
        std::stable_sort(session->plan.begin(), session->plan.end(),
            [](const Snapshot::ClassPlanItem& a, const Snapshot::ClassPlanItem& b) {
                return a.instances.size() < b.instances.size();
            });

        // ---- Phase 30b: build the streaming decoder; DO NOT decode here ----
        // The decoder owns the plan (moved) + the heap-walk context. Decode
        // happens on demand in snapshotChunk, which frees each chunk's payload
        // after it ships — bounding the game-process transient peak to ~one
        // chunk instead of the whole decoded snapshot (the ~4.5 GB buffer on
        // Utawarerumono). decodeStats / truncation / post-stream game memory
        // are finalized on the LAST chunk, not here (see snapshotChunk).
        const std::size_t totalClasses = session->plan.size();
        IL2CPP::Il2CppDomain* domain = IL2CPP::GetDomain();
        if (domain != nullptr) {
            session->streamDecoder = std::make_unique<Il2cppStreamDecoder>(
                domain,
                std::move(session->plan),
                std::move(knownIlKlasses),
                arrayCap,
                instanceDecodeBudget,
                elementFieldCap,
                elementCaptureBudget,
                session->arrayElementTargets,
                session->arrayElementFields,
                session->captureStaticValues,
                effValueStructFields,
                effStructMemberCap,
                memBudgetBytesIl,
                memBeforeIl.privateUsage,
                formatHex,
                decodeField,
                walkArrayElementsInline);
        }
        // session->plan is now empty (moved into the decoder); the captured
        // totalClasses above is used for the response + plan summary below.

        for (const auto& asmName : requestedAssemblies) {
            session->planByAssembly.push_back(planByAsm[asmName]);
        }
        const std::size_t totalInst    = session->totalInstances;
        const std::string capturedAt   = session->capturedAt;
        const std::string buildId      = session->gameBuildId;
        const std::string sessionGuid  = session->gameSessionId;

        json planArr = json::array();
        for (const auto& pe : session->planByAssembly) {
            planArr.push_back({
                {"assembly",      pe.assembly},
                {"classCount",    pe.classCount},
                {"instanceCount", pe.instanceCount},
            });
        }

        std::uint64_t sid = Snapshot::SessionManager::Instance().Begin(std::move(session));

        json scanStats = {
            {"regionsTotal",     bulk.regionsTotal},
            {"regionsScanned",   bulk.regionsScanned},
            {"regionsSkipImage", bulk.regionsSkipImage},
            {"regionsSkipStack", bulk.regionsSkipStack},
            {"bytesScanned",     bulk.bytesScanned},
        };
        // Phase 30b — no decodeStats here: decode happens during snapshotChunk,
        // so decodedInstances / truncation / peakPrivateUsage are finalized on
        // the last chunk's response (the UI reads them from there).

        // Pre-decode game-process memory snapshot for the Begin response. (Decode
        // hasn't run yet — the post-stream growth shows up on the last chunk.)
        const ProcessMemorySnapshot memAfterIl = QueryProcessMemory();

        Discrete::Log::Write(MagicStrings::LogCategoryScan,
            "snapshotBegin(IL2CPP) id=" + std::to_string(sid) +
            " classes=" + std::to_string(totalClasses) +
            " instances=" + std::to_string(totalInst) +
            " streaming=1 (decode deferred to snapshotChunk)" +
            " instBudget=" + std::to_string(instanceDecodeBudget) +
            " memBudget=" + std::to_string(memBudgetBytesIl) +
            " buildId=" + buildId +
            " gameWorkingSet=" + std::to_string(memAfterIl.workingSet) +
            " gamePrivateUsage=" + std::to_string(memAfterIl.privateUsage));

        return json{
            {"ok",                      true},
            {"snapshotId",              std::to_string(sid)},
            {"capturedAt",              capturedAt},
            {"gameBuildId",             buildId},
            {"gameSessionId",           sessionGuid},
            {"instanceCap",             instanceCap},
            {"arrayCap",                arrayCap},
            {"arrayElementFields",      effArrayElementFields},
            {"valueStructFields",       effValueStructFields},
            {"structMemberCap",         effStructMemberCap},
            {"elementFieldCap",         elementFieldCap},
            {"totalClasses",            totalClasses},
            {"totalInstances",          totalInst},
            {"excludedSystemAssemblies", excludedSystemCount},
            {"includeSystemAssemblies",  includeSystemAsm},
            {"plan",                    std::move(planArr)},
            {"scanStats",               std::move(scanStats)},
            // Phase 30b — decodeStats + truncatedByMemory/Count omitted here;
            // they're finalized on the last snapshotChunk (decode is deferred).
            {"gamePointerSize",         memAfterIl.pointerSize},
            {"gameWorkingSet",          memAfterIl.workingSet},
            {"gamePrivateUsage",        memAfterIl.privateUsage},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdSnapshotChunk,
        [](const json& args) -> json {
        // Phase 30b — IL2CPP streaming: decode plan rows ON DEMAND via the
        // session's streamDecoder, then this chunk's payload is freed once the
        // response is serialized (it's never retained in the session). That
        // bounds the game-process peak to ~one chunk instead of the whole
        // decoded snapshot. Falls back to the legacy pre-built `classes` slicer
        // if a session has no decoder (shouldn't happen on IL2CPP post-30b).
        std::string sidStr = args.value("snapshotId", std::string{});
        if (sidStr.empty()) return json{{"error", "missing 'snapshotId'"}};
        std::uint64_t sid = 0;
        try { sid = std::stoull(sidStr); } catch (...) {
            return json{{"error", "bad 'snapshotId'"}};
        }

        std::int64_t cursorIn = args.value("cursor", static_cast<std::int64_t>(-1));
        std::size_t  limit    = static_cast<std::size_t>(args.value("limit", 32));
        if (limit == 0)   limit = 32;
        if (limit > 200)  limit = 200;

        json classes = json::array();
        std::size_t totalPlan   = 0;
        std::size_t startCursor = 0;
        std::size_t nextCursor  = 0;
        bool done = false;
        // Final-chunk stats — only filled (on done) for a streaming session.
        json decodeStats;
        bool haveStats   = false;
        bool truncByMem  = false;
        bool truncByCount = false;
        // NOTE: the decode runs INSIDE View, i.e. while the SessionManager mutex
        // is held. That serializes concurrent snapshot RPCs (in practice the UI
        // captures one snapshot at a time) but never deadlocks — DecodeRange
        // touches only the IL2CPP runtime, never the SessionManager. Keeping the
        // decoder owned by the (locked) session is what makes a concurrent
        // snapshotEnd / expiry safe against a use-after-free on the decoder.
        bool found = Snapshot::SessionManager::Instance().View(sid,
            [&](Snapshot::Session& s) {
                startCursor = (cursorIn < 0) ? 0 : static_cast<std::size_t>(cursorIn);
                if (s.streamDecoder) {
                    totalPlan = s.streamDecoder->PlanSize();
                    if (startCursor > totalPlan) startCursor = totalPlan;
                    nextCursor = s.streamDecoder->DecodeRange(startCursor, limit, classes);
                    done = (nextCursor >= totalPlan);
                    if (done) {
                        decodeStats  = s.streamDecoder->DecodeStats();
                        truncByMem   = s.streamDecoder->TruncatedByMemory();
                        truncByCount = s.streamDecoder->TruncatedByCount();
                        haveStats    = true;
                    }
                } else {
                    // Legacy decode-in-Begin slicer (pre-built payloads).
                    totalPlan = s.classes.size();
                    if (startCursor > totalPlan) startCursor = totalPlan;
                    std::size_t end = std::min(totalPlan, startCursor + limit);
                    for (std::size_t i = startCursor; i < end; ++i) {
                        classes.push_back(s.classes[i]);
                    }
                    nextCursor = startCursor + static_cast<std::size_t>(classes.size());
                    done = (nextCursor >= totalPlan);
                }
            });
        if (!found) {
            return json{{"error", "session_not_found"}, {"snapshotId", sidStr}};
        }
        json resp = {
            {"ok",         true},
            {"snapshotId", sidStr},
            {"cursor",     startCursor},
            {"nextCursor", nextCursor},
            {"done",       done},
            {"classes",    std::move(classes)},
        };
        // Phase 30b — finalized decode stats + truncation + post-stream game
        // memory ship on the LAST chunk (decode ran across the chunks, so these
        // aren't known until now). The UI prefers these over the Begin values.
        if (haveStats) {
            resp["decodeStats"]       = std::move(decodeStats);
            resp["truncatedByMemory"] = truncByMem;
            resp["truncatedByCount"]  = truncByCount;
            const ProcessMemorySnapshot memAfter = QueryProcessMemory();
            resp["gameWorkingSet"]    = memAfter.workingSet;
            resp["gamePrivateUsage"]  = memAfter.privateUsage;
            resp["gamePointerSize"]   = memAfter.pointerSize;
        }
        return resp;
    });

    PipeServer::RegisterHandler(MagicStrings::CmdSnapshotEnd,
        [](const json& args) -> json {
        std::string sidStr = args.value("snapshotId", std::string{});
        if (sidStr.empty()) return json{{"error", "missing 'snapshotId'"}};
        std::uint64_t sid = 0;
        try { sid = std::stoull(sidStr); } catch (...) {
            return json{{"error", "bad 'snapshotId'"}};
        }
        bool dropped = Snapshot::SessionManager::Instance().End(sid);
        return json{
            {"ok",         true},
            {"snapshotId", sidStr},
            {"dropped",    dropped},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetFieldInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = IL2CPP::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        for (const auto& f : res.info.fields) {
            if (f.name == fieldName) {
                return json{
                    {"name", f.name}, {"type", f.typeName},
                    {"offset", f.offset}, {"flags", f.flags},
                    {"static", (f.flags & 0x10u) != 0},
                };
            }
        }
        return json{{"error", "field not found: " + fieldName}};
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetMethodInfo,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = IL2CPP::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        json matches = json::array();
        for (const auto& m : res.info.methods) {
            if (m.name == methodName) {
                matches.push_back({
                    {"name", m.name}, {"token", m.token}, {"flags", m.flags},
                    {"static", (m.flags & 0x10u) != 0},
                    {"address", formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
                });
            }
        }
        if (matches.empty()) return json{{"error", "method not found: " + methodName}};
        return json{{"matches", std::move(matches)}};
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetStaticFieldData,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto sa = IL2CPP::ResolveStaticFieldAddress(k, fieldName);
        if (!sa.ok) return json{{"error", "static field not resolvable: " + fieldName}};
        return json{
            {"field", fieldName}, {"type", sa.typeName},
            {"offset", sa.offset}, {"flags", sa.flags},
            {"address", formatHex(reinterpret_cast<std::uintptr_t>(sa.address))},
        };
    });

    // Phase 5 segment 2: list metadata fields (including const literals stripped
    // from the runtime field iterator) for a given type.
    PipeServer::RegisterHandler("il2cppGetMetadataTypeFields",
        [formatHex](const json& args) -> json {
        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) return json{{"error", "global-metadata.dat not loaded"}};
        std::string ns   = args.value("namespace", std::string{});
        std::string name = args.value("name", std::string{});
        if (name.empty()) return json{{"error", "missing 'name' arg"}};

        auto td = mp.FindTypeByName(ns, name);
        if (!td) return json{{"error", "type not found in metadata: " + ns + "." + name}};

        // Local: ECMA-335 §II.23.2 compressed signed int decoder (same logic
        // as in getEnumValues; duplicated to keep handlers self-contained).
        auto decodeCompressedSigned = [](const std::uint8_t* p, std::size_t avail,
                                          std::int64_t& outValue,
                                          std::size_t&  outBytes) -> bool {
            if (avail < 1) return false;
            std::uint8_t b0 = p[0];
            std::uint32_t enc = 0; std::size_t w = 0; std::int64_t bias = 0;
            if ((b0 & 0x80u) == 0u) { enc = b0; w = 1; bias = 0x40; }
            else if ((b0 & 0xC0u) == 0x80u) {
                if (avail < 2) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x3Fu) << 8) | p[1];
                w = 2; bias = 0x2000;
            } else if ((b0 & 0xE0u) == 0xC0u) {
                if (avail < 4) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x1Fu) << 24)
                    | (static_cast<std::uint32_t>(p[1])       << 16)
                    | (static_cast<std::uint32_t>(p[2])       <<  8)
                    |  static_cast<std::uint32_t>(p[3]);
                w = 4; bias = 0x10000000;
            } else { return false; }
            std::int64_t mag = static_cast<std::int64_t>(enc >> 1);
            outValue = (enc & 1u) ? (mag - bias) : mag;
            outBytes = w;
            return true;
        };
        // Decode a FieldDefault blob given the IL2CPP type-tag byte
        // (from MetadataRegistration::GetTypeTag). Returns true on success
        // and fills `out` with `value` + `enc_bytes`.
        //
        // **Per-version dispatch on integer encoding**:
        //   * v24 / v27 (Unity 2018 / 2019 / 2020 IL2CPP): raw little-endian,
        //     exactly sizeof(T) bytes — no sentinels, no compressed-int.
        //     Surfaced 2026-05-07 against FF VI (Unity 2019.4): byte sequence
        //     `FFFFFF7F` is `Int32.MaxValue` (2147483647) under raw LE, but
        //     was mis-decoded as the type-min sentinel under the v29+ rules.
        //   * v29+ (Unity 2021+ IL2CPP): sentinels + §II.23.2 compressed
        //     signed int (zigzag-style, 1/2/4 bytes; ±2^28 range, hence the
        //     sentinels for values outside).
        //
        // R4 / R8 (and Boolean / Char) are unaffected — they're always raw LE
        // regardless of metadata version.
        const std::int32_t metadataVersion = mp.version();
        auto decodeByTag = [&decodeCompressedSigned, metadataVersion]
            (std::uint8_t tag, const std::uint8_t* p, std::size_t avail,
             json& out) -> bool {
            const bool useRawLE = (metadataVersion <= 27);
            auto cmp = [&](auto&& cast1, auto&& cast2,
                           std::int64_t maxVal, std::int64_t minVal,
                           std::size_t typeSize) -> bool {
                if (useRawLE) {
                    // v24 / v27 path: read exactly typeSize bytes as little-
                    // endian. cast1/cast2 handle truncation + sign-extension.
                    if (avail < typeSize) return false;
                    std::int64_t raw = 0;
                    std::memcpy(&raw, p, typeSize);
                    out["value"] = cast2(cast1(raw));
                    out["enc_bytes"] = typeSize;
                    out["encoding"] = "raw_le";
                    return true;
                }
                // v29+ path: sentinels + compressed-int.
                if (avail >= 1) {
                    if (p[0] == 0xFE) {
                        out["value"] = cast2(cast1(maxVal));
                        out["enc_bytes"] = 1;
                        out["sentinel"] = "type_max";
                        out["encoding"] = "sentinel_compressed";
                        return true;
                    }
                    if (p[0] == 0xFF) {
                        out["value"] = cast2(cast1(minVal));
                        out["enc_bytes"] = 1;
                        out["sentinel"] = "type_min";
                        out["encoding"] = "sentinel_compressed";
                        return true;
                    }
                }
                std::int64_t raw = 0; std::size_t w = 0;
                if (!decodeCompressedSigned(p, avail, raw, w)) return false;
                out["value"] = cast2(cast1(raw));
                out["enc_bytes"] = w;
                out["encoding"] = "sentinel_compressed";
                return true;
            };
            switch (tag) {
                case 0x02:  // Boolean
                    if (avail < 1) return false;
                    out["value"] = (p[0] != 0); out["enc_bytes"] = 1; return true;
                case 0x03:  // Char
                    if (avail < 2) return false;
                    {
                        std::uint16_t v = 0; std::memcpy(&v, p, 2);
                        out["value"] = v; out["enc_bytes"] = 2;
                    }
                    return true;
                case 0x04:  // I1
                    return cmp([](std::int64_t x){ return static_cast<std::int8_t>(x); },
                               [](std::int8_t x){ return static_cast<std::int64_t>(x); },
                               0x7F, -0x80, 1);
                case 0x05:  // U1
                    return cmp([](std::int64_t x){ return static_cast<std::uint8_t>(x); },
                               [](std::uint8_t x){ return static_cast<std::uint64_t>(x); },
                               0xFF, 0, 1);
                case 0x06:  // I2
                    return cmp([](std::int64_t x){ return static_cast<std::int16_t>(x); },
                               [](std::int16_t x){ return static_cast<std::int64_t>(x); },
                               0x7FFF, -0x8000, 2);
                case 0x07:  // U2
                    return cmp([](std::int64_t x){ return static_cast<std::uint16_t>(x); },
                               [](std::uint16_t x){ return static_cast<std::uint64_t>(x); },
                               0xFFFF, 0, 2);
                case 0x08:  // I4
                    return cmp([](std::int64_t x){ return static_cast<std::int32_t>(x); },
                               [](std::int32_t x){ return static_cast<std::int64_t>(x); },
                               0x7FFFFFFFLL, -0x80000000LL, 4);
                case 0x09:  // U4
                    return cmp([](std::int64_t x){ return static_cast<std::uint32_t>(x); },
                               [](std::uint32_t x){ return static_cast<std::uint64_t>(x); },
                               0xFFFFFFFFLL, 0, 4);
                case 0x0A:  // I8
                    if (avail < 8) return false;
                    {
                        std::int64_t v = 0; std::memcpy(&v, p, 8);
                        out["value"] = v; out["enc_bytes"] = 8;
                    }
                    return true;
                case 0x0B:  // U8
                    if (avail < 8) return false;
                    {
                        std::uint64_t v = 0; std::memcpy(&v, p, 8);
                        out["value"] = v; out["enc_bytes"] = 8;
                    }
                    return true;
                case 0x0C:  // R4
                    if (avail < 4) return false;
                    {
                        float v = 0.f; std::memcpy(&v, p, 4);
                        out["enc_bytes"] = 4;
                        // JSON spec disallows Infinity / -Infinity / NaN as
                        // numeric literals; nlohmann/json silently coerces
                        // those to null. Emit a `value_kind` tag so consumers
                        // can recover the original float-special-case meaning.
                        if (std::isnan(v)) {
                            out["value"] = nullptr;
                            out["value_kind"] = "nan";
                        } else if (std::isinf(v)) {
                            out["value"] = nullptr;
                            out["value_kind"] = (v > 0.f) ? "infinity" : "-infinity";
                        } else {
                            out["value"] = v;
                            out["value_kind"] = "normal";
                        }
                    }
                    return true;
                case 0x0D:  // R8
                    if (avail < 8) return false;
                    {
                        double v = 0.0; std::memcpy(&v, p, 8);
                        out["enc_bytes"] = 8;
                        if (std::isnan(v)) {
                            out["value"] = nullptr;
                            out["value_kind"] = "nan";
                        } else if (std::isinf(v)) {
                            out["value"] = nullptr;
                            out["value_kind"] = (v > 0.0) ? "infinity" : "-infinity";
                        } else {
                            out["value"] = v;
                            out["value_kind"] = "normal";
                        }
                    }
                    return true;
                case 0x0E:  // String — handled separately via TryDecodeStringDefault
                    return false;
                default:
                    return false;
            }
        };

        const bool regOk = IL2CPP::MetadataRegistration::Find();

        auto fields = mp.EnumerateTypeFields(*td);
        json arr = json::array();
        for (const auto& f : fields) {
            json entry = {
                {"name", f.name},
                {"index", f.index},
                {"type_index", f.typeIndex},
                {"token", f.token},
            };
            if (auto def = mp.FindFieldDefault(f.index)) {
                std::string hex;
                hex.reserve(def->data.size() * 2);
                static const char* kHex = "0123456789ABCDEF";
                std::size_t take = std::min<std::size_t>(def->data.size(), 16);
                for (std::size_t i = 0; i < take; ++i) {
                    hex.push_back(kHex[def->data[i] >> 4]);
                    hex.push_back(kHex[def->data[i] & 0xF]);
                }
                json defaultJson = {
                    {"type_index", def->typeIndex},
                    {"data_index", def->dataIndex},
                    {"raw_hex_first16", hex},
                    {"raw_size", def->data.size()},
                };
                // String decode is metadata-only; doesn't need the runtime types[] table.
                if (auto s = mp.TryDecodeStringDefault(def->dataIndex)) {
                    defaultJson["decoded_string"] = *s;
                }
                // Type-aware numeric / float / bool decode (Phase 5 segment 2d
                // follow-up): lookup the FieldDefault's runtime type tag via
                // MetadataRegistration and decode the blob accordingly.
                if (regOk && !def->data.empty()) {
                    std::uint8_t tag =
                        IL2CPP::MetadataRegistration::GetTypeTag(def->typeIndex);
                    if (tag != 0) {
                        defaultJson["type_tag"] = tag;
                        json decoded;
                        if (decodeByTag(tag, def->data.data(), def->data.size(),
                                        decoded)) {
                            for (auto kv = decoded.begin(); kv != decoded.end();
                                 ++kv) {
                                defaultJson[kv.key()] = kv.value();
                            }
                        }
                    }
                }
                entry["default"] = std::move(defaultJson);
            }
            arr.push_back(std::move(entry));
        }
        return json{
            {"namespace", td->ns},
            {"name", td->name},
            {"type_index", td->index},
            {"flags", td->flags},
            {"token", td->token},
            {"field_start", td->fieldStart},
            {"field_count", td->fieldCount},
            {"registration_found", regOk},
            {"fields", std::move(arr)},
        };
    });

    // Phase 5 segment 3 — getMetadataStats (IL2CPP).
    // Reads from global-metadata.dat directly — faster than the runtime path
    // and includes literal-only types stripped from the runtime field
    // iterator.
    PipeServer::RegisterHandler("getMetadataStats",
        [updateStats, buildStatsJson](const json&) -> json {
        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) return json{{"error", "global-metadata.dat not loaded"}};
        NameStats s;
        std::uint32_t count = mp.TypeDefinitionCount();
        for (std::uint32_t i = 0; i < count; ++i) {
            auto td = mp.GetTypeDefinition(static_cast<std::int32_t>(i));
            if (!td) continue;
            updateStats(s, td->ns, td->name);
        }
        json out = buildStatsJson(s);
        out["scope"] = "metadata_all";
        return out;
    });

    // Phase 5 segment 2d follow-up — diagnostics for the runtime
    // Il2CppMetadataRegistration AOB scan.
    PipeServer::RegisterHandler("il2cppGetMetadataRegistration",
        [formatHex](const json&) -> json {
        const bool ok = IL2CPP::MetadataRegistration::Find();
        json result = {{"found", ok}};
        if (ok) {
            result["address"]     = formatHex(reinterpret_cast<std::uintptr_t>(
                IL2CPP::MetadataRegistration::GetBase()));
            result["types_array"] = formatHex(reinterpret_cast<std::uintptr_t>(
                IL2CPP::MetadataRegistration::TypesArray()));
            result["types_count"] = IL2CPP::MetadataRegistration::TypesCount();
        }
        return result;
    });

    // Phase 5: global-metadata.dat string literal dump.
    PipeServer::RegisterHandler(MagicStrings::CmdIL2CPPGetMetadataStrings,
        [](const json& args) -> json {
        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) {
            return json{{"error", "global-metadata.dat not loaded"}};
        }
        std::uint32_t limit = 0;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = args["limit"].get<std::uint32_t>();
        }
        std::uint32_t offset = 0;
        if (args.contains("offset") && args["offset"].is_number_unsigned()) {
            offset = args["offset"].get<std::uint32_t>();
        }
        std::uint32_t total = mp.StringLiteralCount();
        if (offset >= total) {
            return json{
                {"version", mp.version()},
                {"count", total}, {"offset", offset},
                {"strings", json::array()},
            };
        }
        std::uint32_t available = total - offset;
        std::uint32_t take = (limit == 0) ? available : std::min(limit, available);

        json arr = json::array();
        for (std::uint32_t i = 0; i < take; ++i) {
            if (auto s = mp.GetStringLiteral(offset + i)) arr.push_back(*s);
            else                                          arr.push_back(nullptr);
        }
        return json{
            {"version", mp.version()},
            {"count", total},
            {"offset", offset},
            {"returned", take},
            {"strings", std::move(arr)},
        };
    });

    // Phase 6 segment C — findInstances (IL2CPP).
    // Each Il2CppObject's first qword is its Il2CppClass*. Resolve the class
    // and scan committed RW pages for that pointer.
    PipeServer::RegisterHandler(MagicStrings::CmdFindInstances,
        [resolveClassFromArgs, formatHex, computeArenaExclude, filterMatches](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = std::min<std::size_t>(args["limit"].get<std::size_t>(), 4096);
        }

        // Phase 29h — exclude the Il2CppClass* metadata arena (see
        // computeArenaExclude above). Excluded hits don't count toward `limit`,
        // so the walk continues past the low-address arena to the real
        // instances. Both filters default on; `excludeArena` / `skipMapped`
        // args let a caller A/B against the legacy raw scan.
        const bool excludeArena = args.value("excludeArena", true);
        const bool skipMapped   = args.value("skipMapped", true);
        std::uintptr_t exLow = 0, exHigh = 0;
        computeArenaExclude(excludeArena, exLow, exHigh);

        Memory::ScanStats stats{};
        auto matches = Memory::ScanForPointer(reinterpret_cast<std::uintptr_t>(k),
                                              limit, &stats, exLow, exHigh, skipMapped);
        // Phase 32 — `sane` layer needs this class's managed reference-field
        // offsets (non-static, non-value-type). Mirrors walkInstance's chain walk.
        std::vector<std::uint64_t> refOffsets;
        std::vector<std::uint64_t> boolOffsets;
        if (args.value("sane", false)) {
            const auto& rr = IL2CPP::GetResolver();
            IL2CPP::Il2CppDomain* dom = IL2CPP::GetDomain();
            std::vector<IL2CPP::Il2CppClass*> chain;
            if (dom != nullptr) {
                IL2CPP::ThreadAttachGuard guard(dom);
                if (guard.Attached()) {
                    for (IL2CPP::Il2CppClass* cur = k; cur != nullptr && chain.size() < 16; ) {
                        chain.push_back(cur);
                        if (rr.class_get_parent == nullptr) break;
                        cur = SehGetParent(rr.class_get_parent, cur);
                    }
                }
            }
            if (!chain.empty() && chain.size() < 16) {
                for (auto* c : chain) {
                    IL2CPP::ClassInfoResult res = IL2CPP::ReadClassInfo(c);
                    if (!res.ok) continue;
                    for (const auto& f : res.info.fields) {
                        if (f.flags & 0x10u) continue;   // static
                        // System.Boolean is a value type — capture its offset for
                        // the 0/1 sanity probe BEFORE the value-type skip below.
                        if (f.typeName == "System.Boolean") { boolOffsets.push_back(static_cast<std::uint64_t>(f.offset)); continue; }
                        if (f.isValueType) continue;     // value type (incl IntPtr / struct / enum)
                        refOffsets.push_back(static_cast<std::uint64_t>(f.offset));
                    }
                }
            }
        }
        // Phase 32 — generic post-scan filter (validate / aliveOnly / sane / fieldFilter).
        // expectedHeader = Il2CppClass* (the first qword of a real IL2CPP object).
        json filterStats = filterMatches(matches, reinterpret_cast<std::uintptr_t>(k), args, refOffsets, boolOffsets);
        json arr = json::array();
        for (const void* p : matches) {
            arr.push_back(formatHex(reinterpret_cast<std::uintptr_t>(p)));
        }
        {
            char line[320];
            std::snprintf(line, sizeof(line),
                "findInstances(IL2CPP) class=%p matches=%zu "
                "regions total=%zu scanned=%zu skipImage=%zu skipMapped=%zu skipStack=%zu "
                "bytes=%zu stacks=%zu excluded=%zu exRange=[%p,%p)",
                static_cast<const void*>(k), matches.size(),
                stats.regionsTotal, stats.regionsScanned,
                stats.regionsSkipImage, stats.regionsSkipMapped, stats.regionsSkipStack,
                stats.bytesScanned, stats.threadStacks, stats.excludedMatches,
                reinterpret_cast<const void*>(exLow), reinterpret_cast<const void*>(exHigh));
            Discrete::Log::Write(MagicStrings::LogCategoryScan, line);
        }
        return json{
            {"class_ptr", formatHex(reinterpret_cast<std::uintptr_t>(k))},
            {"count",     arr.size()},
            {"limit",     limit},
            {"matches",   std::move(arr)},
            {"filter",    std::move(filterStats)},
            {"scan_stats", json{
                {"regions_total",       stats.regionsTotal},
                {"regions_scanned",     stats.regionsScanned},
                {"regions_skip_image",  stats.regionsSkipImage},
                {"regions_skip_mapped", stats.regionsSkipMapped},
                {"regions_skip_stack",  stats.regionsSkipStack},
                {"bytes_scanned",       stats.bytesScanned},
                {"excluded_matches",    stats.excludedMatches},
                {"exclude_low",         formatHex(exLow)},
                {"exclude_high",        formatHex(exHigh)},
                {"thread_stacks",       stats.threadStacks},
            }},
        };
    });

    // Phase 21a — listFieldValuesAcrossInstances (IL2CPP). See the Mono
    // handler above for the contract; per-backend differences are:
    //   * heap scan target is the Il2CppClass* (not a VTable).
    //   * `vtable` is not emitted (Mono-specific).
    PipeServer::RegisterHandler(MagicStrings::CmdListFieldValuesAcrossInstances,
        [resolveClassFromArgs, formatHex, decodeField, computeArenaExclude](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        if (!args.contains("fieldOffset") || !args["fieldOffset"].is_number()) {
            return json{{"error", "missing/invalid 'fieldOffset' (int)"}};
        }
        std::int64_t fieldOffset = args["fieldOffset"].get<std::int64_t>();
        if (fieldOffset < 0 || fieldOffset > (1 << 24)) {
            return json{{"error", "'fieldOffset' out of range"}};
        }
        std::string fieldType = args.value("fieldType", std::string{"System.Object"});

        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = std::min<std::size_t>(args["limit"].get<std::size_t>(), 4096);
        }

        // Phase 29h — same metadata-arena exclusion as findInstances; this
        // handler enumerates instances via the identical pointer scan, so it
        // suffered the same arena-residue crowd-out.
        const bool excludeArena = args.value("excludeArena", true);
        const bool skipMapped   = args.value("skipMapped", true);
        std::uintptr_t exLow = 0, exHigh = 0;
        computeArenaExclude(excludeArena, exLow, exHigh);

        Memory::ScanStats stats{};
        auto matches = Memory::ScanForPointer(reinterpret_cast<std::uintptr_t>(k),
                                              limit, &stats, exLow, exHigh, skipMapped);

        json rows = json::array();
        for (const void* p : matches) {
            std::uintptr_t inst = reinterpret_cast<std::uintptr_t>(p);
            std::uintptr_t fieldAddr = inst + static_cast<std::uintptr_t>(fieldOffset);
            json row = {
                {"instance",     formatHex(inst)},
                {"fieldAddress", formatHex(fieldAddr)},
            };
            json decoded = decodeField(fieldType, fieldAddr);
            for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                row[kv.key()] = kv.value();
            }
            rows.push_back(std::move(row));
        }
        return json{
            {"class_ptr",   formatHex(reinterpret_cast<std::uintptr_t>(k))},
            {"count",       rows.size()},
            {"limit",       limit},
            {"fieldOffset", fieldOffset},
            {"fieldType",   fieldType},
            {"rows",        std::move(rows)},
            {"scan_stats", json{
                {"regions_scanned",     stats.regionsScanned},
                {"regions_skip_mapped", stats.regionsSkipMapped},
                {"bytes_scanned",       stats.bytesScanned},
                {"excluded_matches",    stats.excludedMatches},
                {"exclude_low",         formatHex(exLow)},
                {"exclude_high",        formatHex(exHigh)},
            }},
        };
    });

    // Phase 27a — scanFieldValue (IL2CPP). Mirror of the Mono handler above;
    // see that block for full design notes. IL2CPP-specific differences:
    //   * Heap scan target for class-scoped mode is the Il2CppClass* itself
    //     (the first qword of an object header). Mono uses MonoVTable*.
    //   * `vtable` is not emitted (IL2CPP doesn't separate it).
    //   * AOT means JIT-compile is not needed; all field offsets are static.
    PipeServer::RegisterHandler(MagicStrings::CmdScanFieldValue,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::string dtStr = args.value("dataType", std::string{"int32"});

        // Phase 27b-4 — string scan path (IL2CPP). Mirror of the Mono
        // handler's string branch; backend difference is just which
        // WhatIsAt + BuildClassRangeCache we call.
        if (dtStr == "string") {
            std::string encStr   = args.value("encoding",  std::string{"utf16"});
            std::string mmStr    = args.value("matchMode", std::string{"contains"});
            Memory::StringEncoding enc{};
            Memory::StringMatchMode mm{};
            if (!ParseStringEncoding(encStr, enc))     return json{{"error", "unknown 'encoding'"}};
            if (!ParseStringMatchMode(mmStr, mm))      return json{{"error", "unknown 'matchMode'"}};
            if (!args.contains("value") || !args["value"].is_string()) {
                return json{{"error", "string scan requires 'value' (string)"}};
            }
            std::string needleUtf8 = args["value"].get<std::string>();
            auto needleBytes = EncodeNeedleForString(needleUtf8, enc);
            if (needleBytes.empty()) return json{{"error", "needle encoding failed"}};

            Memory::StringScanRequest sreq{};
            sreq.encoding  = enc;
            sreq.matchMode = mm;
            sreq.needle    = std::move(needleBytes);
            sreq.limit     = static_cast<std::size_t>(args.value("limit", 5000));
            if (sreq.limit == 0 || sreq.limit > 50000) sreq.limit = 5000;

            std::size_t maxBackwardScan = static_cast<std::size_t>(args.value("maxBackwardScan", 16384));
            if (maxBackwardScan > 16384) maxBackwardScan = 16384;
            if (maxBackwardScan == 0)    maxBackwardScan = 4096;

            if (!IL2CPP::BuildClassRangeCache()) {
                return json{{"error", "address-finder class cache build failed"}};
            }

            Memory::ValueScanStats sstats{};
            auto candidates = Memory::ScanForString(sreq, &sstats);

            // Phase 30 P2 — one-shot reverse-lookup index instead of a backward
            // scan per candidate. Build under the attach guard (the guard isn't
            // needed for Build's raw walk, but Lookup's field resolution needs
            // it); fall back to WhatIsAt if the index couldn't build.
            IL2CPP::ThreadAttachGuard strGuard(IL2CPP::GetDomain());
            IL2CPP::ReverseLookupIndex strIndex;
            bool strIndexOk = candidates.size() >= kRevIndexMinCandidates && strIndex.Build();

            json rows = json::array();
            std::size_t droppedNonManaged = 0;
            std::string matchedDisplay = DecodeMatchedNeedleToUtf8(sreq.needle, enc);
            for (const auto& c : candidates) {
                std::uintptr_t addr = reinterpret_cast<std::uintptr_t>(c.addr);
                IL2CPP::AddressLookup lookup = strIndexOk
                    ? strIndex.Lookup(addr, maxBackwardScan)
                    : IL2CPP::WhatIsAt(addr, maxBackwardScan);
                if (!lookup.found) { droppedNonManaged++; continue; }
                json row = {
                    {"addr",            formatHex(addr)},
                    {"instAddr",        formatHex(lookup.instance)},
                    {"asm",             lookup.assembly},
                    {"ns",              lookup.ns},
                    {"class",           lookup.className},
                    {"fieldOffset",     static_cast<std::int64_t>(lookup.fieldOffset)},
                    {"currentValue",    matchedDisplay},
                    {"matchedLength",   c.length},
                };
                if (lookup.field.present) {
                    row["field"]     = lookup.field.name;
                    row["fieldType"] = lookup.field.typeName;
                    row["fieldKind"] = lookup.field.kind;
                } else if (lookup.nearestField.present) {
                    row["nearestField"] = json{
                        {"name",  lookup.nearestField.name},
                        {"delta", static_cast<std::int64_t>(lookup.nearestFieldDelta)},
                    };
                    row["fieldKind"] = "near";
                } else {
                    row["fieldKind"] = "interior";
                }
                rows.push_back(std::move(row));
            }
            return json{
                {"ok",                true},
                {"mode",              "global"},
                {"dataType",          "string"},
                {"encoding",          encStr},
                {"matchMode",         mmStr},
                {"count",             rows.size()},
                {"droppedNonManaged", droppedNonManaged},
                {"truncated",         candidates.size() >= sreq.limit},
                {"scannedBytes",      sstats.bytesScanned},
                {"readFailureBytes",  sstats.readFailureBytes},
                {"candidates",        std::move(rows)},
            };
        }

        Memory::ValueDataType dt{};
        if (!ParseValueDataType(dtStr, dt)) {
            return json{{"error", "unknown 'dataType' (try int32/uint32/float/double/...)"}};
        }
        std::string stStr = args.value("scanType", std::string{"exact"});
        Memory::ValueScanType st{};
        if (!ParseValueScanType(stStr, st)) {
            return json{{"error", "unknown 'scanType' (expected exact|range)"}};
        }
        if (!args.contains("value")) return json{{"error", "missing 'value'"}};

        Memory::ValueScanRequest req{};
        req.dataType  = dt;
        req.scanType  = st;
        req.tolerance = args.value("tolerance", 0.0001);
        req.alignment = static_cast<std::size_t>(args.value("alignment", 0));
        req.limit     = static_cast<std::size_t>(args.value("limit", 50000));
        if (req.limit == 0)        req.limit = 50000;
        if (req.limit > 50000)     req.limit = 50000;

        if (!PackJsonValueToBytes(args["value"], dt, req.lo)) {
            return json{{"error", "'value' out of range or not numeric for dataType"}};
        }
        if (st == Memory::ValueScanType::Range) {
            if (!args.contains("valueTo")) {
                return json{{"error", "scanType=range requires 'valueTo'"}};
            }
            if (!PackJsonValueToBytes(args["valueTo"], dt, req.hi)) {
                return json{{"error", "'valueTo' out of range or not numeric for dataType"}};
            }
        }

        std::size_t maxBackwardScan = static_cast<std::size_t>(
            args.value("maxBackwardScan", 16384));
        if (maxBackwardScan > 16384) maxBackwardScan = 16384;
        if (maxBackwardScan == 0)    maxBackwardScan = 4096;

        const bool classScope = args.value("classScope", false);
        const char* expectedClrName = ValueDataTypeClrName(dt);

        // Typed comparator — same body as the Mono path (re-inlined here
        // rather than hoisted because the helper depends on req which we
        // want to keep on the stack).
        auto matchesValue = [&](const std::uint8_t* candidate) -> bool {
            std::uint8_t v[8] = {};
            const std::size_t valueSize = Memory::ValueDataTypeSize(dt);
            std::memcpy(v, candidate, valueSize);
            const bool isRange = (st == Memory::ValueScanType::Range);
            #define CMP_INT(T) do { \
                T cur; std::memcpy(&cur, v, sizeof(T)); \
                T lo;  std::memcpy(&lo, req.lo, sizeof(T)); \
                if (isRange) { T hi; std::memcpy(&hi, req.hi, sizeof(T)); return cur >= lo && cur <= hi; } \
                return cur == lo; \
            } while (0)
            #define CMP_FLT(T) do { \
                T cur; std::memcpy(&cur, v, sizeof(T)); \
                T lo;  std::memcpy(&lo, req.lo, sizeof(T)); \
                if (!std::isfinite(static_cast<double>(cur))) return false; \
                if (isRange) { T hi; std::memcpy(&hi, req.hi, sizeof(T)); return cur >= lo && cur <= hi; } \
                return std::fabs(static_cast<double>(cur) - static_cast<double>(lo)) <= req.tolerance; \
            } while (0)
            switch (dt) {
                case Memory::ValueDataType::Int8:   CMP_INT(std::int8_t);
                case Memory::ValueDataType::UInt8:  CMP_INT(std::uint8_t);
                case Memory::ValueDataType::Int16:  CMP_INT(std::int16_t);
                case Memory::ValueDataType::UInt16: CMP_INT(std::uint16_t);
                case Memory::ValueDataType::Int32:  CMP_INT(std::int32_t);
                case Memory::ValueDataType::UInt32: CMP_INT(std::uint32_t);
                case Memory::ValueDataType::Int64:  CMP_INT(std::int64_t);
                case Memory::ValueDataType::UInt64: CMP_INT(std::uint64_t);
                case Memory::ValueDataType::Float:  CMP_FLT(float);
                case Memory::ValueDataType::Double: CMP_FLT(double);
            }
            #undef CMP_INT
            #undef CMP_FLT
            return false;
        };

        // -------- Class-scoped mode --------
        if (classScope) {
            std::string err;
            IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
            if (k == nullptr) return json{{"error", err}};

            IL2CPP::Il2CppDomain* domain = IL2CPP::GetDomain();
            if (domain == nullptr) return json{{"error", "no il2cpp domain"}};

            const std::size_t instanceLimit = std::min<std::size_t>(req.limit * 4, 4096);
            auto matches = Memory::ScanForPointer(
                reinterpret_cast<std::uintptr_t>(k), instanceLimit, nullptr);

            // ReadClassInfo creates its own ThreadAttachGuard. Nesting an
            // outer guard here triggers the IL2CPP analog of the Phase
            // 20-MT build-#54 BongoCat killer (nested attach corrupts
            // pipe-worker thread state). Don't add one.
            IL2CPP::ClassInfoResult infoResult = IL2CPP::ReadClassInfo(k);
            if (!infoResult.ok) return json{{"error", "ReadClassInfo failed"}};
            const IL2CPP::ClassInfo& info = infoResult.info;
            std::string asmName = args.value("assembly", std::string{});

            // FIELD_ATTRIBUTE_STATIC (ECMA-335) = 0x10.
            constexpr std::uint32_t kFieldAttrStatic = 0x10;
            const std::size_t valueSize = Memory::ValueDataTypeSize(dt);

            json rows = json::array();
            std::size_t fieldsConsidered = 0;
            for (const void* p : matches) {
                if (rows.size() >= req.limit) break;
                std::uintptr_t inst = reinterpret_cast<std::uintptr_t>(p);
                for (const auto& f : info.fields) {
                    if ((f.flags & kFieldAttrStatic) != 0) continue;
                    if (f.typeName != expectedClrName) continue;
                    fieldsConsidered++;
                    std::uintptr_t fieldAddr = inst + static_cast<std::uintptr_t>(f.offset);
                    std::uint8_t   buf[8] = {};
                    if (!Memory::SafeReadBytes(reinterpret_cast<void*>(fieldAddr), buf, valueSize)) {
                        continue;
                    }
                    if (!matchesValue(buf)) continue;

                    json row = {
                        {"addr",          formatHex(fieldAddr)},
                        {"instAddr",      formatHex(inst)},
                        {"asm",           asmName},
                        {"ns",            info.self.ns},
                        {"class",         info.self.name},
                        {"field",         f.name},
                        {"fieldOffset",   static_cast<std::int64_t>(f.offset)},
                        {"fieldType",     f.typeName},
                        {"fieldKind",     "leaf"},
                        {"currentValue",  DecodeValueBytesToJson(buf, dt)},
                    };
                    rows.push_back(std::move(row));
                    if (rows.size() >= req.limit) break;
                }
            }

            return json{
                {"ok",                true},
                {"mode",              "class-scoped"},
                {"dataType",          dtStr},
                {"scanType",          stStr},
                {"count",             rows.size()},
                {"truncated",         rows.size() >= req.limit},
                {"instancesScanned",  matches.size()},
                {"fieldsConsidered",  fieldsConsidered},
                {"droppedNonManaged", 0},
                {"scannedBytes",      static_cast<std::uint64_t>(matches.size()) * valueSize},
                {"candidates",        std::move(rows)},
            };
        }

        // -------- Global mode --------
        if (!IL2CPP::BuildClassRangeCache()) {
            return json{{"error", "address-finder class cache build failed"}};
        }

        // Phase 27 hardening — decoupled raw budget + per-instance cap +
        // compiler-class noise filter. See the Mono scanFieldValue handler
        // above for the full rationale.
        const std::size_t emitLimit = req.limit;
        constexpr std::size_t kRawScanCap = 50000;
        Memory::ValueScanRequest scanReq = req;
        scanReq.limit = kRawScanCap;

        Memory::ValueScanStats stats{};
        auto candidates = Memory::ScanForValue(scanReq, &stats);

        // Phase 27 follow-up — outer ThreadAttachGuard so the
        // per-WhatIsAt inner guards become reentrant no-ops. Fixes the
        // 30s+ shutdown hang from accumulated Il2CppThread refs.
        IL2CPP::ThreadAttachGuard scanGuard(IL2CPP::GetDomain());

        // Phase 30 P2 — build the reverse-lookup index once, then resolve each
        // candidate via O(log N) binary search instead of a per-candidate
        // backward scan. Fall back to WhatIsAt if the index couldn't build or
        // the candidate count is below the amortization threshold.
        IL2CPP::ReverseLookupIndex revIndex;
        bool revIndexOk = candidates.size() >= kRevIndexMinCandidates && revIndex.Build();

        json rows = json::array();
        std::size_t droppedNonManaged = 0, droppedDiversity = 0, droppedNoise = 0;
        std::unordered_map<std::uintptr_t, int> perInstanceCount;
        for (const auto& c : candidates) {
            if (rows.size() >= emitLimit) break;
            std::uintptr_t addr = reinterpret_cast<std::uintptr_t>(c.addr);
            IL2CPP::AddressLookup lookup = revIndexOk
                ? revIndex.Lookup(addr, maxBackwardScan)
                : IL2CPP::WhatIsAt(addr, maxBackwardScan);
            if (!lookup.found) { droppedNonManaged++; continue; }
            if (IsCompilerGeneratedClassName(lookup.className)) { droppedNoise++; continue; }
            if (++perInstanceCount[lookup.instance] > kPerInstanceCap) { droppedDiversity++; continue; }

            json row = {
                {"addr",            formatHex(addr)},
                {"instAddr",        formatHex(lookup.instance)},
                {"asm",             lookup.assembly},
                {"ns",              lookup.ns},
                {"class",           lookup.className},
                {"fieldOffset",     static_cast<std::int64_t>(lookup.fieldOffset)},
                {"currentValue",    DecodeValueBytesToJson(c.value, dt)},
            };
            if (lookup.field.present) {
                row["field"]     = lookup.field.name;
                row["fieldType"] = lookup.field.typeName;
                row["fieldKind"] = lookup.field.kind;
            } else if (lookup.nearestField.present) {
                row["nearestField"] = json{
                    {"name",      lookup.nearestField.name},
                    {"typeName",  lookup.nearestField.typeName},
                    {"delta",     static_cast<std::int64_t>(lookup.nearestFieldDelta)},
                };
                row["fieldKind"] = "near";
            } else {
                row["fieldKind"] = "interior";
            }
            rows.push_back(std::move(row));
        }

        {
            char line[320];
            std::snprintf(line, sizeof(line),
                "scanFieldValue(IL2CPP) dt=%s raw=%zu rows=%zu non=%zu noise=%zu div=%zu bytes=%zu "
                "revIndex=%d idxInst=%zu idxHits=%zu idxLocal=%zu",
                dtStr.c_str(), candidates.size(), rows.size(),
                droppedNonManaged, droppedNoise, droppedDiversity, stats.bytesScanned,
                revIndexOk ? 1 : 0, revIndex.InstanceCount(), revIndex.HitCount(), revIndex.LocalityHits());
            Discrete::Log::Write(MagicStrings::LogCategoryScan, line);
        }

        return json{
            {"ok",                true},
            {"mode",              "global"},
            {"dataType",          dtStr},
            {"scanType",          stStr},
            {"count",             rows.size()},
            {"droppedNonManaged", droppedNonManaged},
            {"droppedNoise",      droppedNoise},
            {"droppedDiversity",  droppedDiversity},
            {"truncated",         candidates.size() >= kRawScanCap || rows.size() >= emitLimit},
            {"scannedBytes",      stats.bytesScanned},
            {"readFailureBytes",  stats.readFailureBytes},
            {"candidates",        std::move(rows)},
            {"scan_stats", json{
                {"regions_total",      stats.regionsTotal},
                {"regions_scanned",    stats.regionsScanned},
                {"regions_skip_image", stats.regionsSkipImage},
                {"regions_skip_stack", stats.regionsSkipStack},
                {"thread_stacks",      stats.threadStacks},
            }},
        };
    });

    // Phase 27b — beginValueScan (IL2CPP). Mirror of the Mono backend
    // beginValueScan; same global-mode-only design, stores candidate
    // metadata resolved by IL2CPP::WhatIsAt into ValueScan::SessionManager
    // so subsequent refineValueScan calls don't have to re-scan memory.
    // refineValueScan + endValueScan are registered in RegisterCoreHandlers
    // (backend-agnostic — they touch only process memory + the session
    // container).
    PipeServer::RegisterHandler(MagicStrings::CmdBeginValueScan,
        [formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::string dtStr = args.value("dataType", std::string{"int32"});
        Memory::ValueDataType dt{};
        if (!ParseValueDataType(dtStr, dt)) {
            return json{{"error", "unknown 'dataType'"}};
        }
        std::string stStr = args.value("scanType", std::string{"exact"});
        Memory::ValueScanType st{};
        if (!ParseValueScanType(stStr, st)) {
            return json{{"error", "scanType must be exact|range for beginValueScan (refine modes go through refineValueScan)"}};
        }
        if (!args.contains("value")) return json{{"error", "missing 'value'"}};

        Memory::ValueScanRequest req{};
        req.dataType  = dt;
        req.scanType  = st;
        req.tolerance = args.value("tolerance", 0.0001);
        req.alignment = static_cast<std::size_t>(args.value("alignment", 0));
        req.limit     = static_cast<std::size_t>(args.value("limit", 50000));
        if (req.limit == 0 || req.limit > 50000) req.limit = 50000;
        if (!PackJsonValueToBytes(args["value"], dt, req.lo)) {
            return json{{"error", "'value' out of range / not numeric for dataType"}};
        }
        if (st == Memory::ValueScanType::Range) {
            if (!args.contains("valueTo")) return json{{"error", "scanType=range requires 'valueTo'"}};
            if (!PackJsonValueToBytes(args["valueTo"], dt, req.hi)) {
                return json{{"error", "'valueTo' out of range / not numeric for dataType"}};
            }
        }
        std::size_t maxBackwardScan = static_cast<std::size_t>(args.value("maxBackwardScan", 16384));
        if (maxBackwardScan > 16384) maxBackwardScan = 16384;
        if (maxBackwardScan == 0)    maxBackwardScan = 4096;

        if (!IL2CPP::BuildClassRangeCache()) {
            return json{{"error", "address-finder class cache build failed"}};
        }

        // Phase 27 hardening — same decoupled-budget + diversity-cap +
        // noise-filter as scanFieldValue.
        const std::size_t emitLimit = req.limit;
        constexpr std::size_t kRawScanCap = 50000;
        Memory::ValueScanRequest scanReq = req;
        scanReq.limit = kRawScanCap;

        Memory::ValueScanStats stats{};
        auto rawCandidates = Memory::ScanForValue(scanReq, &stats);

        // Phase 27 follow-up — outer ThreadAttachGuard for the WhatIsAt
        // batch. See the IL2CPP scanFieldValue handler above.
        IL2CPP::ThreadAttachGuard scanGuard(IL2CPP::GetDomain());

        // Phase 30 P2 — reverse-lookup index (see scanFieldValue above).
        // Gated on candidate count (begin uses rawCandidates).
        // reuseIndexMs > 0 lets the aggregate "any numeric width" fan-out share
        // one index build across its N back-to-back scans. Single scans omit it.
        std::uint32_t reuseIndexMs =
            static_cast<std::uint32_t>(args.value("reuseIndexMs", 0));
        IL2CPP::ReverseLookupIndex revIndex;
        bool revIndexOk = rawCandidates.size() >= kRevIndexMinCandidates && revIndex.Build(reuseIndexMs);

        std::vector<ValueScan::Candidate> sessionCands;
        sessionCands.reserve(std::min<std::size_t>(emitLimit, 4096));
        std::size_t droppedNonManaged = 0, droppedDiversity = 0, droppedNoise = 0;
        std::unordered_map<std::uintptr_t, int> perInstanceCount;
        const std::size_t vSize = Memory::ValueDataTypeSize(dt);
        for (const auto& c : rawCandidates) {
            if (sessionCands.size() >= emitLimit) break;
            std::uintptr_t addr = reinterpret_cast<std::uintptr_t>(c.addr);
            IL2CPP::AddressLookup lookup = revIndexOk
                ? revIndex.Lookup(addr, maxBackwardScan)
                : IL2CPP::WhatIsAt(addr, maxBackwardScan);
            if (!lookup.found) { droppedNonManaged++; continue; }
            if (IsCompilerGeneratedClassName(lookup.className)) { droppedNoise++; continue; }
            if (++perInstanceCount[lookup.instance] > kPerInstanceCap) { droppedDiversity++; continue; }

            ValueScan::Candidate sc{};
            sc.addr        = addr;
            sc.instAddr    = lookup.instance;
            sc.fieldOffset = static_cast<std::int64_t>(lookup.fieldOffset);
            sc.asmName     = lookup.assembly;
            sc.ns          = lookup.ns;
            sc.className   = lookup.className;
            if (lookup.field.present) {
                sc.field     = lookup.field.name;
                sc.fieldType = lookup.field.typeName;
                sc.fieldKind = lookup.field.kind;
            } else if (lookup.nearestField.present) {
                sc.fieldKind         = "near";
                sc.nearestFieldName  = lookup.nearestField.name;
                sc.nearestFieldDelta = static_cast<std::int64_t>(lookup.nearestFieldDelta);
            } else {
                sc.fieldKind = "interior";
            }
            std::memcpy(sc.prevValue, c.value, vSize);
            sessionCands.push_back(std::move(sc));
        }

        std::uint64_t sessionId = ValueScan::SessionManager::Instance().Begin(dt, std::move(sessionCands));

        json rows = json::array();
        std::size_t kept = 0;
        auto fmtHex = std::function<std::string(std::uintptr_t)>(formatHex);
        ValueScan::SessionManager::Instance().ViewWith(sessionId,
            [&](Memory::ValueDataType d, const std::vector<ValueScan::Candidate>& cands) {
            kept = cands.size();
            for (const auto& sc : cands) rows.push_back(EmitCandidateRow(sc, d, fmtHex));
        });

        {
            char line[256];
            std::snprintf(line, sizeof(line),
                "beginValueScan(IL2CPP) sess=%llu dt=%s raw=%zu kept=%zu non=%zu noise=%zu div=%zu bytes=%zu",
                static_cast<unsigned long long>(sessionId), dtStr.c_str(),
                rawCandidates.size(), kept,
                droppedNonManaged, droppedNoise, droppedDiversity, stats.bytesScanned);
            Discrete::Log::Write(MagicStrings::LogCategoryScan, line);
        }

        return json{
            {"ok",                true},
            {"sessionId",         std::to_string(sessionId)},
            {"mode",              "global"},
            {"dataType",          dtStr},
            {"scanType",          stStr},
            {"count",             kept},
            {"totalCandidates",   kept},
            {"droppedNonManaged", droppedNonManaged},
            {"droppedNoise",      droppedNoise},
            {"droppedDiversity",  droppedDiversity},
            {"truncated",         rawCandidates.size() >= kRawScanCap || kept >= emitLimit},
            {"scannedBytes",      stats.bytesScanned},
            {"readFailureBytes",  stats.readFailureBytes},
            {"candidates",        std::move(rows)},
        };
    });

    // Phase 6 segment B — walkInstance (IL2CPP). Same shape as the Mono handler
    // above; the .NET object layout (16-byte header, then fields by offset) is
    // shared between both backends.
    PipeServer::RegisterHandler(MagicStrings::CmdWalkInstance,
        [parseAddr, formatHex, decodeField, resolveClassFromArgs](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        // Optional cap on emitted Dictionary<K,V> key/value pairs (default 256).
        std::size_t dictPairCap = kDictDefaultPairCap;
        if (args.contains("dictLimit") && args["dictLimit"].is_number()) {
            const long long dl = args["dictLimit"].get<long long>();
            if (dl > 0) dictPairCap = static_cast<std::size_t>(dl);
        }
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        // Step trace (each Write flushes — survives a CTD) so a crash while
        // drilling into a nested field pinpoints the exact step / field.
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "walkInstance(IL2CPP): addr=" + formatHex(addr) +
            " header-klass=" + formatHex(reinterpret_cast<std::uintptr_t>(k)));

        const auto& r = IL2CPP::GetResolver();
        IL2CPP::Il2CppDomain* domain = IL2CPP::GetDomain();
        if (domain == nullptr) return json{{"error", "no il2cpp domain"}};

        // Phase 31-6 (a) — value-type struct walk: subtract the object header
        // for inline structs (member offsets are header-inclusive, but there's
        // no header at `addr`). Same convention as Mono (confirmed there via
        // live-smoke); IL2CPP field offsets are likewise object-relative incl.
        // the Il2CppObject header. Reference-type walks: no adjustment.
        // (IL2CPP path still wants its own live-smoke to confirm — gated on
        // class_is_valuetype so reference walks are never affected.)
        const long structHeaderAdjust =
            (r.class_is_valuetype != nullptr && r.class_is_valuetype(k) != 0)
                ? static_cast<long>(ManagedLayout::kObjectHeader) : 0;

        std::vector<IL2CPP::Il2CppClass*> chain;
        {
            IL2CPP::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "il2cpp_thread_attach failed"}};
            for (IL2CPP::Il2CppClass* cur = k; cur != nullptr && chain.size() < 16; ) {
                chain.push_back(cur);
                if (r.class_get_parent == nullptr) break;
                cur = SehGetParent(r.class_get_parent, cur);
            }
        }
        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "walkInstance: chain depth=" + std::to_string(chain.size()));

        // A real managed class's inheritance chain terminates at System.Object
        // within a handful of levels (true even for generic instantiations).
        // Hitting the 16-level cap means class_get_parent kept returning
        // non-null GARBAGE — i.e. the header didn't point to a real class (a
        // value-type field misread as a reference, or a stale pointer). Don't
        // ReadClassInfo those garbage class pointers: even with the per-field
        // SEH guards that's reading nonsense, and it's never useful. Bail.
        if (chain.size() >= 16) {
            Discrete::Log::Write(MagicStrings::LogCategoryWalk,
                "walkInstance: runaway inheritance chain (>=16) — not a managed object, bailing");
            return json{{"error",
                "address does not resolve to a managed object "
                "(runaway class chain — likely a value-type field or stale pointer)"}};
        }

        // Phase 31-6 — process-lifetime ClassInfo cache for the walk path
        // (see the Mono handler for the rationale: avoids re-running
        // ReadClassInfo's field/method enumeration for recurring classes during
        // deep drilldown). Mutex for concurrent interactive/bulk pipes.
        static std::unordered_map<IL2CPP::Il2CppClass*, IL2CPP::ClassInfoResult> s_walkClassCache;
        static std::mutex s_walkClassCacheMtx;

        json fields = json::array();
        std::string selfNs, selfName;
        for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
            IL2CPP::ClassInfoResult res;
            bool cached = false;
            {
                std::lock_guard<std::mutex> lk(s_walkClassCacheMtx);
                auto cit = s_walkClassCache.find(*it);
                if (cit != s_walkClassCache.end()) { res = cit->second; cached = true; }
            }
            if (!cached) {
                Discrete::Log::Write(MagicStrings::LogCategoryWalk,
                    "walkInstance:  ReadClassInfo class=" +
                    formatHex(reinterpret_cast<std::uintptr_t>(*it)));
                res = IL2CPP::ReadClassInfo(*it);
                Discrete::Log::Write(MagicStrings::LogCategoryWalk,
                    "walkInstance:  ReadClassInfo done ok=" + std::to_string(res.ok ? 1 : 0) +
                    " fields=" + std::to_string(res.ok ? res.info.fields.size() : std::size_t(0)) +
                    " name=" + (res.ok ? res.info.self.name : std::string("?")));
                if (res.ok) {
                    std::lock_guard<std::mutex> lk(s_walkClassCacheMtx);
                    s_walkClassCache.emplace(*it, res);
                }
            }
            if (!res.ok) continue;
            const bool isSelf = (*it == k);
            if (isSelf) { selfNs = res.info.self.ns; selfName = res.info.self.name; }
            for (const auto& f : res.info.fields) {
                if (f.flags & 0x10u) continue;
                // Struct-relative offset for value-type walks (Phase 31-6 a).
                const long relOffset = static_cast<long>(f.offset) - structHeaderAdjust;
                std::uintptr_t fieldAddr = addr + static_cast<std::uintptr_t>(relOffset);
                json entry = {
                    {"name",    f.name},
                    {"type",    f.typeName},
                    {"offset",  relOffset},
                    {"from",    res.info.self.name},
                    {"address", formatHex(fieldAddr)},
                };
                if (f.isEnum)      entry["is_enum"]       = true;
                if (f.isValueType) entry["is_value_type"] = true;
                if (!f.underlyingTypeName.empty())
                    entry["underlying_type"] = f.underlyingTypeName;
                const std::string& decodeType = (f.isEnum && !f.underlyingTypeName.empty())
                    ? f.underlyingTypeName : f.typeName;
                json decoded = decodeField(decodeType, fieldAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                    entry[kv.key()] = kv.value();
                }
                // Phase 31-2 (Phase B) — inline generic value-type struct fan-out
                // (see the Mono walkInstance handler). Always-on interactive walk.
                if (ShouldExpandValueStruct(f.isValueType, f.isEnum,
                                            f.valueTypeClass, f.typeName, entry)) {
                    json sub = CaptureIl2cppStructFields(decodeField, f.valueTypeClass,
                        fieldAddr, kStructMaxDepth, kStructDefaultMemberCap);
                    if (sub.contains("fields")) {
                        entry.erase("ptr");
                        entry["fields"] = std::move(sub["fields"]);
                    } else if (sub.contains("struct_error")) {
                        entry["struct_error"] = sub["struct_error"];
                    }
                }
                // Dictionary<K,V> — emit key→value pairs as child fields.
                else if (IsDictionaryType(f.typeName) && entry.contains("ptr")) {
                    std::uintptr_t dptr = 0;
                    if (Memory::SafeReadBytes(reinterpret_cast<void*>(fieldAddr), &dptr,
                                              sizeof(dptr)) && dptr != 0) {
                        json d = CaptureIl2cppDictionary(decodeField, dptr,
                                                         kStructMaxDepth, dictPairCap);
                        if (d.contains("fields")) {
                            entry.erase("ptr");
                            entry["dict"]                = true;
                            entry["dict_count"]          = d.value("dict_count", 0);
                            entry["dict_entries_offset"] = d.value("dict_entries_offset", 0);
                            entry["dict_data_offset"]    = d.value("dict_data_offset", 0);
                            entry["dict_stride"]         = d.value("dict_stride", 0);
                            if (d.value("dict_truncated", false)) entry["dict_truncated"] = true;
                            entry["fields"] = std::move(d["fields"]);
                        } else if (d.contains("dict_error")) {
                            entry["dict_error"] = d["dict_error"];
                        }
                    }
                }
                fields.push_back(std::move(entry));
                if (fields.size() >= 4096) break;
            }
            if (fields.size() >= 4096) break;
        }

        Discrete::Log::Write(MagicStrings::LogCategoryWalk,
            "walkInstance: all fields decoded, count=" + std::to_string(fields.size()));

        // Sort by offset — see Mono walkInstance for rationale.
        std::sort(fields.begin(), fields.end(),
                  [](const json& a, const json& b) {
                      return a.value("offset", 0) < b.value("offset", 0);
                  });

        return json{
            {"address",      formatHex(addr)},
            {"namespace",    selfNs},
            {"name",         selfName},
            {"depth",        chain.size()},
            {"count",        fields.size()},
            // Authoritative game pointer size (IL2CPP is x64-only today, so
            // this is 8 — but emit it for symmetry with the Mono handler so
            // the exporter has a single source of truth).
            {"pointer_size", static_cast<int>(Discrete::ManagedLayout::kPointerSize)},
            {"fields",       std::move(fields)},
        };
    });

    // Phase 5 segment 2d + Phase 6 segment D — getEnumValues (IL2CPP).
    // Type-aware decode: detects the enum's underlying integer width from
    // the runtime `value__` field, then decodes each FieldDefault blob using
    // the matching IL2CPP encoding rules (verified against Unity's IL2CPP
    // FieldDefaultValueWriter — encoded form depends on type):
    //   I1/U1/I2/U2/I4/U4 → ECMA-335 §II.23.2 compressed signed int, then
    //                       sign-extended / clamped to the target width.
    //   I8/U8             → 8 bytes raw little-endian.
    //   R4 / R8           → 4 / 8 bytes raw little-endian (float / double).
    //   Boolean           → 1 byte (0 / 1).
    //   Char              → 2 bytes raw little-endian (UTF-16 code unit).
    // Full Il2CppMetadataRegistration AOB scan (so non-enum const literals
    // can also be type-aware) is still pending — see plan.md.
    PipeServer::RegisterHandler(MagicStrings::CmdGetEnumValues,
        [resolveClassFromArgs](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto info = IL2CPP::ReadClassInfo(k);
        if (!info.ok) return json{{"error", "ReadClassInfo failed"}};
        const bool isEnum = (info.info.parent.name == "Enum");
        if (!isEnum) return json{{"error", "class is not an enum"},
                                 {"parent", info.info.parent.name}};

        // Find the storage field `value__` — its CLR type is the enum's
        // underlying integer type. Default to System.Int32 if missing
        // (ECMA-335 §I.8.5.2 says int32 is the default underlying type).
        std::string underlying = "System.Int32";
        for (const auto& f : info.info.fields) {
            if (f.name == "value__") { underlying = f.typeName; break; }
        }

        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) return json{{"error", "global-metadata.dat not loaded"}};
        std::string ns   = args.value("namespace", std::string{});
        std::string name = args.value("name", std::string{});
        auto td = mp.FindTypeByName(ns, name);
        if (!td) return json{{"error", "type not found in metadata: " + ns + "." + name}};

        // Decode one ECMA-335 §II.23.2 compressed signed int. Used for I1..U4
        // FieldDefaults (IL2CPP packs all integer literals ≤32-bit this way).
        auto decodeCompressedSigned = [](const std::uint8_t* p, std::size_t avail,
                                          std::int64_t& outValue,
                                          std::size_t&  outBytes) -> bool {
            if (avail < 1) return false;
            std::uint8_t b0 = p[0];
            std::uint32_t enc = 0;
            std::size_t   width = 0;
            std::int64_t  bias = 0;
            if ((b0 & 0x80u) == 0u) {
                enc = b0; width = 1; bias = 0x40;
            } else if ((b0 & 0xC0u) == 0x80u) {
                if (avail < 2) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x3Fu) << 8) | p[1];
                width = 2; bias = 0x2000;
            } else if ((b0 & 0xE0u) == 0xC0u) {
                if (avail < 4) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x1Fu) << 24)
                    | (static_cast<std::uint32_t>(p[1])       << 16)
                    | (static_cast<std::uint32_t>(p[2])       <<  8)
                    |  static_cast<std::uint32_t>(p[3]);
                width = 4; bias = 0x10000000;
            } else {
                return false;  // 0xE0–0xFF reserved
            }
            std::int64_t magnitude = static_cast<std::int64_t>(enc >> 1);
            outValue = (enc & 1u) ? (magnitude - bias) : magnitude;
            outBytes = width;
            return true;
        };

        // Type-aware decode: takes the underlying enum type name and a
        // FieldDefault blob, fills `entry` with `value` (typed) +
        // `enc_bytes`. Returns false on shape mismatch.
        auto decodeTyped = [&decodeCompressedSigned]
            (const std::string& typeName, const std::uint8_t* p, std::size_t avail,
             json& entry) -> bool {
            auto compressed = [&](auto&& signCast, auto&& finalCast) -> bool {
                std::int64_t raw = 0; std::size_t w = 0;
                if (!decodeCompressedSigned(p, avail, raw, w)) return false;
                entry["value"]     = finalCast(signCast(raw));
                entry["enc_bytes"] = w;
                return true;
            };
            if (typeName == "System.SByte")
                return compressed([](std::int64_t x){ return static_cast<std::int8_t>(x);  },
                                  [](std::int8_t x){ return static_cast<std::int64_t>(x); });
            if (typeName == "System.Byte")
                return compressed([](std::int64_t x){ return static_cast<std::uint8_t>(x); },
                                  [](std::uint8_t x){ return static_cast<std::uint64_t>(x); });
            if (typeName == "System.Int16")
                return compressed([](std::int64_t x){ return static_cast<std::int16_t>(x); },
                                  [](std::int16_t x){ return static_cast<std::int64_t>(x); });
            if (typeName == "System.UInt16")
                return compressed([](std::int64_t x){ return static_cast<std::uint16_t>(x); },
                                  [](std::uint16_t x){ return static_cast<std::uint64_t>(x); });
            if (typeName == "System.Int32")
                return compressed([](std::int64_t x){ return static_cast<std::int32_t>(x); },
                                  [](std::int32_t x){ return static_cast<std::int64_t>(x); });
            if (typeName == "System.UInt32")
                return compressed([](std::int64_t x){ return static_cast<std::uint32_t>(x); },
                                  [](std::uint32_t x){ return static_cast<std::uint64_t>(x); });
            if (typeName == "System.Int64") {
                if (avail < 8) return false;
                std::int64_t v = 0; std::memcpy(&v, p, 8);
                entry["value"] = v; entry["enc_bytes"] = 8; return true;
            }
            if (typeName == "System.UInt64") {
                if (avail < 8) return false;
                std::uint64_t v = 0; std::memcpy(&v, p, 8);
                entry["value"] = v; entry["enc_bytes"] = 8; return true;
            }
            if (typeName == "System.Single") {
                if (avail < 4) return false;
                float v = 0.f; std::memcpy(&v, p, 4);
                entry["value"] = v; entry["enc_bytes"] = 4; return true;
            }
            if (typeName == "System.Double") {
                if (avail < 8) return false;
                double v = 0.0; std::memcpy(&v, p, 8);
                entry["value"] = v; entry["enc_bytes"] = 8; return true;
            }
            if (typeName == "System.Boolean") {
                if (avail < 1) return false;
                entry["value"] = (p[0] != 0); entry["enc_bytes"] = 1; return true;
            }
            if (typeName == "System.Char") {
                if (avail < 2) return false;
                std::uint16_t v = 0; std::memcpy(&v, p, 2);
                entry["value"] = v; entry["enc_bytes"] = 2; return true;
            }
            return false;
        };

        json values = json::array();
        auto fields = mp.EnumerateTypeFields(*td);
        static const char* kHex = "0123456789ABCDEF";
        for (const auto& f : fields) {
            // Enums have one instance field "value__" plus a literal per member.
            // Skip the storage field; its FieldDefault is absent anyway.
            if (f.name == "value__") continue;
            auto def = mp.FindFieldDefault(f.index);
            if (!def) continue;
            json entry = {
                {"name", f.name},
                {"field_index", f.index},
            };
            if (def->data.empty() ||
                !decodeTyped(underlying, def->data.data(), def->data.size(), entry)) {
                entry["decode_error"] = "type-aware decode failed for " + underlying;
            }
            std::string hex;
            std::size_t take = std::min<std::size_t>(def->data.size(), 8);
            for (std::size_t i = 0; i < take; ++i) {
                hex.push_back(kHex[def->data[i] >> 4]);
                hex.push_back(kHex[def->data[i] & 0xF]);
            }
            entry["raw_hex"] = hex;
            values.push_back(std::move(entry));
        }
        return json{
            {"namespace",       td->ns},
            {"name",            td->name},
            {"underlying_type", underlying},
            {"count",           values.size()},
            {"values",          std::move(values)},
        };
    });

    // Phase 17a — address finder (IL2CPP); 17b adds field-offset resolution.
    // Backward 8-byte stride scan; matches Il2CppClass* candidates against a
    // cache built lazily on first call. Cache is one full
    // EnumerateAssemblies → EnumerateClasses pass through every image; ~tens
    // of ms on first hit, sub-ms on subsequent ones. The 17b pass walks the
    // parent chain inside the same attach scope and resolves the queried byte
    // offset back to a declared field (or the nearest one below it).
    PipeServer::RegisterHandler(MagicStrings::CmdWhatIsAt,
        [parseAddr, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::size_t maxBackward = 4096;
        if (args.contains("maxBackwardScan") && args["maxBackwardScan"].is_number_unsigned()) {
            maxBackward = args["maxBackwardScan"].get<std::size_t>();
        }
        auto hit = IL2CPP::WhatIsAt(addr, maxBackward);
        if (!hit.found) {
            return json{
                {"found",   false},
                {"reason",  hit.reason},
                {"address", formatHex(hit.queriedAddr)},
                {"scannedRange", json{
                    {"from", formatHex(hit.scannedFrom)},
                    {"to",   formatHex(hit.scannedTo)},
                }},
                {"scannedSteps", hit.scannedSteps},
            };
        }
        // Phase 17b — render FieldHit (shared shape across backends).
        auto fieldToJson = [](const IL2CPP::FieldHit& f) -> json {
            json out = {
                {"name",         f.name},
                {"type",         f.typeName},
                {"offset",       f.offset},
                {"size",         f.size},
                {"isStatic",     f.isStatic},
                {"isValueType",  f.isValueType},
                {"isEnum",       f.isEnum},
                {"declaredIn",   f.declaredIn},
                {"kind",         f.kind},
            };
            if (!f.underlyingTypeName.empty())
                out["underlying_type"] = f.underlyingTypeName;
            if (f.kind == "interior")
                out["interiorOffset"] = static_cast<std::int64_t>(f.interiorOffset);
            return out;
        };
        json result = {
            {"found",       true},
            {"address",     formatHex(hit.queriedAddr)},
            {"instance",    formatHex(hit.instance)},
            {"class_ptr",   formatHex(hit.klassPtr)},
            {"assembly",    hit.assembly},
            {"namespace",   hit.ns},
            {"class",       hit.className},
            {"fieldOffset", static_cast<std::int64_t>(hit.fieldOffset)},
            {"scannedRange", json{
                {"from", formatHex(hit.scannedFrom)},
                {"to",   formatHex(hit.scannedTo)},
            }},
            {"scannedSteps", hit.scannedSteps},
        };
        result["field"]        = hit.field.present        ? fieldToJson(hit.field)        : json(nullptr);
        result["nearestField"] = hit.nearestField.present ? fieldToJson(hit.nearestField) : json(nullptr);
        if (hit.nearestField.present) {
            result["nearestFieldDelta"] = static_cast<std::int64_t>(hit.nearestFieldDelta);
        }
        return result;
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetClassRangeStats,
        [formatHex](const json& args) -> json {
        if (args.value("rebuild", false)) IL2CPP::InvalidateClassRangeCache();
        IL2CPP::BuildClassRangeCache();
        auto s = IL2CPP::GetClassRangeStats();
        return json{
            {"backend",     "IL2CPP"},
            {"built",       s.built},
            {"class_count", s.classCount},
            {"min_klass",   formatHex(s.minKlass)},
            {"max_klass",   formatHex(s.maxKlass)},
        };
    });

    // Phase 18 — find method instances (IL2CPP). Returns every overload of
    // `method` on the supplied class, with the AOT native address read out
    // of MethodInfo->methodPointer (or il2cpp_method_get_pointer where
    // bound). No JIT step — IL2CPP is AOT.
    PipeServer::RegisterHandler(MagicStrings::CmdFindMethodInstances,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch = args.value("signatureMatch", std::string{});
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto fr = IL2CPP::FindMethodInstances(k, methodName, sigMatch);
        if (!fr.ok) return json{{"error", fr.error}};

        json matches = json::array();
        for (const auto& ov : fr.matches) {
            json names = json::array();
            for (const auto& s : ov.paramNames) names.push_back(s);
            json types = json::array();
            for (const auto& s : ov.paramTypes) types.push_back(s);
            matches.push_back({
                {"name",          ov.name},
                {"token",         ov.token},
                {"flags",         ov.flags},
                {"iflags",        ov.iflags},
                {"isStatic",      ov.isStatic},
                {"isVirtual",     ov.isVirtual},
                {"isAbstract",    ov.isAbstract},
                {"compiled",      ov.compiled},
                {"address",       formatHex(reinterpret_cast<std::uintptr_t>(ov.address))},
                {"paramTypes",    std::move(types)},
                {"paramNames",    std::move(names)},
                {"returnType",    ov.returnType},
                {"declaringClass",     ov.declaringClass},
                {"declaringNamespace", ov.declaringNamespace},
                {"signature",     ov.signatureString},
                {"humanSuffix",   ov.humanSuffix},
            });
        }
        return json{
            {"method",  methodName},
            {"count",   matches.size()},
            {"matches", std::move(matches)},
        };
    });

    // Phase 19a — callMethod (IL2CPP, static-only). Parses typed args from
    // the JSON request, marshals into IL2CPP's void** params, invokes
    // il2cpp_runtime_invoke under SEH, captures managed exceptions, and
    // unboxes the return value per the resolved method's formal returnType.
    // 19c will lift the static-only / no-string restrictions; 19a stays
    // tight so the wire shape is locked in before instance dispatch lands.
    PipeServer::RegisterHandler(MagicStrings::CmdCallMethod,
        [resolveClassFromArgs, parseAddr, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch   = args.value("signatureMatch", std::string{});

        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        // 19a: instance must be absent / null. Parse for forward-compat; the
        // invoker rejects non-zero with a distinct error code.
        std::uintptr_t instance = 0;
        if (args.contains("instance") && !args["instance"].is_null()) {
            if (!parseAddr(args["instance"], instance)) {
                return json{{"error", "invalid 'instance' (hex string or integer)"}};
            }
        }

        // Decode each {type, value} into a typed CallArg. Numeric values may
        // arrive as JSON int / uint / float / hex-string ("0xCAFEBABE") for
        // address args. Enforce the wire `type` tags listed in
        // docs/lua-invoke.md — unknown tags get rejected so a typo doesn't
        // silently coerce to null.
        using IL2CPP::CallArg;
        using IL2CPP::CallArgKind;
        std::vector<CallArg> typedArgs;

        const json& argsArr = args.contains("args") && args["args"].is_array()
                                ? args["args"] : json::array();
        typedArgs.reserve(argsArr.size());
        for (std::size_t i = 0; i < argsArr.size(); ++i) {
            const json& a = argsArr[i];
            if (!a.is_object()) {
                return json{{"error", std::string{"args["} + std::to_string(i) + "] not an object"}};
            }
            std::string ty = a.value("type", std::string{});
            CallArg out;

            if (ty == "null") {
                out.kind = CallArgKind::Null;
            } else if (ty == "bool" || ty == "boolean") {
                out.kind = CallArgKind::Bool;
                if (a.contains("value")) {
                    if      (a["value"].is_boolean())          out.i64 = a["value"].get<bool>() ? 1 : 0;
                    else if (a["value"].is_number_integer())    out.i64 = a["value"].get<std::int64_t>();
                    else if (a["value"].is_number_unsigned())   out.u64 = a["value"].get<std::uint64_t>();
                }
            } else if (ty == "byte"   || ty == "uint8")  { out.kind = CallArgKind::Byte;   out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "sbyte"  || ty == "int8")   { out.kind = CallArgKind::SByte;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "int16"  || ty == "short")  { out.kind = CallArgKind::Int16;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint16" || ty == "ushort") { out.kind = CallArgKind::UInt16; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int32"  || ty == "int")    { out.kind = CallArgKind::Int32;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint32" || ty == "uint")   { out.kind = CallArgKind::UInt32; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int64"  || ty == "long")   { out.kind = CallArgKind::Int64;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint64" || ty == "ulong")  { out.kind = CallArgKind::UInt64; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "float"  || ty == "single") { out.kind = CallArgKind::Float;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "double") { out.kind = CallArgKind::Double;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "char")   { out.kind = CallArgKind::Char;
                  if (a.contains("value")) {
                      if      (a["value"].is_string() && !a["value"].get<std::string>().empty())
                                                          out.u64 = static_cast<std::uint8_t>(a["value"].get<std::string>()[0]);
                      else if (a["value"].is_number())     out.u64 = a["value"].get<std::uint64_t>();
                  }
              }
              else if (ty == "string") {
                  out.kind = CallArgKind::String;          // rejected by invoker in 19a
                  out.s = a.value("value", std::string{});
              }
              else if (ty == "address" || ty == "intptr" || ty == "uintptr" || ty == "ptr") {
                  out.kind = CallArgKind::Address;
                  if (a.contains("value")) {
                      std::uintptr_t addr = 0;
                      if (parseAddr(a["value"], addr)) {
                          out.u64 = addr;
                      } else {
                          return json{{"error", std::string{"args["} + std::to_string(i) + "].value not a valid address"}};
                      }
                  }
              }
              else {
                  return json{{"error", std::string{"args["} + std::to_string(i) + "] unknown type '" + ty + "'"}};
              }
            typedArgs.push_back(std::move(out));
        }

        // Phase 20-MT: optional main-thread marshaling — mirror of the Mono
        // handler above. See that branch for the full comment.
        const bool mainThread = args.value("main_thread", false);
        const std::int32_t mtTimeoutMs = args.value("main_thread_timeout_ms",
                                                    std::int32_t{5000});

        IL2CPP::CallMethodResult cr;
        std::string dispatchPath = "worker_direct";
        if (mainThread) {
            using namespace Discrete::MainThreadDispatcher;
            if (!IsInitialized()) {
                (void)Init();
                if (IL2CPP::IsResolved() && !IL2CPP::MainThreadHook::IsInstalled()) {
                    (void)IL2CPP::MainThreadHook::Install();
                }
            }
            DispatchResult disp = SubmitAndWait(
                [&]() { cr = IL2CPP::CallMethod(k, methodName, sigMatch, instance, typedArgs); },
                mtTimeoutMs);
            dispatchPath = disp.diagnostic_path;
            if (!disp.ok) {
                json result;
                result["ok"]    = false;
                result["error"] = disp.error;
                result["dispatch"] = json{{"main_thread", true}, {"path", disp.diagnostic_path},
                                          {"timed_out",   disp.timed_out},
                                          {"rejected",    disp.rejected}};
                return result;
            }
        } else {
            cr = IL2CPP::CallMethod(k, methodName, sigMatch, instance, typedArgs);
        }

        // Render the typed return into a JSON-friendly value. The discriminator
        // chooses how returnValue is encoded — null for Void / Unsupported,
        // numeric for ints/floats, hex string for addresses.
        json returnValue = nullptr;
        switch (cr.ret.kind) {
        case IL2CPP::CallReturn::Kind::Void:        returnValue = nullptr; break;
        case IL2CPP::CallReturn::Kind::SignedInt:   returnValue = cr.ret.i64; break;
        case IL2CPP::CallReturn::Kind::UnsignedInt: returnValue = cr.ret.u64; break;
        case IL2CPP::CallReturn::Kind::Float:       returnValue = cr.ret.f; break;
        case IL2CPP::CallReturn::Kind::Address:     returnValue = formatHex(cr.ret.u64); break;
        case IL2CPP::CallReturn::Kind::String:      returnValue = cr.ret.s; break;
        case IL2CPP::CallReturn::Kind::Unsupported: returnValue = nullptr; break;
        }

        json result;
        if (!cr.error.empty()) {
            result["ok"]    = false;
            result["error"] = cr.error;
            return result;
        }
        result["ok"]          = cr.ok;
        result["returnType"]  = cr.ret.typeName;
        result["returnValue"] = std::move(returnValue);
        result["exception"]   = cr.exception.empty() ? json(nullptr) : json(cr.exception);
        result["elapsedMs"]   = cr.elapsedMs;
        result["method"] = json{
            {"address",     formatHex(reinterpret_cast<std::uintptr_t>(cr.addressCalled))},
            {"signature",   cr.methodSignature},
            {"humanSuffix", cr.humanSuffix},
        };
        result["dispatch"] = json{{"main_thread", mainThread}, {"path", std::move(dispatchPath)}};
        if (cr.ret.kind == IL2CPP::CallReturn::Kind::Unsupported) {
            // Tell the caller why returnValue is null even though the call
            // succeeded. 19a doesn't decode multi-field value-type returns.
            result["note"] = "value-type-by-value returns are not decoded in 19a; pass an out-buffer in v2 or read the address";
        }
        return result;
    });
#endif
}

}  // namespace

extern "C" {

int Discrete_Init() {
    bool expected = false;
    if (!g_initialized.compare_exchange_strong(expected, true)) return 0;
    Discrete::Log::Init();
    Discrete::Log::Init("ExportAPI: Discrete_Init");
    RegisterCoreHandlers();

    // Build #547 follow-up — disconnect-time GC drain.
    // Long-running batch RPCs (Phase 28 findFieldReferences walking 2000+
    // method headers, Phase 27 value scans, Phase 25 multi-asm searchSymbols)
    // leave Mono / IL2CPP metadata-cache + finalizer-queue entries behind.
    // Each entry is individually small, but BDWGC processes the resulting
    // queue at process shutdown rather than incrementally — observed live:
    // BongoCat post-findFieldReferences game-shutdown latency tens of
    // seconds (user reported 2026-05-25). Draining at the disconnect
    // boundary (after EVERY client disconnects, while DiscreteUI / the
    // bridge is still attached) keeps the user-perceptible game shutdown
    // back at ~1s.
    //
    // The hook runs on the per-client worker thread that's about to exit,
    // AFTER ::DisconnectNamedPipe + ::CloseHandle — so the pipe slot is
    // already returned to the OS and the next client can connect
    // concurrently with this drain. Safe to be slow (~50-200ms on
    // BongoCat-sized images).
    //
    // Bound under both compile branches — the resolver `gc_collect` slot
    // is optional, so a runtime that doesn't export `mono_gc_collect` /
    // `il2cpp_gc_collect` (legacy / stripped) silently no-ops.
#if defined(DISCRETE_BACKEND_MONO)
    Discrete::PipeServer::RegisterDisconnectHook([]() {
        const auto& r = Discrete::Mono::GetResolver();
        if (!r.resolved || r.gc_collect == nullptr) return;
        Discrete::Mono::MonoDomain* domain =
            Discrete::Mono::GetRootDomain();
        if (domain == nullptr) return;
        // -1 = full collection (max generation). Wrap in ThreadAttachGuard
        // for the same reason every other Mono runtime call does — pipe
        // worker isn't a Mono-known thread.
        Discrete::Mono::ThreadAttachGuard guard(domain);
        if (!guard.Attached()) return;
        r.gc_collect(-1);
    });
    Discrete::Log::Init(
        "ExportAPI: registered Mono GC-drain disconnect hook");
#elif defined(DISCRETE_BACKEND_IL2CPP)
    Discrete::PipeServer::RegisterDisconnectHook([]() {
        const auto& r = Discrete::IL2CPP::GetResolver();
        if (!r.resolved || r.gc_collect == nullptr) return;
        Discrete::IL2CPP::Il2CppDomain* domain =
            Discrete::IL2CPP::GetDomain();
        if (domain == nullptr) return;
        // 0 = collect any generation (IL2CPP signature differs from Mono;
        // documented in il2cpp-api-types.h as "generation hint", 0 means
        // collect everything reachable).
        Discrete::IL2CPP::ThreadAttachGuard guard(domain);
        if (!guard.Attached()) return;
        r.gc_collect(0);
    });
    Discrete::Log::Init(
        "ExportAPI: registered IL2CPP GC-drain disconnect hook");
#endif

    return 0;
}

void Discrete_Shutdown() {
    if (!g_initialized.exchange(false)) return;
    Discrete::PipeServer::Stop();
    // Phase 20-MT: tear down the main-thread hook + dispatcher so we don't
    // leave the runtime_invoke detour pointing at our soon-to-be-unloaded
    // code. Safe to call when never installed (atomic-guarded no-op).
#if defined(DISCRETE_BACKEND_MONO)
    Discrete::Mono::MainThreadHook::Uninstall();
#elif defined(DISCRETE_BACKEND_IL2CPP)
    Discrete::IL2CPP::MainThreadHook::Uninstall();
#endif
    Discrete::MainThreadDispatcher::Shutdown();
    Discrete::Log::Init("ExportAPI: Discrete_Shutdown");
    Discrete::Log::Shutdown();
}

int Discrete_StartPipe() {
    if (!g_initialized.load()) Discrete_Init();
    if (Discrete::PipeServer::IsRunning()) return 0;
    return Discrete::PipeServer::Start() ? 0 : 1;
}

void Discrete_StopPipe() {
    Discrete::PipeServer::Stop();
}

DiscreteBackend Discrete_GetBackendType() {
    return BackendOfBuild();
}

std::uint32_t Discrete_GetBuildNumber() {
    return static_cast<std::uint32_t>(DISCRETE_BUILD_NUMBER);
}

}  // extern "C"
