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
// well-known primitives; otherwise the last segment after '.' or '/' with
// generic backticks (`Foo`1[Bar]`) and `<...>` instantiations stripped.
// Trailing `[]` becomes `Arr`. The result is sanitised to [A-Za-z0-9_] so it
// can be used directly as a CE symbol-name fragment.
inline std::string ShortenManagedTypeName(const std::string& full) {
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
    for (const auto& p : kMap) {
        if (full == p.in) return p.out;
    }

    bool isArr = false;
    std::string base = full;
    if (base.size() >= 2 && base.compare(base.size() - 2, 2, "[]") == 0) {
        base.resize(base.size() - 2);
        isArr = true;
    }
    if (auto lt = base.find('<'); lt != std::string::npos) base.resize(lt);
    if (auto bt = base.find('`'); bt != std::string::npos) base.resize(bt);
    if (auto dot = base.find_last_of("./"); dot != std::string::npos) base = base.substr(dot + 1);

    std::string out;
    out.reserve(base.size() + (isArr ? 3 : 0));
    for (char c : base) {
        const unsigned char uc = static_cast<unsigned char>(c);
        const bool isAlnum =
            (uc >= '0' && uc <= '9') || (uc >= 'A' && uc <= 'Z') || (uc >= 'a' && uc <= 'z');
        out.push_back((isAlnum || uc == '_') ? c : '_');
    }
    if (out.empty()) out = "T";
    if (isArr) out += "Arr";
    return out;
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
