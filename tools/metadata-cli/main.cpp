// metadata-cli — Phase 13. Standalone parser for IL2CPP global-metadata.dat
// files. Dumps assemblies / classes / fields / strip-stats from a metadata
// blob WITHOUT injecting into any process.
//
// Use cases:
//   * CI dumping (run against a game backup, capture JSON for diffing).
//   * Static analysis of stripped/obfuscated metadata before launching the
//     game.
//   * Sanity-check that MetadataParser produces the same view of metadata
//     as the live IL2CPP runtime (compare CLI output vs the DLL's
//     getMetadataStats / getClasses).
//
// Subcommands:
//   metadata-cli info  <spec>                — version + table sizes
//   metadata-cli stats <spec>                — strip/obfuscation heuristic
//   metadata-cli types <spec>                — JSON line per type
//   metadata-cli type  <spec> <ns> <name>    — fields + defaults of one type
//
// <spec> is resolved by PathResolver: a full file path, a game install root,
// or a Steam game name all work — see PathResolver.h for the exact rules.
//
// Output format: line-oriented JSON to stdout. Errors → stderr + exit 1.
// No host process is touched — Logger is intentionally not initialised so
// per-PID log dirs aren't created for a CLI invocation.

#include "../../dll/src/il2cpp/MetadataParser.h"
#include "PEParser.h"
#include "RegistrationFinder.h"
#include "CodeGenModulesFinder.h"
#include "SymbolPipeline.h"
#include "PathResolver.h"

#include <algorithm>
#include <fstream>
#include <vector>

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <optional>
#include <string>
#include <string_view>

using Discrete::IL2CPP::Metadata::Parser;
namespace Resolve = Discrete::CLI::PathResolve;

namespace {

// --ascii: emit only 7-bit ASCII, escaping every non-ASCII character as
// \uXXXX. Output then survives any shell, console code page, or transport,
// because nothing outside 0x00-0x7F is ever written.
//
// Why this exists: PowerShell does not let a native program write to the
// console directly — it captures stdout into a pipe and re-decodes the bytes
// using [Console]::OutputEncoding. That value is cached when the shell
// starts, so a session launched under a legacy code page keeps decoding as
// (say) Big5 even after `chcp 65001` reports UTF-8. Our UTF-8 bytes are then
// misread, and a legacy double-byte lead byte swallows the following ASCII
// character — which silently eats a JSON closing quote and breaks parsing.
// Escaping sidesteps the whole problem; JSON parsers decode \uXXXX natively.
bool g_ascii = false;

void PutJsonString(const std::string& s) {
    std::fputc('"', stdout);
    if (g_ascii) {
        // Work in UTF-16 so each escape is one code unit. Astral characters
        // come out as a surrogate pair — two \uXXXX escapes — which is
        // exactly how JSON represents them.
        for (wchar_t wc : Resolve::Utf8ToWide(s)) {
            auto u = static_cast<unsigned>(wc);
            switch (u) {
                case L'\\': std::fputs("\\\\", stdout); continue;
                case L'"':  std::fputs("\\\"", stdout); continue;
                case L'\b': std::fputs("\\b",  stdout); continue;
                case L'\f': std::fputs("\\f",  stdout); continue;
                case L'\n': std::fputs("\\n",  stdout); continue;
                case L'\r': std::fputs("\\r",  stdout); continue;
                case L'\t': std::fputs("\\t",  stdout); continue;
                default: break;
            }
            if (u < 0x20 || u > 0x7E) std::fprintf(stdout, "\\u%04X", u);
            else                      std::fputc(static_cast<char>(u), stdout);
        }
        std::fputc('"', stdout);
        return;
    }
    for (unsigned char c : s) {
        switch (c) {
            case '\\': std::fputs("\\\\", stdout); break;
            case '"':  std::fputs("\\\"", stdout); break;
            case '\b': std::fputs("\\b",  stdout); break;
            case '\f': std::fputs("\\f",  stdout); break;
            case '\n': std::fputs("\\n",  stdout); break;
            case '\r': std::fputs("\\r",  stdout); break;
            case '\t': std::fputs("\\t",  stdout); break;
            default:
                if (c < 0x20) std::fprintf(stdout, "\\u%04X", c);
                else          std::fputc(static_cast<char>(c), stdout);
                break;
        }
    }
    std::fputc('"', stdout);
}

void PutJsonWide(const std::wstring& w) {
    PutJsonString(Resolve::WideToUtf8(w));
}

// Resolve a <spec> argument into the artefact path a subcommand needs.
// Prints the failure diagnostic (and, for anything but a verbatim file path,
// a one-line provenance note) to stderr so stdout stays pure JSON.
std::optional<std::wstring> ResolveSpec(const std::wstring& spec, Resolve::Target target) {
    std::string err;
    auto r = Resolve::Resolve(spec, target, &err);
    if (!r) {
        std::fprintf(stderr, "metadata-cli: %s\n", err.empty() ? "path resolution failed" : err.c_str());
        return std::nullopt;
    }
    if (r->origin != Resolve::Origin::ExplicitFile) {
        std::fprintf(stderr, "metadata-cli: using %s  [%s]\n",
                     Resolve::WideToUtf8(r->path).c_str(), r->note.c_str());
    }
    return r->path;
}

int CmdInfo(const std::wstring& path) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed — file missing, not metadata, sanity mismatch, "
                   "or unsupported version (>31 needs the v38 section-based header — see "
                   "docs/il2cpp-metadata-crosscheck.md)\n", stderr);
        return 1;
    }
    // Phase 14c shipped v24 / v29 / v31 type-table parsing. v27 (Unity 2020)
    // still skipped — no test target locally. Anything else falls through
    // to the strings-only path with a stderr note.
    std::uint32_t typeCount = p.TypeDefinitionCount();
    bool typeTablesParsed = typeCount > 0;
    std::string v24Label = p.v24SubVersionLabel();
    // For v24 metadata only, emit a "v24_sub_version" field so consumers
    // can tell apart Unity-2018-era v24.0 / v24.1 (with extra deprecated
    // struct fields) from the standard 92-byte v24.2-v24.5. Auto-detected
    // by Parser::DetectV24Variant() at load time.
    if (!v24Label.empty()) {
        std::printf("{\"version\":%d,\"v24_sub_version\":\"%s\","
                    "\"loaded\":true,"
                    "\"type_tables_parsed\":%s,"
                    "\"type_definition_count\":%u,"
                    "\"string_literal_count\":%u}\n",
                    p.version(), v24Label.c_str(),
                    typeTablesParsed ? "true" : "false",
                    typeCount,
                    p.StringLiteralCount());
    } else {
        std::printf("{\"version\":%d,\"loaded\":true,"
                    "\"type_tables_parsed\":%s,"
                    "\"type_definition_count\":%u,"
                    "\"string_literal_count\":%u}\n",
                    p.version(),
                    typeTablesParsed ? "true" : "false",
                    typeCount,
                    p.StringLiteralCount());
    }
    if (!typeTablesParsed) {
        // Two reasons this fires:
        //   1. Version dispatch doesn't ship a TypeDefinition layout (e.g.
        //      a brand new version > 39, or v16-v23 / v25-v26 / v28 /
        //      v30 — string heap still parses).
        //   2. Layout *was* dispatched but ValidateTypeTable() rejected
        //      the table because TypeDef[0] failed the `<Module>` /
        //      token=0x02000001 invariant. Surfaces Unity-build-specific
        //      variants like Unity 2018.1's v24, whose stride differs
        //      from the 2019.x v24 our layout is calibrated against.
        // The DLL log records which case; from the CLI just point users
        // at the log for the per-version detail.
        std::fprintf(stderr,
            "metadata-cli: note — version %d type tables not parsed. Either "
            "the version has no TypeDefinition layout shipped, or the table "
            "failed the TypeDef[0]='<Module>'/token=0x02000001 sanity check "
            "(layout-stride mismatch — see MetadataParser log). String-literal "
            "queries still work.\n",
            p.version());
    }
    return 0;
}

int CmdStats(const std::wstring& path) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed — see `info` subcommand for diagnostics, "
                   "or check version is in [16, 31]\n", stderr);
        return 1;
    }
    // Replicates the heuristic from ExportAPI.cpp's getMetadataStats so a
    // CLI run produces the exact same verdict as the live DLL would.
    std::uint32_t total            = 0;
    std::uint32_t withNamespace    = 0;
    std::uint32_t singleChar       = 0;
    std::uint32_t shortTwoChar     = 0;
    std::uint32_t compilerGen      = 0;
    std::uint32_t numericLead      = 0;
    std::uint64_t totalNameLen     = 0;
    std::size_t   minNameLen       = static_cast<std::size_t>(-1);
    std::size_t   maxNameLen       = 0;

    std::uint32_t count = p.TypeDefinitionCount();
    for (std::uint32_t i = 0; i < count; ++i) {
        auto td = p.GetTypeDefinition(static_cast<std::int32_t>(i));
        if (!td) continue;
        ++total;
        if (!td->ns.empty()) ++withNamespace;
        const std::string& name = td->name;
        if (name.empty()) continue;
        if (name[0] == '<') { ++compilerGen; continue; }
        if (name[0] >= '0' && name[0] <= '9') ++numericLead;
        if      (name.size() == 1) ++singleChar;
        else if (name.size() == 2) ++shortTwoChar;
        totalNameLen += name.size();
        if (name.size() < minNameLen) minNameLen = name.size();
        if (name.size() > maxNameLen) maxNameLen = name.size();
    }

    std::uint32_t denom = (total > compilerGen) ? (total - compilerGen) : 0u;
    double ratio = (denom > 0)
        ? static_cast<double>(singleChar + shortTwoChar) / static_cast<double>(denom)
        : 0.0;
    const char* verdict = (ratio > 0.30) ? "heavy_strip"
                        : (ratio > 0.10) ? "partial_strip"
                                         : "clean";

    std::printf("{\"total_classes\":%u,\"with_namespace\":%u,"
                "\"single_char_names\":%u,\"short_names_2\":%u,"
                "\"compiler_generated\":%u,\"numeric_lead\":%u,"
                "\"min_name_length\":%zu,\"max_name_length\":%zu,"
                "\"avg_name_length\":%.2f,"
                "\"strip_ratio\":%.4f,\"verdict\":\"%s\"}\n",
                total, withNamespace, singleChar, shortTwoChar,
                compilerGen, numericLead,
                (minNameLen == static_cast<std::size_t>(-1) ? 0u : minNameLen),
                maxNameLen,
                (denom > 0 ? static_cast<double>(totalNameLen) / static_cast<double>(denom) : 0.0),
                ratio, verdict);
    return 0;
}

int CmdTypes(const std::wstring& path) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed — see `info` subcommand for diagnostics, "
                   "or check version is in [16, 31]\n", stderr);
        return 1;
    }
    std::uint32_t count = p.TypeDefinitionCount();
    for (std::uint32_t i = 0; i < count; ++i) {
        auto td = p.GetTypeDefinition(static_cast<std::int32_t>(i));
        if (!td) continue;
        std::fputs("{\"index\":", stdout);
        std::printf("%d,\"namespace\":", td->index);
        PutJsonString(td->ns);
        std::fputs(",\"name\":", stdout);
        PutJsonString(td->name);
        std::printf(",\"field_count\":%u,\"method_count\":%u,\"token\":%u}\n",
                    td->fieldCount, td->methodCount, td->token);
    }
    return 0;
}

// Phase D-5.1 smoke — list every Image (assembly DLL) in the metadata.
// Used to validate the new MetadataParser image-table read across the
// v29 / v31 / v39 corpus before D-5.2's cross-linker leans on it.
int CmdImages(const std::wstring& path) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed — see `info` subcommand\n", stderr);
        return 1;
    }
    std::uint32_t count = p.ImageCount();
    std::printf("{\"version\":%d,\"image_count\":%u,\"images\":[",
                p.version(), count);
    bool first = true;
    for (std::uint32_t i = 0; i < count; ++i) {
        auto img = p.GetImage(static_cast<std::int32_t>(i));
        if (!img) continue;
        if (!first) std::fputc(',', stdout);
        first = false;
        std::fputs("{\"index\":", stdout);
        std::printf("%d,\"name\":", img->index);
        PutJsonString(img->name);
        std::printf(",\"type_start\":%d,\"type_count\":%u,"
                    "\"method_start\":%d,\"method_end\":%d,"
                    "\"token\":%u}",
                    img->typeStart, img->typeCount,
                    img->methodStart, img->methodEnd, img->token);
    }
    std::fputs("]}\n", stdout);
    return 0;
}

int CmdMethods(const std::wstring& path) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed — see `info` subcommand for diagnostics, "
                   "or check version is in [16, 39]\n", stderr);
        return 1;
    }
    // One JSON line per MethodDefinition. Includes the declaring type's
    // FQN for usability (declaringTypeIndex is otherwise opaque without
    // a follow-up GetTypeDefinition call). Parameter types stay as
    // raw indices — they reference the runtime Il2CppType table which
    // lives in the binary, not metadata.dat (deferred to Phase D).
    std::uint32_t count = p.MethodDefinitionCount();
    for (std::uint32_t i = 0; i < count; ++i) {
        auto md = p.GetMethodDefinition(static_cast<std::int32_t>(i));
        if (!md) continue;
        std::string declNs, declName;
        if (auto td = p.GetTypeDefinition(md->declaringTypeIndex)) {
            declNs   = td->ns;
            declName = td->name;
        }
        std::fputs("{\"index\":", stdout);
        std::printf("%d,\"declaring_namespace\":", md->index);
        PutJsonString(declNs);
        std::fputs(",\"declaring_type\":", stdout);
        PutJsonString(declName);
        std::fputs(",\"name\":", stdout);
        PutJsonString(md->name);
        std::printf(",\"return_type_index\":%d,\"parameter_count\":%u,"
                    "\"token\":%u,\"flags\":%u}\n",
                    md->returnTypeIndex, md->parameterCount,
                    md->token, md->flags);
    }
    return 0;
}

int CmdMethod(const std::wstring& path,
              const std::string& ns,
              const std::string& typeName,
              const std::string& methodName) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed — see `info` subcommand for diagnostics\n", stderr);
        return 1;
    }
    auto td = p.FindTypeByName(ns, typeName);
    if (!td) {
        std::fprintf(stderr, "metadata-cli: type not found: %s.%s\n", ns.c_str(), typeName.c_str());
        return 1;
    }
    auto methods = p.EnumerateTypeMethods(*td);
    // Filter by name. IL2CPP supports overloading — multiple methods can
    // share a name with different parameter signatures, so we emit an
    // array of overloads (each with its parameter list).
    std::fputs("{\"namespace\":", stdout);
    PutJsonString(td->ns);
    std::fputs(",\"type\":", stdout);
    PutJsonString(td->name);
    std::fputs(",\"method_name\":", stdout);
    PutJsonString(methodName);
    std::fputs(",\"overloads\":[", stdout);
    bool firstM = true;
    std::uint32_t matched = 0;
    for (const auto& m : methods) {
        if (m.name != methodName) continue;
        if (!firstM) std::fputc(',', stdout);
        firstM = false;
        ++matched;
        std::printf("{\"index\":%d,\"token\":%u,"
                    "\"return_type_index\":%d,"
                    "\"flags\":%u,\"parameters\":[",
                    m.index, m.token, m.returnTypeIndex, m.flags);
        auto params = p.EnumerateMethodParameters(m);
        bool firstP = true;
        for (const auto& pp : params) {
            if (!firstP) std::fputc(',', stdout);
            firstP = false;
            std::fputs("{\"name\":", stdout);
            PutJsonString(pp.name);
            std::printf(",\"index\":%d,\"type_index\":%d,\"token\":%u}",
                        pp.index, pp.typeIndex, pp.token);
        }
        std::fputs("]}", stdout);
    }
    std::fputs("]}\n", stdout);
    if (matched == 0) {
        std::fprintf(stderr,
            "metadata-cli: no method named '%s' on %s.%s (type has %u methods total)\n",
            methodName.c_str(), ns.c_str(), typeName.c_str(),
            static_cast<unsigned>(methods.size()));
        return 1;
    }
    return 0;
}

int CmdType(const std::wstring& path, const std::string& ns, const std::string& name) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed — see `info` subcommand for diagnostics, "
                   "or check version is in [16, 31]\n", stderr);
        return 1;
    }
    auto td = p.FindTypeByName(ns, name);
    if (!td) {
        std::fprintf(stderr, "metadata-cli: type not found: %s.%s\n", ns.c_str(), name.c_str());
        return 1;
    }
    auto fields = p.EnumerateTypeFields(*td);
    std::fputs("{\"namespace\":", stdout);
    PutJsonString(td->ns);
    std::fputs(",\"name\":", stdout);
    PutJsonString(td->name);
    std::printf(",\"index\":%d,\"token\":%u,\"fields\":[", td->index, td->token);
    bool first = true;
    for (const auto& f : fields) {
        if (!first) std::fputc(',', stdout);
        first = false;
        std::fputs("{\"name\":", stdout);
        PutJsonString(f.name);
        std::printf(",\"index\":%d,\"type_index\":%d,\"token\":%u",
                    f.index, f.typeIndex, f.token);
        // Decode any FieldDefault that looks string-shaped — numeric
        // defaults need the runtime types[] table which the offline tool
        // can't see, so we limit ourselves to strings.
        auto def = p.FindFieldDefault(f.index);
        if (def) {
            std::printf(",\"has_default\":true");
            auto sv = p.TryDecodeStringDefault(def->dataIndex);
            if (sv) {
                std::fputs(",\"default_string\":", stdout);
                PutJsonString(*sv);
            }
        }
        std::fputc('}', stdout);
    }
    std::fputs("]}\n", stdout);
    return 0;
}

// Phase D step 1+2 — minimal PE parser surface. Reads GameAssembly.dll's
// DOS/NT/section headers and dumps a JSON description. Set up for the
// follow-up `registration` + `symbols` subcommands (Phase D steps 3-5),
// but useful on its own for sanity-checking that a file looks like an
// IL2CPP binary before launching the full pipeline.
int CmdPeInfo(const std::wstring& path) {
    using namespace Discrete::Phase_D::PE;

    std::vector<std::uint8_t> bytes;
    std::string err;
    auto imgOpt = ParseFile(path, &bytes, &err);
    if (!imgOpt) {
        std::fprintf(stderr, "metadata-cli: pe-info failed: %s\n",
                     err.empty() ? "unknown error" : err.c_str());
        return 1;
    }
    const Image& img = *imgOpt;

    // Spot-detect "looks like an IL2CPP GameAssembly.dll": scan the .rdata
    // section for the `il2cpp_init` export-name string. Real GameAssembly
    // .dlls export 100s of il2cpp_* symbols; PE puts export-name strings
    // in .rdata. Bounded by section size, so this scales to 170 MB games
    // (Songs of Silence) without blowing up the wall-clock — a 35 MB
    // .rdata linear scan completes in well under a second.
    bool hasIl2CppMarker = false;
    {
        const char* needle = "il2cpp_init";
        const std::size_t needleLen = std::strlen(needle);
        // Scan every initialized-data section: typical games have one
        // .rdata, but some have multiple (.rodata variants) so iterate.
        for (const auto& s : img.sections) {
            if (s.rawSize < needleLen) continue;
            // Skip exec sections — needle definitely won't be in code.
            constexpr std::uint32_t kExec = 0x20000000u; // IMAGE_SCN_MEM_EXECUTE
            if (s.characteristics & kExec) continue;
            std::size_t end = std::min<std::size_t>(
                static_cast<std::size_t>(s.fileOffset) + s.rawSize,
                bytes.size());
            std::size_t start = static_cast<std::size_t>(s.fileOffset);
            if (start >= end) continue;
            for (std::size_t i = start; i + needleLen <= end; ++i) {
                if (std::memcmp(bytes.data() + i, needle, needleLen) == 0) {
                    hasIl2CppMarker = true; break;
                }
            }
            if (hasIl2CppMarker) break;
        }
    }

    const char* machineStr =
        img.machine == Machine::Amd64 ? "x64" :
        img.machine == Machine::I386  ? "x86" : "unknown";

    std::printf("{\"machine\":\"%s\",\"is64Bit\":%s,"
                "\"imageBase\":\"0x%016llX\","
                "\"entryPointRva\":\"0x%08X\","
                "\"sizeOfImage\":%u,"
                "\"sectionCount\":%zu,"
                "\"hasIl2CppMarker\":%s,"
                "\"sections\":[",
                machineStr,
                img.is64Bit ? "true" : "false",
                static_cast<unsigned long long>(img.imageBase),
                img.entryPointRva,
                img.sizeOfImage,
                img.sections.size(),
                hasIl2CppMarker ? "true" : "false");

    bool first = true;
    for (const auto& s : img.sections) {
        if (!first) std::fputc(',', stdout);
        first = false;
        std::fputs("{\"name\":", stdout);
        PutJsonString(s.name);
        std::printf(",\"rva\":\"0x%08X\","
                    "\"virtualSize\":%u,"
                    "\"fileOffset\":\"0x%08X\","
                    "\"rawSize\":%u,"
                    "\"characteristics\":\"0x%08X\"}",
                    s.virtualAddress, s.virtualSize,
                    s.fileOffset, s.rawSize, s.characteristics);
    }
    std::fputs("]}\n", stdout);
    return 0;
}

// Phase D step 3 — offline Il2CppMetadataRegistration finder. Combines
// the Phase D-1 PE parser with a port of the runtime's structural-shape
// scanner (see dll/src/il2cpp/IL2CPPMetadataRegistration.cpp), but
// adapted for file bytes: pointer fields in the on-disk struct hold
// absolute VAs assuming load at the PE's preferred imageBase, so we
// translate VA → RVA → file-offset via the PE section table.
int CmdRegistration(const std::wstring& path) {
    using namespace Discrete::Phase_D;

    std::vector<std::uint8_t> bytes;
    std::string err;
    auto imgOpt = PE::ParseFile(path, &bytes, &err);
    if (!imgOpt) {
        std::fprintf(stderr, "metadata-cli: pe-info failed: %s\n",
                     err.empty() ? "unknown error" : err.c_str());
        return 1;
    }
    const PE::Image& img = *imgOpt;

    err.clear();
    auto found = Registration::FindOffline(bytes.data(), bytes.size(), img, &err);
    if (!found) {
        std::fprintf(stderr, "metadata-cli: registration finder failed: %s\n",
                     err.empty() ? "unknown error" : err.c_str());
        // Emit a partial JSON so consumers can still see what was tried.
        std::printf("{\"found\":false,\"image_base\":\"0x%016llX\","
                    "\"section_count\":%zu}\n",
                    static_cast<unsigned long long>(img.imageBase),
                    img.sections.size());
        return 1;
    }
    const Registration::Found& f = *found;

    std::printf("{\"found\":true,"
                "\"image_base\":\"0x%016llX\","
                "\"registration_rva\":\"0x%08llX\","
                "\"registration_va\":\"0x%016llX\","
                "\"types_count\":%u,"
                "\"types_array_rva\":\"0x%08llX\","
                "\"types_array_va\":\"0x%016llX\","
                "\"section\":",
                static_cast<unsigned long long>(img.imageBase),
                static_cast<unsigned long long>(f.regRva),
                static_cast<unsigned long long>(img.imageBase + f.regRva),
                f.typesCount,
                static_cast<unsigned long long>(f.typesArrayRva),
                static_cast<unsigned long long>(f.typesArrayVa));
    PutJsonString(f.sectionName);
    std::printf(",\"near_misses\":%d}\n", f.nearMissCount);
    return 0;
}

// Phase D step 4 — offline codeGenModules finder. Locates the IL2CPP
// `Il2CppCodeRegistration::codeGenModules` array (v24.2+) by scanning
// for runs of pointers to structs whose first field resolves to a
// ".dll" string. For each module, emits the name + native methodPointers
// RVA + count + a sample of the first 3 method RVAs. Foundation for the
// final cross-link to metadata MethodDefinitions (Phase D-5).
int CmdCodeModules(const std::wstring& path) {
    using namespace Discrete::Phase_D;

    std::vector<std::uint8_t> bytes;
    std::string err;
    auto imgOpt = PE::ParseFile(path, &bytes, &err);
    if (!imgOpt) {
        std::fprintf(stderr, "metadata-cli: code-modules pe-parse failed: %s\n",
                     err.empty() ? "unknown error" : err.c_str());
        return 1;
    }
    const PE::Image& img = *imgOpt;

    err.clear();
    auto found = CodeGenModules::FindOffline(bytes.data(), bytes.size(), img,
                                              /*minModules=*/2, /*maxModules=*/1024,
                                              &err);
    if (!found) {
        std::fprintf(stderr, "metadata-cli: code-modules finder failed: %s\n",
                     err.empty() ? "unknown error" : err.c_str());
        std::printf("{\"found\":false,\"image_base\":\"0x%016llX\"}\n",
                    static_cast<unsigned long long>(img.imageBase));
        return 1;
    }
    const CodeGenModules::Found& f = *found;

    std::printf("{\"found\":true,"
                "\"image_base\":\"0x%016llX\","
                "\"code_gen_modules_array_rva\":\"0x%08llX\","
                "\"code_gen_modules_array_va\":\"0x%016llX\","
                "\"module_count\":%zu,"
                "\"section\":",
                static_cast<unsigned long long>(img.imageBase),
                static_cast<unsigned long long>(f.arrayRva),
                static_cast<unsigned long long>(f.arrayVa),
                f.moduleCount);
    PutJsonString(f.sectionName);
    std::fputs(",\"modules\":[", stdout);
    bool firstMod = true;
    for (const auto& m : f.modules) {
        if (!firstMod) std::fputc(',', stdout);
        firstMod = false;
        std::fputs("{\"name\":", stdout);
        PutJsonString(m.name);
        std::printf(",\"module_rva\":\"0x%08llX\","
                    "\"method_count\":%llu,"
                    "\"method_pointers_rva\":\"0x%08llX\","
                    "\"sample_methods\":[",
                    static_cast<unsigned long long>(m.modulePtrRva),
                    static_cast<unsigned long long>(m.methodCount),
                    static_cast<unsigned long long>(m.methodPointersRva));

        // Print first 3 method RVAs from the methodPointers[] table.
        // The pointers are absolute VAs in the file (post-relocation),
        // so we convert each to an RVA before emitting. Zero-valued
        // entries (some methods are abstract / external-only and have
        // no AOT body) are emitted as "0x00000000" so the JSON shape
        // stays uniform.
        auto methodPtrsFileOff = (img.imageBase <= m.methodPointersVa)
            ? img.RvaToFileOffset(m.methodPointersVa - img.imageBase)
            : std::nullopt;
        bool firstM = true;
        std::size_t sampleN = std::min<std::size_t>(3, m.methodCount);
        for (std::size_t i = 0; i < sampleN; ++i) {
            if (!firstM) std::fputc(',', stdout);
            firstM = false;
            std::uint64_t methodVa = 0;
            if (methodPtrsFileOff &&
                *methodPtrsFileOff + (i + 1) * 8 <= bytes.size()) {
                std::memcpy(&methodVa, bytes.data() + *methodPtrsFileOff + i * 8, 8);
            }
            std::uint64_t methodRva =
                (methodVa >= img.imageBase) ? (methodVa - img.imageBase) : 0;
            std::printf("\"0x%08llX\"",
                        static_cast<unsigned long long>(methodRva));
        }
        std::fputs("]}", stdout);
    }
    std::fputs("]}\n", stdout);
    return 0;
}

// Phase D-5 final — `symbols` subcommand. End-to-end pipeline:
//   GameAssembly.dll (PE) + global-metadata.dat → method→RVA cross-link
//   → write .dd64 + Ghidra symbols.txt + IDA .idc to `<prefix>` paths.
//
// Usage:
//   metadata-cli symbols <spec> <output-prefix>
//   metadata-cli symbols <GameAssembly.dll> <global-metadata.dat> <output-prefix>
//
// The short form resolves both inputs from one <spec> (game root, Steam game
// name, or either of the two files). The long form stays supported for
// scripts that already pass both paths, and for the odd install where the
// two artefacts don't sit under a common root.
//
// Produces three files: <prefix>.dd64, <prefix>.ghidra.txt, <prefix>.idc.
// Stdout is a JSON stats summary so the caller can see resolution rate
// (resolved / total methods) without having to count file rows.
int CmdSymbols(int argc, wchar_t** argv) {
    std::wstring gaPath, metadataPath, outPrefix;

    if (argc == 4) {
        // Short form: <spec> <output-prefix>.
        std::string err;
        auto pair = Resolve::ResolvePair(argv[2], &err);
        if (!pair) {
            std::fprintf(stderr, "metadata-cli: %s\n",
                         err.empty() ? "path resolution failed" : err.c_str());
            return 1;
        }
        gaPath       = pair->gameAssembly;
        metadataPath = pair->metadata;
        outPrefix    = argv[3];
        std::fprintf(stderr, "metadata-cli: using %s + %s  [%s]\n",
                     Resolve::WideToUtf8(pair->gameAssembly).c_str(),
                     Resolve::WideToUtf8(pair->metadata).c_str(),
                     pair->note.c_str());
    } else if (argc >= 5) {
        // Long form: each path still goes through the resolver so a game
        // root works in either slot.
        auto ga = ResolveSpec(argv[2], Resolve::Target::GameAssembly);
        if (!ga) return 1;
        auto md = ResolveSpec(argv[3], Resolve::Target::Metadata);
        if (!md) return 1;
        gaPath       = *ga;
        metadataPath = *md;
        outPrefix    = argv[4];
    } else {
        std::fputs(
            "USAGE: metadata-cli symbols <spec> <output-prefix>\n"
            "       metadata-cli symbols <GameAssembly.dll> <global-metadata.dat> <output-prefix>\n",
            stderr);
        return 2;
    }

    if (gaPath.empty() || metadataPath.empty() || outPrefix.empty()) {
        std::fprintf(stderr, "metadata-cli: symbols requires non-empty paths\n");
        return 2;
    }

    std::string err;
    auto r = Discrete::Phase_D::Symbols::ResolveSymbols(metadataPath, gaPath, &err);
    if (!r) {
        std::fprintf(stderr, "metadata-cli: symbols pipeline failed: %s\n",
                     err.empty() ? "unknown error" : err.c_str());
        return 1;
    }

    // We need imageBase from the PE for absolute-address formats (Ghidra
    // + IDA emit absolute, .dd64 stays module-relative). Re-parse the PE
    // header — cheap, just reads the DOS/NT bytes again.
    std::vector<std::uint8_t> peBytes;
    auto peImg = Discrete::Phase_D::PE::ParseFile(gaPath, &peBytes, &err);
    if (!peImg) {
        std::fprintf(stderr, "metadata-cli: re-parse for imageBase failed: %s\n", err.c_str());
        return 1;
    }
    std::uint64_t imageBase = peImg->imageBase;

    // Write the three formats. `\n` translation lives at the OS layer
    // for ofstream in text mode; we want LF for cross-tool compat
    // (matches the C# SymbolExporter which writes UTF-8 without BOM).
    auto writeFile = [&](const wchar_t* suffix, const std::string& body) -> bool {
        std::wstring path = outPrefix + suffix;
        std::ofstream f(path, std::ios::binary);   // MSVC accepts a wide path
        if (!f) {
            std::fprintf(stderr, "metadata-cli: cannot open %s for write\n",
                         Resolve::WideToUtf8(path).c_str());
            return false;
        }
        f.write(body.data(), static_cast<std::streamsize>(body.size()));
        return f.good();
    };

    std::string dd64   = Discrete::Phase_D::Symbols::RenderDd64(*r, "GameAssembly.dll", imageBase);
    std::string ghidra = Discrete::Phase_D::Symbols::RenderGhidra(*r, imageBase);
    std::string ida    = Discrete::Phase_D::Symbols::RenderIda(*r, imageBase);

    bool ok = writeFile(L".dd64", dd64)
           && writeFile(L".ghidra.txt", ghidra)
           && writeFile(L".idc", ida);
    if (!ok) return 1;

    // Stats line for the CLI consumer.
    std::printf("{\"ok\":true,"
                "\"image_base\":\"0x%016llX\","
                "\"images_total\":%zu,"
                "\"images_matched\":%zu,"
                "\"classes_total\":%zu,"
                "\"methods_total\":%zu,"
                "\"methods_resolved\":%zu,"
                "\"methods_null_ptr\":%zu,"
                "\"resolve_ratio\":%.4f,"
                "\"output_prefix\":",
                static_cast<unsigned long long>(imageBase),
                r->imagesTotal, r->imagesMatched,
                r->classesTotal,
                r->methodsTotal, r->methodsResolved, r->methodsNullPtr,
                r->methodsTotal > 0
                    ? static_cast<double>(r->methodsResolved) / static_cast<double>(r->methodsTotal)
                    : 0.0);
    PutJsonWide(outPrefix);
    std::fputs("}\n", stdout);
    return 0;
}

// `resolve` — diagnostic surface for the path rules. Shows what a <spec>
// maps to without parsing anything, so a user can check auto-detection
// before running a long dump. Exits 1 only when *neither* artefact is found.
int CmdResolve(const std::wstring& spec) {
    std::string errMeta, errGa;
    auto meta = Resolve::Resolve(spec, Resolve::Target::Metadata,     &errMeta);
    auto ga   = Resolve::Resolve(spec, Resolve::Target::GameAssembly, &errGa);

    // Report how the *spec* was read, not how the re-resolve below ran.
    std::optional<Resolve::Origin> specOrigin;
    std::wstring specRoot;
    std::string  specNote;
    if (meta || ga) {
        const Resolve::Resolved& s = meta ? *meta : *ga;
        specOrigin = s.origin;
        specRoot   = s.gameRoot;
        specNote   = s.note;
    }

    // An existing file is taken verbatim for *whatever* target was asked
    // for — correct for the parsing subcommands (the user named that exact
    // file), but here it would report the same path in both slots. Pin the
    // named file to the slot its extension implies and re-resolve the other
    // one from the derived install root.
    auto endsWithI = [](const std::wstring& s, const wchar_t* suf) {
        std::size_t n = std::wcslen(suf);
        return s.size() >= n && ::_wcsnicmp(s.c_str() + (s.size() - n), suf, n) == 0;
    };
    if (meta && meta->origin == Resolve::Origin::ExplicitFile && !meta->gameRoot.empty()) {
        if (endsWithI(meta->path, L".dat")) {
            errGa.clear();
            ga = Resolve::Resolve(meta->gameRoot, Resolve::Target::GameAssembly, &errGa);
        } else if (endsWithI(meta->path, L".dll")) {
            errMeta.clear();
            auto reMeta = Resolve::Resolve(meta->gameRoot, Resolve::Target::Metadata, &errMeta);
            meta = reMeta;
        }
    }

    if (!meta && !ga) {
        std::fprintf(stderr, "metadata-cli: %s\n",
                     errMeta.empty() ? "path resolution failed" : errMeta.c_str());
        std::fputs("{\"resolved\":false}\n", stdout);
        return 1;
    }

    const char* originStr =
        specOrigin == Resolve::Origin::ExplicitFile ? "explicit-file" :
        specOrigin == Resolve::Origin::SteamName    ? "steam-name"    : "game-root";

    std::fputs("{\"resolved\":true,\"spec\":", stdout);
    PutJsonWide(spec);
    std::fputs(",\"origin\":", stdout);
    PutJsonString(originStr);
    std::fputs(",\"game_root\":", stdout);
    PutJsonWide(specRoot);
    std::fputs(",\"global_metadata\":", stdout);
    if (meta) PutJsonWide(meta->path); else std::fputs("null", stdout);
    std::fputs(",\"game_assembly\":", stdout);
    if (ga)   PutJsonWide(ga->path);   else std::fputs("null", stdout);
    std::fputs(",\"note\":", stdout);
    PutJsonString(specNote);
    std::fputs("}\n", stdout);

    // Surface the half that missed — an IL2CPP dump needs both, a Mono game
    // legitimately has neither of these two files.
    if (!meta) std::fprintf(stderr, "metadata-cli: note — %s\n", errMeta.c_str());
    if (!ga)   std::fprintf(stderr, "metadata-cli: note — %s\n", errGa.c_str());
    return 0;
}

// `steam-games` — one JSON line per install Steam reports. The machine-readable
// half of the discoverability answer for "what name do I type?".
int CmdSteamGames() {
    auto games = Resolve::EnumerateSteamGames();
    for (const auto& g : games) {
        std::fputs("{\"app_id\":", stdout);
        if (g.appId.empty()) std::fputs("null", stdout); else PutJsonWide(g.appId);
        std::fputs(",\"name\":", stdout);
        PutJsonWide(g.name);
        std::fputs(",\"install_dir\":", stdout);
        PutJsonWide(g.installDir);
        std::fputs(",\"library\":", stdout);
        PutJsonWide(g.library);
        std::fputs("}\n", stdout);
    }
    if (games.empty()) {
        std::fputs("metadata-cli: no Steam installs found (Steam not installed, or "
                   "HKCU\\Software\\Valve\\Steam unreadable)\n", stderr);
        return 1;
    }
    return 0;
}

// Terminal columns a string occupies. UTF-16 code units in the CJK / Hangul /
// fullwidth blocks render double-width, so a naive .size() misaligns any table
// containing them — and Steam store names routinely do (活俠傳).
std::size_t DisplayWidth(const std::wstring& s) {
    std::size_t w = 0;
    for (wchar_t c : s) {
        auto u = static_cast<unsigned>(c);
        // Low surrogate: the pair was already counted on the high half.
        if (u >= 0xDC00 && u <= 0xDFFF) continue;
        bool wide =
            (u >= 0x1100 && u <= 0x115F) ||   // Hangul Jamo
            (u >= 0x2E80 && u <= 0xA4CF) ||   // CJK radicals .. Yi
            (u >= 0xAC00 && u <= 0xD7A3) ||   // Hangul syllables
            (u >= 0xD800 && u <= 0xDBFF) ||   // high surrogate (assume wide: emoji / CJK ext)
            (u >= 0xF900 && u <= 0xFAFF) ||   // CJK compatibility ideographs
            (u >= 0xFE30 && u <= 0xFE4F) ||   // CJK compatibility forms
            (u >= 0xFF00 && u <= 0xFF60) ||   // fullwidth forms
            (u >= 0xFFE0 && u <= 0xFFE6);     // fullwidth signs
        w += wide ? 2 : 1;
    }
    return w;
}

// A table cell, rendered per the --ascii setting. Under --ascii a non-ASCII
// name becomes its \uXXXX escapes: not pretty, but unambiguous and immune to
// the shell's decoding — and column widths still line up, because the escaped
// form is plain ASCII whose display width is its length.
std::string RenderCell(const std::wstring& s) {
    if (!g_ascii) return Resolve::WideToUtf8(s);
    std::string out;
    char buf[8];
    for (wchar_t wc : s) {
        auto u = static_cast<unsigned>(wc);
        if (u >= 0x20 && u <= 0x7E) {
            out.push_back(static_cast<char>(u));
        } else {
            std::snprintf(buf, sizeof buf, "\\u%04X", u);
            out += buf;
        }
    }
    return out;
}

std::size_t CellWidth(const std::wstring& s) {
    return g_ascii ? RenderCell(s).size() : DisplayWidth(s);
}

void PutPadded(const std::wstring& s, std::size_t column) {
    std::fputs(RenderCell(s).c_str(), stdout);
    std::size_t w = CellWidth(s);
    for (std::size_t i = w; i < column; ++i) std::fputc(' ', stdout);
}

// `steam-vdf` — human-readable dump of what Steam's VDF files declare:
// every library folder from libraryfolders.vdf, and under each, the
// appmanifest name -> install target folder mapping. This is the "what do I
// type?" listing; `steam-games` is the same data as JSON for scripts.
//
// Unlike the matcher, this includes manifests whose install folder is gone —
// flagged with `!` — because a stale manifest is genuinely in the VDF and
// otherwise shows up only as a confusing absence.
int CmdSteamVdf() {
    auto libs  = Resolve::EnumerateSteamLibraryRoots();
    auto games = Resolve::EnumerateSteamGames(/*includeMissing=*/true);

    if (libs.empty()) {
        std::fputs("metadata-cli: no Steam installation found (checked "
                   "HKCU\\Software\\Valve\\Steam and HKLM\\SOFTWARE\\Valve\\Steam)\n", stderr);
        return 1;
    }

    std::printf("Steam libraries: %zu   (apps: %zu)\n", libs.size(), games.size());

    std::size_t missing = 0;
    for (const std::wstring& lib : libs) {
        // Rows for this library, in enumeration order.
        std::vector<const Resolve::SteamGame*> rows;
        for (const auto& g : games) {
            if (g.library == lib) rows.push_back(&g);
        }

        std::fputc('\n', stdout);
        std::fputs("[", stdout);
        std::fputs(Resolve::WideToUtf8(lib).c_str(), stdout);
        std::printf("]  %zu apps\n", rows.size());
        if (rows.empty()) continue;

        // Size the columns to the content — names and appids vary a lot and
        // truncating a name would defeat the point of the listing.
        std::size_t wId = std::strlen("APPID");
        std::size_t wNm = std::strlen("NAME");
        for (const auto* g : rows) {
            wId = std::max(wId, CellWidth(g->appId));
            wNm = std::max(wNm, CellWidth(g->name));
        }

        std::fputs("  ", stdout);
        PutPadded(L"APPID", wId + 2);
        PutPadded(L"NAME", wNm + 2);
        std::fputs("INSTALL FOLDER\n", stdout);

        for (const auto* g : rows) {
            if (!g->exists) ++missing;
            std::fputs(g->exists ? "  " : "! ", stdout);
            PutPadded(g->appId.empty() ? L"-" : g->appId, wId + 2);
            PutPadded(g->name, wNm + 2);
            std::fputs(RenderCell(g->installDir).c_str(), stdout);
            std::fputc('\n', stdout);
        }
    }

    if (missing > 0) {
        std::fprintf(stderr,
            "metadata-cli: note — %zu entr%s marked `!` have an appmanifest but no "
            "install folder (uninstalled, or moved outside Steam). They are not "
            "matchable by name.\n",
            missing, missing == 1 ? "y" : "ies");
    }
    return 0;
}

// Every string we emit is UTF-8, but a console renders bytes through its
// *output code page* — cp950 / cp932 / cp1252 by default. A UTF-8 name then
// shows as mojibake, and worse: a legacy double-byte lead byte swallows the
// following ASCII character, so `"活俠傳"` loses its closing quote and the
// JSON stops parsing.
//
// Switch the console to UTF-8 for the duration of the run and put it back
// afterwards (the code page belongs to the console, not to us — leaving it
// flipped would surprise the next command). If the user already runs a UTF-8
// console, we touch nothing.
//
// stdout also goes unbuffered while it's a console: the CRT converts each
// write from the console CP, and a multi-byte sequence split across a stdio
// flush boundary would corrupt. Unbuffered means every write we make is a
// complete string. Redirected output keeps normal buffering — there's no
// conversion in that path, so splitting is harmless, and `types` / `methods`
// dump tens of thousands of lines where buffering matters.
class ConsoleUtf8Scope {
public:
    ConsoleUtf8Scope() {
        auto isConsole = [](DWORD stdHandle) {
            HANDLE h = ::GetStdHandle(stdHandle);
            DWORD mode = 0;
            return h != nullptr && h != INVALID_HANDLE_VALUE && ::GetConsoleMode(h, &mode) != 0;
        };
        bool consoleOut = isConsole(STD_OUTPUT_HANDLE);
        bool consoleErr = isConsole(STD_ERROR_HANDLE);
        if (!consoleOut && !consoleErr) return;

        UINT cp = ::GetConsoleOutputCP();
        if (cp != CP_UTF8 && ::SetConsoleOutputCP(CP_UTF8)) prevCp_ = cp;
        if (consoleOut) std::setvbuf(stdout, nullptr, _IONBF, 0);
    }

    ~ConsoleUtf8Scope() {
        std::fflush(stdout);
        std::fflush(stderr);
        if (prevCp_ != 0) ::SetConsoleOutputCP(prevCp_);
    }

    ConsoleUtf8Scope(const ConsoleUtf8Scope&)            = delete;
    ConsoleUtf8Scope& operator=(const ConsoleUtf8Scope&) = delete;

private:
    UINT prevCp_ = 0;   // 0 = nothing to restore
};

// Deliberately pure 7-bit ASCII — no em dashes, no arrows. This is the text a
// user reads *while* their shell is mangling UTF-8, so it must never be
// mojibake itself. Goes to stdout for an explicit `--help`, stderr otherwise.
void Usage(std::FILE* out) {
    std::fputs(
        "metadata-cli - offline IL2CPP global-metadata.dat parser + PE inspector\n\n"
        "USAGE:\n"
        "  metadata-cli info    <spec>\n"
        "  metadata-cli stats   <spec>\n"
        "  metadata-cli types   <spec>\n"
        "  metadata-cli type    <spec> <namespace> <name>\n"
        "  metadata-cli methods <spec>\n"
        "  metadata-cli method  <spec> <namespace> <type> <method>\n"
        "  metadata-cli images        <spec>\n"
        "  metadata-cli pe-info       <spec>\n"
        "  metadata-cli registration  <spec>\n"
        "  metadata-cli code-modules  <spec>\n"
        "  metadata-cli symbols       <spec> <output-prefix>\n"
        "  metadata-cli symbols       <GameAssembly.dll> <global-metadata.dat> <output-prefix>\n"
        "      (emits <prefix>.dd64 + <prefix>.ghidra.txt + <prefix>.idc)\n"
        "  metadata-cli resolve       <spec>       show what <spec> maps to\n"
        "  metadata-cli steam-vdf                  list Steam VDF: name -> install folder\n"
        "  metadata-cli steam-games                same, as JSON lines (for scripts)\n"
        "  metadata-cli --help                     this text\n\n"
        "OPTIONS:\n"
        "  --ascii    Escape every non-ASCII character as \\uXXXX, so output is pure\n"
        "             7-bit ASCII that no shell can corrupt. May appear anywhere on\n"
        "             the command line. See ENCODING below.\n\n"
        "<spec> is any of:\n"
        "  * a full file path        \"...\\Metadata\\global-metadata.dat\" / \"...\\GameAssembly.dll\"\n"
        "                            (used verbatim; missing file = hard failure)\n"
        "  * a game install root     \"D:\\SteamLibrary\\steamapps\\common\\My Game\"\n"
        "                            (the file is located by Unity's fixed layout)\n"
        "  * a Steam game name       \"Songs of Silence\"\n"
        "                            (install path looked up via libraryfolders.vdf)\n\n"
        "ENCODING - non-ASCII game names in PowerShell:\n"
        "  Output is UTF-8. PowerShell never lets a native program write to the\n"
        "  console directly: it captures stdout and decodes the bytes using\n"
        "  [Console]::OutputEncoding, which is cached when the shell STARTS. A\n"
        "  session opened under a legacy code page therefore keeps mangling UTF-8\n"
        "  even after `chcp 65001` reports otherwise. That is not just a display\n"
        "  glitch: a legacy double-byte character eats the JSON closing quote, so\n"
        "  the output stops parsing.\n\n"
        "  Diagnose (`chcp` is NOT the right check):\n"
        "      [Console]::OutputEncoding\n\n"
        "  Fix - recommended, helps every CLI tool. Add it to your $PROFILE:\n"
        "      [Console]::OutputEncoding = [Text.Encoding]::UTF8\n\n"
        "  Or leave the shell alone and pass --ascii:\n"
        "      metadata-cli steam-games --ascii\n"
        "      (metadata-cli steam-games --ascii | ConvertFrom-Json).name\n\n"
        "  A CJK-named game can also be selected by its install FOLDER name, which\n"
        "  is usually ASCII - <spec> matching accepts either.\n\n"
        "Output is line-oriented JSON to stdout. Errors go to stderr (exit 1).\n",
        out);
}

}  // namespace

// wmain, not main: <spec> can be a Steam store name with non-ASCII
// characters, and narrow argv on Windows is ANSI-code-page encoded — a CJK
// game name would be mangled before it ever reached the resolver.
int wmain(int argc, wchar_t** argv) {
    // Must outlive every code path below — its destructor restores the
    // console code page, so no `return` may bypass it.
    ConsoleUtf8Scope consoleUtf8;

    // Strip global options out of argv before subcommand dispatch, so every
    // subcommand's positional arguments keep their existing indices.
    std::vector<wchar_t*> av;
    av.reserve(static_cast<std::size_t>(argc));
    for (int i = 0; i < argc; ++i) {
        if (i > 0 && std::wcscmp(argv[i], L"--ascii") == 0) { g_ascii = true; continue; }
        av.push_back(argv[i]);
    }
    argc = static_cast<int>(av.size());
    argv = av.data();

    if (argc < 2) { Usage(stderr); return 2; }
    std::wstring_view sub = argv[1];

    // An explicit help request is a successful run: text to stdout, exit 0.
    if (sub == L"--help" || sub == L"-h" || sub == L"/?" || sub == L"help") {
        Usage(stdout);
        return 0;
    }

    // The `symbols` subcommand has two positional shapes, so its arg parsing
    // is delegated to CmdSymbols. `steam-games` takes no arguments at all.
    if (sub == L"symbols")     return CmdSymbols(argc, argv);
    if (sub == L"steam-games") return CmdSteamGames();
    if (sub == L"steam-vdf")   return CmdSteamVdf();

    if (argc < 3) { Usage(stderr); return 2; }
    std::wstring spec = argv[2];
    if (spec.empty()) {
        std::fprintf(stderr, "metadata-cli: invalid path argument\n");
        return 2;
    }

    if (sub == L"resolve") return CmdResolve(spec);

    // Metadata-consuming subcommands resolve to global-metadata.dat; the PE
    // ones resolve to GameAssembly.dll.
    auto run = [&](Resolve::Target target, auto&& body) -> int {
        auto path = ResolveSpec(spec, target);
        if (!path) return 1;
        return body(*path);
    };
    const auto kMeta = Resolve::Target::Metadata;
    const auto kGa   = Resolve::Target::GameAssembly;

    if      (sub == L"info")    return run(kMeta, [](const std::wstring& p) { return CmdInfo(p); });
    else if (sub == L"stats")   return run(kMeta, [](const std::wstring& p) { return CmdStats(p); });
    else if (sub == L"types")   return run(kMeta, [](const std::wstring& p) { return CmdTypes(p); });
    else if (sub == L"type") {
        if (argc < 5) { Usage(stderr); return 2; }
        std::string ns = Resolve::WideToUtf8(argv[3]);
        std::string nm = Resolve::WideToUtf8(argv[4]);
        return run(kMeta, [&](const std::wstring& p) { return CmdType(p, ns, nm); });
    }
    else if (sub == L"methods") return run(kMeta, [](const std::wstring& p) { return CmdMethods(p); });
    else if (sub == L"images")  return run(kMeta, [](const std::wstring& p) { return CmdImages(p); });
    else if (sub == L"method") {
        if (argc < 6) { Usage(stderr); return 2; }
        std::string ns = Resolve::WideToUtf8(argv[3]);
        std::string ty = Resolve::WideToUtf8(argv[4]);
        std::string mn = Resolve::WideToUtf8(argv[5]);
        return run(kMeta, [&](const std::wstring& p) { return CmdMethod(p, ns, ty, mn); });
    }
    else if (sub == L"pe-info")      return run(kGa, [](const std::wstring& p) { return CmdPeInfo(p); });
    else if (sub == L"registration") return run(kGa, [](const std::wstring& p) { return CmdRegistration(p); });
    else if (sub == L"code-modules") return run(kGa, [](const std::wstring& p) { return CmdCodeModules(p); });
    Usage(stderr);
    return 2;
}
