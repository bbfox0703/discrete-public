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

#include "IL2CPPClassWalker.h"
#include "IL2CPPDomain.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstring>

namespace UnityCEDumper::IL2CPP {

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
    void* staticBase = SehStaticData(r.class_get_static_data, out.klass);
    if (staticBase == nullptr) {
        out.error = "il2cpp_class_get_static_data returned null for declaring class";
        return out;
    }
    out.address = static_cast<std::uint8_t*>(staticBase) + out.offset;
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

    const std::uintptr_t targetAddr = reinterpret_cast<std::uintptr_t>(target.address);

    // Helper: emit one hit if it survives kind + assembly attribution.
    // Captures all the lambda state to keep the inner scan loop tight.
    auto emitHit = [&](std::uintptr_t startIP, std::uint8_t op) -> bool {
        const char* k = OpcodeKind(op);
        if (kindFilter == FieldRefKind::Read  && std::strcmp(k, "read")  != 0) return false;
        if (kindFilter == FieldRefKind::Write && std::strcmp(k, "write") != 0) return false;
        const MethodIndexEntry* enc = FindEnclosing(methodIndex, startIP);
        if (enc == nullptr) return false;   // hit in a method outside the target image
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

    result.ok = true;
    return result;
}

}  // namespace UnityCEDumper::IL2CPP
