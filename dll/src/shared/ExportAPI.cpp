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
#  include "../il2cpp/IL2CPPResolver.h"
#  include "../il2cpp/MetadataParser.h"
#endif

#include <nlohmann/json.hpp>

#include <windows.h>

#include <atomic>
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
                // Best-effort string decode (ECMA-335 length-prefix + UTF-8).
                // Pure-disk path; numeric / enum decode waits on segment 2d.
                if (auto s = mp.TryDecodeStringDefault(def->dataIndex)) {
                    defaultJson["decoded_string"] = *s;
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
            {"fields", std::move(arr)},
        };
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
