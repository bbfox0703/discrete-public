// IL2CPPFieldXref.cpp — Phase 28b implementation.
//
// RIP scanner: walks the .text section of `GameAssembly.dll` for x64
// RIP-relative `mov / lea` instructions whose absolute target equals the
// resolved static-storage address of the requested field. Each hit gets
// attributed to its enclosing method via binary search against a sorted
// method-pointer index built from the same `class_get_methods` walk the
// rest of the IL2CPP backend uses.
//
// Limitations (v1):
//   * Static fields only — instance fields require heuristic
//     `mov reg, [rcx+offset]` AOB scans with high false-positive cost.
//   * Same-image attribution — methods in OTHER assemblies that reference
//     the static field are detected by the scan but get filtered out
//     because their methodPointer doesn't appear in the method index.
//     Cross-image attribution is a v2 enhancement.
//   * Opcode coverage: MOV r/m32, r (0x89), MOV r, r/m32 (0x8B), LEA
//     (0x8D). MOV r/m32, imm32 (0xC7) and SIMD movss / movsd (0xF3 0x0F 0x10)
//     are out — most static-field reads in Unity AOT IL2CPP are scalar
//     MOV / LEA so this catches the vast majority.

#include "IL2CPPFieldXref.h"

#include "IL2CPPChainDetector.h"
#include "IL2CPPFieldXrefScan.h"
#include "IL2CPPClassPointerScanner.h"
#include "IL2CPPClassWalker.h"
#include "IL2CPPDomain.h"
#include "IL2CPPLazyInitSlotScanner.h"
#include "IL2CPPResolver.h"
#include "IL2CPPStaticFieldsOffset.h"
#include "IL2CPPThreadAttach.h"
#include "../shared/FieldXrefHelpers.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ManagedLayout.h"
#include "../shared/Memory.h"
#include "../shared/RuntimeHealth.h"
#include "../shared/ScanCancel.h"
#include "../shared/SehFilter.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <unordered_set>

namespace Discrete::IL2CPP {

namespace {

// Classify a primary opcode by access direction.
//   0x8B  MOV r, r/m   → "read"
//   0x8D  LEA r, m     → "read"
//   0x89  MOV r/m, r   → "write"
const char* OpcodeKind(std::uint8_t op) noexcept {
    switch (op) {
        case 0x8B: return "read";
        case 0x8D: return "read";
        case 0x89: return "write";
        default:   return "";
    }
}

// One entry in the per-image method-pointer index. Sorted by `address` so
// `std::upper_bound` recovers the enclosing method for any IP.
struct MethodIndexEntry {
    std::uintptr_t address;
    Il2CppClass*   klass = nullptr;  // B-L7: pointer identity for nested-class match
    std::string    ns;
    std::string    className;
    std::string    methodName;
    std::uint32_t  methodToken;
    std::uint32_t  methodFlags;
    bool           isMethodStatic;
};

#pragma warning(push)
#pragma warning(disable: 4509)
const MethodInfo* SehNextMethod(fn_class_get_methods fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
FieldInfo* SehNextField(fn_class_get_fields fn, Il2CppClass* k, void** iter) {
    unsigned long code = 0;
    __try { return fn(k, iter); }
    __except ((code = GetExceptionCode()),
              Seh::MemoryAccessExceptionFilter(code, GetExceptionInformation())) {
        RuntimeHealth::MarkRuntimeFault("il2cpp_class_get_fields", code);
        return nullptr;
    }
}
void* SehStaticData(fn_class_get_static_data fn, Il2CppClass* k) {
    __try { return fn(k); } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
void* SehMethodPtr(fn_method_get_pointer fn, const MethodInfo* m) {
    __try { return fn(m); } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
void* SehMethodPtrFromStruct(const MethodInfo* m) {
    __try { return *reinterpret_cast<void* const*>(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
std::uint32_t SehMethodFlags(fn_method_get_flags fn, const MethodInfo* m, std::uint32_t* iflags) {
    __try { return fn(m, iflags); } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
std::uint32_t SehMethodToken(fn_method_get_token fn, const MethodInfo* m) {
    __try { return fn(m); } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
const char* SehMethodName(fn_method_get_name fn, const MethodInfo* m) {
    __try { return fn(m); } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
bool SehClassIsValueType(fn_class_is_valuetype fn, Il2CppClass* k) {
    __try { return (fn(k) & 0xFF) != 0; }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return false; }
}
Il2CppClass* SehClassGetParent(fn_class_get_parent fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehClassName(fn_class_get_name fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehClassNs(fn_class_get_namespace fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
#pragma warning(pop)

bool IsValueTypeClass(Il2CppClass* k) {
    if (k == nullptr) return false;
    const auto& r = GetResolver();
    if (r.class_is_valuetype != nullptr) {
        return SehClassIsValueType(r.class_is_valuetype, k);
    }
    if (r.class_get_parent != nullptr && r.class_get_name != nullptr) {
        Il2CppClass* parent = SehClassGetParent(r.class_get_parent, k);
        if (parent != nullptr) {
            const char* pName = SehClassName(r.class_get_name, parent);
            const char* pNs   = r.class_get_namespace ? SehClassNs(r.class_get_namespace, parent) : nullptr;
            if (pName != nullptr && pNs != nullptr && std::strcmp(pNs, "System") == 0 &&
                (std::strcmp(pName, "ValueType") == 0 || std::strcmp(pName, "Enum") == 0)) {
                return true;
            }
        }
    }
    return false;
}

// B-M9: query Win64 PE .pdata unwind table for the exact [begin, end) bounds
// of the AOT function containing `ip`.
bool LookupRuntimeFunctionBounds(std::uintptr_t ip,
                                 std::uintptr_t& outBegin,
                                 std::uintptr_t& outEnd) noexcept {
#if defined(_M_AMD64) || defined(_M_ARM64)
    DWORD64 imageBase = 0;
    PRUNTIME_FUNCTION rf = ::RtlLookupFunctionEntry(
        static_cast<DWORD64>(ip), &imageBase, nullptr);
    if (rf == nullptr || imageBase == 0) return false;
    outBegin = static_cast<std::uintptr_t>(imageBase + rf->BeginAddress);
    outEnd   = static_cast<std::uintptr_t>(imageBase + rf->EndAddress);
    return outEnd > outBegin;
#else
    (void)ip; (void)outBegin; (void)outEnd;
    return false;
#endif
}

void* ReadMethodPointer(const MethodInfo* m) {
    if (m == nullptr) return nullptr;
    const auto& r = GetResolver();
    if (r.method_get_pointer != nullptr) {
        if (void* p = SehMethodPtr(r.method_get_pointer, m)) return p;
    }
    return SehMethodPtrFromStruct(m);
}

// Walk every class in `image` and accumulate a sorted method-pointer
// index. Out-parameter receives the total methods walked (drives the
// `scanned_methods` response field).
std::vector<MethodIndexEntry> BuildMethodIndex(const Il2CppImage* image,
                                                std::size_t* outMethodCount) {
    std::vector<MethodIndexEntry> idx;
    if (outMethodCount != nullptr) *outMethodCount = 0;
    if (image == nullptr) return idx;

    const auto& r = GetResolver();
    if (r.class_get_methods == nullptr) return idx;

    auto classes = EnumerateClasses(image);
    idx.reserve(classes.size() * 8);
    for (const auto& ce : classes) {
        if (ce.klass == nullptr) continue;

        void* iter = nullptr;
        for (;;) {
            const MethodInfo* m = SehNextMethod(r.class_get_methods, ce.klass, &iter);
            if (m == nullptr) break;
            if (outMethodCount != nullptr) ++(*outMethodCount);

            void* p = ReadMethodPointer(m);
            if (p == nullptr) continue;
            MethodIndexEntry e;
            e.address     = reinterpret_cast<std::uintptr_t>(p);
            e.klass       = ce.klass;
            e.ns          = ce.ns;
            e.className   = ce.name;
            if (r.method_get_name != nullptr) {
                if (const char* mn = SehMethodName(r.method_get_name, m)) e.methodName = mn;
            }
            if (r.method_get_token != nullptr) {
                e.methodToken = SehMethodToken(r.method_get_token, m);
            }
            std::uint32_t iflags = 0;
            if (r.method_get_flags != nullptr) {
                e.methodFlags = SehMethodFlags(r.method_get_flags, m, &iflags);
                e.isMethodStatic = (e.methodFlags & 0x10u) != 0;
            }
            idx.push_back(std::move(e));
        }
    }

    std::sort(idx.begin(), idx.end(),
              [](const MethodIndexEntry& a, const MethodIndexEntry& b) {
                  return a.address < b.address;
              });
    return idx;
}

// B-M9 — exact enclosing-method attribution using Win64 .pdata bounds
// (RtlLookupFunctionEntry). Any hit inside another assembly's AOT method
// interleaved in GameAssembly.dll .text has a funcBegin that is NOT in this
// image's method index and is cleanly rejected instead of misattributed.
const MethodIndexEntry* FindEnclosing(const std::vector<MethodIndexEntry>& idx,
                                       std::uintptr_t ip) noexcept {
    return FieldXref::FindEnclosingMethodExact(idx, ip, LookupRuntimeFunctionBounds);
}

struct ResolvedStaticField {
    Il2CppClass*   klass       = nullptr;
    void*          address     = nullptr;
    std::size_t    offset      = 0;
    std::string    typeName;
    std::uint32_t  flags       = 0;
    bool           isStatic    = false;
    bool           isValueType = false;
    std::string    error;
};

ResolvedStaticField ResolveStaticField(const Il2CppImage* image,
                                       const std::string& ns,
                                       const std::string& className,
                                       const std::string& fieldName) {
    ResolvedStaticField out;

    out.klass = FindClass(image, ns, className);
    if (out.klass == nullptr) {
        out.error = "class not found: " + (ns.empty() ? className : ns + "." + className);
        return out;
    }

    const auto& r = GetResolver();
    if (r.class_get_static_data == nullptr) {
        out.error = "il2cpp_class_get_static_data unbound";
        return out;
    }
    if (r.class_get_fields == nullptr || r.field_get_name == nullptr) {
        out.error = "il2cpp runtime missing field iteration exports";
        return out;
    }

    void* iter = nullptr;
    FieldInfo* targetField = nullptr;
    for (;;) {
        FieldInfo* f = SehNextField(r.class_get_fields, out.klass, &iter);
        if (f == nullptr) break;
        const char* name = r.field_get_name(f);
        if (name == nullptr) continue;
        if (fieldName == name) {
            targetField = f;
            if (r.field_get_offset != nullptr) out.offset = r.field_get_offset(f);
            if (r.field_get_flags  != nullptr) out.flags  = r.field_get_flags(f);
            out.isStatic = (out.flags & 0x10u) != 0;  // FieldAttributes::Static
            if (r.field_get_type != nullptr && r.type_get_name != nullptr) {
                if (const Il2CppType* ft = r.field_get_type(f)) {
                    if (char* tn = r.type_get_name(ft)) {
                        out.typeName = tn;
                        if (r.free != nullptr) r.free(tn);
                    }
                }
            }
            break;
        }
    }
    if (targetField == nullptr) {
        out.error = "field not found: " + fieldName;
        return out;
    }
    if (!out.isStatic) {
        out.error = "field references for instance fields not supported on IL2CPP backend in v1";
        return out;
    }
    // FieldAttributes::Literal (0x40) — `const` fields and enum members.
    // The C# compiler inlines literals as `ldc.i4`/`ldc.r4`/etc. with the
    // value baked into the call site, so no `ldsfld <token>` IL op is ever
    // emitted (Mono path) and no `mov reg, [rip+static_addr]` is ever
    // emitted (IL2CPP path) — the field's metadata token never appears in
    // executable code. IL2CPP additionally returns NULL from
    // class_get_static_data for enum-only classes because there's no real
    // static storage block to allocate. Either way, Phase 28 v1 cannot
    // find references to literal fields; surface the limit clearly so the
    // user knows it's a semantic constraint, not a backend bug.
    if ((out.flags & 0x40u) != 0) {
        out.error =
            "literal const field (or enum member) — the C# compiler inlines "
            "the value at every use site (`ldc.i4 <value>` etc.), so the "
            "field's metadata token never appears in IL or native code and "
            "there's nothing for Phase 28 to match against. "
            "This is a semantic limitation of inlined constants, not a "
            "backend bug.";
        return out;
    }
    if (Discrete::ManagedLayout::NormalizeFieldOffset32(out.offset) < 0) {
        out.error =
            "thread-static field ([ThreadStatic]) — stored per thread in TLS "
            "rather than in the class's shared static_fields block (offset=-1), "
            "so there is no single static address for Phase 28 to scan.";
        return out;
    }
    void* staticBase = SehStaticData(r.class_get_static_data, out.klass);
    if (staticBase == nullptr) {
        // Non-literal static field whose declaring class hasn't allocated
        // a static-storage block. Two common causes: (1) the class hasn't
        // been initialized yet (IL2CPP allocates lazily on first access —
        // try touching a static method or reading another static field
        // first), (2) the class is a generic open type whose per-
        // instantiation storage hasn't been requested.
        out.error =
            "il2cpp_class_get_static_data returned null — the declaring "
            "class has no allocated static storage block. Either the "
            "class hasn't been initialised at runtime yet (try invoking "
            "any of its static methods from CE / the Methods tab first), "
            "or it's a generic open type without active instantiations. "
            "Phase 28 v1 doesn't force class init.";
        return out;
    }
    out.address = static_cast<std::uint8_t*>(staticBase) + out.offset;
    return out;
}

// Phase 28b instance path — resolve an INSTANCE field's class + offset.
// Mirrors ResolveStaticField's field-iteration prefix but does NOT touch
// static storage (no class_get_static_data) and rejects STATIC fields
// (the caller routes those to ResolveStaticField). `out.offset` is the
// field's offset within the Il2CppObject INCLUDING the object header — the
// exact value the AOT codegen encodes as the disp in `mov reg, [base+disp]`,
// so it can be compared directly against a decoded memory operand's
// displacement. `out.address` stays null (no single storage address for an
// instance field — it lives per-object on the GC heap).
ResolvedStaticField ResolveInstanceField(const Il2CppImage* image,
                                         const std::string& ns,
                                         const std::string& className,
                                         const std::string& fieldName) {
    ResolvedStaticField out;

    out.klass = FindClass(image, ns, className);
    if (out.klass == nullptr) {
        out.error = "class not found: " + (ns.empty() ? className : ns + "." + className);
        return out;
    }

    const auto& r = GetResolver();
    if (r.class_get_fields == nullptr || r.field_get_name == nullptr) {
        out.error = "il2cpp runtime missing field iteration exports";
        return out;
    }

    void* iter = nullptr;
    FieldInfo* targetField = nullptr;
    for (;;) {
        FieldInfo* f = SehNextField(r.class_get_fields, out.klass, &iter);
        if (f == nullptr) break;
        const char* name = r.field_get_name(f);
        if (name == nullptr) continue;
        if (fieldName == name) {
            targetField = f;
            if (r.field_get_offset != nullptr) out.offset = r.field_get_offset(f);
            if (r.field_get_flags  != nullptr) out.flags  = r.field_get_flags(f);
            out.isStatic = (out.flags & 0x10u) != 0;  // FieldAttributes::Static
            if (r.field_get_type != nullptr && r.type_get_name != nullptr) {
                if (const Il2CppType* ft = r.field_get_type(f)) {
                    if (char* tn = r.type_get_name(ft)) {
                        out.typeName = tn;
                        if (r.free != nullptr) r.free(tn);
                    }
                }
            }
            break;
        }
    }
    if (targetField == nullptr) {
        out.error = "field not found: " + fieldName;
        return out;
    }
    if (out.isStatic) {
        // Caller should have routed this to ResolveStaticField. Defensive.
        out.error = "internal: static field routed to instance resolver";
        return out;
    }
    // Literal (0x40) instance fields don't exist in C# (const implies static),
    // but be defensive — an inlined-constant offset is meaningless to scan for.
    if ((out.flags & 0x40u) != 0) {
        out.error = "literal field has no instance storage to scan for";
        return out;
    }
    out.isValueType = IsValueTypeClass(out.klass);
    return out;
}

// PE walker — given a module's image base, returns the (RVA, virtualSize)
// of every section flagged as executable. Reads via SafeReadBytes so an
// AC-stripped header still leaves the rest of the DLL intact (the PE
// headers we need are below 4KB and never AC-stripped in practice).
using ExecutableSection = XrefScan::ExecSection;  // absolute VA + VirtualSize

std::vector<ExecutableSection> EnumerateExecutableSections(void* moduleBase) {
    std::vector<ExecutableSection> out;
    if (moduleBase == nullptr) return out;
    auto base = reinterpret_cast<std::uint8_t*>(moduleBase);

    auto dos = Memory::SafeRead<IMAGE_DOS_HEADER>(base);
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE) return out;
    auto nt = Memory::SafeRead<IMAGE_NT_HEADERS64>(base + dos->e_lfanew);
    if (!nt || nt->Signature != IMAGE_NT_SIGNATURE) return out;

    auto sectionsAddr = base + dos->e_lfanew
                        + offsetof(IMAGE_NT_HEADERS64, OptionalHeader)
                        + nt->FileHeader.SizeOfOptionalHeader;
    const std::uint16_t nSect = nt->FileHeader.NumberOfSections;
    out.reserve(nSect);
    for (std::uint16_t i = 0; i < nSect; ++i) {
        auto sh = Memory::SafeRead<IMAGE_SECTION_HEADER>(sectionsAddr + i * sizeof(IMAGE_SECTION_HEADER));
        if (!sh) break;
        if ((sh->Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) continue;
        ExecutableSection s;
        s.va   = reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress;
        s.size = sh->Misc.VirtualSize;
        out.push_back(s);
    }
    return out;
}

}  // namespace

FindFieldRefsResult FindStaticFieldReferences(const Il2CppImage* image,
                                              const std::string& ns,
                                              const std::string& className,
                                              const std::string& fieldName,
                                              FieldRefKind       kindFilter,
                                              std::size_t        limit) {
    FindFieldRefsResult result;
    if (image == nullptr) { result.error = "image is null"; return result; }
    if (limit == 0) limit = 200;

    const auto& r = GetResolver();
    if (!r.resolved) { result.error = "il2cpp runtime not resolved"; return result; }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) { result.error = "il2cpp domain unavailable"; return result; }

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "il2cpp thread attach failed"; return result; }

    ResolvedStaticField target = ResolveStaticField(image, ns, className, fieldName);
    if (!target.error.empty()) { result.error = std::move(target.error); return result; }
    result.staticAddress = target.address;

    Log::Write(MagicStrings::LogCategoryWalk,
               "FieldXref: target " + (ns.empty() ? className : ns + "." + className) +
               "." + fieldName + " staticAddress=0x" +
               [](std::uintptr_t v) {
                   char buf[32]; std::snprintf(buf, sizeof(buf), "%016llX",
                       static_cast<unsigned long long>(v)); return std::string(buf);
               }(reinterpret_cast<std::uintptr_t>(target.address)) +
               " offset=" + std::to_string(target.offset) +
               " type=" + target.typeName);

    // GameAssembly.dll module bounds for the RIP scan.
    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) {
        result.error = "GameAssembly.dll not loaded — cannot run RIP scan";
        return result;
    }

    auto sections = EnumerateExecutableSections(gaBase);
    if (sections.empty()) {
        result.error = "no executable sections in GameAssembly.dll";
        return result;
    }

    // Method-pointer index drives the IP → method attribution. Built once,
    // reused for every hit.
    std::vector<MethodIndexEntry> methodIndex = BuildMethodIndex(image,
                                                                 &result.scannedMethods);
    if (ScanCancel::IsCancelRequested()) {
        result.cancelled = true;
        result.ok = false;
        result.matches.clear();
        return result;
    }

    Log::Write(MagicStrings::LogCategoryWalk,
               "FieldXref: methodIndex built — " +
               std::to_string(methodIndex.size()) + " methods indexed (of " +
               std::to_string(result.scannedMethods) + " walked), executable sections=" +
               std::to_string(sections.size()));

    const std::uintptr_t targetAddr = reinterpret_cast<std::uintptr_t>(target.address);

    // Diagnostic counters — distinguish "no candidates matched the target" from
    // "candidates matched but were dropped" (cross-assembly attribution / kind
    // filter). Logged once at end of scan.
    std::size_t candidateHits     = 0;  // disp32 resolved to targetAddr
    std::size_t droppedKindFilter = 0;  // candidate dropped by Read/Write filter
    std::size_t droppedNoEnclose  = 0;  // candidate dropped — not in this image

    // Helper: emit one hit if it survives kind + assembly attribution.
    // Captures all the lambda state to keep the inner scan loop tight.
    auto emitHit = [&](std::uintptr_t startIP, std::uint8_t op) -> bool {
        ++candidateHits;
        const char* k = OpcodeKind(op);
        if (kindFilter == FieldRefKind::Read  && std::strcmp(k, "read")  != 0) {
            ++droppedKindFilter; return false;
        }
        if (kindFilter == FieldRefKind::Write && std::strcmp(k, "write") != 0) {
            ++droppedKindFilter; return false;
        }
        const MethodIndexEntry* enc = FindEnclosing(methodIndex, startIP);
        if (enc == nullptr) {  // hit in a method outside the target image
            ++droppedNoEnclose;
            return false;
        }
        FieldRefMatch hit;
        hit.ns             = enc->ns;
        hit.className      = enc->className;
        hit.methodName     = enc->methodName;
        hit.methodToken    = enc->methodToken;
        hit.methodFlags    = enc->methodFlags;
        hit.isMethodStatic = enc->isMethodStatic;
        hit.methodAddress  = reinterpret_cast<void*>(enc->address);
        hit.hitAddress     = reinterpret_cast<void*>(startIP);
        hit.isStatic       = true;
        hit.refKind        = k;
        hit.confidence     = "exact";
        result.matches.push_back(std::move(hit));
        return true;
    };

    // Scan kernel (IL2CPPFieldXrefScan): every single-hop RIP-relative
    // candidate, in scan order. The kind filter, method attribution and limit
    // cut-off are applied here in that same order, so the result (and the
    // counters logged below) match the original inline loop exactly.
    bool ripCancelled = false;
    for (const auto& c : XrefScan::ScanRipRelative(sections, targetAddr, &ripCancelled)) {
        if (ripCancelled || ScanCancel::IsCancelRequested()) {
            result.cancelled = true;
            result.ok = false;
            result.matches.clear();
            return result;
        }
        if (emitHit(c.ip, c.opcode) && result.matches.size() >= limit) {
            result.truncated = true;
            break;
        }
    }
    if (ripCancelled || ScanCancel::IsCancelRequested()) {
        result.cancelled = true;
        result.ok = false;
        result.matches.clear();
        return result;
    }

    Log::Write(MagicStrings::LogCategoryWalk,
               "FieldXref: scan complete — candidateHits=" + std::to_string(candidateHits) +
               " emitted=" + std::to_string(result.matches.size()) +
               " droppedKindFilter=" + std::to_string(droppedKindFilter) +
               " droppedNoEnclose=" + std::to_string(droppedNoEnclose) +
               " truncated=" + (result.truncated ? "1" : "0"));

    // ---------------------------------------------------------------------
    // Phase 28b-v2 — class-pointer chained-disassembly fallback.
    //
    // When v1's single-hop RIP scan returned 0 matches AND the field's
    // static-storage address sits OUTSIDE GameAssembly.dll's module image
    // (= IL2CPP allocated `static_fields` on the runtime heap, which is
    // the ~universal case for user-code statics), the v1 scanner was
    // structurally unable to see references. v2 chases the 2-hop chain
    // the AOT code actually emits:
    //     mov R1, [rip + CLASS_PTR_DISP32]    ; hop 1 — class pointer load
    //     mov R2, [R1 + STATIC_FIELDS_OFF]    ; hop 2 — deref to static block
    //     mov R3, [R2 + FIELD_OFFSET]         ; hop 3 — read (or write/lea)
    //
    // Stage A (offline-ish): locate every `.data` / `.rdata` slot holding
    //   `(uintptr_t)target.klass` — these are the `[rip + DISP32]` targets
    //   we'll scan for in stage B.
    // Stage B (executable scan + forward-trace, IL2CPPChainDetector): for
    //   each candidate hop-1 load, decode forward to confirm hops 2 + 3 with
    //   the expected offsets.
    //
    // Only fires when v1 returned 0 — preserves backward compatibility
    // for the rare case where the static IS baked into the module image
    // (then v1 is authoritative + already returned the matches).
    {
        const std::size_t gaSize = ModuleHelper::GetModuleSize(MagicStrings::ModuleIL2CPP);
        const std::uintptr_t gaLo = reinterpret_cast<std::uintptr_t>(gaBase);
        const std::uintptr_t gaHi = gaLo + gaSize;
        const bool outsideModule  = (targetAddr < gaLo) || (targetAddr >= gaHi);

        if (result.matches.empty() && outsideModule && target.klass != nullptr) {
            const std::size_t sfo = GetStaticFieldsOffsetInClass();
            if (sfo == 0) {
                Log::Write(MagicStrings::LogCategoryWalk,
                    "FieldXref: v2 chain detection unavailable — "
                    "offsetof(static_fields) discovery failed at init");
            } else {
                // ---- v2 direct slots (Phase 28b-v2) -------------------------
                auto slots = EnumerateClassPointerSlots(target.klass);
                const std::size_t v2SlotCount = slots.size();

                // ---- v3 lazy-init slots (Phase 28b-v3 + v3.1) ---------------
                //
                // For each per-method lazy-init slot discovered in `.text`,
                // try TWO complementary methods to determine if the slot is
                // associated with our target class:
                //
                //   v3.0 — runtime value match: read the slot's current
                //   value with `SafeRead`. If equals target class pointer,
                //   slot is associated. Works only when the AOT method
                //   that uses the slot has executed at least once in this
                //   session (so the slot has been populated by IL2CPP's
                //   `il2cpp_codegen_initialize_runtime_metadata`).
                //
                //   v3.1 — static token decode: at scan time the scanner
                //   captured each slot's initial value. If it was a
                //   metadata-usage token of kind=TypeInfo, decode the
                //   embedded type index, look up `Il2CppType*` in the
                //   runtime types[] array, bridge to `Il2CppClass*` via
                //   `il2cpp_class_from_il2cpp_type`. Compare to target.
                //   This works EVEN WHEN THE LAMBDA / METHOD HAS NEVER
                //   EXECUTED — the slot is still token-shaped but we
                //   can resolve what it WOULD become.
                //
                // Both paths union into the chain detector's input set.
                std::size_t v3CandidateSlots   = 0;
                std::size_t v3MatchingRuntime  = 0;  // v3.0 hits
                {
                    const std::uintptr_t targetClassVal =
                        reinterpret_cast<std::uintptr_t>(target.klass);
                    const auto& lazySlots = EnumerateLazyInitSlots();
                    v3CandidateSlots = lazySlots.size();

                    // Fast dedup against v2 slots — small set, linear scan
                    // is fine for the common case (v2 returns 0-12 slots).
                    auto slotAlreadyKnown = [&slots](std::uintptr_t addr) {
                        for (const auto& existing : slots) {
                            if (existing.address == addr) return true;
                        }
                        return false;
                    };

                    for (const auto& ls : lazySlots) {
                        if (ScanCancel::IsCancelRequested()) {
                            result.cancelled = true;
                            result.ok = false;
                            result.matches.clear();
                            return result;
                        }
                        if (slotAlreadyKnown(ls.slotAddress)) continue;

                        // v3.0 — runtime value match (B-L3: removed dead v3.1
                        // ResolveTypeInfoSlotClass fallback which only re-read
                        // the same slot after v3.0 had already checked it).
                        std::uintptr_t slotValue = 0;
                        if (!Memory::SafeReadBytes(
                                reinterpret_cast<const void*>(ls.slotAddress),
                                &slotValue, sizeof(slotValue)) ||
                            slotValue != targetClassVal) {
                            continue;
                        }
                        ++v3MatchingRuntime;

                        ClassPointerSlot extra{};
                        extra.address   = ls.slotAddress;
                        extra.sectionVa = 0;
                        slots.push_back(extra);
                    }
                }
                const std::size_t v3MatchingSlots = v3MatchingRuntime;

                Log::Write(MagicStrings::LogCategoryWalk,
                    "FieldXref: v2 starting — staticFieldsOffset=" +
                    std::to_string(sfo) + " fieldOffset=" +
                    std::to_string(target.offset) +
                    " v2DirectSlots=" + std::to_string(v2SlotCount) +
                    " v3LazyInitCandidates=" + std::to_string(v3CandidateSlots) +
                    " v3MatchingRuntime=" + std::to_string(v3MatchingRuntime) +
                    " totalSlots=" + std::to_string(slots.size()));

                if (!slots.empty()) {
                    if (ScanCancel::IsCancelRequested()) {
                        result.cancelled = true;
                        result.ok = false;
                        result.matches.clear();
                        return result;
                    }
                    ChainScanStats v2stats{};
                    bool chainCancelled = false;
                    auto chains = DetectFieldAccessChains(
                        slots, sfo, target.offset, limit, &v2stats, &chainCancelled);
                    if (chainCancelled || ScanCancel::IsCancelRequested()) {
                        result.cancelled = true;
                        result.ok = false;
                        result.matches.clear();
                        return result;
                    }
                    Log::Write(MagicStrings::LogCategoryWalk,
                        "FieldXref: v2 scan complete — hopOneCandidates=" +
                        std::to_string(v2stats.hopOneCandidates) +
                        " chainBroken=" + std::to_string(v2stats.chainBroken) +
                        " chainsEmitted=" + std::to_string(v2stats.chainsEmitted) +
                        " bytesScanned=" + std::to_string(v2stats.bytesScanned) +
                        " failedReads=" + std::to_string(v2stats.failedReads));

                    // Build the v3-slot address set so we can tag each match
                    // with the right confidence ("chain-lazy" for hits whose
                    // hop-1 IP loads from a v3-only slot, "chain-exact" for
                    // hits surfaced via v2's direct-slot set).
                    std::unordered_set<std::uintptr_t> v3OnlySlots;
                    if (v3MatchingSlots > 0) {
                        v3OnlySlots.reserve(v3MatchingSlots);
                        for (std::size_t i = v2SlotCount; i < slots.size(); ++i) {
                            v3OnlySlots.insert(slots[i].address);
                        }
                    }

                    // Attribute each chain hit to its enclosing method via
                    // the existing v1 method-pointer index. Apply read/write
                    // filter at this layer (v2 already knows kind from the
                    // chain shape, but the user-requested filter is enforced
                    // here for parity with the v1 path).
                    //
                    // Note: ChainMatch doesn't currently carry which slot
                    // the hop-1 referenced — we'd need to extend the chain
                    // detector to do per-hit slot attribution. v3.0 makes
                    // the simplifying assumption that if ANY v3-only slot
                    // exists, mark the hit as "chain-lazy" only when v2
                    // direct slots were zero. Otherwise it's "chain-exact"
                    // (v2 would've found it without us).
                    const std::string confidenceTag =
                        (v2SlotCount == 0 && v3MatchingSlots > 0)
                            ? "chain-lazy"
                            : "chain-exact";

                    std::size_t v2Dropped = 0;
                    for (const auto& c : chains) {
                        if (kindFilter == FieldRefKind::Read  && c.refKind != "read")  {
                            ++v2Dropped; continue;
                        }
                        if (kindFilter == FieldRefKind::Write && c.refKind != "write") {
                            ++v2Dropped; continue;
                        }
                        const MethodIndexEntry* enc =
                            FindEnclosing(methodIndex, c.hitIP);
                        if (enc == nullptr) {
                            ++v2Dropped;  // hit in a different assembly's code
                            continue;
                        }
                        FieldRefMatch m;
                        m.ns             = enc->ns;
                        m.className      = enc->className;
                        m.methodName     = enc->methodName;
                        m.methodToken    = enc->methodToken;
                        m.methodFlags    = enc->methodFlags;
                        m.isMethodStatic = enc->isMethodStatic;
                        m.methodAddress  = reinterpret_cast<void*>(enc->address);
                        m.hitAddress     = reinterpret_cast<void*>(c.hitIP);
                        m.isStatic       = true;
                        m.refKind        = c.refKind;
                        m.confidence     = confidenceTag;
                        result.matches.push_back(std::move(m));
                    }
                    Log::Write(MagicStrings::LogCategoryWalk,
                        "FieldXref: v2 emitted " +
                        std::to_string(result.matches.size()) +
                        " matches (dropped " + std::to_string(v2Dropped) +
                        " on kind/enclose filter, confidence=" +
                        confidenceTag + ")");
                }
            }
        }
    }

    // Build #555 follow-up — IL2CPP structural-limit advisory.
    // Diagnosis from 活俠傳/SaGa F2R smoke 2026-05-25: every IL2CPP static field
    // we tried returned 0 matches (`SteamManager` static, `<>9__0_0` /
    // `<>9__0_1` lambda caches). Lambda caches MUST have ≥1 read+write in IL
    // (the closure-cache pattern), so 0 matches is structurally impossible
    // unless the RIP scanner physically cannot see the references.
    //
    // Root cause: IL2CPP allocates `static_fields` blocks on the runtime
    // heap during class init (`il2cpp_class_get_static_data` returns a
    // pointer into a high-VA heap region like 0x18BD5735630). No machine
    // instruction in the AOT-compiled .text can RIP-relative-address a
    // runtime-allocated address — the displacement isn't known at compile
    // time. So compiled code reaches the field via class-pointer indirection:
    //     mov rax, [rip+CLASS_PTR]            ; load Il2CppClass* (in .data)
    //     mov rax, [rax + offsetof(static_fields)]
    //     mov rax, [rax + 0x10]               ; field at offset 16
    // The RIP scan would have to be redesigned to chase this 2-hop chain
    // (Phase 28b-v2, see plan.md) which needs a proper x64 disassembler.
    //
    // Heuristic for surfacing the note: address sits outside the
    // GameAssembly.dll module image. If it's inside (rare — only happens
    // when IL2CPP baked the static into .data, which AFAICT modern Unity
    // never does for user code), the scan IS authoritative and 0 matches
    // means the field really is unused; no note needed.
    if (result.matches.empty()) {
        std::size_t gaSize = ModuleHelper::GetModuleSize(MagicStrings::ModuleIL2CPP);
        std::uintptr_t gaLo = reinterpret_cast<std::uintptr_t>(gaBase);
        std::uintptr_t gaHi = gaLo + gaSize;
        bool outsideModule = (targetAddr < gaLo) || (targetAddr >= gaHi);
        if (outsideModule && candidateHits == 0) {
            // ----------------------------------------------------------
            // User-facing note: kept short + jargon-free per user
            // feedback (the prior 6-sentence technical wall-of-text was
            // unhelpful for the UI banner — most users hitting this case
            // aren't IL2CPP internals experts).
            //
            // The full technical reasoning lives below in this comment
            // block and in the DLL log line above. Future maintainers:
            // do NOT inline the technical detail back into the user-
            // facing string — keep it here.
            //
            // What's actually going on:
            //   * v1 RIP scan checks for `mov reg, [rip+disp]` where
            //     disp points directly at the field's storage. Fails on
            //     modern IL2CPP because `static_fields` is heap-allocated
            //     by the runtime — no compile-time disp can address it.
            //   * v2 chain detector looks for the 2-hop pattern
            //     `mov reg, [rip+CLASS_PTR]; mov reg2, [reg+0xB8]; ...`.
            //     Works when AOT code has cached the `Il2CppClass*` in
            //     `.data` (singletons, user-statics initialised early).
            //   * v3.0 lazy-init slot scanner finds per-method init-call
            //     cache slots in `.text` (`lea reg, [SLOT]; call init`).
            //     Resolves slot→class via runtime value comparison —
            //     works when the AOT method that uses the slot has
            //     executed at least once.
            //   * The remaining case is "AOT method has never run in
            //     this session" (e.g. compiler-generated `<>c` lambda
            //     caches whose lambda body hasn't been called). v3.1
            //     attempted static slot→class mapping but hit a v29
            //     `metadataUsages=NULL` layout dead-end. v3.2 plan in
            //     STATUS.md but deferred indefinitely (niche use case).
            //
            // For end users:
            //   * Singletons / regular game state: v2/v3.0 usually work.
            //     If hitting this banner on those, the class hasn't been
            //     initialised yet — exercise the code path first.
            //   * Compiler-generated `<>c` lambda caches: not supported
            //     on IL2CPP. Use Mono backend if available.
            // ----------------------------------------------------------
            result.note =
                "No matches found. The IL2CPP scanner can't detect "
                "references to this field type yet. This usually means "
                "either (a) the code that uses this field hasn't run in "
                "the current game session — try playing the relevant "
                "game content first, then re-scan, or (b) it's a "
                "compiler-generated field (lambda cache, async-state-"
                "machine, etc.) which isn't supported on the IL2CPP "
                "backend in this build. Mono targets handle these cases "
                "correctly via IL walking.";
        }
    }

    result.ok = true;
    return result;
}

// ---------------------------------------------------------------------------
// Phase 28b instance path — INSTANCE-field reverse lookup.
//
// Static fields have a single RIP-addressable (or chain-reachable) storage
// address, so the static scanner can match the WHOLE .text. Instance fields
// live per-object on the GC heap — there is no single address to match. The
// codegen for `this.field` is `mov reg, [base + fieldOffset]` where `base`
// derives from the instance pointer; the only stable signal is the
// displacement == the field's offset within Il2CppObject.
//
// `[reg + disp]` is ubiquitous, so to keep false positives sane we scope the
// scan to the DECLARING CLASS'S OWN METHODS only (where `this` is an instance
// of the target type, making disp==fieldOffset a plausible field access).
// Within those methods every `[gpReg + fieldOffset]` access (base != RIP /
// RSP / RBP, no index) is emitted as a HEURISTIC match — we do NOT taint-track
// `this` in v1, so offset collisions with other objects touched in the same
// method are possible. The UI surfaces confidence="heuristic" + an advisory.
//
// Known v1 recall gaps (documented, accepted): external classes accessing a
// public field are missed (out of scope); cross-method inlining of accessors
// is missed (inherent vs Mono's pre-JIT IL walk); rbp-spilled `this` reloads
// are still matched (we match the displacement, not the base provenance).
FindFieldRefsResult FindInstanceFieldReferences(const Il2CppImage* image,
                                                const std::string& ns,
                                                const std::string& className,
                                                const std::string& fieldName,
                                                FieldRefKind       kindFilter,
                                                std::size_t        limit) {
    FindFieldRefsResult result;
    if (image == nullptr) { result.error = "image is null"; return result; }
    if (limit == 0) limit = 200;

    const auto& r = GetResolver();
    if (!r.resolved) { result.error = "il2cpp runtime not resolved"; return result; }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) { result.error = "il2cpp domain unavailable"; return result; }

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "il2cpp thread attach failed"; return result; }

    ResolvedStaticField target = ResolveInstanceField(image, ns, className, fieldName);
    if (!target.error.empty()) { result.error = std::move(target.error); return result; }
    result.staticAddress = nullptr;  // instance fields have no single storage addr

    // B-L7: for value-type declaring classes (structs), AOT instance methods
    // receive an UNBOXED `this` pointer, so the machine-code displacement is
    // `metadataOffset - kObjectHeader` rather than the boxed field offset.
    const std::size_t fieldOffset = FieldXref::ComputeIl2CppInstanceScanOffset(
        target.offset, target.isValueType, ManagedLayout::kObjectHeader);
    if (fieldOffset == 0 || fieldOffset > 0x10000u) {
        // Offset 0 (no real instance slot) / implausibly large (generic /
        // thread-static sentinel). Proceed but log — a disp==0 match would be
        // dominated by `[reg]`-with-no-disp noise (we require disp.size!=0 so
        // disp==0 can't actually fire, but the log helps diagnose 0-match).
        Log::Write(MagicStrings::LogCategoryWalk,
                   "FieldXref(instance): suspicious fieldOffset=" +
                   std::to_string(fieldOffset) + " for " +
                   (ns.empty() ? className : ns + "." + className) + "." + fieldName);
    }

    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) {
        result.error = "GameAssembly.dll not loaded — cannot run instance scan";
        return result;
    }
    auto sections = EnumerateExecutableSections(gaBase);
    if (sections.empty()) {
        result.error = "no executable sections in GameAssembly.dll";
        return result;
    }
    // Section-end lookup: clamp a method's decode range to the section it
    // starts in so we never read past .text into the next section.
    auto sectionEndFor = [&sections](std::uintptr_t ip) -> std::uintptr_t {
        for (const auto& s : sections) {
            if (ip >= s.va && ip < s.va + s.size) return s.va + s.size;
        }
        return 0;  // not in any executable section
    };

    // Full sorted method index (drives method-end via next-greater address
    // and the scanned-method count).
    std::vector<MethodIndexEntry> methodIndex = BuildMethodIndex(image,
                                                                 &result.scannedMethods);
    if (ScanCancel::IsCancelRequested()) {
        result.cancelled = true;
        result.ok = false;
        result.matches.clear();
        return result;
    }

    Log::Write(MagicStrings::LogCategoryWalk,
               "FieldXref(instance): target " +
               (ns.empty() ? className : ns + "." + className) + "." + fieldName +
               " offset=" + std::to_string(fieldOffset) +
               " type=" + target.typeName +
               " methodIndex=" + std::to_string(methodIndex.size()));

    // Per-method decode range cap. Real Unity AOT methods are almost always
    // < 8 KB; huge switch/Burst methods are rare and clamping them only loses
    // a few far-out hits, which is the right trade for a heuristic.
    constexpr std::size_t kMaxMethodBytes = 8u * 1024u;

    std::size_t scannedDeclMethods = 0;
    std::size_t candidateHits      = 0;
    std::size_t droppedKindFilter  = 0;

    // Decode ranges of the declaring class's methods. end = next-greater
    // address in the FULL sorted index (the next method's entry), clamped to
    // start+cap and the section end.
    // rangeOrdinal = scannedDeclMethods after that range's method was counted.
    // The original inline loop stopped walking methods at the limit, so a
    // truncated scan logs the ordinal of the method that hit it, not the total.
    std::vector<XrefScan::MethodRange> ranges;
    std::vector<const MethodIndexEntry*> rangeMethod;
    std::vector<std::size_t> rangeOrdinal;
    for (std::size_t mi = 0; mi < methodIndex.size(); ++mi) {
        const MethodIndexEntry& m = methodIndex[mi];
        // B-L7: compare by Il2CppClass* pointer identity rather than (ns, name)
        // strings so nested classes resolved via dot-fallback (`Ns.Outer` +
        // `Inner` vs `Ns` + `Outer/Inner`) never silently produce 0 methods.
        if (m.klass != target.klass) continue;
        ++scannedDeclMethods;

        const std::uintptr_t methodStart = m.address;
        std::uintptr_t methodEnd = methodStart + kMaxMethodBytes;
        // B-M9: clamp to exact .pdata function end when available so we never
        // bleed into an interleaved method from another assembly.
        std::uintptr_t pdataBegin = 0, pdataEnd = 0;
        if (LookupRuntimeFunctionBounds(methodStart, pdataBegin, pdataEnd) &&
            pdataBegin == methodStart && pdataEnd > methodStart && pdataEnd < methodEnd) {
            methodEnd = pdataEnd;
        }
        if (mi + 1 < methodIndex.size()) {
            const std::uintptr_t next = methodIndex[mi + 1].address;
            if (next > methodStart && next < methodEnd) methodEnd = next;
        }
        const std::uintptr_t secEnd = sectionEndFor(methodStart);
        if (secEnd == 0) continue;  // method entry not in an exec section
        if (methodEnd > secEnd) methodEnd = secEnd;

        std::size_t rangeLen = static_cast<std::size_t>(methodEnd - methodStart);
        if (rangeLen == 0) continue;
        if (rangeLen > kMaxMethodBytes) rangeLen = kMaxMethodBytes;
        ranges.push_back({methodStart, rangeLen});
        rangeMethod.push_back(&m);
        rangeOrdinal.push_back(scannedDeclMethods);
    }

    // Scan kernel (IL2CPPFieldXrefScan): every `[gpr + fieldOffset]` memory
    // operand in those ranges, in decode order. Kind filter and limit apply
    // here in the same order as the original inline loop.
    bool instCancelled = false;
    for (const auto& h : XrefScan::ScanInstanceAccesses(ranges, fieldOffset, &instCancelled)) {
        if (instCancelled || ScanCancel::IsCancelRequested()) {
            result.cancelled = true;
            result.ok = false;
            result.matches.clear();
            return result;
        }
        const char* k = h.write ? "write" : "read";
        ++candidateHits;
        if (kindFilter == FieldRefKind::Read  && std::strcmp(k, "read")  != 0) {
            ++droppedKindFilter; continue;
        }
        if (kindFilter == FieldRefKind::Write && std::strcmp(k, "write") != 0) {
            ++droppedKindFilter; continue;
        }

        const MethodIndexEntry& m = *rangeMethod[h.method];
        FieldRefMatch hit;
        hit.ns             = m.ns;
        hit.className      = m.className;
        hit.methodName     = m.methodName;
        hit.methodToken    = m.methodToken;
        hit.methodFlags    = m.methodFlags;
        hit.isMethodStatic = m.isMethodStatic;
        hit.methodAddress  = reinterpret_cast<void*>(m.address);
        hit.hitAddress     = reinterpret_cast<void*>(h.ip);
        hit.isStatic       = false;
        hit.refKind        = k;
        hit.confidence     = "heuristic";
        result.matches.push_back(std::move(hit));
        if (result.matches.size() >= limit) {
            result.truncated   = true;
            scannedDeclMethods = rangeOrdinal[h.method];
            break;
        }
    }

    // Dedup by hitAddress (one instruction can in principle be emitted once
    // per matching memory operand; collapse to one row per IP). Matches the
    // chain detector's sort+unique-by-hitIP house style.
    std::sort(result.matches.begin(), result.matches.end(),
              [](const FieldRefMatch& a, const FieldRefMatch& b) {
                  return a.hitAddress < b.hitAddress;
              });
    result.matches.erase(
        std::unique(result.matches.begin(), result.matches.end(),
                    [](const FieldRefMatch& a, const FieldRefMatch& b) {
                        return a.hitAddress == b.hitAddress;
                    }),
        result.matches.end());

    Log::Write(MagicStrings::LogCategoryWalk,
               "FieldXref(instance): scan complete — declMethods=" +
               std::to_string(scannedDeclMethods) +
               " candidateHits=" + std::to_string(candidateHits) +
               " emitted=" + std::to_string(result.matches.size()) +
               " droppedKindFilter=" + std::to_string(droppedKindFilter) +
               " truncated=" + (result.truncated ? "1" : "0"));

    result.ok = true;
    return result;
}

}  // namespace Discrete::IL2CPP
