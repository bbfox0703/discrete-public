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
        std::fputs("metadata-cli: load failed (file missing, not metadata, or sanity mismatch)\n", stderr);
        return 1;
    }
    // Phase 14c shipped v24 / v29 / v31 type-table parsing. v27 (Unity 2020)
    // still skipped — no test target locally. Anything else falls through
    // to the strings-only path with a stderr note.
    std::uint32_t typeCount = p.TypeDefinitionCount();
    bool typeTablesParsed = typeCount > 0;
    std::printf("{\"version\":%d,\"loaded\":true,"
                "\"type_tables_parsed\":%s,"
                "\"type_definition_count\":%u,"
                "\"string_literal_count\":%u}\n",
                p.version(),
                typeTablesParsed ? "true" : "false",
                typeCount,
                p.StringLiteralCount());
    if (!typeTablesParsed) {
        std::fprintf(stderr,
            "metadata-cli: note — version %d type tables not parsed (v24, v27, "
            "v29, v31 supported today). String-literal queries still work.\n",
            p.version());
    }
    return 0;
}

int CmdStats(const std::wstring& path) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed\n", stderr);
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
        std::fputs("metadata-cli: load failed\n", stderr);
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

int CmdType(const std::wstring& path, const std::string& ns, const std::string& name) {
    Parser p;
    if (!p.LoadFromPath(path)) {
        std::fputs("metadata-cli: load failed\n", stderr);
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

void Usage() {
    std::fputs(
        "metadata-cli — offline IL2CPP global-metadata.dat parser (Phase 13)\n\n"
        "USAGE:\n"
        "  metadata-cli info  <path-to-global-metadata.dat>\n"
        "  metadata-cli stats <path-to-global-metadata.dat>\n"
        "  metadata-cli types <path-to-global-metadata.dat>\n"
        "  metadata-cli type  <path-to-global-metadata.dat> <namespace> <name>\n\n"
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
    if      (sub == "info")  return CmdInfo(path);
    else if (sub == "stats") return CmdStats(path);
    else if (sub == "types") return CmdTypes(path);
    else if (sub == "type") {
        if (argc < 5) { Usage(); return 2; }
        return CmdType(path, argv[3], argv[4]);
    }
    Usage();
    return 2;
}
