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
//   metadata-cli info  <path>                — version + table sizes
//   metadata-cli stats <path>                — strip/obfuscation heuristic
//   metadata-cli types <path>                — JSON line per type
//   metadata-cli type  <path> <ns> <name>    — fields + defaults of one type
//
// Output format: line-oriented JSON to stdout. Errors → stderr + exit 1.
// No host process is touched — Logger is intentionally not initialised so
// per-PID log dirs aren't created for a CLI invocation.

#include "../../dll/src/il2cpp/MetadataParser.h"
#include "PEParser.h"
#include "RegistrationFinder.h"
#include "CodeGenModulesFinder.h"

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>

using UnityCEDumper::IL2CPP::Metadata::Parser;

namespace {

// Convert a UTF-8 argv string to a wide path for the parser's CreateFileW.
std::wstring Utf8ToWide(const char* utf8) {
    if (utf8 == nullptr || *utf8 == '\0') return {};
    int n = ::MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring out(static_cast<std::size_t>(n - 1), L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, utf8, -1, out.data(), n);
    return out;
}

// JSON-escape a UTF-8 string into stdout. Lazy: only the characters CE /
// nlohmann would actually emit need quoting (\, ", control bytes).
void PutJsonString(const std::string& s) {
    std::fputc('"', stdout);
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
    using namespace UnityCEDumper::Phase_D::PE;

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
    using namespace UnityCEDumper::Phase_D;

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
    using namespace UnityCEDumper::Phase_D;

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

void Usage() {
    std::fputs(
        "metadata-cli — offline IL2CPP global-metadata.dat parser + PE inspector\n\n"
        "USAGE:\n"
        "  metadata-cli info    <path-to-global-metadata.dat>\n"
        "  metadata-cli stats   <path-to-global-metadata.dat>\n"
        "  metadata-cli types   <path-to-global-metadata.dat>\n"
        "  metadata-cli type    <path-to-global-metadata.dat> <namespace> <name>\n"
        "  metadata-cli methods <path-to-global-metadata.dat>\n"
        "  metadata-cli method  <path-to-global-metadata.dat> <namespace> <type> <method>\n"
        "  metadata-cli pe-info       <path-to-GameAssembly.dll>\n"
        "  metadata-cli registration  <path-to-GameAssembly.dll>\n"
        "  metadata-cli code-modules  <path-to-GameAssembly.dll>\n\n"
        "Output is line-oriented JSON to stdout. Errors → stderr (exit 1).\n",
        stderr);
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) { Usage(); return 2; }
    std::string_view sub = argv[1];
    std::wstring path = Utf8ToWide(argv[2]);
    if (path.empty()) {
        std::fprintf(stderr, "metadata-cli: invalid path argument\n");
        return 2;
    }
    if      (sub == "info")    return CmdInfo(path);
    else if (sub == "stats")   return CmdStats(path);
    else if (sub == "types")   return CmdTypes(path);
    else if (sub == "type") {
        if (argc < 5) { Usage(); return 2; }
        return CmdType(path, argv[3], argv[4]);
    }
    else if (sub == "methods") return CmdMethods(path);
    else if (sub == "method") {
        if (argc < 6) { Usage(); return 2; }
        return CmdMethod(path, argv[3], argv[4], argv[5]);
    }
    else if (sub == "pe-info")      return CmdPeInfo(path);
    else if (sub == "registration") return CmdRegistration(path);
    else if (sub == "code-modules") return CmdCodeModules(path);
    Usage();
    return 2;
}
