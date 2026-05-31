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
#include "IL2CPPClassPointerScanner.h"
#include "IL2CPPClassWalker.h"
#include "IL2CPPDomain.h"
#include "IL2CPPLazyInitSlotScanner.h"
#include "IL2CPPResolver.h"
#include "IL2CPPStaticFieldsOffset.h"
#include "IL2CPPThreadAttach.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <Zydis/Zydis.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <unordered_set>

namespace Discrete::IL2CPP {

namespace {

// True iff `m` is a ModRM byte whose mod=00 + r/m=101 — meaning the
// operand uses RIP-relative addressing (disp32 follows in the next 4
// bytes). The reg field (bits 3-5) selects the destination register but
// doesn't change addressing.
inline bool IsRipRelativeModrm(std::uint8_t m) noexcept {
    return (m & 0xC7u) == 0x05u;  // mod=00 in bits 6-7, r/m=101 in bits 0-2
}

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

// True iff `b` is a REX byte in the 0x40..0x4F range (any combination of
// W/R/X/B). We accept any REX value since the W bit (operand-size) doesn't
// affect RIP-relative decoding.
inline bool IsRexByte(std::uint8_t b) noexcept {
    return (b & 0xF0u) == 0x40u;
}

inline std::int32_t ReadInt32LE(const std::uint8_t* p) noexcept {
    std::uint32_t v =
          static_cast<std::uint32_t>(p[0])
        | (static_cast<std::uint32_t>(p[1]) <<  8)
        | (static_cast<std::uint32_t>(p[2]) << 16)
        | (static_cast<std::uint32_t>(p[3]) << 24);
    return static_cast<std::int32_t>(v);
}

// Canonicalise a register to its 64-bit enclosing form so rax / eax /
// ax / al all compare equal. ZYDIS_REGISTER_NONE stays NONE. Duplicated
// from IL2CPPChainDetector.cpp's anon namespace (same "duplicate the tiny
// helper until a 3rd consumer appears" convention used there for
// EnumerateExecutableSections) — promote to a shared header if a 3rd
// consumer lands.
inline ZydisRegister Canonical(ZydisRegister r) {
    if (r == ZYDIS_REGISTER_NONE) return r;
    ZydisRegister canon = ZydisRegisterGetLargestEnclosing(
        ZYDIS_MACHINE_MODE_LONG_64, r);
    return canon != ZYDIS_REGISTER_NONE ? canon : r;
}

// One entry in the per-image method-pointer index. Sorted by `address` so
// `std::upper_bound` recovers the enclosing method for any IP.
struct MethodIndexEntry {
    std::uintptr_t address;
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
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
FieldInfo* SehNextField(fn_class_get_fields fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehStaticData(fn_class_get_static_data fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehMethodPtr(fn_method_get_pointer fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehMethodPtrFromStruct(const MethodInfo* m) {
    __try { return *reinterpret_cast<void* const*>(m); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
std::uint32_t SehMethodFlags(fn_method_get_flags fn, const MethodInfo* m, std::uint32_t* iflags) {
    __try { return fn(m, iflags); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
std::uint32_t SehMethodToken(fn_method_get_token fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
const char* SehMethodName(fn_method_get_name fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

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

// Locate the largest entry whose address ≤ `ip` (i.e. the enclosing method
// per the contiguous-AOT-emission assumption). Returns nullptr if no
// such entry exists. Caller may double-check by computing `ip - entry.address`
// to reject hits that fall implausibly far past the entry — for v1 we
// trust the index; a missing entry just means the hit's enclosing method
// lives in a different assembly.
const MethodIndexEntry* FindEnclosing(const std::vector<MethodIndexEntry>& idx,
                                       std::uintptr_t ip) noexcept {
    if (idx.empty()) return nullptr;
    auto it = std::upper_bound(
        idx.begin(), idx.end(), ip,
        [](std::uintptr_t value, const MethodIndexEntry& e) {
            return value < e.address;
        });
    if (it == idx.begin()) return nullptr;
    --it;
    return &(*it);
}

struct ResolvedStaticField {
    Il2CppClass*   klass    = nullptr;
    void*          address  = nullptr;
    std::size_t    offset   = 0;
    std::string    typeName;
    std::uint32_t  flags    = 0;
    bool           isStatic = false;
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
    return out;
}

// PE walker — given a module's image base, returns the (RVA, virtualSize)
// of every section flagged as executable. Reads via SafeReadBytes so an
// AC-stripped header still leaves the rest of the DLL intact (the PE
// headers we need are below 4KB and never AC-stripped in practice).
struct ExecutableSection {
    std::uintptr_t va;        // absolute virtual address
    std::size_t    size;      // VirtualSize from the section header
};

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

    // Scan each executable section. Buffer 1 MB at a time to keep peak
    // memory bounded on the 170 MB GameAssembly.dll seen in the test
    // matrix (Songs of Silence). Each chunk reads `want` "owned" bytes
    // plus a 16-byte tail overlap so a 7-byte instruction starting near
    // the chunk boundary still fits without being re-emitted by the
    // next iteration.
    constexpr std::size_t kChunkSize = 1024u * 1024u;
    constexpr std::size_t kOverlap   = 16u;
    std::vector<std::uint8_t> buf(kChunkSize + kOverlap);

    for (const auto& sec : sections) {
        if (result.truncated) break;
        std::size_t pos = 0;
        while (pos < sec.size && !result.truncated) {
            std::size_t want     = (sec.size - pos) < kChunkSize ? (sec.size - pos) : kChunkSize;
            std::size_t readWant = want + kOverlap;
            if (pos + readWant > sec.size) readWant = sec.size - pos;
            const std::uint8_t* secBase = reinterpret_cast<const std::uint8_t*>(sec.va);
            if (!Memory::SafeReadBytes(secBase + pos, buf.data(), readWant)) {
                // AC-stripped page — skip 4 KB and continue.
                pos += 4096u;
                continue;
            }
            // Only emit hits whose *start* falls within the owned portion
            // of the chunk; the overlap bytes are read for context only.
            std::size_t emitEnd = (want > 0) ? want : 0;
            // Need at least 7 bytes from p to read a candidate 7-byte
            // instruction; cap emitEnd to fit.
            if (readWant < 7u) {
                pos += want;
                continue;
            }
            std::size_t scanEnd = std::min(emitEnd, readWant - 7u + 1u);

            for (std::size_t p = 0; p < scanEnd; ++p) {
                if (result.truncated) break;
                std::uint8_t b0 = buf[p];

                // Try 7-byte form FIRST (REX + op + ModRM + disp32). This
                // is the canonical 64-bit form for static-field MOVs; trying
                // it first means the 6-byte form below can use the "prev
                // byte was REX" heuristic to skip the duplicate that would
                // otherwise fire at p+1 when a REX instruction sits at p.
                if (IsRexByte(b0)) {
                    std::uint8_t b1 = buf[p + 1];
                    if ((b1 == 0x8B || b1 == 0x8D || b1 == 0x89) &&
                        IsRipRelativeModrm(buf[p + 2])) {
                        std::int32_t disp = ReadInt32LE(buf.data() + p + 3);
                        std::uintptr_t startIP = sec.va + pos + p;
                        std::uintptr_t endIP   = startIP + 7;
                        std::uintptr_t hitTarget = endIP + static_cast<std::intptr_t>(disp);
                        if (hitTarget == targetAddr) {
                            if (emitHit(startIP, b1) &&
                                result.matches.size() >= limit) {
                                result.truncated = true;
                            }
                            continue;  // don't double-test as 6-byte form
                        }
                    }
                }

                // Try 6-byte form (no REX): op + ModRM + disp32. Skipped
                // when the previous byte is a REX prefix — that would mean
                // the real instruction is the 7-byte form starting one
                // byte earlier, which we already considered.
                if (p > 0 && IsRexByte(buf[p - 1])) continue;

                if ((b0 == 0x8B || b0 == 0x8D || b0 == 0x89) &&
                    IsRipRelativeModrm(buf[p + 1])) {
                    std::int32_t disp = ReadInt32LE(buf.data() + p + 2);
                    std::uintptr_t startIP = sec.va + pos + p;
                    std::uintptr_t endIP   = startIP + 6;
                    std::uintptr_t hitTarget = endIP + static_cast<std::intptr_t>(disp);
                    if (hitTarget == targetAddr) {
                        if (emitHit(startIP, b0) &&
                            result.matches.size() >= limit) {
                            result.truncated = true;
                        }
                    }
                }
            }
            pos += want;
        }
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
    // Stage B (executable scan + Zydis forward-trace): for each candidate
    //   hop-1 load, decode forward via Zydis to confirm hops 2 + 3 with
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
                std::size_t v3MatchingStatic   = 0;  // v3.1 hits
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
                        if (slotAlreadyKnown(ls.slotAddress)) continue;

                        bool matched     = false;
                        bool matchedHow  = false;  // false=runtime, true=static
                        (void)matchedHow;

                        // v3.0 — runtime value match
                        std::uintptr_t slotValue = 0;
                        if (Memory::SafeReadBytes(
                                reinterpret_cast<const void*>(ls.slotAddress),
                                &slotValue, sizeof(slotValue))) {
                            if (slotValue == targetClassVal) {
                                matched = true;
                                ++v3MatchingRuntime;
                            }
                        }

                        // v3.1 — static token decode (only if v3.0 didn't match)
                        if (!matched && ls.tokenKind ==
                                static_cast<std::uint8_t>(MetadataUsageKind::TypeInfo)) {
                            if (Il2CppClass* staticClass =
                                    ResolveTypeInfoSlotClass(ls)) {
                                if (reinterpret_cast<std::uintptr_t>(staticClass)
                                        == targetClassVal) {
                                    matched = true;
                                    matchedHow = true;
                                    ++v3MatchingStatic;
                                }
                            }
                        }

                        if (!matched) continue;

                        ClassPointerSlot extra{};
                        extra.address   = ls.slotAddress;
                        extra.sectionVa = 0;
                        slots.push_back(extra);
                    }
                }
                const std::size_t v3MatchingSlots =
                    v3MatchingRuntime + v3MatchingStatic;

                Log::Write(MagicStrings::LogCategoryWalk,
                    "FieldXref: v2 starting — staticFieldsOffset=" +
                    std::to_string(sfo) + " fieldOffset=" +
                    std::to_string(target.offset) +
                    " v2DirectSlots=" + std::to_string(v2SlotCount) +
                    " v3LazyInitCandidates=" + std::to_string(v3CandidateSlots) +
                    " v3MatchingRuntime=" + std::to_string(v3MatchingRuntime) +
                    " v3MatchingStatic=" + std::to_string(v3MatchingStatic) +
                    " totalSlots=" + std::to_string(slots.size()));

                if (!slots.empty()) {
                    ChainScanStats v2stats{};
                    auto chains = DetectFieldAccessChains(
                        slots, sfo, target.offset, limit, &v2stats);
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

    const auto& r = GetResolver();
    if (!r.resolved) { result.error = "il2cpp runtime not resolved"; return result; }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) { result.error = "il2cpp domain unavailable"; return result; }

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "il2cpp thread attach failed"; return result; }

    ResolvedStaticField target = ResolveInstanceField(image, ns, className, fieldName);
    if (!target.error.empty()) { result.error = std::move(target.error); return result; }
    result.staticAddress = nullptr;  // instance fields have no single storage addr

    const std::size_t fieldOffset = target.offset;
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

    Log::Write(MagicStrings::LogCategoryWalk,
               "FieldXref(instance): target " +
               (ns.empty() ? className : ns + "." + className) + "." + fieldName +
               " offset=" + std::to_string(fieldOffset) +
               " type=" + target.typeName +
               " methodIndex=" + std::to_string(methodIndex.size()));

    ZydisDecoder decoder;
    ZydisDecoderInit(&decoder, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64);

    // Per-method decode range cap. Real Unity AOT methods are almost always
    // < 8 KB; huge switch/Burst methods are rare and clamping them only loses
    // a few far-out hits, which is the right trade for a heuristic.
    constexpr std::size_t kMaxMethodBytes = 8u * 1024u;
    std::vector<std::uint8_t> buf(kMaxMethodBytes);

    std::size_t scannedDeclMethods = 0;
    std::size_t candidateHits      = 0;
    std::size_t droppedKindFilter  = 0;

    for (std::size_t mi = 0; mi < methodIndex.size() && !result.truncated; ++mi) {
        const MethodIndexEntry& m = methodIndex[mi];
        if (m.ns != ns || m.className != className) continue;
        ++scannedDeclMethods;

        const std::uintptr_t methodStart = m.address;
        // end = next-greater address in the FULL sorted index (the next
        // method's entry), clamped to start+cap and the section end.
        std::uintptr_t methodEnd = methodStart + kMaxMethodBytes;
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

        if (!Memory::SafeReadBytes(reinterpret_cast<const void*>(methodStart),
                                   buf.data(), rangeLen)) {
            // AC-stripped page or unreadable — skip this method.
            continue;
        }

        // Linear-decode the method body. Stop at the first int3 (0xCC)
        // padding run or a decode failure (clamped tail) — both signal we've
        // run off the end of the real method, which kills cross-method FPs.
        std::size_t consumed = 0;
        while (consumed < rangeLen && !result.truncated) {
            if (buf[consumed] == 0xCC) break;  // int3 padding → end of method
            ZydisDecodedInstruction instr;
            ZydisDecodedOperand     operands[ZYDIS_MAX_OPERAND_COUNT];
            ZyanStatus status = ZydisDecoderDecodeFull(
                &decoder, buf.data() + consumed, rangeLen - consumed,
                &instr, operands);
            if (!ZYAN_SUCCESS(status)) break;  // clamped tail / bad bytes

            const std::uintptr_t insnIP = methodStart + consumed;

            for (std::uint8_t oi = 0; oi < instr.operand_count_visible; ++oi) {
                const ZydisDecodedOperand& op = operands[oi];
                if (op.type != ZYDIS_OPERAND_TYPE_MEMORY) continue;
                if (op.mem.disp.size == 0) continue;                 // no disp
                if (op.mem.index != ZYDIS_REGISTER_NONE) continue;   // [base+index*s]
                if (static_cast<std::size_t>(op.mem.disp.value) != fieldOffset) continue;
                const ZydisRegister base = Canonical(op.mem.base);
                if (base == ZYDIS_REGISTER_NONE) continue;           // [disp] absolute
                if (base == ZYDIS_REGISTER_RIP)  continue;           // static path
                if (base == ZYDIS_REGISTER_RSP)  continue;           // stack frame
                if (base == ZYDIS_REGISTER_RBP)  continue;           // frame local

                // Classify read vs write from the memory operand's action.
                // lea computes an address (AGEN, no read/write action) — treat
                // as a read, matching the static path's LEA → "read".
                const char* k;
                if (instr.mnemonic == ZYDIS_MNEMONIC_LEA) {
                    k = "read";
                } else if (op.actions & ZYDIS_OPERAND_ACTION_MASK_WRITE) {
                    k = "write";
                } else if (op.actions & ZYDIS_OPERAND_ACTION_MASK_READ) {
                    k = "read";
                } else {
                    continue;  // memory operand neither read nor written — skip
                }

                ++candidateHits;
                if (kindFilter == FieldRefKind::Read  && std::strcmp(k, "read")  != 0) {
                    ++droppedKindFilter; continue;
                }
                if (kindFilter == FieldRefKind::Write && std::strcmp(k, "write") != 0) {
                    ++droppedKindFilter; continue;
                }

                FieldRefMatch hit;
                hit.ns             = m.ns;
                hit.className      = m.className;
                hit.methodName     = m.methodName;
                hit.methodToken    = m.methodToken;
                hit.methodFlags    = m.methodFlags;
                hit.isMethodStatic = m.isMethodStatic;
                hit.methodAddress  = reinterpret_cast<void*>(methodStart);
                hit.hitAddress     = reinterpret_cast<void*>(insnIP);
                hit.isStatic       = false;
                hit.refKind        = k;
                hit.confidence     = "heuristic";
                result.matches.push_back(std::move(hit));
                if (result.matches.size() >= limit) { result.truncated = true; break; }
            }

            consumed += instr.length;
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
