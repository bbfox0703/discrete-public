// ExportAPI.cpp — implementation of the C ABI surface.

#define UNITYCEDUMPER_BUILDING_DLL
#include "ExportAPI.h"

#include "BackendDetector.h"
#include "Logger.h"
#include "MagicStrings.h"
#include "PipeServer.h"

#if defined(UNITYCEDUMPER_BACKEND_MONO)
#  include "../mono/MonoClassWalker.h"
#  include "../mono/MonoDomain.h"
#  include "../mono/MonoResolver.h"
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
    auto formatHex = [](std::uintptr_t v) {
        char buf[32];
        ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "0x%016llX",
                      static_cast<unsigned long long>(v));
        return std::string(buf);
    };

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
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
    // Phase 4: IL2CPP API path. Same command names as the Mono variant.
    auto formatHex = [](std::uintptr_t v) {
        char buf[32];
        ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "0x%016llX",
                      static_cast<unsigned long long>(v));
        return std::string(buf);
    };

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
                entry["default"] = {
                    {"type_index", def->typeIndex},
                    {"data_index", def->dataIndex},
                    {"raw_hex_first16", hex},
                    {"raw_size", def->data.size()},
                };
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
