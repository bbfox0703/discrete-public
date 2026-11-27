// MonoFieldXref.cpp — Phase 28a implementation.
//
// IL walker: enumerates every class in `image`, iterates each class's
// declared methods, fetches the method's IL bytecode via
// `mono_method_get_header` + `mono_method_header_get_code`, then walks the
// bytecode op-by-op. At each `ldfld` / `stfld` / `ldsfld` / `stsfld`
// (plus the address-loading variants `ldflda` / `ldsflda`, which count as
// reads), we read the 4-byte little-endian metadata token operand:
//   * FieldDef (0x04xxxxxx): direct comparison against `target.token`.
//   * MemberRef (0x0Axxxxxx, B-M8): resolved once per unique token via
//     `mono_field_from_token` (memoized across the walk) and matched against
//     the target field's identity (field pointer, same-image FieldDef token,
//     or same-image declaring definition `(ns, Outer/Inner)` + field name for
//     closed generic instantiations like `Singleton<Player>.instance`).
//
// Generic methods are scanned once at the open-generic level — instantiated
// versions share the same IL body, so a token match in the open generic
// counts for every instantiation. Matches dnSpy / ILSpy navigation semantics.

#include "MonoFieldXref.h"

#include "MonoClassWalker.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "MonoTypeParser.h"
#include "../shared/FieldXrefHelpers.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/RuntimeHealth.h"
#include "../shared/ScanCancel.h"
#include "../shared/SehFilter.h"

#include <windows.h>

#include <cstdint>
#include <cstring>
#include <unordered_map>

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
        // B-L11: overflow-safe switch advance on both x64 and 32-bit x86.
        if (remaining < 5) return 0;
        std::uint32_t count = ReadTokenLE(p + 1);
        return FieldXref::SafeIlSwitchAdvance(count, remaining);
    }
    if (operandLen < 0) operandLen = 0;
    std::size_t total = 1u + static_cast<std::size_t>(operandLen);
    return (remaining >= total) ? total : 0;
}

// SEH leaf helpers — each wraps ONE call to a Mono runtime function.
#pragma warning(push)
#pragma warning(disable: 4509)
MonoMethod* SehNextMethod(fn_class_get_methods fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoMethodHeader* SehGetHeader(fn_method_get_header fn, MonoMethod* m) {
    __try { return fn(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const std::uint8_t* SehGetCode(fn_method_header_get_code fn,
                               MonoMethodHeader* h,
                               std::uint32_t* size, std::uint32_t* stack) {
    __try {
        return reinterpret_cast<const std::uint8_t*>(fn(h, size, stack));
    } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) {
        if (size  != nullptr) *size  = 0;
        if (stack != nullptr) *stack = 0;
        return nullptr;
    }
}
void SehFreeHeader(fn_metadata_free_mh fn, MonoMethodHeader* h) {
    __try { fn(h); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) {}
}
MonoClassField* SehNextField(fn_class_get_fields fn, MonoClass* k, void** iter) {
    unsigned long code = 0;
    __try { return fn(k, iter); }
    __except ((code = GetExceptionCode()),
              Seh::MemoryAccessExceptionFilter(code, GetExceptionInformation())) {
        RuntimeHealth::MarkRuntimeFault("mono_class_get_fields", code);
        return nullptr;
    }
}
std::uint32_t SehGetFieldToken(fn_class_get_field_token fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
const char* SehFieldName(fn_field_get_name fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
std::uint32_t SehFieldFlags(fn_field_get_flags fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
int SehFieldOffset(fn_field_get_offset fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
MonoType* SehFieldType(fn_field_get_type fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoClassField* SehFieldFromToken(fn_field_from_token fn,
                                  MonoImage* img,
                                  std::uint32_t tok,
                                  MonoClass** outKlass) {
    __try { return fn(img, tok, outKlass, nullptr); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) {
        if (outKlass) *outKlass = nullptr;
        return nullptr;
    }
}
MonoClass* SehFieldGetParent(fn_field_get_parent fn, MonoClassField* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoImage* SehClassImage(fn_class_get_image fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehClassName(fn_class_get_name fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehClassNs(fn_class_get_namespace fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
MonoClass* SehClassNesting(fn_class_get_nesting_type fn, MonoClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehMethodName(fn_method_get_name fn, MonoMethod* m) {
    __try { return fn(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
std::uint32_t SehMethodToken(fn_method_get_token fn, MonoMethod* m) {
    __try { return fn(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
std::uint32_t SehMethodFlags(fn_method_get_flags fn, MonoMethod* m, std::uint32_t* iflags) {
    __try { return fn(m, iflags); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
#pragma warning(pop)

// Build the canonical `(ns, Outer/Inner)` name for `k`, matching
// EnumerateClasses / DescribeClass so nested and generic definitions compare
// consistently regardless of whether the caller passed `Ns.Outer` + `Inner`
// or `Ns` + `Outer/Inner`.
void DescribeClassCanonical(MonoClass* k, std::string& outNs, std::string& outName) {
    outNs.clear();
    outName.clear();
    if (k == nullptr) return;
    const auto& r = GetResolver();
    if (r.class_get_name == nullptr) return;
    const char* leafC = SehClassName(r.class_get_name, k);
    if (leafC == nullptr) return;
    std::string full = leafC;
    MonoClass* cur   = k;
    MonoClass* outer = k;
    if (r.class_get_nesting_type != nullptr) {
        for (int depth = 0; depth < 16; ++depth) {
            MonoClass* nest = SehClassNesting(r.class_get_nesting_type, cur);
            if (nest == nullptr) break;
            const char* nm = SehClassName(r.class_get_name, nest);
            if (nm != nullptr && *nm != '\0') full = std::string{nm} + "/" + full;
            outer = nest;
            cur   = nest;
        }
    }
    outName = std::move(full);
    if (r.class_get_namespace != nullptr) {
        if (const char* nsC = SehClassNs(r.class_get_namespace, outer)) {
            outNs = nsC;
        }
    }
}

struct ResolvedField {
    MonoClass*                   klass    = nullptr;
    MonoClassField*              field    = nullptr;
    std::uint32_t                token    = 0;
    bool                         isStatic = false;
    std::string                  typeName;
    int                          offset   = 0;
    FieldXref::MonoFieldIdentity identity;
    std::string                  error;
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
    std::uint32_t fflags = 0;
    for (;;) {
        MonoClassField* f = SehNextField(r.class_get_fields, out.klass, &iter);
        if (f == nullptr) break;
        const char* name = SehFieldName(r.field_get_name, f);
        if (name == nullptr) continue;
        if (fieldName == name) {
            out.field = f;
            out.token = SehGetFieldToken(r.class_get_field_token, f);
            if (r.field_get_flags != nullptr) {
                fflags = SehFieldFlags(r.field_get_flags, f);
                out.isStatic = (fflags & 0x10u) != 0;  // FieldAttributes::Static
            }
            if (r.field_get_offset != nullptr) {
                out.offset = SehFieldOffset(r.field_get_offset, f);
            }
            if (r.field_get_type != nullptr) {
                if (MonoType* ft = SehFieldType(r.field_get_type, f)) {
                    out.typeName = TypeName(ft);
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

    out.identity.fieldPtr   = out.field;
    out.identity.imagePtr   = (r.class_get_image != nullptr) ? SehClassImage(r.class_get_image, out.klass) : image;
    out.identity.fieldToken = out.token;
    out.identity.fieldName  = fieldName;
    DescribeClassCanonical(out.klass, out.identity.ns, out.identity.className);
    return out;
}

// B-M8: resolve a MemberRef (`0x0Axxxxxx`) operand once per unique token in
// `image`, returning true iff it resolves to `target.identity`. Memoized so
// repeated references pay a single hash lookup.
bool MemberRefMatchesTarget(MonoImage* image,
                            std::uint32_t tok,
                            const FieldXref::MonoFieldIdentity& target,
                            std::unordered_map<std::uint32_t, bool>& cache,
                            std::size_t& unresolvedMemberRefs) {
    auto it = cache.find(tok);
    if (it != cache.end()) return it->second;

    const auto& r = GetResolver();
    if (r.field_from_token == nullptr) {
        ++unresolvedMemberRefs;
        cache.emplace(tok, false);
        return false;
    }

    MonoClass* retKlass = nullptr;
    MonoClassField* rf = SehFieldFromToken(r.field_from_token, image, tok, &retKlass);
    if (rf == nullptr) {
        ++unresolvedMemberRefs;
        cache.emplace(tok, false);
        return false;
    }
    if (retKlass == nullptr && r.field_get_parent != nullptr) {
        retKlass = SehFieldGetParent(r.field_get_parent, rf);
    }

    FieldXref::MonoFieldIdentity cand;
    cand.fieldPtr = rf;
    if (retKlass != nullptr) {
        cand.imagePtr = (r.class_get_image != nullptr) ? SehClassImage(r.class_get_image, retKlass) : nullptr;
        DescribeClassCanonical(retKlass, cand.ns, cand.className);
    }
    if (r.class_get_field_token != nullptr) {
        cand.fieldToken = SehGetFieldToken(r.class_get_field_token, rf);
    }
    if (r.field_get_name != nullptr) {
        if (const char* fn = SehFieldName(r.field_get_name, rf)) {
            cand.fieldName = fn;
        }
    }

    const bool matched = FieldXref::MatchesMonoFieldIdentity(cand, target);
    cache.emplace(tok, matched);
    return matched;
}

template <typename TokenMatcherFn>
bool ScanIlForFieldRefs(const std::uint8_t* code, std::size_t codeSize,
                        TokenMatcherFn&& tokenMatches, FieldRefKind kindFilter,
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
            if (tokenMatches(tok)) {
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
    if (limit == 0) limit = 200;

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

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "mono thread attach failed"; return result; }

    ResolvedField target = ResolveTargetField(image, ns, className, fieldName);
    if (!target.error.empty()) { result.error = std::move(target.error); return result; }
    result.fieldToken = target.token;

    // B-M8: per-walk memo of MemberRef (0x0Axxxxxx) tokens in `image`.
    std::unordered_map<std::uint32_t, bool> memberRefCache;
    std::size_t unresolvedMemberRefs = 0;
    auto tokenMatches = [&](std::uint32_t tok) -> bool {
        if (tok == target.token) return true;
        if (FieldXref::IsMemberRefToken(tok)) {
            return MemberRefMatchesTarget(image, tok, target.identity,
                                          memberRefCache, unresolvedMemberRefs);
        }
        return false;
    };

    auto classes = EnumerateClasses(image);
    for (const auto& ce : classes) {
        if (ce.klass == nullptr) continue;
        if (ScanCancel::IsCancelRequested()) {
            result.cancelled = true;
            result.ok = false;
            result.matches.clear();
            return result;
        }
        if (result.truncated) break;

        void* methIter = nullptr;
        for (;;) {
            if (ScanCancel::IsCancelRequested()) {
                result.cancelled = true;
                result.ok = false;
                result.matches.clear();
                return result;
            }
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
                std::string mname;
                if (r.method_get_name != nullptr) {
                    if (const char* mn = SehMethodName(r.method_get_name, m)) mname = mn;
                }
                std::uint32_t mtoken = 0;
                if (r.method_get_token != nullptr) mtoken = SehMethodToken(r.method_get_token, m);
                std::uint32_t mflags = 0;
                bool mstatic = false;
                if (r.method_get_flags != nullptr) {
                    std::uint32_t iflags = 0;
                    mflags = SehMethodFlags(r.method_get_flags, m, &iflags);
                    mstatic = (mflags & 0x10u) != 0;
                }

                bool capped = ScanIlForFieldRefs(
                    code, codeSize, tokenMatches, kindFilter, limit,
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

    // B-M8: surface a clear advisory when the declaring class is generic and
    // either mono_field_from_token was unbound or open-generic MemberRef
    // tokens (using type parameters like !0) couldn't be resolved without a
    // generic context.
    if (FieldXref::IsGenericTypeName(target.identity.className)) {
        if (r.field_from_token == nullptr) {
            result.note =
                "Declaring class is generic: references to fields on generic types use "
                "MemberRef (0x0A) tokens in IL, and this Mono runtime does not export "
                "mono_field_from_token — results may miss generic references.";
        } else if (unresolvedMemberRefs > 0 && result.matches.empty()) {
            result.note =
                "Declaring class is generic: closed-generic MemberRef (0x0A) references "
                "were scanned via mono_field_from_token, but open-generic self-references "
                "(bound to type parameters like !0) require a generic context and may not "
                "appear here.";
        }
    }

    result.ok = true;
    return result;
}

}  // namespace Discrete::Mono
