// ExportAPI.cpp — implementation of the C ABI surface.

#define UNITYCEDUMPER_BUILDING_DLL
#include "ExportAPI.h"

#include "BackendDetector.h"
#include "Logger.h"
#include "MagicStrings.h"
#include "Memory.h"
#include "PipeServer.h"

#if defined(UNITYCEDUMPER_BACKEND_MONO)
#  include "../mono/MonoClassWalker.h"
#  include "../mono/MonoDomain.h"
#  include "../mono/MonoResolver.h"
#  include "../mono/MonoThreadAttach.h"
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
#  include "../il2cpp/IL2CPPClassWalker.h"
#  include "../il2cpp/IL2CPPDomain.h"
#  include "../il2cpp/IL2CPPMetadataRegistration.h"
#  include "../il2cpp/IL2CPPResolver.h"
#  include "../il2cpp/IL2CPPThreadAttach.h"
#  include "../il2cpp/MetadataParser.h"
#endif

#include <nlohmann/json.hpp>

#include <windows.h>

#include <atomic>
#include <map>
#include <mutex>
#include <string>

using nlohmann::json;

namespace {

std::atomic<bool> g_initialized{false};

constexpr UnityDumperBackend BackendOfBuild() {
#if defined(UNITYCEDUMPER_BACKEND_MONO)
    return UnityDumperBackend_Mono;
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
    return UnityDumperBackend_IL2CPP;
#else
    return UnityDumperBackend_Unknown;
#endif
}

constexpr const char* BackendName(UnityDumperBackend b) {
    switch (b) {
        case UnityDumperBackend_Mono:   return "Mono";
        case UnityDumperBackend_IL2CPP: return "IL2CPP";
        default: return "Unknown";
    }
}

std::string Utf8FromWide(const std::wstring& w) {
    if (w.empty()) return {};
    int n = ::WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()),
                                  nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(static_cast<std::size_t>(n), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()),
                          out.data(), n, nullptr, nullptr);
    return out;
}

json BuildHandshakeJson() {
    using namespace UnityCEDumper;
    auto d = BackendDetector::Detect();

    json j;
    j["engine"]       = "Unity";
    j["backend"]      = BackendDetector::BackendName(d.backend);
    j["dll_variant"]  = BackendName(BackendOfBuild());
    j["game_module"]  = Utf8FromWide(d.gameModule);
    j["unity_version"]    = d.unityVersion ? json(*d.unityVersion) : json(nullptr);
    j["metadata_version"] = d.metadataVersion ? json(*d.metadataVersion) : json(nullptr);

    std::uint32_t assembliesCount = 0;
#if defined(UNITYCEDUMPER_BACKEND_MONO)
    if (d.backend == BackendDetector::Backend::Mono && Mono::IsResolved()) {
        assembliesCount = static_cast<std::uint32_t>(Mono::EnumerateAssemblies().size());
    }
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
    if (d.backend == BackendDetector::Backend::IL2CPP && IL2CPP::IsResolved()) {
        assembliesCount = static_cast<std::uint32_t>(IL2CPP::EnumerateAssemblies().size());
    }
#endif
    j["assemblies_count"] = assembliesCount;
    j["build"]    = static_cast<std::uint32_t>(UNITYCEDUMPER_BUILD_NUMBER);
    j["protocol"] = 1;
    return j;
}

void RegisterCoreHandlers() {
    using namespace UnityCEDumper;
    PipeServer::RegisterHandler("ping", [](const json&) -> json {
        return json{{"pong", true}};
    });
    PipeServer::RegisterHandler(MagicStrings::CmdHandshake, [](const json&) -> json {
        return BuildHandshakeJson();
    });
    // Phase 2: `scan` re-runs backend detection and returns the same shape as handshake.
    PipeServer::RegisterHandler(MagicStrings::CmdScan, [](const json&) -> json {
        return BuildHandshakeJson();
    });

    // ----------------------------------------------------------------------
    // Phase 6 — backend-agnostic memory ops.
    // readMemory / writeMemory / resolvePointer act on the host process's
    // address space. They use SEH-protected primitives in Memory.cpp so a
    // bad pointer returns an error envelope instead of crashing the game.
    // ----------------------------------------------------------------------

    auto parseAddr = [](const json& v, std::uintptr_t& out) -> bool {
        if (v.is_string()) {
            const std::string s = v.get<std::string>();
            const char* p = s.c_str();
            if (s.size() >= 2 && p[0] == '0' && (p[1] == 'x' || p[1] == 'X')) p += 2;
            char* end = nullptr;
            unsigned long long val = ::_strtoui64(p, &end, 16);
            if (end == p) return false;
            out = static_cast<std::uintptr_t>(val);
            return true;
        }
        if (v.is_number_unsigned()) {
            out = static_cast<std::uintptr_t>(v.get<std::uint64_t>());
            return true;
        }
        if (v.is_number_integer()) {
            out = static_cast<std::uintptr_t>(v.get<std::int64_t>());
            return true;
        }
        return false;
    };

    auto bytesToHex = [](const std::uint8_t* data, std::size_t n) {
        static const char* kHex = "0123456789ABCDEF";
        std::string out;
        out.reserve(n * 2);
        for (std::size_t i = 0; i < n; ++i) {
            out.push_back(kHex[data[i] >> 4]);
            out.push_back(kHex[data[i] & 0xF]);
        }
        return out;
    };

    auto hexToBytes = [](const std::string& hex, std::vector<std::uint8_t>& out) -> bool {
        std::string clean;
        clean.reserve(hex.size());
        for (char c : hex) {
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == ',') continue;
            clean.push_back(c);
        }
        if (clean.size() % 2 != 0) return false;
        out.clear();
        out.reserve(clean.size() / 2);
        auto hexNibble = [](char c, std::uint8_t& n) {
            if (c >= '0' && c <= '9') { n = c - '0';      return true; }
            if (c >= 'a' && c <= 'f') { n = 10 + c - 'a'; return true; }
            if (c >= 'A' && c <= 'F') { n = 10 + c - 'A'; return true; }
            return false;
        };
        for (std::size_t i = 0; i + 1 < clean.size(); i += 2) {
            std::uint8_t hi = 0, lo = 0;
            if (!hexNibble(clean[i], hi) || !hexNibble(clean[i + 1], lo)) return false;
            out.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
        }
        return true;
    };

    auto formatHex = [](std::uintptr_t v) {
        char buf[32];
        ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "0x%016llX",
                      static_cast<unsigned long long>(v));
        return std::string(buf);
    };

    // Decode one field's value given its CLR type name and starting address.
    // Returns a JSON object whose keys are merged into the per-field entry by
    // the caller. On unreadable memory, returns {"error": "unreadable"}.
    //
    // The .NET String layout is identical between Mono (Unity bdwgc) and IL2CPP
    // — header is 16 bytes (vtable + sync), then int32 length at +0x10, then
    // UTF-16 chars at +0x14. So this decoder is backend-agnostic.
    auto decodeField = [formatHex, bytesToHex](const std::string& typeName,
                                               std::uintptr_t addr) -> json {
        json out;
        auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
            return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
        };

        if (typeName == "System.Boolean") {
            std::uint8_t v = 0;
            if (rd(addr, &v, 1)) out["value"] = (v != 0); else out["error"] = "unreadable";
        } else if (typeName == "System.Byte") {
            std::uint8_t v = 0;
            if (rd(addr, &v, 1)) out["value"] = static_cast<unsigned>(v); else out["error"] = "unreadable";
        } else if (typeName == "System.SByte") {
            std::int8_t v = 0;
            if (rd(addr, &v, 1)) out["value"] = static_cast<int>(v); else out["error"] = "unreadable";
        } else if (typeName == "System.Int16") {
            std::int16_t v = 0;
            if (rd(addr, &v, 2)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.UInt16" || typeName == "System.Char") {
            std::uint16_t v = 0;
            if (rd(addr, &v, 2)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Int32") {
            std::int32_t v = 0;
            if (rd(addr, &v, 4)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.UInt32") {
            std::uint32_t v = 0;
            if (rd(addr, &v, 4)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Int64") {
            std::int64_t v = 0;
            if (rd(addr, &v, 8)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.UInt64") {
            std::uint64_t v = 0;
            if (rd(addr, &v, 8)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Single") {
            float v = 0.f;
            if (rd(addr, &v, 4)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.Double") {
            double v = 0.0;
            if (rd(addr, &v, 8)) out["value"] = v; else out["error"] = "unreadable";
        } else if (typeName == "System.IntPtr" || typeName == "System.UIntPtr") {
            std::uintptr_t v = 0;
            if (rd(addr, &v, 8)) out["ptr"] = formatHex(v); else out["error"] = "unreadable";
        } else if (typeName == "System.String") {
            // Reference to MonoString / Il2CppString. Follow the pointer if non-null
            // and decode the inline UTF-16 buffer to UTF-8.
            std::uintptr_t sptr = 0;
            if (!rd(addr, &sptr, 8)) { out["error"] = "unreadable"; return out; }
            out["ptr"] = formatHex(sptr);
            if (sptr == 0) { out["string"] = nullptr; return out; }
            std::int32_t len = 0;
            if (!rd(sptr + 0x10, &len, 4)) { out["string_error"] = "header unreadable"; return out; }
            if (len < 0 || len > 65536)    { out["string_error"] = "implausible length"; out["string_length"] = len; return out; }
            std::vector<std::uint16_t> chars(static_cast<std::size_t>(len));
            if (len > 0 && !rd(sptr + 0x14, chars.data(),
                               static_cast<std::size_t>(len) * 2u)) {
                out["string_error"] = "chars unreadable";
                return out;
            }
            // UTF-16 → UTF-8 with U+FFFD on lone surrogates.
            std::string utf8;
            utf8.reserve(static_cast<std::size_t>(len));
            auto pushCp = [&utf8](std::uint32_t cp) {
                if (cp < 0x80u) utf8.push_back(static_cast<char>(cp));
                else if (cp < 0x800u) {
                    utf8.push_back(static_cast<char>(0xC0u | (cp >> 6)));
                    utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                } else if (cp < 0x10000u) {
                    utf8.push_back(static_cast<char>(0xE0u | (cp >> 12)));
                    utf8.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
                    utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                } else {
                    utf8.push_back(static_cast<char>(0xF0u | (cp >> 18)));
                    utf8.push_back(static_cast<char>(0x80u | ((cp >> 12) & 0x3Fu)));
                    utf8.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
                    utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                }
            };
            for (std::size_t i = 0; i < chars.size(); ++i) {
                std::uint32_t cp = chars[i];
                if (cp >= 0xD800u && cp <= 0xDBFFu && i + 1 < chars.size()) {
                    std::uint32_t lo = chars[i + 1];
                    if (lo >= 0xDC00u && lo <= 0xDFFFu) {
                        cp = 0x10000u + ((cp - 0xD800u) << 10) + (lo - 0xDC00u);
                        ++i;
                    } else {
                        cp = 0xFFFDu;
                    }
                } else if (cp >= 0xDC00u && cp <= 0xDFFFu) {
                    cp = 0xFFFDu;
                }
                pushCp(cp);
            }
            out["string"] = std::move(utf8);
            out["string_length"] = len;
        } else {
            // Reference / unknown value type: report the raw pointer-sized qword.
            // Caller can resolvePointer or walkInstance again on it.
            std::uintptr_t v = 0;
            if (rd(addr, &v, 8)) out["ptr"] = formatHex(v); else out["error"] = "unreadable";
        }
        (void)bytesToHex;  // reserved for future raw-bytes inclusion
        return out;
    };

    PipeServer::RegisterHandler(MagicStrings::CmdReadMemory,
        [parseAddr, bytesToHex, formatHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address' (hex string or integer)"}};
        }
        std::size_t size = 0;
        if (args.contains("size") && args["size"].is_number_unsigned()) {
            size = args["size"].get<std::size_t>();
        }
        if (size == 0 || size > 1 * 1024 * 1024) {
            return json{{"error", "'size' must be in [1, 1048576]"}};
        }
        std::vector<std::uint8_t> buf(size);
        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(addr), buf.data(), size)) {
            return json{
                {"error", "memory not readable"},
                {"address", formatHex(addr)},
                {"size", size},
            };
        }
        return json{
            {"address", formatHex(addr)},
            {"size", size},
            {"hex", bytesToHex(buf.data(), buf.size())},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdWriteMemory,
        [parseAddr, hexToBytes, formatHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        if (!args.contains("hex") || !args["hex"].is_string()) {
            return json{{"error", "missing 'hex' (compact hex string)"}};
        }
        std::vector<std::uint8_t> bytes;
        if (!hexToBytes(args["hex"].get<std::string>(), bytes)) {
            return json{{"error", "'hex' is not valid hex"}};
        }
        if (bytes.empty() || bytes.size() > 1 * 1024 * 1024) {
            return json{{"error", "byte length must be in [1, 1048576]"}};
        }
        if (!Memory::SafeWriteBytes(reinterpret_cast<void*>(addr), bytes.data(), bytes.size())) {
            return json{
                {"error", "memory not writable"},
                {"address", formatHex(addr)},
                {"size", bytes.size()},
            };
        }
        return json{
            {"address", formatHex(addr)},
            {"written", bytes.size()},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdResolvePointer,
        [parseAddr, bytesToHex, formatHex](const json& args) -> json {
        std::uintptr_t base = 0;
        if (!args.contains("base") || !parseAddr(args["base"], base)) {
            return json{{"error", "missing/invalid 'base'"}};
        }
        std::vector<std::int64_t> offsets;
        if (args.contains("offsets") && args["offsets"].is_array()) {
            for (const auto& o : args["offsets"]) {
                if      (o.is_number_integer())  offsets.push_back(o.get<std::int64_t>());
                else if (o.is_number_unsigned()) offsets.push_back(static_cast<std::int64_t>(o.get<std::uint64_t>()));
                else return json{{"error", "offsets[] must be integers"}};
            }
        }
        // Resolve: at each step except the last, dereference (base + offset) as
        // a pointer; the final offset is added but NOT dereferenced. This is
        // the standard Cheat Engine pointer-chain semantics.
        std::vector<std::string> trace;
        trace.reserve(offsets.size() + 1);
        std::uintptr_t cur = base;
        trace.push_back(formatHex(cur));
        for (std::size_t i = 0; i < offsets.size(); ++i) {
            std::uintptr_t addr = static_cast<std::uintptr_t>(
                static_cast<std::int64_t>(cur) + offsets[i]);
            if (i + 1 < offsets.size()) {
                auto deref = Memory::SafeRead<std::uintptr_t>(reinterpret_cast<void*>(addr));
                if (!deref) {
                    return json{
                        {"error", "deref failed at step"},
                        {"step", i},
                        {"address", formatHex(addr)},
                        {"trace", trace},
                    };
                }
                cur = *deref;
            } else {
                cur = addr;  // last offset is just added, not dereferenced
            }
            trace.push_back(formatHex(cur));
        }
        json result{
            {"final_address", formatHex(cur)},
            {"trace", trace},
        };
        if (args.contains("size") && args["size"].is_number_unsigned()) {
            std::size_t size = args["size"].get<std::size_t>();
            if (size > 0 && size <= 4096) {
                std::vector<std::uint8_t> buf(size);
                if (Memory::SafeReadBytes(reinterpret_cast<void*>(cur), buf.data(), size)) {
                    result["size"] = size;
                    result["hex"] = bytesToHex(buf.data(), buf.size());
                }
            }
        }
        return result;
    });

    // ------------------------------------------------------------------------
    // Phase 6 segment D — symbol registry (CE-style named pointers).
    // Backend-agnostic in-memory map. Survives until the DLL unloads. Useful
    // for stashing addresses returned by getStaticFieldData / findInstances /
    // walkInstance and recalling them by a friendly name from later commands.
    // ------------------------------------------------------------------------
    static std::mutex g_symbolMutex;
    static std::map<std::string, std::uintptr_t> g_symbolMap;

    PipeServer::RegisterHandler(MagicStrings::CmdRegisterSymbol,
        [parseAddr, formatHex](const json& args) -> json {
        std::string name = args.value("name", std::string{});
        if (name.empty()) return json{{"error", "missing 'name'"}};
        // Action: "set" (default), "delete", "get". Symmetric with CE.
        std::string action = args.value("action", std::string{"set"});
        std::lock_guard<std::mutex> lk(g_symbolMutex);
        if (action == "delete") {
            std::size_t n = g_symbolMap.erase(name);
            return json{{"name", name}, {"deleted", n != 0}};
        }
        if (action == "get") {
            auto it = g_symbolMap.find(name);
            if (it == g_symbolMap.end()) return json{{"error", "symbol not found"}, {"name", name}};
            return json{{"name", name}, {"address", formatHex(it->second)}};
        }
        // Default: set.
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        g_symbolMap[name] = addr;
        return json{{"name", name}, {"address", formatHex(addr)}, {"set", true}};
    });

    PipeServer::RegisterHandler("lookupSymbol",
        [formatHex](const json& args) -> json {
        std::string name = args.value("name", std::string{});
        if (name.empty()) return json{{"error", "missing 'name'"}};
        std::lock_guard<std::mutex> lk(g_symbolMutex);
        auto it = g_symbolMap.find(name);
        if (it == g_symbolMap.end()) return json{{"error", "symbol not found"}, {"name", name}};
        return json{{"name", name}, {"address", formatHex(it->second)}};
    });

    PipeServer::RegisterHandler("listSymbols",
        [formatHex](const json&) -> json {
        std::lock_guard<std::mutex> lk(g_symbolMutex);
        json arr = json::array();
        for (const auto& [k, v] : g_symbolMap) {
            arr.push_back({{"name", k}, {"address", formatHex(v)}});
        }
        return json{{"count", arr.size()}, {"symbols", std::move(arr)}};
    });

    // ------------------------------------------------------------------------
    // Phase 5 segment 3 — stripped / obfuscated metadata heuristic.
    // Backend-agnostic helpers; each backend block below registers its own
    // `getMetadataStats` handler that feeds the shared collectors.
    // Heuristic: count single-letter / two-letter type names (excluding the
    // C# compiler-generated `<...>` types) as a fraction of all "user"
    // names. Vanilla Unity sits well under 5%; Beebyte / Eazfuscator builds
    // routinely exceed 30%.
    // ------------------------------------------------------------------------
    struct NameStats {
        std::size_t total            = 0;
        std::size_t with_namespace   = 0;
        std::size_t single_char      = 0;
        std::size_t short_two_char   = 0;
        std::size_t compiler_gen     = 0;
        std::size_t numeric_lead     = 0;
        std::uint64_t total_name_len = 0;
        std::size_t min_name_len     = static_cast<std::size_t>(-1);
        std::size_t max_name_len     = 0;
    };
    auto updateStats = [](NameStats& s, const std::string& ns,
                           const std::string& name) {
        s.total++;
        if (!ns.empty()) s.with_namespace++;
        if (name.empty()) return;
        if (name[0] == '<') { s.compiler_gen++; return; }  // <PrivateImpl> etc.
        if (name[0] >= '0' && name[0] <= '9') s.numeric_lead++;
        if      (name.size() == 1) s.single_char++;
        else if (name.size() == 2) s.short_two_char++;
        s.total_name_len += name.size();
        if (name.size() < s.min_name_len) s.min_name_len = name.size();
        if (name.size() > s.max_name_len) s.max_name_len = name.size();
    };
    auto buildStatsJson = [](const NameStats& s) -> json {
        json out = {
            {"total_classes",      s.total},
            {"with_namespace",     s.with_namespace},
            {"single_char_names",  s.single_char},
            {"short_names_2",      s.short_two_char},
            {"compiler_generated", s.compiler_gen},
            {"numeric_lead",       s.numeric_lead},
        };
        if (s.total > 0) {
            // Average / min / max are reported over non-compiler-generated
            // names since `total_name_len` excludes those by construction.
            std::size_t denom = (s.total > s.compiler_gen) ?
                                (s.total - s.compiler_gen) : 0;
            if (denom > 0) {
                out["avg_name_length"] =
                    static_cast<double>(s.total_name_len) / static_cast<double>(denom);
            }
        }
        if (s.min_name_len != static_cast<std::size_t>(-1)) {
            out["min_name_length"] = s.min_name_len;
        }
        out["max_name_length"] = s.max_name_len;

        // Verdict: ratio of "stripped-shape" names over user (non compiler-
        // generated) types. Thresholds calibrated against vanilla Unity
        // (~1-2%) and typical commercial obfuscators (>30%).
        std::size_t denom = (s.total > s.compiler_gen) ?
                            (s.total - s.compiler_gen) : 0;
        if (denom > 0) {
            double ratio = static_cast<double>(s.single_char + s.short_two_char) /
                           static_cast<double>(denom);
            out["strip_ratio"] = ratio;
            std::string verdict;
            if      (ratio > 0.30) verdict = "heavy_strip";
            else if (ratio > 0.10) verdict = "partial_strip";
            else                   verdict = "clean";
            out["verdict"] = verdict;
        }
        return out;
    };

#if defined(UNITYCEDUMPER_BACKEND_MONO)
    // Phase 3 segment 1: list every Mono-loaded assembly.
    PipeServer::RegisterHandler(MagicStrings::CmdGetAssemblies, [](const json&) -> json {
        if (!Mono::IsResolved()) {
            return json{
                {"error", "mono runtime not resolved"},
                {"assemblies", json::array()},
                {"count", 0},
            };
        }
        auto vec = Mono::EnumerateAssemblies();
        json arr = json::array();
        for (const auto& a : vec) {
            arr.push_back({{"name", a.name}, {"path", a.path}});
        }
        return json{
            {"assemblies", std::move(arr)},
            {"count", vec.size()},
        };
    });

    // Phase 3 segment 2: classes / class info / field info / method info.
    PipeServer::RegisterHandler(MagicStrings::CmdGetClasses, [](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string assemblyName = args.value("assembly", std::string{});
        if (assemblyName.empty()) return json{{"error", "missing 'assembly' arg"}};
        Mono::MonoImage* img = Mono::FindImageByName(assemblyName);
        if (img == nullptr) return json{{"error", "assembly not found: " + assemblyName}};

        auto classes = Mono::EnumerateClasses(img);
        json arr = json::array();
        for (const auto& c : classes) {
            arr.push_back({
                {"namespace", c.ns},
                {"name", c.name},
                {"token", c.token},
            });
        }
        return json{
            {"assembly", assemblyName},
            {"count", classes.size()},
            {"classes", std::move(arr)},
        };
    });

    auto resolveClassFromArgs = [](const json& args, std::string& errOut) -> Mono::MonoClass* {
        std::string assemblyName = args.value("assembly", std::string{});
        std::string ns           = args.value("namespace", std::string{});
        std::string name         = args.value("name", std::string{});
        if (assemblyName.empty() || name.empty()) {
            errOut = "missing 'assembly' or 'name' arg";
            return nullptr;
        }
        Mono::MonoImage* img = Mono::FindImageByName(assemblyName);
        if (img == nullptr) { errOut = "assembly not found: " + assemblyName; return nullptr; }
        Mono::MonoClass* k = Mono::FindClass(img, ns, name);
        if (k == nullptr) { errOut = "class not found: " + ns + "." + name; return nullptr; }
        return k;
    };

    PipeServer::RegisterHandler(MagicStrings::CmdGetClassInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = Mono::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};

        json fields = json::array();
        for (const auto& f : res.info.fields) {
            fields.push_back({
                {"name", f.name},
                {"type", f.typeName},
                {"offset", f.offset},
                {"flags", f.flags},
                {"static", (f.flags & 0x10u) != 0},
            });
        }
        json methods = json::array();
        for (const auto& m : res.info.methods) {
            methods.push_back({
                {"name", m.name},
                {"token", m.token},
                {"flags", m.flags},
                {"static", (m.flags & 0x10u) != 0},
            });
        }
        json parent = nullptr;
        if (!res.info.parent.name.empty()) {
            parent = json{
                {"namespace", res.info.parent.ns},
                {"name", res.info.parent.name},
            };
        }
        return json{
            {"namespace", res.info.self.ns},
            {"name",      res.info.self.name},
            {"parent",    parent},
            {"fields",    std::move(fields)},
            {"methods",   std::move(methods)},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetFieldInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = Mono::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        for (const auto& f : res.info.fields) {
            if (f.name == fieldName) {
                return json{
                    {"name", f.name},
                    {"type", f.typeName},
                    {"offset", f.offset},
                    {"flags", f.flags},
                    {"static", (f.flags & 0x10u) != 0},
                };
            }
        }
        return json{{"error", "field not found: " + fieldName}};
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetMethodInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = Mono::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        json matches = json::array();
        for (const auto& m : res.info.methods) {
            if (m.name == methodName) {
                matches.push_back({
                    {"name", m.name},
                    {"token", m.token},
                    {"flags", m.flags},
                    {"static", (m.flags & 0x10u) != 0},
                });
            }
        }
        if (matches.empty()) return json{{"error", "method not found: " + methodName}};
        return json{{"matches", std::move(matches)}};
    });

    // Phase 3 segment 3: static field address + JIT method native address.
    // (formatHex is the shared lambda declared in the backend-agnostic block above.)

    PipeServer::RegisterHandler(MagicStrings::CmdGetStaticFieldData,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto sa = Mono::ResolveStaticFieldAddress(k, fieldName);
        if (!sa.ok) return json{{"error", "static field not resolvable: " + fieldName}};
        return json{
            {"field",   fieldName},
            {"type",    sa.typeName},
            {"offset",  sa.offset},
            {"flags",   sa.flags},
            {"address", formatHex(reinterpret_cast<std::uintptr_t>(sa.address))},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdMonoCompileMethod,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto cm = Mono::CompileMethodByName(k, methodName);
        if (!cm.ok) return json{{"error", "mono_compile_method failed for: " + methodName}};
        return json{
            {"method",  cm.name},
            {"token",   cm.token},
            {"address", formatHex(reinterpret_cast<std::uintptr_t>(cm.address))},
        };
    });

    // Phase 6 segment C — findInstances (Mono).
    // Each MonoObject's first qword is its MonoVTable*. We resolve the class's
    // VTable for the root domain and scan committed RW pages for that pointer.
    PipeServer::RegisterHandler(MagicStrings::CmdFindInstances,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        const auto& r = Mono::GetResolver();
        if (r.class_vtable == nullptr) return json{{"error", "mono_class_vtable not exported"}};

        Mono::MonoDomain* domain = Mono::GetRootDomain();
        if (domain == nullptr) return json{{"error", "no mono root domain"}};

        Mono::MonoVTable* vtable = nullptr;
        {
            Mono::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
            vtable = r.class_vtable(domain, k);
        }
        if (vtable == nullptr) return json{{"error", "mono_class_vtable returned null"}};

        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = std::min<std::size_t>(args["limit"].get<std::size_t>(), 4096);
        }
        auto matches = Memory::ScanForPointer(reinterpret_cast<std::uintptr_t>(vtable), limit);
        json arr = json::array();
        for (const void* p : matches) {
            arr.push_back(formatHex(reinterpret_cast<std::uintptr_t>(p)));
        }
        return json{
            {"vtable",  formatHex(reinterpret_cast<std::uintptr_t>(vtable))},
            {"count",   arr.size()},
            {"limit",   limit},
            {"matches", std::move(arr)},
        };
    });

    // Phase 6 segment B — walkInstance (Mono).
    // Given an instance address + class spec, walk the inheritance chain and
    // decode every instance field's value. Each MonoObject's first 16 bytes
    // are the header (vtable + sync); field offsets returned by
    // mono_field_get_offset are absolute from the object start, so
    // address + offset is ready to read.
    PipeServer::RegisterHandler(MagicStrings::CmdWalkInstance,
        [parseAddr, formatHex, decodeField, resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        const auto& r = Mono::GetResolver();
        Mono::MonoDomain* domain = Mono::GetRootDomain();
        if (domain == nullptr) return json{{"error", "no mono root domain"}};

        // Walk parent chain (cap at 16 levels — defends against pathological loops).
        std::vector<Mono::MonoClass*> chain;
        {
            Mono::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
            for (Mono::MonoClass* cur = k; cur != nullptr && chain.size() < 16; ) {
                chain.push_back(cur);
                if (r.class_get_parent == nullptr) break;
                cur = r.class_get_parent(cur);
            }
        }

        // Walk base-class first so derived fields appear last (typical layout).
        json fields = json::array();
        std::string selfNs, selfName;
        for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
            auto res = Mono::ReadClassInfo(*it);
            if (!res.ok) continue;
            const bool isSelf = (*it == k);
            if (isSelf) { selfNs = res.info.self.ns; selfName = res.info.self.name; }
            for (const auto& f : res.info.fields) {
                if (f.flags & 0x10u) continue;  // skip static
                std::uintptr_t fieldAddr = addr + static_cast<std::uintptr_t>(f.offset);
                json entry = {
                    {"name",    f.name},
                    {"type",    f.typeName},
                    {"offset",  f.offset},
                    {"from",    res.info.self.name},
                    {"address", formatHex(fieldAddr)},
                };
                json decoded = decodeField(f.typeName, fieldAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                    entry[kv.key()] = kv.value();
                }
                fields.push_back(std::move(entry));
                if (fields.size() >= 4096) break;
            }
            if (fields.size() >= 4096) break;
        }

        return json{
            {"address",   formatHex(addr)},
            {"namespace", selfNs},
            {"name",      selfName},
            {"depth",     chain.size()},
            {"count",     fields.size()},
            {"fields",    std::move(fields)},
        };
    });

    // Phase 6 segment D — monoGetVTable.
    // Resolves the MonoVTable* for a class in the root domain. The same call
    // findInstances uses internally, exposed for callers that want to do their
    // own pointer scans or to feed registerSymbol.
    PipeServer::RegisterHandler(MagicStrings::CmdMonoGetVTable,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        const auto& r = Mono::GetResolver();
        if (r.class_vtable == nullptr) return json{{"error", "mono_class_vtable not exported"}};
        Mono::MonoDomain* domain = Mono::GetRootDomain();
        if (domain == nullptr) return json{{"error", "no mono root domain"}};

        Mono::MonoVTable* vt = nullptr;
        {
            Mono::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "mono_thread_attach failed"}};
            vt = r.class_vtable(domain, k);
        }
        if (vt == nullptr) return json{{"error", "mono_class_vtable returned null"}};
        return json{
            {"vtable", formatHex(reinterpret_cast<std::uintptr_t>(vt))},
        };
    });

    // Phase 5 segment 3 — getMetadataStats (Mono).
    // Walks every class across every loaded assembly. Each Mono helper does
    // its own thread attach, so we deliberately don't hold an outer guard
    // (nested attach/detach destroyed BongoCat in build #54).
    PipeServer::RegisterHandler("getMetadataStats",
        [updateStats, buildStatsJson](const json&) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        NameStats s;
        auto assemblies = Mono::EnumerateAssemblies();
        std::size_t scanned = 0;
        for (const auto& a : assemblies) {
            Mono::MonoImage* img = Mono::FindImageByName(a.name);
            if (img == nullptr) continue;
            auto classes = Mono::EnumerateClasses(img);
            for (const auto& c : classes) updateStats(s, c.ns, c.name);
            ++scanned;
        }
        json out = buildStatsJson(s);
        out["scope"]              = "all_assemblies";
        out["assemblies_scanned"] = scanned;
        return out;
    });

    // Phase 6 segment D — getEnumValues (Mono stub).
    // Mono enum decode requires extra resolver bindings (mono_class_is_enum,
    // mono_class_enum_basetype, mono_field_get_data) which we haven't bound
    // yet. IL2CPP path goes via the metadata parser. For now report the gap
    // explicitly so callers can branch.
    PipeServer::RegisterHandler(MagicStrings::CmdGetEnumValues, [](const json&) -> json {
        return json{
            {"error", "getEnumValues not yet implemented for Mono backend"},
            {"hint",  "needs mono_field_get_data + mono_class_enum_basetype bindings"},
        };
    });
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
    // Phase 4: IL2CPP API path. Same command names as the Mono variant.
    // (formatHex is the shared lambda declared in the backend-agnostic block above.)

    PipeServer::RegisterHandler(MagicStrings::CmdGetAssemblies, [](const json&) -> json {
        if (!IL2CPP::IsResolved()) {
            return json{
                {"error", "il2cpp runtime not resolved"},
                {"assemblies", json::array()},
                {"count", 0},
            };
        }
        auto vec = IL2CPP::EnumerateAssemblies();
        json arr = json::array();
        for (const auto& a : vec) {
            arr.push_back({{"name", a.name}, {"path", a.path}});
        }
        return json{
            {"assemblies", std::move(arr)},
            {"count", vec.size()},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetClasses, [](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string assemblyName = args.value("assembly", std::string{});
        if (assemblyName.empty()) return json{{"error", "missing 'assembly' arg"}};
        const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
        if (img == nullptr) return json{{"error", "assembly not found: " + assemblyName}};

        auto classes = IL2CPP::EnumerateClasses(img);
        json arr = json::array();
        for (const auto& c : classes) {
            arr.push_back({{"namespace", c.ns}, {"name", c.name}});
        }
        return json{
            {"assembly", assemblyName},
            {"count", classes.size()},
            {"classes", std::move(arr)},
        };
    });

    auto resolveClassFromArgs = [](const json& args, std::string& errOut) -> IL2CPP::Il2CppClass* {
        std::string assemblyName = args.value("assembly", std::string{});
        std::string ns           = args.value("namespace", std::string{});
        std::string name         = args.value("name", std::string{});
        if (assemblyName.empty() || name.empty()) {
            errOut = "missing 'assembly' or 'name' arg"; return nullptr;
        }
        const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(assemblyName);
        if (img == nullptr) { errOut = "assembly not found: " + assemblyName; return nullptr; }
        IL2CPP::Il2CppClass* k = IL2CPP::FindClass(img, ns, name);
        if (k == nullptr) { errOut = "class not found: " + ns + "." + name; return nullptr; }
        return k;
    };

    PipeServer::RegisterHandler(MagicStrings::CmdGetClassInfo,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = IL2CPP::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};

        json fields = json::array();
        for (const auto& f : res.info.fields) {
            fields.push_back({
                {"name", f.name}, {"type", f.typeName},
                {"offset", f.offset}, {"flags", f.flags},
                {"static", (f.flags & 0x10u) != 0},
            });
        }
        json methods = json::array();
        for (const auto& m : res.info.methods) {
            methods.push_back({
                {"name", m.name}, {"token", m.token}, {"flags", m.flags},
                {"static", (m.flags & 0x10u) != 0},
                {"address", formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
            });
        }
        json parent = nullptr;
        if (!res.info.parent.name.empty()) {
            parent = json{{"namespace", res.info.parent.ns}, {"name", res.info.parent.name}};
        }
        return json{
            {"namespace", res.info.self.ns}, {"name", res.info.self.name},
            {"parent", parent},
            {"fields", std::move(fields)}, {"methods", std::move(methods)},
        };
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetFieldInfo,
        [resolveClassFromArgs](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = IL2CPP::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        for (const auto& f : res.info.fields) {
            if (f.name == fieldName) {
                return json{
                    {"name", f.name}, {"type", f.typeName},
                    {"offset", f.offset}, {"flags", f.flags},
                    {"static", (f.flags & 0x10u) != 0},
                };
            }
        }
        return json{{"error", "field not found: " + fieldName}};
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetMethodInfo,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto res = IL2CPP::ReadClassInfo(k);
        if (!res.ok) return json{{"error", "ReadClassInfo failed"}};
        json matches = json::array();
        for (const auto& m : res.info.methods) {
            if (m.name == methodName) {
                matches.push_back({
                    {"name", m.name}, {"token", m.token}, {"flags", m.flags},
                    {"static", (m.flags & 0x10u) != 0},
                    {"address", formatHex(reinterpret_cast<std::uintptr_t>(m.pointer))},
                });
            }
        }
        if (matches.empty()) return json{{"error", "method not found: " + methodName}};
        return json{{"matches", std::move(matches)}};
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetStaticFieldData,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string fieldName = args.value("field", std::string{});
        if (fieldName.empty()) return json{{"error", "missing 'field' arg"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto sa = IL2CPP::ResolveStaticFieldAddress(k, fieldName);
        if (!sa.ok) return json{{"error", "static field not resolvable: " + fieldName}};
        return json{
            {"field", fieldName}, {"type", sa.typeName},
            {"offset", sa.offset}, {"flags", sa.flags},
            {"address", formatHex(reinterpret_cast<std::uintptr_t>(sa.address))},
        };
    });

    // Phase 5 segment 2: list metadata fields (including const literals stripped
    // from the runtime field iterator) for a given type.
    PipeServer::RegisterHandler("il2cppGetMetadataTypeFields",
        [formatHex](const json& args) -> json {
        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) return json{{"error", "global-metadata.dat not loaded"}};
        std::string ns   = args.value("namespace", std::string{});
        std::string name = args.value("name", std::string{});
        if (name.empty()) return json{{"error", "missing 'name' arg"}};

        auto td = mp.FindTypeByName(ns, name);
        if (!td) return json{{"error", "type not found in metadata: " + ns + "." + name}};

        // Local: ECMA-335 §II.23.2 compressed signed int decoder (same logic
        // as in getEnumValues; duplicated to keep handlers self-contained).
        auto decodeCompressedSigned = [](const std::uint8_t* p, std::size_t avail,
                                          std::int64_t& outValue,
                                          std::size_t&  outBytes) -> bool {
            if (avail < 1) return false;
            std::uint8_t b0 = p[0];
            std::uint32_t enc = 0; std::size_t w = 0; std::int64_t bias = 0;
            if ((b0 & 0x80u) == 0u) { enc = b0; w = 1; bias = 0x40; }
            else if ((b0 & 0xC0u) == 0x80u) {
                if (avail < 2) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x3Fu) << 8) | p[1];
                w = 2; bias = 0x2000;
            } else if ((b0 & 0xE0u) == 0xC0u) {
                if (avail < 4) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x1Fu) << 24)
                    | (static_cast<std::uint32_t>(p[1])       << 16)
                    | (static_cast<std::uint32_t>(p[2])       <<  8)
                    |  static_cast<std::uint32_t>(p[3]);
                w = 4; bias = 0x10000000;
            } else { return false; }
            std::int64_t mag = static_cast<std::int64_t>(enc >> 1);
            outValue = (enc & 1u) ? (mag - bias) : mag;
            outBytes = w;
            return true;
        };
        // Decode a FieldDefault blob given the IL2CPP type-tag byte
        // (from MetadataRegistration::GetTypeTag). Returns true on success
        // and fills `out` with `value` + `enc_bytes`.
        //
        // Integer encoding observed empirically against System.Int32 in
        // SaGa Frontier 2 Remastered:
        //   * lead byte 0xFE = type-max sentinel (1 byte total)
        //   * lead byte 0xFF = type-min sentinel (1 byte total)
        //   * else §II.23.2 compressed signed int (zigzag-style, 1/2/4 bytes)
        // The §II.23.2 form maxes at ±2^28; values like Int32.MaxValue
        // (2147483647) fall outside that range, hence the sentinels.
        auto decodeByTag = [&decodeCompressedSigned]
            (std::uint8_t tag, const std::uint8_t* p, std::size_t avail,
             json& out) -> bool {
            auto cmp = [&](auto&& cast1, auto&& cast2,
                           std::int64_t maxVal, std::int64_t minVal) -> bool {
                if (avail >= 1) {
                    if (p[0] == 0xFE) {
                        out["value"] = cast2(cast1(maxVal));
                        out["enc_bytes"] = 1;
                        out["sentinel"] = "type_max";
                        return true;
                    }
                    if (p[0] == 0xFF) {
                        out["value"] = cast2(cast1(minVal));
                        out["enc_bytes"] = 1;
                        out["sentinel"] = "type_min";
                        return true;
                    }
                }
                std::int64_t raw = 0; std::size_t w = 0;
                if (!decodeCompressedSigned(p, avail, raw, w)) return false;
                out["value"] = cast2(cast1(raw));
                out["enc_bytes"] = w;
                return true;
            };
            switch (tag) {
                case 0x02:  // Boolean
                    if (avail < 1) return false;
                    out["value"] = (p[0] != 0); out["enc_bytes"] = 1; return true;
                case 0x03:  // Char
                    if (avail < 2) return false;
                    {
                        std::uint16_t v = 0; std::memcpy(&v, p, 2);
                        out["value"] = v; out["enc_bytes"] = 2;
                    }
                    return true;
                case 0x04:  // I1
                    return cmp([](std::int64_t x){ return static_cast<std::int8_t>(x); },
                               [](std::int8_t x){ return static_cast<std::int64_t>(x); },
                               0x7F, -0x80);
                case 0x05:  // U1
                    return cmp([](std::int64_t x){ return static_cast<std::uint8_t>(x); },
                               [](std::uint8_t x){ return static_cast<std::uint64_t>(x); },
                               0xFF, 0);
                case 0x06:  // I2
                    return cmp([](std::int64_t x){ return static_cast<std::int16_t>(x); },
                               [](std::int16_t x){ return static_cast<std::int64_t>(x); },
                               0x7FFF, -0x8000);
                case 0x07:  // U2
                    return cmp([](std::int64_t x){ return static_cast<std::uint16_t>(x); },
                               [](std::uint16_t x){ return static_cast<std::uint64_t>(x); },
                               0xFFFF, 0);
                case 0x08:  // I4
                    return cmp([](std::int64_t x){ return static_cast<std::int32_t>(x); },
                               [](std::int32_t x){ return static_cast<std::int64_t>(x); },
                               0x7FFFFFFFLL, -0x80000000LL);
                case 0x09:  // U4
                    return cmp([](std::int64_t x){ return static_cast<std::uint32_t>(x); },
                               [](std::uint32_t x){ return static_cast<std::uint64_t>(x); },
                               0xFFFFFFFFLL, 0);
                case 0x0A:  // I8
                    if (avail < 8) return false;
                    {
                        std::int64_t v = 0; std::memcpy(&v, p, 8);
                        out["value"] = v; out["enc_bytes"] = 8;
                    }
                    return true;
                case 0x0B:  // U8
                    if (avail < 8) return false;
                    {
                        std::uint64_t v = 0; std::memcpy(&v, p, 8);
                        out["value"] = v; out["enc_bytes"] = 8;
                    }
                    return true;
                case 0x0C:  // R4
                    if (avail < 4) return false;
                    {
                        float v = 0.f; std::memcpy(&v, p, 4);
                        out["value"] = v; out["enc_bytes"] = 4;
                    }
                    return true;
                case 0x0D:  // R8
                    if (avail < 8) return false;
                    {
                        double v = 0.0; std::memcpy(&v, p, 8);
                        out["value"] = v; out["enc_bytes"] = 8;
                    }
                    return true;
                case 0x0E:  // String — handled separately via TryDecodeStringDefault
                    return false;
                default:
                    return false;
            }
        };

        const bool regOk = IL2CPP::MetadataRegistration::Find();

        auto fields = mp.EnumerateTypeFields(*td);
        json arr = json::array();
        for (const auto& f : fields) {
            json entry = {
                {"name", f.name},
                {"index", f.index},
                {"type_index", f.typeIndex},
                {"token", f.token},
            };
            if (auto def = mp.FindFieldDefault(f.index)) {
                std::string hex;
                hex.reserve(def->data.size() * 2);
                static const char* kHex = "0123456789ABCDEF";
                std::size_t take = std::min<std::size_t>(def->data.size(), 16);
                for (std::size_t i = 0; i < take; ++i) {
                    hex.push_back(kHex[def->data[i] >> 4]);
                    hex.push_back(kHex[def->data[i] & 0xF]);
                }
                json defaultJson = {
                    {"type_index", def->typeIndex},
                    {"data_index", def->dataIndex},
                    {"raw_hex_first16", hex},
                    {"raw_size", def->data.size()},
                };
                // String decode is metadata-only; doesn't need the runtime types[] table.
                if (auto s = mp.TryDecodeStringDefault(def->dataIndex)) {
                    defaultJson["decoded_string"] = *s;
                }
                // Type-aware numeric / float / bool decode (Phase 5 segment 2d
                // follow-up): lookup the FieldDefault's runtime type tag via
                // MetadataRegistration and decode the blob accordingly.
                if (regOk && !def->data.empty()) {
                    std::uint8_t tag =
                        IL2CPP::MetadataRegistration::GetTypeTag(def->typeIndex);
                    if (tag != 0) {
                        defaultJson["type_tag"] = tag;
                        json decoded;
                        if (decodeByTag(tag, def->data.data(), def->data.size(),
                                        decoded)) {
                            for (auto kv = decoded.begin(); kv != decoded.end();
                                 ++kv) {
                                defaultJson[kv.key()] = kv.value();
                            }
                        }
                    }
                }
                entry["default"] = std::move(defaultJson);
            }
            arr.push_back(std::move(entry));
        }
        return json{
            {"namespace", td->ns},
            {"name", td->name},
            {"type_index", td->index},
            {"flags", td->flags},
            {"token", td->token},
            {"field_start", td->fieldStart},
            {"field_count", td->fieldCount},
            {"registration_found", regOk},
            {"fields", std::move(arr)},
        };
    });

    // Phase 5 segment 3 — getMetadataStats (IL2CPP).
    // Reads from global-metadata.dat directly — faster than the runtime path
    // and includes literal-only types stripped from the runtime field
    // iterator.
    PipeServer::RegisterHandler("getMetadataStats",
        [updateStats, buildStatsJson](const json&) -> json {
        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) return json{{"error", "global-metadata.dat not loaded"}};
        NameStats s;
        std::uint32_t count = mp.TypeDefinitionCount();
        for (std::uint32_t i = 0; i < count; ++i) {
            auto td = mp.GetTypeDefinition(static_cast<std::int32_t>(i));
            if (!td) continue;
            updateStats(s, td->ns, td->name);
        }
        json out = buildStatsJson(s);
        out["scope"] = "metadata_all";
        return out;
    });

    // Phase 5 segment 2d follow-up — diagnostics for the runtime
    // Il2CppMetadataRegistration AOB scan.
    PipeServer::RegisterHandler("il2cppGetMetadataRegistration",
        [formatHex](const json&) -> json {
        const bool ok = IL2CPP::MetadataRegistration::Find();
        json result = {{"found", ok}};
        if (ok) {
            result["address"]     = formatHex(reinterpret_cast<std::uintptr_t>(
                IL2CPP::MetadataRegistration::GetBase()));
            result["types_array"] = formatHex(reinterpret_cast<std::uintptr_t>(
                IL2CPP::MetadataRegistration::TypesArray()));
            result["types_count"] = IL2CPP::MetadataRegistration::TypesCount();
        }
        return result;
    });

    // Phase 5: global-metadata.dat string literal dump.
    PipeServer::RegisterHandler(MagicStrings::CmdIL2CPPGetMetadataStrings,
        [](const json& args) -> json {
        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) {
            return json{{"error", "global-metadata.dat not loaded"}};
        }
        std::uint32_t limit = 0;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = args["limit"].get<std::uint32_t>();
        }
        std::uint32_t offset = 0;
        if (args.contains("offset") && args["offset"].is_number_unsigned()) {
            offset = args["offset"].get<std::uint32_t>();
        }
        std::uint32_t total = mp.StringLiteralCount();
        if (offset >= total) {
            return json{
                {"version", mp.version()},
                {"count", total}, {"offset", offset},
                {"strings", json::array()},
            };
        }
        std::uint32_t available = total - offset;
        std::uint32_t take = (limit == 0) ? available : std::min(limit, available);

        json arr = json::array();
        for (std::uint32_t i = 0; i < take; ++i) {
            if (auto s = mp.GetStringLiteral(offset + i)) arr.push_back(*s);
            else                                          arr.push_back(nullptr);
        }
        return json{
            {"version", mp.version()},
            {"count", total},
            {"offset", offset},
            {"returned", take},
            {"strings", std::move(arr)},
        };
    });

    // Phase 6 segment C — findInstances (IL2CPP).
    // Each Il2CppObject's first qword is its Il2CppClass*. Resolve the class
    // and scan committed RW pages for that pointer.
    PipeServer::RegisterHandler(MagicStrings::CmdFindInstances,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = std::min<std::size_t>(args["limit"].get<std::size_t>(), 4096);
        }
        auto matches = Memory::ScanForPointer(reinterpret_cast<std::uintptr_t>(k), limit);
        json arr = json::array();
        for (const void* p : matches) {
            arr.push_back(formatHex(reinterpret_cast<std::uintptr_t>(p)));
        }
        return json{
            {"class_ptr", formatHex(reinterpret_cast<std::uintptr_t>(k))},
            {"count",     arr.size()},
            {"limit",     limit},
            {"matches",   std::move(arr)},
        };
    });

    // Phase 6 segment B — walkInstance (IL2CPP). Same shape as the Mono handler
    // above; the .NET object layout (16-byte header, then fields by offset) is
    // shared between both backends.
    PipeServer::RegisterHandler(MagicStrings::CmdWalkInstance,
        [parseAddr, formatHex, decodeField, resolveClassFromArgs](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        const auto& r = IL2CPP::GetResolver();
        IL2CPP::Il2CppDomain* domain = IL2CPP::GetDomain();
        if (domain == nullptr) return json{{"error", "no il2cpp domain"}};

        std::vector<IL2CPP::Il2CppClass*> chain;
        {
            IL2CPP::ThreadAttachGuard guard(domain);
            if (!guard.Attached()) return json{{"error", "il2cpp_thread_attach failed"}};
            for (IL2CPP::Il2CppClass* cur = k; cur != nullptr && chain.size() < 16; ) {
                chain.push_back(cur);
                if (r.class_get_parent == nullptr) break;
                cur = r.class_get_parent(cur);
            }
        }

        json fields = json::array();
        std::string selfNs, selfName;
        for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
            auto res = IL2CPP::ReadClassInfo(*it);
            if (!res.ok) continue;
            const bool isSelf = (*it == k);
            if (isSelf) { selfNs = res.info.self.ns; selfName = res.info.self.name; }
            for (const auto& f : res.info.fields) {
                if (f.flags & 0x10u) continue;
                std::uintptr_t fieldAddr = addr + static_cast<std::uintptr_t>(f.offset);
                json entry = {
                    {"name",    f.name},
                    {"type",    f.typeName},
                    {"offset",  f.offset},
                    {"from",    res.info.self.name},
                    {"address", formatHex(fieldAddr)},
                };
                json decoded = decodeField(f.typeName, fieldAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                    entry[kv.key()] = kv.value();
                }
                fields.push_back(std::move(entry));
                if (fields.size() >= 4096) break;
            }
            if (fields.size() >= 4096) break;
        }

        return json{
            {"address",   formatHex(addr)},
            {"namespace", selfNs},
            {"name",      selfName},
            {"depth",     chain.size()},
            {"count",     fields.size()},
            {"fields",    std::move(fields)},
        };
    });

    // Phase 5 segment 2d + Phase 6 segment D — getEnumValues (IL2CPP).
    // Type-aware decode: detects the enum's underlying integer width from
    // the runtime `value__` field, then decodes each FieldDefault blob using
    // the matching IL2CPP encoding rules (verified against Unity's IL2CPP
    // FieldDefaultValueWriter — encoded form depends on type):
    //   I1/U1/I2/U2/I4/U4 → ECMA-335 §II.23.2 compressed signed int, then
    //                       sign-extended / clamped to the target width.
    //   I8/U8             → 8 bytes raw little-endian.
    //   R4 / R8           → 4 / 8 bytes raw little-endian (float / double).
    //   Boolean           → 1 byte (0 / 1).
    //   Char              → 2 bytes raw little-endian (UTF-16 code unit).
    // Full Il2CppMetadataRegistration AOB scan (so non-enum const literals
    // can also be type-aware) is still pending — see plan.md.
    PipeServer::RegisterHandler(MagicStrings::CmdGetEnumValues,
        [resolveClassFromArgs](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto info = IL2CPP::ReadClassInfo(k);
        if (!info.ok) return json{{"error", "ReadClassInfo failed"}};
        const bool isEnum = (info.info.parent.name == "Enum");
        if (!isEnum) return json{{"error", "class is not an enum"},
                                 {"parent", info.info.parent.name}};

        // Find the storage field `value__` — its CLR type is the enum's
        // underlying integer type. Default to System.Int32 if missing
        // (ECMA-335 §I.8.5.2 says int32 is the default underlying type).
        std::string underlying = "System.Int32";
        for (const auto& f : info.info.fields) {
            if (f.name == "value__") { underlying = f.typeName; break; }
        }

        const auto& mp = IL2CPP::Metadata::Parser::Get();
        if (!mp.loaded()) return json{{"error", "global-metadata.dat not loaded"}};
        std::string ns   = args.value("namespace", std::string{});
        std::string name = args.value("name", std::string{});
        auto td = mp.FindTypeByName(ns, name);
        if (!td) return json{{"error", "type not found in metadata: " + ns + "." + name}};

        // Decode one ECMA-335 §II.23.2 compressed signed int. Used for I1..U4
        // FieldDefaults (IL2CPP packs all integer literals ≤32-bit this way).
        auto decodeCompressedSigned = [](const std::uint8_t* p, std::size_t avail,
                                          std::int64_t& outValue,
                                          std::size_t&  outBytes) -> bool {
            if (avail < 1) return false;
            std::uint8_t b0 = p[0];
            std::uint32_t enc = 0;
            std::size_t   width = 0;
            std::int64_t  bias = 0;
            if ((b0 & 0x80u) == 0u) {
                enc = b0; width = 1; bias = 0x40;
            } else if ((b0 & 0xC0u) == 0x80u) {
                if (avail < 2) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x3Fu) << 8) | p[1];
                width = 2; bias = 0x2000;
            } else if ((b0 & 0xE0u) == 0xC0u) {
                if (avail < 4) return false;
                enc = (static_cast<std::uint32_t>(b0 & 0x1Fu) << 24)
                    | (static_cast<std::uint32_t>(p[1])       << 16)
                    | (static_cast<std::uint32_t>(p[2])       <<  8)
                    |  static_cast<std::uint32_t>(p[3]);
                width = 4; bias = 0x10000000;
            } else {
                return false;  // 0xE0–0xFF reserved
            }
            std::int64_t magnitude = static_cast<std::int64_t>(enc >> 1);
            outValue = (enc & 1u) ? (magnitude - bias) : magnitude;
            outBytes = width;
            return true;
        };

        // Type-aware decode: takes the underlying enum type name and a
        // FieldDefault blob, fills `entry` with `value` (typed) +
        // `enc_bytes`. Returns false on shape mismatch.
        auto decodeTyped = [&decodeCompressedSigned]
            (const std::string& typeName, const std::uint8_t* p, std::size_t avail,
             json& entry) -> bool {
            auto compressed = [&](auto&& signCast, auto&& finalCast) -> bool {
                std::int64_t raw = 0; std::size_t w = 0;
                if (!decodeCompressedSigned(p, avail, raw, w)) return false;
                entry["value"]     = finalCast(signCast(raw));
                entry["enc_bytes"] = w;
                return true;
            };
            if (typeName == "System.SByte")
                return compressed([](std::int64_t x){ return static_cast<std::int8_t>(x);  },
                                  [](std::int8_t x){ return static_cast<std::int64_t>(x); });
            if (typeName == "System.Byte")
                return compressed([](std::int64_t x){ return static_cast<std::uint8_t>(x); },
                                  [](std::uint8_t x){ return static_cast<std::uint64_t>(x); });
            if (typeName == "System.Int16")
                return compressed([](std::int64_t x){ return static_cast<std::int16_t>(x); },
                                  [](std::int16_t x){ return static_cast<std::int64_t>(x); });
            if (typeName == "System.UInt16")
                return compressed([](std::int64_t x){ return static_cast<std::uint16_t>(x); },
                                  [](std::uint16_t x){ return static_cast<std::uint64_t>(x); });
            if (typeName == "System.Int32")
                return compressed([](std::int64_t x){ return static_cast<std::int32_t>(x); },
                                  [](std::int32_t x){ return static_cast<std::int64_t>(x); });
            if (typeName == "System.UInt32")
                return compressed([](std::int64_t x){ return static_cast<std::uint32_t>(x); },
                                  [](std::uint32_t x){ return static_cast<std::uint64_t>(x); });
            if (typeName == "System.Int64") {
                if (avail < 8) return false;
                std::int64_t v = 0; std::memcpy(&v, p, 8);
                entry["value"] = v; entry["enc_bytes"] = 8; return true;
            }
            if (typeName == "System.UInt64") {
                if (avail < 8) return false;
                std::uint64_t v = 0; std::memcpy(&v, p, 8);
                entry["value"] = v; entry["enc_bytes"] = 8; return true;
            }
            if (typeName == "System.Single") {
                if (avail < 4) return false;
                float v = 0.f; std::memcpy(&v, p, 4);
                entry["value"] = v; entry["enc_bytes"] = 4; return true;
            }
            if (typeName == "System.Double") {
                if (avail < 8) return false;
                double v = 0.0; std::memcpy(&v, p, 8);
                entry["value"] = v; entry["enc_bytes"] = 8; return true;
            }
            if (typeName == "System.Boolean") {
                if (avail < 1) return false;
                entry["value"] = (p[0] != 0); entry["enc_bytes"] = 1; return true;
            }
            if (typeName == "System.Char") {
                if (avail < 2) return false;
                std::uint16_t v = 0; std::memcpy(&v, p, 2);
                entry["value"] = v; entry["enc_bytes"] = 2; return true;
            }
            return false;
        };

        json values = json::array();
        auto fields = mp.EnumerateTypeFields(*td);
        static const char* kHex = "0123456789ABCDEF";
        for (const auto& f : fields) {
            // Enums have one instance field "value__" plus a literal per member.
            // Skip the storage field; its FieldDefault is absent anyway.
            if (f.name == "value__") continue;
            auto def = mp.FindFieldDefault(f.index);
            if (!def) continue;
            json entry = {
                {"name", f.name},
                {"field_index", f.index},
            };
            if (def->data.empty() ||
                !decodeTyped(underlying, def->data.data(), def->data.size(), entry)) {
                entry["decode_error"] = "type-aware decode failed for " + underlying;
            }
            std::string hex;
            std::size_t take = std::min<std::size_t>(def->data.size(), 8);
            for (std::size_t i = 0; i < take; ++i) {
                hex.push_back(kHex[def->data[i] >> 4]);
                hex.push_back(kHex[def->data[i] & 0xF]);
            }
            entry["raw_hex"] = hex;
            values.push_back(std::move(entry));
        }
        return json{
            {"namespace",       td->ns},
            {"name",            td->name},
            {"underlying_type", underlying},
            {"count",           values.size()},
            {"values",          std::move(values)},
        };
    });
#endif
}

}  // namespace

extern "C" {

int UnityDumper_Init() {
    bool expected = false;
    if (!g_initialized.compare_exchange_strong(expected, true)) return 0;
    UnityCEDumper::Log::Init();
    UnityCEDumper::Log::Init("ExportAPI: UnityDumper_Init");
    RegisterCoreHandlers();
    return 0;
}

void UnityDumper_Shutdown() {
    if (!g_initialized.exchange(false)) return;
    UnityCEDumper::PipeServer::Stop();
    UnityCEDumper::Log::Init("ExportAPI: UnityDumper_Shutdown");
    UnityCEDumper::Log::Shutdown();
}

int UnityDumper_StartPipe() {
    if (!g_initialized.load()) UnityDumper_Init();
    if (UnityCEDumper::PipeServer::IsRunning()) return 0;
    return UnityCEDumper::PipeServer::Start() ? 0 : 1;
}

void UnityDumper_StopPipe() {
    UnityCEDumper::PipeServer::Stop();
}

UnityDumperBackend UnityDumper_GetBackendType() {
    return BackendOfBuild();
}

std::uint32_t UnityDumper_GetBuildNumber() {
    return static_cast<std::uint32_t>(UNITYCEDUMPER_BUILD_NUMBER);
}

}  // extern "C"
