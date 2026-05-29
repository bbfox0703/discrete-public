// SymbolPipeline.cpp — see header for design.

#include "SymbolPipeline.h"

#include <algorithm>
#include <cstring>
#include <sstream>
#include <unordered_map>

namespace Discrete::Phase_D::Symbols {

namespace {

// ASCII-only label sanitizer — mirror of
// ui/UnityDumpUI.Core/Services/Export/SymbolExporter.cs::SanitizeLabel.
// Collapses every non-[A-Za-z0-9_] character to '_' so the output is
// debugger-safe (x64dbg / Ghidra / IDA all require this shape).
std::string Sanitize(const std::string& in) {
    if (in.empty()) return "unknown";
    std::string out;
    out.reserve(in.size());
    for (char c : in) {
        bool ok = (c >= '0' && c <= '9') ||
                  (c >= 'A' && c <= 'Z') ||
                  (c >= 'a' && c <= 'z') ||
                  c == '_';
        out.push_back(ok ? c : '_');
    }
    return out.empty() ? std::string("unknown") : out;
}

std::string BuildClassLabel(const ResolvedClass& c) {
    std::string raw = "Class_";
    if (!c.ns.empty()) raw += c.ns + "_";
    raw += c.name.empty() ? std::string("Anon") : c.name;
    return Sanitize(raw);
}

std::string BuildMethodLabel(const ResolvedMethod& m) {
    std::string raw;
    if (!m.ns.empty()) raw += m.ns + "_";
    raw += m.className.empty() ? std::string("Anon") : m.className;
    raw += "__";
    raw += m.methodName.empty() ? std::string("Anon") : m.methodName;
    // Token disambiguates overloads — see header note on
    // humanSuffix deferred from offline.
    char buf[32];
    ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "_t%08X", m.token);
    raw += buf;
    return Sanitize(raw);
}

// JSON string escape — lazy, matches what nlohmann::json + the live
// .dd64 emitter would write. We only handle backslash, quote, and
// control characters (the only inputs that can appear in IL2CPP
// label / address strings).
std::string EscapeJsonString(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 2);
    for (unsigned char c : s) {
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b";  break;
            case '\f': out += "\\f";  break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "\\u%04X", c);
                    out += buf;
                } else {
                    out.push_back(static_cast<char>(c));
                }
                break;
        }
    }
    return out;
}

// Strip a trailing ".dll" so we can match codeGenModule names ("X.dll")
// against Image names ("X"). Matches the Unity convention — metadata
// Image table stores names without suffix; codeGenModule.moduleName
// always includes ".dll".
std::string StripDllSuffix(const std::string& s) {
    if (s.size() >= 4 &&
        s.compare(s.size() - 4, 4, ".dll") == 0) {
        return s.substr(0, s.size() - 4);
    }
    return s;
}

// v0.2.2 — walk the declaring-type chain to build a fully-qualified
// nested-type name ("Outer/Middle/Leaf"). Mirror of the live UI's
// il2cpp_class_get_declaring_type loop in IL2CPPClassWalker. Uses
// MetadataParser::GetDeclaringTypeIndex which lazy-builds a child→
// parent map from the on-disk nestedTypeIndices[] table (the on-disk
// `TypeDefinition::declaringTypeIndex` is a TypeIndex pointing into
// the runtime types[] table — not directly resolvable offline).
//
// Cap of 16 hops defends against a hypothetical declaring-chain
// cycle; real nested chains are 2-3 levels deep.
std::string BuildNestedTypeName(
    const Discrete::IL2CPP::Metadata::Parser& parser,
    const Discrete::IL2CPP::Metadata::Parser::TypeDefView& leaf) {
    std::string out = leaf.name;
    std::int32_t cursor = parser.GetDeclaringTypeIndex(leaf.index);
    for (int hops = 0; hops < 16 && cursor >= 0; ++hops) {
        auto outer = parser.GetTypeDefinition(cursor);
        if (!outer || outer->name.empty()) break;
        out = outer->name + "/" + out;
        std::int32_t next = parser.GetDeclaringTypeIndex(cursor);
        if (next == cursor) break;  // self-loop guard
        cursor = next;
    }
    return out;
}

// v0.2.2 — namespace of the outermost (top-level) declaring type in
// the chain. Nested types in IL2CPP carry ns="" themselves; the real
// namespace lives on the outermost type. Same logic as the live
// IL2CPPClassWalker / `Class.GetNamespace` traversal.
std::string OutermostNamespace(
    const Discrete::IL2CPP::Metadata::Parser& parser,
    const Discrete::IL2CPP::Metadata::Parser::TypeDefView& leaf) {
    std::int32_t cursor = parser.GetDeclaringTypeIndex(leaf.index);
    if (cursor < 0) return leaf.ns;
    for (int hops = 0; hops < 16; ++hops) {
        auto outer = parser.GetTypeDefinition(cursor);
        if (!outer) break;
        std::int32_t next = parser.GetDeclaringTypeIndex(cursor);
        if (next < 0) return outer->ns;
        if (next == cursor) break;
        cursor = next;
    }
    return leaf.ns;
}

}  // namespace

std::optional<PipelineResult> ResolveSymbols(const std::wstring& metadataPath,
                                              const std::wstring& gaPath,
                                              std::string*        errOut) {
    auto fail = [&](const char* msg) -> std::optional<PipelineResult> {
        if (errOut) *errOut = msg;
        return std::nullopt;
    };

    // -- metadata side --
    using Discrete::IL2CPP::Metadata::Parser;
    Parser parser;
    if (!parser.LoadFromPath(metadataPath)) return fail("metadata load failed");
    if (parser.TypeDefinitionCount() == 0)  return fail("metadata: type-tables not parsed for this version");
    if (parser.ImageCount() == 0)           return fail("metadata: image table empty (pre-v24.2 deferred)");

    // -- PE side --
    std::vector<std::uint8_t> bytes;
    std::string err;
    auto img = PE::ParseFile(gaPath, &bytes, &err);
    if (!img) {
        if (errOut) *errOut = "PE parse failed: " + err;
        return std::nullopt;
    }

    auto modulesOpt = CodeGenModules::FindOffline(bytes.data(), bytes.size(), *img,
                                                   /*minModules=*/2,
                                                   /*maxModules=*/1024,
                                                   &err);
    if (!modulesOpt) {
        if (errOut) *errOut = "codeGenModules scan failed: " + err;
        return std::nullopt;
    }

    // Build name → module index lookup so per-method resolution is O(1).
    // Both sides of the join already include ".dll" suffix (metadata Image
    // table stores names like "Assembly-CSharp.dll" verbatim, and the
    // codeGenModule moduleName field is the same string), so we match
    // directly without normalisation. The earlier StripDllSuffix attempt
    // produced 0% matches on SaGa F2R / Heroes / Songs of Silence —
    // empirically the names are identical, no stripping needed.
    std::unordered_map<std::string, std::size_t> moduleIdxByName;
    moduleIdxByName.reserve(modulesOpt->modules.size());
    for (std::size_t i = 0; i < modulesOpt->modules.size(); ++i) {
        moduleIdxByName.emplace(modulesOpt->modules[i].name, i);
    }

    PipelineResult res;
    res.imagesTotal = parser.ImageCount();

    // -- Class list — one row per TypeDefinition, used by the .dd64
    // class-pointer-labels path. We don't have offline access to the
    // runtime Il2CppClass* pointer (that's a runtime structure built
    // by IL2CPP at startup, not part of the PE); so for offline output
    // we emit class names with `classRva == 0`. The Class_ labels still
    // serve as helpful debugger comments alongside the method labels,
    // and any future Il2CppClass*-pointer-array finder can fill them in.
    res.classesTotal = parser.TypeDefinitionCount();

    // -- Walk every method via the Image table → cross-link via codeGenModules.
    auto images = parser.EnumerateImages();
    for (const auto& image : images) {
        auto it = moduleIdxByName.find(image.name);
        if (it == moduleIdxByName.end()) continue;
        ++res.imagesMatched;

        const auto& module = modulesOpt->modules[it->second];
        // methodPointers[] file-offset base.
        auto mptrsFileOff = (img->imageBase <= module.methodPointersVa)
            ? img->RvaToFileOffset(module.methodPointersVa - img->imageBase)
            : std::nullopt;
        if (!mptrsFileOff) continue;

        // For each type in this image, walk its methods.
        std::int32_t typeEnd = std::min<std::int32_t>(
            image.typeStart + static_cast<std::int32_t>(image.typeCount),
            static_cast<std::int32_t>(parser.TypeDefinitionCount()));
        for (std::int32_t ti = image.typeStart; ti < typeEnd; ++ti) {
            auto type = parser.GetTypeDefinition(ti);
            if (!type) continue;

            // v0.2.2 — compute the fully-qualified nested-type name once
            // per type (e.g. "MainGame/PLY_WORK" instead of just
            // "PLY_WORK"). Namespace lifts from the outermost declaring
            // type — IL2CPP stores `ns` only on the top-level type, not
            // on nested children. Matches the live UI's
            // IL2CPPClassWalker behaviour bit-for-bit.
            std::string qualifiedName = BuildNestedTypeName(parser, *type);
            std::string qualifiedNs   = OutermostNamespace(parser, *type);

            // Emit a class row (regardless of whether it has methods).
            ResolvedClass rc;
            rc.assembly = image.name;
            rc.ns       = qualifiedNs;
            rc.name     = qualifiedName;
            // classRva left 0 (see note above).
            res.classes.push_back(std::move(rc));

            if (type->methodCount == 0) continue;
            auto methods = parser.EnumerateTypeMethods(*type);
            for (const auto& m : methods) {
                ++res.methodsTotal;
                std::int32_t localIdx = m.index - image.methodStart;
                if (localIdx < 0 ||
                    static_cast<std::uint64_t>(localIdx) >= module.methodCount) {
                    continue;
                }
                std::uint64_t methodVa = 0;
                if (*mptrsFileOff + (localIdx + 1) * 8 <= bytes.size()) {
                    std::memcpy(&methodVa,
                                bytes.data() + *mptrsFileOff + localIdx * 8, 8);
                }
                if (methodVa == 0 || methodVa < img->imageBase) {
                    ++res.methodsNullPtr;
                    continue;
                }
                ResolvedMethod row;
                row.assembly   = image.name;
                row.ns         = qualifiedNs;
                row.className  = qualifiedName;
                row.methodName = m.name;
                row.token      = m.token;
                row.isStatic   = (m.flags & 0x10u) != 0;
                row.addressRva = methodVa - img->imageBase;
                res.methods.push_back(std::move(row));
                ++res.methodsResolved;
            }
        }
    }

    return res;
}

std::string RenderDd64(const PipelineResult& r,
                        const std::string& moduleName,
                        std::uint64_t moduleBase) {
    // Mirror of ui/UnityDumpUI.Core/Services/Export/SymbolExporter.cs::
    // RenderX64dbgDatabase. JSON shape: { labels:[...], comments:[...] }.
    // Each label has {module, address, manual, text}. Address is module-
    // relative HEX without "0x" prefix (x64dbg's required format).
    (void)moduleBase;  // for parity with the C# signature — we already
                       // emit module-relative RVAs so moduleBase isn't
                       // needed here.

    std::ostringstream out;
    out << "{\n  \"labels\": [\n";

    bool first = true;
    auto emitLabel = [&](const std::string& label, std::uint64_t rva) {
        if (!first) out << ",\n";
        first = false;
        char hex[32];
        ::_snprintf_s(hex, sizeof(hex), _TRUNCATE, "%llX",
                      static_cast<unsigned long long>(rva));
        out << "    {\n";
        out << "      \"module\": \"" << EscapeJsonString(moduleName) << "\",\n";
        out << "      \"address\": \"" << hex << "\",\n";
        out << "      \"manual\": true,\n";
        out << "      \"text\": \"" << EscapeJsonString(label) << "\"\n";
        out << "    }";
    };

    // Class labels (best-effort: classRva is 0 in offline mode, so we skip
    // them here — Phase D-5 follow-up could add an Il2CppClass*-pointer-
    // array finder to populate these). Method labels carry the load.
    for (const auto& m : r.methods) {
        emitLabel(BuildMethodLabel(m), m.addressRva);
    }

    out << "\n  ],\n  \"comments\": [\n";
    first = true;
    for (const auto& m : r.methods) {
        if (!first) out << ",\n";
        first = false;
        char hex[32];
        ::_snprintf_s(hex, sizeof(hex), _TRUNCATE, "%llX",
                      static_cast<unsigned long long>(m.addressRva));
        std::string qualifier = m.assembly + ":";
        if (!m.ns.empty()) qualifier += m.ns + ".";
        qualifier += m.className;
        out << "    {\n";
        out << "      \"module\": \"" << EscapeJsonString(moduleName) << "\",\n";
        out << "      \"address\": \"" << hex << "\",\n";
        out << "      \"manual\": true,\n";
        out << "      \"text\": \"" << EscapeJsonString(qualifier) << "\"\n";
        out << "    }";
    }
    out << "\n  ]\n}\n";
    return out.str();
}

std::string RenderGhidra(const PipelineResult& r, std::uint64_t moduleBase) {
    // Mirror of SymbolExporter.cs::RenderGhidraSymbols. One symbol per line:
    //   LABEL ABSOLUTE_HEX TYPE   (TYPE = 'f' for function, 'l' for label)
    // Absolute address = moduleBase + RVA.
    std::ostringstream out;
    out << "// Generated by Discrete — Ghidra Symbol Import\n";
    out << "// Import via: ImportSymbolsScript.py\n";
    for (const auto& m : r.methods) {
        char hex[32];
        ::_snprintf_s(hex, sizeof(hex), _TRUNCATE, "0x%llX",
                      static_cast<unsigned long long>(moduleBase + m.addressRva));
        out << BuildMethodLabel(m) << ' ' << hex << " f\n";
    }
    return out.str();
}

std::string RenderIda(const PipelineResult& r, std::uint64_t moduleBase) {
    // Mirror of SymbolExporter.cs::RenderIdaScript. IDC script with
    // MakeName(addr, "label") + MakeComm(addr, "qualifier"). Addresses
    // are absolute (moduleBase + RVA).
    std::ostringstream out;
    out << "#include <idc.idc>\n\n";
    out << "// Generated by Discrete — IDA Symbol Import\n";
    out << "static main() {\n";
    for (const auto& m : r.methods) {
        char hex[32];
        ::_snprintf_s(hex, sizeof(hex), _TRUNCATE, "0x%llX",
                      static_cast<unsigned long long>(moduleBase + m.addressRva));
        out << "    MakeName(" << hex << ", \""
            << EscapeJsonString(BuildMethodLabel(m)) << "\");\n";
        std::string qualifier = m.assembly + ":";
        if (!m.ns.empty()) qualifier += m.ns + ".";
        qualifier += m.className;
        out << "    MakeComm(" << hex << ", \""
            << EscapeJsonString(qualifier) << "\");\n";
    }
    out << "}\n";
    return out.str();
}

}  // namespace Discrete::Phase_D::Symbols
