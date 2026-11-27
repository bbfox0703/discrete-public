// MethodSignatureFormat.h — Phase 18 shared helpers for findMethodInstances.
// Header-only so both backend MethodFinder implementations link against the
// same logic; keeps the wire response (humanSuffix / signatureString) byte-
// identical across Mono and IL2CPP.
#pragma once

#include <cstddef>
#include <string>
#include <vector>

namespace Discrete::Shared {

// Human-friendly suffix piece for a single managed type. C# keyword for the
// well-known primitives (including primitive arrays like `System.Int32[]` ->
// `intArr`); generic instantiations preserve shortened type arguments (e.g.
// `List`1<System.Int32>` -> `List_int`, `Dictionary`2<System.String, System.Int32>`
// -> `Dictionary_string_int`); nested types take the innermost segment after
// `/` or `.` outside generic brackets BEFORE stripping arity backticks so
// `Ns.Outer`1/Inner` becomes `Inner` rather than colliding on `Outer`.
// The result is sanitised to [A-Za-z0-9_] so it can be used directly as a CE
// symbol-name fragment.
namespace detail {

inline std::string TrimAsciiSpaces(const std::string& s) {
    std::size_t lo = 0;
    while (lo < s.size() && (s[lo] == ' ' || s[lo] == '\t' || s[lo] == '\r' || s[lo] == '\n')) {
        ++lo;
    }
    std::size_t hi = s.size();
    while (hi > lo && (s[hi - 1] == ' ' || s[hi - 1] == '\t' || s[hi - 1] == '\r' || s[hi - 1] == '\n')) {
        --hi;
    }
    return s.substr(lo, hi - lo);
}

inline std::vector<std::string> SplitTopLevelGenericArgs(const std::string& inner) {
    std::vector<std::string> parts;
    int angleDepth = 0;
    int bracketDepth = 0;
    std::size_t start = 0;
    for (std::size_t i = 0; i < inner.size(); ++i) {
        const char c = inner[i];
        if (c == '<') ++angleDepth;
        else if (c == '>' && angleDepth > 0) --angleDepth;
        else if (c == '[') ++bracketDepth;
        else if (c == ']' && bracketDepth > 0) --bracketDepth;
        else if (c == ',' && angleDepth == 0 && bracketDepth == 0) {
            parts.push_back(inner.substr(start, i - start));
            start = i + 1;
        }
    }
    if (start <= inner.size()) {
        parts.push_back(inner.substr(start));
    }
    return parts;
}

// Find the matching opening delimiter (`openCh`) for the closing delimiter at
// `s.back()`, respecting nested `<...>` and `[...]`. Returns npos if unmatched.
inline std::size_t FindMatchingOpen(const std::string& s, char openCh, char closeCh) {
    if (s.empty() || s.back() != closeCh) return std::string::npos;
    int depth = 0;
    for (std::size_t i = s.size(); i-- > 0; ) {
        const char c = s[i];
        if (c == closeCh) {
            ++depth;
        } else if (c == openCh) {
            --depth;
            if (depth == 0) return i;
        }
    }
    return std::string::npos;
}

// True when `inner` (the slice inside `[...]`) is empty or consists solely of
// commas (i.e. a C# / CLR array rank specifier `[]`, `[,]`, `[,,]`), rather than
// Mono's generic argument list `[System.Int32,System.String]`.
inline bool IsArrayRankSpec(const std::string& inner) {
    for (char c : inner) {
        if (c != ',') return false;
    }
    return true;
}

inline std::string ShortenManagedTypeNameImpl(const std::string& raw, int depth) {
    static constexpr struct { const char* in; const char* out; } kMap[] = {
        {"System.Boolean", "bool"},   {"System.Byte",    "byte"},
        {"System.SByte",   "sbyte"},  {"System.Int16",   "short"},
        {"System.UInt16",  "ushort"}, {"System.Int32",   "int"},
        {"System.UInt32",  "uint"},   {"System.Int64",   "long"},
        {"System.UInt64",  "ulong"},  {"System.Single",  "float"},
        {"System.Double",  "double"}, {"System.Char",    "char"},
        {"System.String",  "string"}, {"System.Object",  "object"},
        {"System.Void",    "void"},   {"System.IntPtr",  "IntPtr"},
        {"System.UIntPtr", "UIntPtr"},
    };

    std::string base = TrimAsciiSpaces(raw);

    // 1. Peel trailing array specifiers (`[]`, `[,]`, jagged `[][]`) BEFORE
    //    checking the primitive table so `System.Int32[]` becomes `intArr`
    //    instead of `Int32Arr` (B-L10).
    std::size_t arrayDims = 0;
    while (!base.empty() && base.back() == ']') {
        const std::size_t open = FindMatchingOpen(base, '[', ']');
        if (open == std::string::npos) break;
        const std::string inside = base.substr(open + 1, base.size() - open - 2);
        if (!IsArrayRankSpec(inside)) break;  // Mono generic args, not an array
        base.resize(open);
        ++arrayDims;
    }

    auto appendArraySuffix = [&](std::string s) {
        for (std::size_t i = 0; i < arrayDims; ++i) s += "Arr";
        return s;
    };

    for (const auto& p : kMap) {
        if (base == p.in) return appendArraySuffix(p.out);
    }

    // 2. Extract trailing generic argument list (`<T1, T2>` or Mono's `[T1,T2]`)
    //    before taking the leaf segment so generic arguments distinguish
    //    `List<int>` (`List_int`) from `List<string>` (`List_string`) (B-L10).
    std::vector<std::string> genericArgShorts;
    if (depth < 8 && !base.empty()) {
        std::size_t open = std::string::npos;
        if (base.back() == '>') {
            open = FindMatchingOpen(base, '<', '>');
        } else if (base.back() == ']') {
            const std::size_t cand = FindMatchingOpen(base, '[', ']');
            if (cand != std::string::npos) {
                const std::string inside = base.substr(cand + 1, base.size() - cand - 2);
                if (!inside.empty() && !IsArrayRankSpec(inside)) {
                    open = cand;
                }
            }
        }
        if (open != std::string::npos) {
            const std::string inside = base.substr(open + 1, base.size() - open - 2);
            base.resize(open);
            for (const auto& arg : SplitTopLevelGenericArgs(inside)) {
                const std::string trimmed = TrimAsciiSpaces(arg);
                if (!trimmed.empty()) {
                    genericArgShorts.push_back(ShortenManagedTypeNameImpl(trimmed, depth + 1));
                }
            }
        }
    }

    // 3. Take the last segment after '/' or '.' at bracket depth 0 BEFORE
    //    stripping generic arity backticks, so `Ns.Outer`1/Inner` yields
    //    `Inner` instead of truncating at `Outer`1` (B-L10).
    {
        int angleDepth = 0;
        int bracketDepth = 0;
        std::size_t lastSep = std::string::npos;
        for (std::size_t i = 0; i < base.size(); ++i) {
            const char c = base[i];
            if (c == '<') ++angleDepth;
            else if (c == '>' && angleDepth > 0) --angleDepth;
            else if (c == '[') ++bracketDepth;
            else if (c == ']' && bracketDepth > 0) --bracketDepth;
            else if ((c == '.' || c == '/') && angleDepth == 0 && bracketDepth == 0) {
                lastSep = i;
            }
        }
        if (lastSep != std::string::npos) {
            base = base.substr(lastSep + 1);
        }
    }

    // 4. Strip remaining generic arity / unclosed brackets on the leaf segment.
    if (auto lt = base.find('<'); lt != std::string::npos) base.resize(lt);
    if (auto bt = base.find('`'); bt != std::string::npos) base.resize(bt);

    std::string out;
    out.reserve(base.size() + arrayDims * 3 + genericArgShorts.size() * 8);
    for (char c : base) {
        const unsigned char uc = static_cast<unsigned char>(c);
        const bool isAlnum =
            (uc >= '0' && uc <= '9') || (uc >= 'A' && uc <= 'Z') || (uc >= 'a' && uc <= 'z');
        out.push_back((isAlnum || uc == '_') ? c : '_');
    }
    if (out.empty()) out = "T";
    for (const auto& ga : genericArgShorts) {
        out += "_";
        out += ga;
    }
    return appendArraySuffix(std::move(out));
}

}  // namespace detail

inline std::string ShortenManagedTypeName(const std::string& full) {
    return detail::ShortenManagedTypeNameImpl(full, 0);
}

// 0 args  → "noargs"
// 1 arg   → "<Short>"
// N same  → "<N><Short>"      (e.g. "3float")
// N mixed → "<S0>_<S1>_..."   (e.g. "string_Card_Point")
inline std::string MakeHumanSuffix(const std::vector<std::string>& paramTypes) {
    if (paramTypes.empty()) return "noargs";

    std::vector<std::string> shorts;
    shorts.reserve(paramTypes.size());
    for (const auto& p : paramTypes) shorts.push_back(ShortenManagedTypeName(p));

    bool allSame = true;
    for (std::size_t i = 1; i < shorts.size(); ++i) {
        if (shorts[i] != shorts[0]) { allSame = false; break; }
    }
    if (allSame) {
        if (shorts.size() == 1) return shorts[0];
        return std::to_string(shorts.size()) + shorts[0];
    }
    std::string out;
    for (std::size_t i = 0; i < shorts.size(); ++i) {
        if (i > 0) out += "_";
        out += shorts[i];
    }
    return out;
}

// Display-only signature string: "(System.Single, System.Single, System.Single)".
// Empty params → "()".
inline std::string MakeSignatureString(const std::vector<std::string>& paramTypes) {
    std::string out = "(";
    for (std::size_t i = 0; i < paramTypes.size(); ++i) {
        if (i > 0) out += ", ";
        out += paramTypes[i];
    }
    out += ")";
    return out;
}

}  // namespace Discrete::Shared
