// MonoFieldXref.cpp — Phase 28a implementation.
//
// IL walker: enumerates every class in `image`, iterates each class's
// declared methods, fetches the method's IL bytecode via
// `mono_method_get_header` + `mono_method_header_get_code`, then walks the
// bytecode op-by-op. At each `ldfld` / `stfld` / `ldsfld` / `stsfld`
// (plus the address-loading variants `ldflda` / `ldsflda`, which count as
// reads), we read the 4-byte little-endian metadata token operand and
// compare against the target field's `mono_class_get_field_token`.
//
// Generic methods are scanned once at the open-generic level — instantiated
// versions share the same IL body, so a token match in the open generic
// counts for every instantiation. Matches dnSpy / ILSpy navigation
// semantics.
//
// Scope (v1): same-assembly references only. Cross-assembly access uses
// MemberRef tokens (0x0Axxxxxx) which would need
// `mono_field_from_token` resolution — deferred to v2 if a consumer asks.

#include "MonoFieldXref.h"

#include "MonoClassWalker.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"

#include <windows.h>

#include <cstdint>
#include <cstring>

namespace Discrete::Mono {

namespace {

// IL opcodes — see ECMA-335 §III.1.2.
constexpr std::uint8_t kOpLdfld      = 0x7B;
constexpr std::uint8_t kOpLdflda     = 0x7C;
constexpr std::uint8_t kOpStfld      = 0x7D;
constexpr std::uint8_t kOpLdsfld     = 0x7E;
constexpr std::uint8_t kOpLdsflda    = 0x7F;
constexpr std::uint8_t kOpStsfld     = 0x80;
constexpr std::uint8_t kOpFePrefix   = 0xFE;

// Operand byte counts for one-byte IL opcodes. Sentinels:
//   -1 = InlineSwitch (4-byte count + 4*count targets)
//   -2 = 0xFE prefix (caller dispatches to two-byte table)
// Unallocated opcodes left at 0 — robust against vendor / future opcodes:
// at worst we mis-advance by a few bytes and miss a field op, but the
// next IL boundary aligns back.
constexpr int kOpLen1[256] = {
    /* 0x00..0x0F */  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
    /* 0x10..0x1F */  1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
    /* 0x20..0x2F */  4, 8, 4, 8, 0, 0, 0, 4, 4, 4, 0, 1, 1, 1, 1, 1,
    /* 0x30..0x3F */  1, 1, 1, 1, 1, 1, 1, 1, 4, 4, 4, 4, 4, 4, 4, 4,
    /* 0x40..0x4F */  4, 4, 4, 4, 4, -1 /*switch*/, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    /* 0x50..0x5F */  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    /* 0x60..0x6F */  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4 /*callvirt*/,
    /* 0x70..0x7F */  4, 4, 4, 4, 4, 4, 0, 0, 0, 4 /*unbox*/, 0, 4 /*ldfld*/, 4, 4, 4, 4,
    /* 0x80..0x8F */  4 /*stsfld*/, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4 /*box*/, 4, 0, 4,
    /* 0x90..0x9F */  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    /* 0xA0..0xAF */  0, 0, 0, 4 /*ldelem*/, 4 /*stelem*/, 4 /*unbox.any*/, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    /* 0xB0..0xBF */  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    /* 0xC0..0xCF */  0, 0, 4 /*refanyval*/, 0, 0, 0, 4 /*mkrefany*/, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    /* 0xD0..0xDF */  4 /*ldtoken*/, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4 /*leave*/, 1 /*leave.s*/, 0,
    /* 0xE0..0xEF */  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    /* 0xF0..0xFF */  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -2 /*fe-prefix*/, 0,
};

// Operand byte counts for two-byte IL opcodes (0xFE prefix + second byte).
constexpr int kOpLen2[32] = {
    /* 0xFE00..0xFE07 */  0, 0, 0, 0, 0, 0, 4 /*ldftn*/, 4 /*ldvirtftn*/,
    /* 0xFE08..0xFE0F */  0, 2 /*ldarg*/, 2 /*ldarga*/, 2 /*starg*/, 2 /*ldloc*/, 2 /*ldloca*/, 2 /*stloc*/, 0 /*localloc*/,
    /* 0xFE10..0xFE17 */  0, 0 /*endfilter*/, 1 /*unaligned.*/, 0 /*volatile.*/, 0 /*tail.*/, 4 /*initobj*/, 4 /*constrained.*/, 0 /*cpblk*/,
    /* 0xFE18..0xFE1F */  0 /*initblk*/, 1 /*no.*/, 0 /*rethrow*/, 0, 4 /*sizeof*/, 0 /*refanytype*/, 0 /*readonly.*/, 0,
};

inline std::uint32_t ReadTokenLE(const std::uint8_t* p) noexcept {
    return  static_cast<std::uint32_t>(p[0])
         | (static_cast<std::uint32_t>(p[1]) <<  8)
         | (static_cast<std::uint32_t>(p[2]) << 16)
         | (static_cast<std::uint32_t>(p[3]) << 24);
}

const char* OpKind(std::uint8_t op) noexcept {
    switch (op) {
        case kOpLdfld:    return "read";
        case kOpLdflda:   return "read";
        case kOpStfld:    return "write";
        case kOpLdsfld:   return "read";
        case kOpLdsflda:  return "read";
        case kOpStsfld:   return "write";
        default:          return "";
    }
}

// Returns how far to advance to reach the next IL instruction starting at
// `*p` (already known to be < end). Outputs the recognized field opcode
// in `*outFieldOp` (0 when not a field op). Returns 0 if the operand bytes
// would extend past `remaining`, signalling end-of-buffer to the caller.
std::size_t IlOpAdvance(const std::uint8_t* p, std::size_t remaining,
                        std::uint8_t* outFieldOp) noexcept {
    if (remaining < 1) { *outFieldOp = 0; return 0; }
    std::uint8_t op = p[0];

    if (op == kOpFePrefix) {
        *outFieldOp = 0;
        if (remaining < 2) return 0;
        std::uint8_t op2 = p[1];
        int operandLen = (op2 < 32) ? kOpLen2[op2] : 0;
        if (operandLen < 0) operandLen = 0;
        std::size_t total = 2u + static_cast<std::size_t>(operandLen);
        return (remaining >= total) ? total : 0;
    }

    *outFieldOp = (OpKind(op)[0] != '\0') ? op : 0;

    int operandLen = kOpLen1[op];
    if (operandLen == -1) {
        // switch: 4-byte count + 4*count targets.
        if (remaining < 5) return 0;
        std::uint32_t count = ReadTokenLE(p + 1);
        std::size_t targetsSize = static_cast<std::size_t>(count) * 4u;
        std::size_t total = 1u + 4u + targetsSize;
        return (remaining >= total) ? total : 0;
    }
    if (operandLen < 0) operandLen = 0;
    std::size_t total = 1u + static_cast<std::size_t>(operandLen);
    return (remaining >= total) ? total : 0;
}

// SEH leaf helpers — each wraps ONE call to a Mono runtime function so the
// __try blocks never share a frame with C++ objects that need destruction.
// Same pattern as MonoClassWalker.cpp.
#pragma warning(push)
#pragma warning(disable: 4509)
MonoMethod* SehNextMethod(fn_class_get_methods fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoMethodHeader* SehGetHeader(fn_method_get_header fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const std::uint8_t* SehGetCode(fn_method_header_get_code fn,
                               MonoMethodHeader* h,
                               std::uint32_t* size, std::uint32_t* stack) {
    __try {
        return reinterpret_cast<const std::uint8_t*>(fn(h, size, stack));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (size  != nullptr) *size  = 0;
        if (stack != nullptr) *stack = 0;
        return nullptr;
    }
}
void SehFreeHeader(fn_metadata_free_mh fn, MonoMethodHeader* h) {
    __try { fn(h); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
MonoClassField* SehNextField(fn_class_get_fields fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
std::uint32_t SehGetFieldToken(fn_class_get_field_token fn, MonoClassField* f) {
    __try { return fn(f); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
#pragma warning(pop)

struct ResolvedField {
    MonoClass*       klass    = nullptr;
    MonoClassField*  field    = nullptr;
    std::uint32_t    token    = 0;
    bool             isStatic = false;
    std::string      typeName;
    int              offset   = 0;
    std::string      error;
};

ResolvedField ResolveTargetField(MonoImage* image,
                                 const std::string& ns,
                                 const std::string& className,
                                 const std::string& fieldName) {
    ResolvedField out;
    if (image == nullptr) { out.error = "image is null"; return out; }
    if (fieldName.empty()) { out.error = "fieldName is empty"; return out; }

    out.klass = FindClass(image, ns, className);
    if (out.klass == nullptr) {
        out.error = "class not found: " + (ns.empty() ? className : ns + "." + className);
        return out;
    }

    const auto& r = GetResolver();
    if (r.class_get_fields == nullptr || r.field_get_name == nullptr) {
        out.error = "mono runtime missing field iteration exports";
        return out;
    }
    if (r.class_get_field_token == nullptr) {
        out.error = "mono_class_get_field_token unbound — cannot resolve target token";
        return out;
    }

    void* iter = nullptr;
    for (;;) {
        MonoClassField* f = SehNextField(r.class_get_fields, out.klass, &iter);
        if (f == nullptr) break;
        const char* name = r.field_get_name(f);
        if (name == nullptr) continue;
        if (fieldName == name) {
            out.field = f;
            out.token = SehGetFieldToken(r.class_get_field_token, f);
            if (r.field_get_flags != nullptr) {
                std::uint32_t flags = r.field_get_flags(f);
                out.isStatic = (flags & 0x10u) != 0;  // FieldAttributes::Static
            }
            if (r.field_get_offset != nullptr) {
                out.offset = r.field_get_offset(f);
            }
            if (r.field_get_type != nullptr && r.type_get_name != nullptr) {
                if (MonoType* ft = r.field_get_type(f)) {
                    if (char* tn = r.type_get_name(ft)) {
                        out.typeName = tn;
                        if (r.free != nullptr) r.free(tn);
                    }
                }
            }
            break;
        }
    }
    if (out.field == nullptr) {
        out.error = "field not found: " + fieldName;
        return out;
    }
    if (out.token == 0) {
        out.error = "mono_class_get_field_token returned 0 for target field";
        return out;
    }
    // FieldAttributes::Literal (0x40) — `const` fields and enum members.
    // The C# compiler inlines literals at every use site (`ldc.i4 <value>`
    // / `ldc.r4` / `ldstr`), so the field's metadata token NEVER appears
    // in any method's IL body. A walk would honestly return 0 matches —
    // semantically correct but misleading to the user, who'd assume the
    // field is unreferenced. Surface the limit explicitly so they know
    // it's a constraint of inlined constants, not a backend miss.
    // Mono reads field flags via `field_get_flags`; we picked them up
    // when iterating above. (The Static bit is 0x10, Literal is 0x40 —
    // checking the Literal bit alone catches both `const` static fields
    // and enum members, which always have Static|Literal set.)
    if (out.field != nullptr) {
        std::uint32_t fflags = 0;
        if (GetResolver().field_get_flags != nullptr) {
            fflags = GetResolver().field_get_flags(out.field);
        }
        if ((fflags & 0x40u) != 0) {
            out.error =
                "literal const field (or enum member) — the C# compiler "
                "inlines the value at every use site (`ldc.i4 <value>` "
                "etc.), so the field's metadata token never appears in IL "
                "and there's nothing for Phase 28 to match against. "
                "This is a semantic limitation of inlined constants, not "
                "a backend bug.";
            return out;
        }
    }
    return out;
}

// Pure function — walks one method's IL body, appends every match into
// `outMatches`. Returns true if the limit cap was hit (truncation flag).
// Caller has already gathered IL bytes + size; this routine touches no
// Mono runtime state, so no SEH guard needed.
bool ScanIlForFieldRefs(const std::uint8_t* code, std::size_t codeSize,
                        std::uint32_t targetToken, FieldRefKind kindFilter,
                        std::size_t matchCap,
                        const std::string& ns, const std::string& className,
                        std::string methodName, std::uint32_t methodToken,
                        std::uint32_t methodFlags, bool isMethodStatic,
                        bool targetIsStatic,
                        std::vector<FieldRefMatch>& outMatches) {
    std::size_t pos = 0;
    while (pos < codeSize) {
        std::uint8_t fieldOp = 0;
        std::size_t advance = IlOpAdvance(code + pos, codeSize - pos, &fieldOp);
        if (advance == 0) break;

        if (fieldOp != 0 && advance >= 5) {
            std::uint32_t tok = ReadTokenLE(code + pos + 1);
            if (tok == targetToken) {
                const char* k = OpKind(fieldOp);
                bool emit = true;
                if (kindFilter == FieldRefKind::Read  && std::strcmp(k, "read")  != 0) emit = false;
                if (kindFilter == FieldRefKind::Write && std::strcmp(k, "write") != 0) emit = false;
                if (emit) {
                    FieldRefMatch hit;
                    hit.ns             = ns;
                    hit.className      = className;
                    hit.methodName     = methodName;
                    hit.methodToken    = methodToken;
                    hit.methodFlags    = methodFlags;
                    hit.isMethodStatic = isMethodStatic;
                    hit.isStatic       = targetIsStatic;
                    hit.ilOffset       = static_cast<std::uint32_t>(pos);
                    hit.refKind        = k;
                    hit.confidence     = "exact";
                    outMatches.push_back(std::move(hit));

                    if (outMatches.size() >= matchCap) return true;
                }
            }
        }
        pos += advance;
    }
    return false;
}

}  // namespace

FindFieldRefsResult FindFieldReferences(MonoImage*         image,
                                        const std::string& ns,
                                        const std::string& className,
                                        const std::string& fieldName,
                                        FieldRefKind       kindFilter,
                                        std::size_t        limit) {
    FindFieldRefsResult result;
    if (image == nullptr) { result.error = "image is null"; return result; }

    const auto& r = GetResolver();
    if (!r.resolved) { result.error = "mono runtime not resolved"; return result; }
    if (r.method_get_header == nullptr || r.method_header_get_code == nullptr) {
        result.error = "mono_method_get_header / mono_method_header_get_code unbound — IL walk unavailable on this runtime";
        return result;
    }
    if (r.class_get_methods == nullptr) {
        result.error = "mono_class_get_methods unbound";
        return result;
    }

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) { result.error = "mono root domain unavailable"; return result; }

    // Outer guard — nested helpers reentrancy-balance via MonoThreadAttach.
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "mono thread attach failed"; return result; }

    ResolvedField target = ResolveTargetField(image, ns, className, fieldName);
    if (!target.error.empty()) { result.error = std::move(target.error); return result; }
    result.fieldToken = target.token;

    auto classes = EnumerateClasses(image);
    for (const auto& ce : classes) {
        if (ce.klass == nullptr) continue;
        if (result.truncated) break;

        void* methIter = nullptr;
        for (;;) {
            MonoMethod* m = SehNextMethod(r.class_get_methods, ce.klass, &methIter);
            if (m == nullptr) break;
            ++result.scannedMethods;

            MonoMethodHeader* hdr = SehGetHeader(r.method_get_header, m);
            if (hdr == nullptr) continue;  // abstract / pinvoke / icall

            std::uint32_t codeSize = 0;
            std::uint32_t maxStack = 0;
            const std::uint8_t* code = SehGetCode(r.method_header_get_code,
                                                  hdr, &codeSize, &maxStack);

            if (code != nullptr && codeSize > 0) {
                // Snapshot method metadata once per method (saves a few SEH
                // calls per matching op and keeps the inner loop tight).
                std::string mname;
                if (r.method_get_name != nullptr) {
                    if (const char* mn = r.method_get_name(m)) mname = mn;
                }
                std::uint32_t mtoken = 0;
                if (r.method_get_token != nullptr) mtoken = r.method_get_token(m);
                std::uint32_t mflags = 0;
                bool mstatic = false;
                if (r.method_get_flags != nullptr) {
                    std::uint32_t iflags = 0;
                    mflags = r.method_get_flags(m, &iflags);
                    mstatic = (mflags & 0x10u) != 0;
                }

                bool capped = ScanIlForFieldRefs(
                    code, codeSize, target.token, kindFilter, limit,
                    ce.ns, ce.name,
                    std::move(mname), mtoken, mflags, mstatic, target.isStatic,
                    result.matches);
                if (capped) result.truncated = true;
            }

            if (r.metadata_free_mh != nullptr) {
                SehFreeHeader(r.metadata_free_mh, hdr);
            }

            if (result.truncated) break;
        }
    }

    result.ok = true;
    return result;
}

}  // namespace Discrete::Mono
