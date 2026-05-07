// ExportAPI.cpp — implementation of the C ABI surface.

#define UNITYCEDUMPER_BUILDING_DLL
#include "ExportAPI.h"

#include "AobPattern.h"
#include "BackendDetector.h"
#include "Logger.h"
#include "MagicStrings.h"
#include "Memory.h"
#include "ModuleHelper.h"
#include "PipeServer.h"

#if defined(UNITYCEDUMPER_BACKEND_MONO)
#  include "../mono/MonoAddressFinder.h"
#  include "../mono/MonoClassWalker.h"
#  include "../mono/MonoDomain.h"
#  include "../mono/MonoMethodFinder.h"
#  include "../mono/MonoMethodInvoker.h"
#  include "../mono/MonoResolver.h"
#  include "../mono/MonoThreadAttach.h"
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
#  include "../il2cpp/IL2CPPAddressFinder.h"
#  include "../il2cpp/IL2CPPClassWalker.h"
#  include "../il2cpp/IL2CPPDomain.h"
#  include "../il2cpp/IL2CPPMethodFinder.h"
#  include "../il2cpp/IL2CPPMethodInvoker.h"
#  include "../il2cpp/IL2CPPMetadataRegistration.h"
#  include "../il2cpp/IL2CPPResolver.h"
#  include "../il2cpp/IL2CPPThreadAttach.h"
#  include "../il2cpp/MetadataParser.h"
#endif

#include <nlohmann/json.hpp>

#include <windows.h>

#include <algorithm>
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
    // Phase 17a follow-up: also drops the address-finder class-pointer cache so
    // newly-loaded assemblies get picked up by the next whatIsAt call.
    PipeServer::RegisterHandler(MagicStrings::CmdScan, [](const json&) -> json {
#if defined(UNITYCEDUMPER_BACKEND_MONO)
        Mono::InvalidateClassRangeCache();
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
        IL2CPP::InvalidateClassRangeCache();
#endif
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
    // Phase 10 follow-up — walkArray (Both backends).
    // Reads a Mono / IL2CPP managed array (T[]) header and returns up to
    // `limit` element addresses. Decodes primitive elements inline; for
    // reference-type arrays (8-byte slots) returns the dereferenced object
    // pointer per slot so callers can chain into walkInstance.
    //
    // Array layout (identical between Mono bdwgc and IL2CPP, x64):
    //   +0x00 vtable*   |   +0x10 bounds*    (NULL for SZARRAY)
    //   +0x08 sync      |   +0x18 max_length (uintptr_t — total length)
    //                       +0x20 vector[0]  (first element)
    //
    // List<T> handling stays client-side — caller readMemory()s the
    // List._items pointer at offset 0x10 first, then walkArray()s that.
    //
    // Phase 10d follow-up: when the caller supplies an
    // `elementAssembly`/`elementNamespace`/`elementName` triple, the active
    // backend resolves the element class and fills element_size +
    // is_pointer + element_class_* from class_value_size /
    // class_is_valuetype. Lets the UI walk struct arrays (Vector3[] etc.)
    // correctly without needing to hand-hint the stride.
    // ------------------------------------------------------------------------
    PipeServer::RegisterHandler(MagicStrings::CmdWalkArray,
        [parseAddr, formatHex, decodeField, bytesToHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::string elementType = args.value("elementType", std::string{});
        std::size_t limit = 256;
        if (args.contains("limit") && args["limit"].is_number_unsigned()) {
            limit = args["limit"].get<std::size_t>();
        }
        if (limit == 0)    limit = 256;
        if (limit > 4096)  limit = 4096;

        // Element width: well-known primitive table first, caller hint as
        // override / fallback for struct arrays we can't introspect from
        // here.
        auto knownElemSize = [](const std::string& t) -> std::size_t {
            if (t == "System.Boolean" || t == "System.Byte" || t == "System.SByte") return 1;
            if (t == "System.Int16"   || t == "System.UInt16" || t == "System.Char") return 2;
            if (t == "System.Int32"   || t == "System.UInt32" || t == "System.Single") return 4;
            if (t == "System.Int64"   || t == "System.UInt64" || t == "System.Double") return 8;
            if (t == "System.IntPtr"  || t == "System.UIntPtr") return 8;
            return 0;  // unknown / reference type
        };

        std::size_t elemSize = knownElemSize(elementType);
        bool        isPointer = false;
        bool        elemClassResolved = false;
        std::string elemClassNs, elemClassName;
        bool        elemIsValueType = false;

        // Optional: caller-supplied element-class spec for runtime lookup.
        // The active backend (this DLL is built for one or the other) drives
        // the resolution. Falls through cleanly when not supplied or when
        // the resolver doesn't have the optional exports bound.
        std::string elemAsm = args.value("elementAssembly",  std::string{});
        std::string elemNs  = args.value("elementNamespace", std::string{});
        std::string elemNm  = args.value("elementName",      std::string{});
        if (!elemAsm.empty() && !elemNm.empty()) {
#if defined(UNITYCEDUMPER_BACKEND_IL2CPP)
            const auto& r = IL2CPP::GetResolver();
            if (r.resolved) {
                const IL2CPP::Il2CppImage* img = IL2CPP::FindImageByName(elemAsm);
                IL2CPP::Il2CppClass* k = (img != nullptr) ? IL2CPP::FindClass(img, elemNs, elemNm) : nullptr;
                if (k != nullptr) {
                    elemClassResolved = true;
                    if (r.class_get_name      != nullptr) elemClassName = r.class_get_name(k)      ? r.class_get_name(k)      : "";
                    if (r.class_get_namespace != nullptr) elemClassNs   = r.class_get_namespace(k) ? r.class_get_namespace(k) : "";
                    if (r.class_is_valuetype != nullptr) elemIsValueType = r.class_is_valuetype(k) != 0;
                    if (r.class_value_size != nullptr) {
                        std::uint32_t align = 0;
                        int sz = r.class_value_size(k, &align);
                        if (sz > 0) elemSize = static_cast<std::size_t>(sz);
                    }
                }
            }
#elif defined(UNITYCEDUMPER_BACKEND_MONO)
            const auto& r = Mono::GetResolver();
            if (r.resolved) {
                // Phase 14d: Mono::Find* helpers do their own attach/detach,
                // so call them OUTSIDE of any guard (avoids the nested
                // attach pattern noted in build #54's BongoCat regression).
                // The bare runtime calls below (class_get_name, etc.) ARE
                // direct resolver invocations on a thread Mono never
                // created, so they need their own attach scope. We open
                // one guard for the whole batch — single attach, not four.
                Mono::MonoImage* img = Mono::FindImageByName(elemAsm);
                Mono::MonoClass* k = (img != nullptr) ? Mono::FindClass(img, elemNs, elemNm) : nullptr;
                if (k != nullptr) {
                    Mono::MonoDomain* domain = Mono::GetRootDomain();
                    Mono::ThreadAttachGuard guard(domain);
                    if (guard.Attached()) {
                        elemClassResolved = true;
                        if (r.class_get_name      != nullptr) elemClassName = r.class_get_name(k)      ? r.class_get_name(k)      : "";
                        if (r.class_get_namespace != nullptr) elemClassNs   = r.class_get_namespace(k) ? r.class_get_namespace(k) : "";
                        if (r.class_is_valuetype != nullptr) elemIsValueType = r.class_is_valuetype(k) != 0;
                        if (r.class_value_size != nullptr) {
                            std::uint32_t align = 0;
                            int sz = r.class_value_size(k, &align);
                            if (sz > 0) elemSize = static_cast<std::size_t>(sz);
                        }
                    }
                }
            }
#endif
            // If resolver bound + class found + we got a value-type, leave
            // is_pointer=false. Otherwise treat as ref by default below.
            if (elemClassResolved) {
                isPointer = !elemIsValueType;
                if (elementType.empty()) {
                    elementType = elemClassNs.empty()
                        ? elemClassName
                        : (elemClassNs + "." + elemClassName);
                }
            }
        }

        // If element-class lookup didn't fire / failed, fall back to the
        // primitive table + caller hints / pointer default.
        if (elemSize == 0) {
            if (args.contains("elementSize") && args["elementSize"].is_number_unsigned()) {
                elemSize = args["elementSize"].get<std::size_t>();
            } else {
                elemSize = 8;
                isPointer = true;
            }
        }

        // Read array header — bounds (validate SZARRAY) + max_length.
        auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
            return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
        };
        std::uintptr_t bounds = 0;
        std::uintptr_t length = 0;
        if (!rd(addr + 0x10, &bounds, 8) || !rd(addr + 0x18, &length, 8)) {
            return json{{"error", "header unreadable"}, {"address", formatHex(addr)}};
        }
        // Sanity: a real managed array has length below ~16M for our use
        // (anything bigger is almost certainly a bad pointer / not an array).
        if (length > 16u * 1024u * 1024u) {
            return json{
                {"error",   "implausible length — not a managed array?"},
                {"address", formatHex(addr)},
                {"length",  length},
            };
        }

        std::uintptr_t dataAddr = addr + 0x20;
        std::size_t    n = (length < limit) ? static_cast<std::size_t>(length) : limit;

        json elements = json::array();
        for (std::size_t i = 0; i < n; ++i) {
            std::uintptr_t slotAddr = dataAddr + i * elemSize;
            json e = {
                {"index",   i},
                {"address", formatHex(slotAddr)},
            };
            if (isPointer) {
                std::uintptr_t v = 0;
                if (rd(slotAddr, &v, 8)) e["ptr"] = formatHex(v);
                else                     e["error"] = "unreadable";
            } else if (!elementType.empty()) {
                // Decode primitive inline using the existing field decoder.
                json decoded = decodeField(elementType, slotAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv)
                    e[kv.key()] = kv.value();
            } else {
                // Caller gave us a struct width but no name — return raw bytes.
                std::vector<std::uint8_t> buf(elemSize);
                if (rd(slotAddr, buf.data(), elemSize))
                    e["hex"] = bytesToHex(buf.data(), buf.size());
                else
                    e["error"] = "unreadable";
            }
            elements.push_back(std::move(e));
        }

        json out = {
            {"address",      formatHex(addr)},
            {"data_address", formatHex(dataAddr)},
            {"length",       length},
            {"limit",        limit},
            {"element_type", elementType},
            {"element_size", elemSize},
            {"is_pointer",   isPointer},
            {"count",        n},
            {"elements",     std::move(elements)},
        };
        if (elemClassResolved) {
            out["element_class_namespace"] = elemClassNs;
            out["element_class_name"]      = elemClassName;
            out["element_is_value_type"]   = elemIsValueType;
        }
        return out;
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

    // ----------------------------------------------------------------------
    // Phase 18a-3 — backend-agnostic AOB pattern generator. Reads N bytes
    // (default 32, capped 128) from the supplied address, walks them with a
    // minimal x64 length disassembler, returns a CE-style pattern string
    // with rel8/rel32 immediates and RIP-relative ModRM disp32 fields
    // wildcarded. Also resolves the containing module name + offset so the
    // UI can emit `aobscanmodule(name, GameAssembly.dll, ...)` directly.
    // ----------------------------------------------------------------------
    PipeServer::RegisterHandler(MagicStrings::CmdGetMethodAobPattern,
        [parseAddr, bytesToHex, formatHex](const json& args) -> json {
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::size_t length = 32;
        if (args.contains("length") && args["length"].is_number_unsigned()) {
            length = args["length"].get<std::size_t>();
        }
        if (length == 0)   length = 32;
        if (length > 128)  length = 128;

        std::vector<std::uint8_t> buf(length);
        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(addr),
                                   buf.data(), buf.size())) {
            return json{
                {"error",   "address unreadable"},
                {"address", formatHex(addr)},
                {"length",  length},
            };
        }

        auto pr = Aob::BuildPattern(buf.data(), buf.size());

        // Resolve containing module — needed for the AA script's
        // aobscanmodule(name, MODULE, AOB) form.
        std::string moduleName;
        std::uintptr_t moduleBase = 0;
        std::size_t    moduleSize = 0;
        for (const auto& mi : ModuleHelper::EnumerateModules()) {
            const auto base = reinterpret_cast<std::uintptr_t>(mi.baseAddress);
            if (base == 0 || mi.sizeOfImage == 0) continue;
            if (addr >= base && addr < base + mi.sizeOfImage) {
                moduleBase = base;
                moduleSize = mi.sizeOfImage;
                int n = ::WideCharToMultiByte(CP_UTF8, 0, mi.name.data(),
                                              static_cast<int>(mi.name.size()),
                                              nullptr, 0, nullptr, nullptr);
                if (n > 0) {
                    moduleName.assign(static_cast<std::size_t>(n), '\0');
                    ::WideCharToMultiByte(CP_UTF8, 0, mi.name.data(),
                                          static_cast<int>(mi.name.size()),
                                          moduleName.data(), n, nullptr, nullptr);
                }
                break;
            }
        }

        std::size_t wildcards = 0;
        for (bool b : pr.wildcardMask) if (b) ++wildcards;

        json result = {
            {"address",      formatHex(addr)},
            {"length",       pr.length},
            {"pattern",      pr.pattern},
            {"bytes",        bytesToHex(pr.bytes.data(), pr.bytes.size())},
            {"moduleName",   moduleName},
            {"moduleBase",   formatHex(moduleBase)},
            {"moduleOffset", formatHex(moduleBase ? (addr - moduleBase) : 0)},
            {"moduleSize",   moduleSize},
            {"stats", json{
                {"instructions", pr.instructionCount},
                {"wildcards",    wildcards},
                {"relBranches",  pr.relBranchCount},
                {"ripRel",       pr.ripRelCount},
            }},
            {"decoderBailed", pr.decoderBailed},
        };
        if (pr.decoderBailed) {
            result["bailedAt"] = pr.bailedAt;
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
                if (f.isEnum)      entry["is_enum"]       = true;
                if (f.isValueType) entry["is_value_type"] = true;
                if (!f.underlyingTypeName.empty())
                    entry["underlying_type"] = f.underlyingTypeName;
                // For enums, decode using the underlying integer type so the
                // value comes back as an int instead of the catch-all 8-byte
                // ptr. Falls back to the original typeName when the underlying
                // wasn't detected (older runtimes without class_enum_basetype).
                const std::string& decodeType = (f.isEnum && !f.underlyingTypeName.empty())
                    ? f.underlyingTypeName : f.typeName;
                json decoded = decodeField(decodeType, fieldAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                    entry[kv.key()] = kv.value();
                }
                fields.push_back(std::move(entry));
                if (fields.size() >= 4096) break;
            }
            if (fields.size() >= 4096) break;
        }

        // Sort by offset so the inspector grid displays fields in memory
        // order regardless of metadata declaration order or parent-chain
        // traversal direction. Inherited fields naturally fall before the
        // class's own fields because base-class layout always precedes
        // derived-class layout in .NET.
        std::sort(fields.begin(), fields.end(),
                  [](const json& a, const json& b) {
                      return a.value("offset", 0) < b.value("offset", 0);
                  });

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

    // Phase 5 follow-up — getEnumValues (Mono).
    // Mono stores const literal defaults as raw little-endian bytes (no
    // type / length prefix and no compressed-int trickery — that's an
    // IL2CPP-specific encoding). The width is determined by the enum's
    // underlying type, which we read from the runtime `value__` field
    // (same trick getEnumValues uses on IL2CPP).
    PipeServer::RegisterHandler(MagicStrings::CmdGetEnumValues,
        [resolveClassFromArgs](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        const auto& r = Mono::GetResolver();
        if (r.field_get_data == nullptr) {
            return json{
                {"error", "mono_field_get_data not exported in this Mono build"},
                {"hint",  "rebuild against a Unity Mono with the standard public API"},
            };
        }
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto info = Mono::ReadClassInfo(k);
        if (!info.ok) return json{{"error", "ReadClassInfo failed"}};
        if (info.info.parent.name != "Enum") {
            return json{
                {"error", "class is not an enum"},
                {"parent", info.info.parent.name},
            };
        }

        // Underlying integer width comes from the value__ field's type name.
        std::string underlying = "System.Int32";
        for (const auto& f : info.info.fields) {
            if (f.name == "value__") { underlying = f.typeName; break; }
        }

        // Per-type raw-LE decoder.
        //
        // mono_field_get_data returns a pointer that includes the blob's
        // ECMA-335 §II.23.2 compressed length prefix (1, 2, or 4 bytes).
        // Empirical: DayOfWeek.Sunday came back as 4 instead of 0 because we
        // were reading the prefix byte (0x04 — the length, not the value)
        // as the first byte of the int32. Skipping the prefix puts us on
        // the actual raw LE value bytes.
        auto decodeRaw = [](const std::string& typeName, const std::uint8_t* raw,
                            json& out) -> bool {
            if (raw == nullptr) { out["error"] = "no default data"; return false; }
            std::size_t prefix = 0;
            std::uint8_t b0 = raw[0];
            if      ((b0 & 0x80u) == 0u)    prefix = 1;
            else if ((b0 & 0xC0u) == 0x80u) prefix = 2;
            else if ((b0 & 0xE0u) == 0xC0u) prefix = 4;
            else { out["error"] = "invalid blob length prefix"; return false; }
            out["blob_prefix_bytes"] = prefix;
            const std::uint8_t* p = raw + prefix;

            auto put = [&](auto v, std::size_t bytes) {
                out["value"] = v;
                out["enc_bytes"] = bytes;
            };
            if (typeName == "System.SByte")   { put(static_cast<std::int64_t>(static_cast<std::int8_t>(*p)),   1); return true; }
            if (typeName == "System.Byte")    { put(static_cast<std::uint64_t>(*p),                            1); return true; }
            if (typeName == "System.Boolean") { out["value"] = (*p != 0); out["enc_bytes"] = 1;                   return true; }
            if (typeName == "System.Int16")   { std::int16_t v = 0;  std::memcpy(&v, p, 2); put(static_cast<std::int64_t>(v),  2); return true; }
            if (typeName == "System.UInt16" ||
                typeName == "System.Char")    { std::uint16_t v = 0; std::memcpy(&v, p, 2); put(static_cast<std::uint64_t>(v), 2); return true; }
            if (typeName == "System.Int32")   { std::int32_t v = 0;  std::memcpy(&v, p, 4); put(static_cast<std::int64_t>(v),  4); return true; }
            if (typeName == "System.UInt32")  { std::uint32_t v = 0; std::memcpy(&v, p, 4); put(static_cast<std::uint64_t>(v), 4); return true; }
            if (typeName == "System.Int64")   { std::int64_t v = 0;  std::memcpy(&v, p, 8); put(v, 8);                             return true; }
            if (typeName == "System.UInt64")  { std::uint64_t v = 0; std::memcpy(&v, p, 8); put(v, 8);                             return true; }
            if (typeName == "System.Single")  { float  v = 0.f;      std::memcpy(&v, p, 4); put(v, 4);                             return true; }
            if (typeName == "System.Double")  { double v = 0.0;      std::memcpy(&v, p, 8); put(v, 8);                             return true; }
            return false;
        };

        static const char* kHex = "0123456789ABCDEF";
        json values = json::array();
        auto literals = Mono::EnumerateLiteralFields(k);
        for (const auto& lit : literals) {
            if (lit.name == "value__") continue;  // storage field, not a member
            json entry = {
                {"name", lit.name},
                {"type", lit.typeName},
            };
            // Diagnostic: show the first ~16 bytes of the blob (length prefix
            // + value) so the encoding is auditable from the pipe response.
            if (lit.data != nullptr) {
                std::string hex;
                auto* bp = reinterpret_cast<const std::uint8_t*>(lit.data);
                for (std::size_t i = 0; i < 16; ++i) {
                    hex.push_back(kHex[bp[i] >> 4]);
                    hex.push_back(kHex[bp[i] & 0xF]);
                }
                entry["raw_hex"] = hex;
            }
            if (!decodeRaw(underlying,
                           reinterpret_cast<const std::uint8_t*>(lit.data),
                           entry)) {
                entry.erase("error");
                entry["decode_error"] = "unsupported underlying type: " + underlying;
            }
            values.push_back(std::move(entry));
        }
        return json{
            {"namespace",       info.info.self.ns},
            {"name",            info.info.self.name},
            {"underlying_type", underlying},
            {"count",           values.size()},
            {"values",          std::move(values)},
        };
    });

    // Phase 17a — address finder (Mono); 17b adds field-offset resolution.
    // Backward 8-byte stride scan from the queried address; matches MonoVTable*
    // candidates against a cache built lazily on first call. The cache walks
    // every assembly's classes once and resolves a vtable per class via
    // mono_class_vtable, then maps vtable→class for the lookup. The 17b
    // pass walks the parent chain inside the same attach scope and resolves
    // the queried byte offset back to a declared field (or the nearest one
    // below it).
    PipeServer::RegisterHandler(MagicStrings::CmdWhatIsAt,
        [parseAddr, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::size_t maxBackward = 4096;
        if (args.contains("maxBackwardScan") && args["maxBackwardScan"].is_number_unsigned()) {
            maxBackward = args["maxBackwardScan"].get<std::size_t>();
        }
        auto hit = Mono::WhatIsAt(addr, maxBackward);
        if (!hit.found) {
            return json{
                {"found",   false},
                {"reason",  hit.reason},
                {"address", formatHex(hit.queriedAddr)},
                {"scannedRange", json{
                    {"from", formatHex(hit.scannedFrom)},
                    {"to",   formatHex(hit.scannedTo)},
                }},
                {"scannedSteps", hit.scannedSteps},
            };
        }
        // Phase 17b — render FieldHit (shared shape across backends).
        auto fieldToJson = [](const Mono::FieldHit& f) -> json {
            json out = {
                {"name",         f.name},
                {"type",         f.typeName},
                {"offset",       f.offset},
                {"size",         f.size},
                {"isStatic",     f.isStatic},
                {"isValueType",  f.isValueType},
                {"isEnum",       f.isEnum},
                {"declaredIn",   f.declaredIn},
                {"kind",         f.kind},
            };
            if (!f.underlyingTypeName.empty())
                out["underlying_type"] = f.underlyingTypeName;
            if (f.kind == "interior")
                out["interiorOffset"] = static_cast<std::int64_t>(f.interiorOffset);
            return out;
        };
        json result = {
            {"found",       true},
            {"address",     formatHex(hit.queriedAddr)},
            {"instance",    formatHex(hit.instance)},
            {"vtable_ptr",  formatHex(hit.vtablePtr)},
            {"class_ptr",   formatHex(hit.klassPtr)},
            {"assembly",    hit.assembly},
            {"namespace",   hit.ns},
            {"class",       hit.className},
            {"fieldOffset", static_cast<std::int64_t>(hit.fieldOffset)},
            {"scannedRange", json{
                {"from", formatHex(hit.scannedFrom)},
                {"to",   formatHex(hit.scannedTo)},
            }},
            {"scannedSteps", hit.scannedSteps},
        };
        result["field"]        = hit.field.present        ? fieldToJson(hit.field)        : json(nullptr);
        result["nearestField"] = hit.nearestField.present ? fieldToJson(hit.nearestField) : json(nullptr);
        if (hit.nearestField.present) {
            result["nearestFieldDelta"] = static_cast<std::int64_t>(hit.nearestFieldDelta);
        }
        return result;
    });

    // Phase 17a follow-up: diagnostics for the class-pointer cache. Lets the
    // user / smoke test confirm the cache is populated and inspect its bounds
    // before issuing a whatIsAt query.
    PipeServer::RegisterHandler(MagicStrings::CmdGetClassRangeStats,
        [formatHex](const json& args) -> json {
        if (args.value("rebuild", false)) Mono::InvalidateClassRangeCache();
        Mono::BuildClassRangeCache();
        auto s = Mono::GetClassRangeStats();
        return json{
            {"backend",      "Mono"},
            {"built",        s.built},
            {"class_count",  s.classCount},
            {"vtable_count", s.vtableCount},
            {"min_klass",    formatHex(s.minKlass)},
            {"max_klass",    formatHex(s.maxKlass)},
            {"min_vtable",   formatHex(s.minVtable)},
            {"max_vtable",   formatHex(s.maxVtable)},
        };
    });

    // Phase 18 — find method instances (Mono). Walks the supplied class's
    // declared methods, returns every overload of `method` with native
    // address (post-JIT) + decoded signature + symbol-safe humanSuffix.
    PipeServer::RegisterHandler(MagicStrings::CmdFindMethodInstances,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch = args.value("signatureMatch", std::string{});
        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto fr = Mono::FindMethodInstances(k, methodName, sigMatch);
        if (!fr.ok) return json{{"error", fr.error}};

        json matches = json::array();
        for (const auto& ov : fr.matches) {
            json names = json::array();
            for (const auto& s : ov.paramNames) names.push_back(s);
            json types = json::array();
            for (const auto& s : ov.paramTypes) types.push_back(s);
            matches.push_back({
                {"name",          ov.name},
                {"token",         ov.token},
                {"flags",         ov.flags},
                {"iflags",        ov.iflags},
                {"isStatic",      ov.isStatic},
                {"isVirtual",     ov.isVirtual},
                {"isAbstract",    ov.isAbstract},
                {"compiled",      ov.compiled},
                {"address",       formatHex(reinterpret_cast<std::uintptr_t>(ov.address))},
                {"paramTypes",    std::move(types)},
                {"paramNames",    std::move(names)},
                {"returnType",    ov.returnType},
                {"declaringClass",     ov.declaringClass},
                {"declaringNamespace", ov.declaringNamespace},
                {"signature",     ov.signatureString},
                {"humanSuffix",   ov.humanSuffix},
            });
        }
        return json{
            {"method",  methodName},
            {"count",   matches.size()},
            {"matches", std::move(matches)},
        };
    });

    // Phase 19b — callMethod (Mono, static-only). Mirror of the IL2CPP
    // handler in the IL2CPP block below: same wire shape, same arg-marshal
    // semantics, mono_runtime_invoke instead of il2cpp_runtime_invoke. 19c
    // will lift the static-only / no-string restrictions; 19b stays tight
    // so the wire shape matches IL2CPP exactly.
    PipeServer::RegisterHandler(MagicStrings::CmdCallMethod,
        [resolveClassFromArgs, parseAddr, formatHex](const json& args) -> json {
        if (!Mono::IsResolved()) return json{{"error", "mono runtime not resolved"}};

        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch   = args.value("signatureMatch", std::string{});

        std::string err;
        Mono::MonoClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        // 19b: instance must be absent / null. Parse for forward-compat; the
        // invoker rejects non-zero with a distinct error code.
        std::uintptr_t instance = 0;
        if (args.contains("instance") && !args["instance"].is_null()) {
            if (!parseAddr(args["instance"], instance)) {
                return json{{"error", "invalid 'instance' (hex string or integer)"}};
            }
        }

        // Decode each {type, value} into a typed CallArg. Identical wire
        // contract to the IL2CPP handler — same `type` tags, same JSON
        // encodings. Spec: docs/lua-invoke.md.
        using Mono::CallArg;
        using Mono::CallArgKind;
        std::vector<CallArg> typedArgs;

        const json& argsArr = args.contains("args") && args["args"].is_array()
                                ? args["args"] : json::array();
        typedArgs.reserve(argsArr.size());
        for (std::size_t i = 0; i < argsArr.size(); ++i) {
            const json& a = argsArr[i];
            if (!a.is_object()) {
                return json{{"error", std::string{"args["} + std::to_string(i) + "] not an object"}};
            }
            std::string ty = a.value("type", std::string{});
            CallArg out;

            if (ty == "null") {
                out.kind = CallArgKind::Null;
            } else if (ty == "bool" || ty == "boolean") {
                out.kind = CallArgKind::Bool;
                if (a.contains("value")) {
                    if      (a["value"].is_boolean())          out.i64 = a["value"].get<bool>() ? 1 : 0;
                    else if (a["value"].is_number_integer())    out.i64 = a["value"].get<std::int64_t>();
                    else if (a["value"].is_number_unsigned())   out.u64 = a["value"].get<std::uint64_t>();
                }
            } else if (ty == "byte"   || ty == "uint8")  { out.kind = CallArgKind::Byte;   out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "sbyte"  || ty == "int8")   { out.kind = CallArgKind::SByte;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "int16"  || ty == "short")  { out.kind = CallArgKind::Int16;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint16" || ty == "ushort") { out.kind = CallArgKind::UInt16; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int32"  || ty == "int")    { out.kind = CallArgKind::Int32;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint32" || ty == "uint")   { out.kind = CallArgKind::UInt32; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int64"  || ty == "long")   { out.kind = CallArgKind::Int64;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint64" || ty == "ulong")  { out.kind = CallArgKind::UInt64; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "float"  || ty == "single") { out.kind = CallArgKind::Float;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "double") { out.kind = CallArgKind::Double;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "char")   { out.kind = CallArgKind::Char;
                  if (a.contains("value")) {
                      if      (a["value"].is_string() && !a["value"].get<std::string>().empty())
                                                          out.u64 = static_cast<std::uint8_t>(a["value"].get<std::string>()[0]);
                      else if (a["value"].is_number())     out.u64 = a["value"].get<std::uint64_t>();
                  }
              }
              else if (ty == "string") {
                  out.kind = CallArgKind::String;          // rejected by invoker in 19b
                  out.s = a.value("value", std::string{});
              }
              else if (ty == "address" || ty == "intptr" || ty == "uintptr" || ty == "ptr") {
                  out.kind = CallArgKind::Address;
                  if (a.contains("value")) {
                      std::uintptr_t addr = 0;
                      if (parseAddr(a["value"], addr)) {
                          out.u64 = addr;
                      } else {
                          return json{{"error", std::string{"args["} + std::to_string(i) + "].value not a valid address"}};
                      }
                  }
              }
              else {
                  return json{{"error", std::string{"args["} + std::to_string(i) + "] unknown type '" + ty + "'"}};
              }
            typedArgs.push_back(std::move(out));
        }

        auto cr = Mono::CallMethod(k, methodName, sigMatch, instance, typedArgs);

        json returnValue = nullptr;
        switch (cr.ret.kind) {
        case Mono::CallReturn::Kind::Void:        returnValue = nullptr; break;
        case Mono::CallReturn::Kind::SignedInt:   returnValue = cr.ret.i64; break;
        case Mono::CallReturn::Kind::UnsignedInt: returnValue = cr.ret.u64; break;
        case Mono::CallReturn::Kind::Float:       returnValue = cr.ret.f; break;
        case Mono::CallReturn::Kind::Address:     returnValue = formatHex(cr.ret.u64); break;
        case Mono::CallReturn::Kind::String:      returnValue = cr.ret.s; break;
        case Mono::CallReturn::Kind::Unsupported: returnValue = nullptr; break;
        }

        json result;
        if (!cr.error.empty()) {
            result["ok"]    = false;
            result["error"] = cr.error;
            return result;
        }
        result["ok"]          = cr.ok;
        result["returnType"]  = cr.ret.typeName;
        result["returnValue"] = std::move(returnValue);
        result["exception"]   = cr.exception.empty() ? json(nullptr) : json(cr.exception);
        result["elapsedMs"]   = cr.elapsedMs;
        result["method"] = json{
            {"address",     formatHex(reinterpret_cast<std::uintptr_t>(cr.addressCalled))},
            {"signature",   cr.methodSignature},
            {"humanSuffix", cr.humanSuffix},
        };
        if (cr.ret.kind == Mono::CallReturn::Kind::Unsupported) {
            result["note"] = "value-type-by-value returns are not decoded in 19b; pass an out-buffer in v2 or read the address";
        }
        return result;
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

    auto resolveClassFromArgs = [parseAddr](const json& args, std::string& errOut) -> IL2CPP::Il2CppClass* {
        // Header-based path: caller hands us the address of an Il2CppObject
        // whose first qword is the runtime Il2CppClass*. Used by the inspector
        // to expand pointer-to-class fields whose declared type is a generic
        // INSTANTIATION (e.g. ClassAry<CARRYITEM>) — those aren't registered
        // by name in the image's class table, so il2cpp_class_from_name can't
        // find them. The runtime always stamps the concrete instantiation's
        // class into the object header, so reading it bypasses the name
        // lookup entirely.
        if (args.contains("klass_from_header_addr")) {
            std::uintptr_t hdrAddr = 0;
            if (!parseAddr(args["klass_from_header_addr"], hdrAddr)) {
                errOut = "invalid 'klass_from_header_addr'"; return nullptr;
            }
            std::uintptr_t klassPtr = 0;
            if (!Memory::SafeReadBytes(reinterpret_cast<void*>(hdrAddr),
                                       &klassPtr, sizeof(klassPtr))) {
                errOut = "object header unreadable"; return nullptr;
            }
            if (klassPtr == 0) { errOut = "object header klass is null"; return nullptr; }
            auto* k = reinterpret_cast<IL2CPP::Il2CppClass*>(klassPtr);
            if (!IL2CPP::ProbeKlassValid(k)) {
                errOut = "klass pointer in header looks invalid";
                return nullptr;
            }
            return k;
        }

        // Direct class-pointer path: caller already knows the Il2CppClass*
        // (e.g. cached from an earlier walk). Same validation as above.
        if (args.contains("class_ptr")) {
            std::uintptr_t klassPtr = 0;
            if (!parseAddr(args["class_ptr"], klassPtr) || klassPtr == 0) {
                errOut = "invalid 'class_ptr'"; return nullptr;
            }
            auto* k = reinterpret_cast<IL2CPP::Il2CppClass*>(klassPtr);
            if (!IL2CPP::ProbeKlassValid(k)) {
                errOut = "class_ptr does not look valid";
                return nullptr;
            }
            return k;
        }

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
                if (f.isEnum)      entry["is_enum"]       = true;
                if (f.isValueType) entry["is_value_type"] = true;
                if (!f.underlyingTypeName.empty())
                    entry["underlying_type"] = f.underlyingTypeName;
                const std::string& decodeType = (f.isEnum && !f.underlyingTypeName.empty())
                    ? f.underlyingTypeName : f.typeName;
                json decoded = decodeField(decodeType, fieldAddr);
                for (auto kv = decoded.begin(); kv != decoded.end(); ++kv) {
                    entry[kv.key()] = kv.value();
                }
                fields.push_back(std::move(entry));
                if (fields.size() >= 4096) break;
            }
            if (fields.size() >= 4096) break;
        }

        // Sort by offset — see Mono walkInstance for rationale.
        std::sort(fields.begin(), fields.end(),
                  [](const json& a, const json& b) {
                      return a.value("offset", 0) < b.value("offset", 0);
                  });

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

    // Phase 17a — address finder (IL2CPP); 17b adds field-offset resolution.
    // Backward 8-byte stride scan; matches Il2CppClass* candidates against a
    // cache built lazily on first call. Cache is one full
    // EnumerateAssemblies → EnumerateClasses pass through every image; ~tens
    // of ms on first hit, sub-ms on subsequent ones. The 17b pass walks the
    // parent chain inside the same attach scope and resolves the queried byte
    // offset back to a declared field (or the nearest one below it).
    PipeServer::RegisterHandler(MagicStrings::CmdWhatIsAt,
        [parseAddr, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::uintptr_t addr = 0;
        if (!args.contains("address") || !parseAddr(args["address"], addr)) {
            return json{{"error", "missing/invalid 'address'"}};
        }
        std::size_t maxBackward = 4096;
        if (args.contains("maxBackwardScan") && args["maxBackwardScan"].is_number_unsigned()) {
            maxBackward = args["maxBackwardScan"].get<std::size_t>();
        }
        auto hit = IL2CPP::WhatIsAt(addr, maxBackward);
        if (!hit.found) {
            return json{
                {"found",   false},
                {"reason",  hit.reason},
                {"address", formatHex(hit.queriedAddr)},
                {"scannedRange", json{
                    {"from", formatHex(hit.scannedFrom)},
                    {"to",   formatHex(hit.scannedTo)},
                }},
                {"scannedSteps", hit.scannedSteps},
            };
        }
        // Phase 17b — render FieldHit (shared shape across backends).
        auto fieldToJson = [](const IL2CPP::FieldHit& f) -> json {
            json out = {
                {"name",         f.name},
                {"type",         f.typeName},
                {"offset",       f.offset},
                {"size",         f.size},
                {"isStatic",     f.isStatic},
                {"isValueType",  f.isValueType},
                {"isEnum",       f.isEnum},
                {"declaredIn",   f.declaredIn},
                {"kind",         f.kind},
            };
            if (!f.underlyingTypeName.empty())
                out["underlying_type"] = f.underlyingTypeName;
            if (f.kind == "interior")
                out["interiorOffset"] = static_cast<std::int64_t>(f.interiorOffset);
            return out;
        };
        json result = {
            {"found",       true},
            {"address",     formatHex(hit.queriedAddr)},
            {"instance",    formatHex(hit.instance)},
            {"class_ptr",   formatHex(hit.klassPtr)},
            {"assembly",    hit.assembly},
            {"namespace",   hit.ns},
            {"class",       hit.className},
            {"fieldOffset", static_cast<std::int64_t>(hit.fieldOffset)},
            {"scannedRange", json{
                {"from", formatHex(hit.scannedFrom)},
                {"to",   formatHex(hit.scannedTo)},
            }},
            {"scannedSteps", hit.scannedSteps},
        };
        result["field"]        = hit.field.present        ? fieldToJson(hit.field)        : json(nullptr);
        result["nearestField"] = hit.nearestField.present ? fieldToJson(hit.nearestField) : json(nullptr);
        if (hit.nearestField.present) {
            result["nearestFieldDelta"] = static_cast<std::int64_t>(hit.nearestFieldDelta);
        }
        return result;
    });

    PipeServer::RegisterHandler(MagicStrings::CmdGetClassRangeStats,
        [formatHex](const json& args) -> json {
        if (args.value("rebuild", false)) IL2CPP::InvalidateClassRangeCache();
        IL2CPP::BuildClassRangeCache();
        auto s = IL2CPP::GetClassRangeStats();
        return json{
            {"backend",     "IL2CPP"},
            {"built",       s.built},
            {"class_count", s.classCount},
            {"min_klass",   formatHex(s.minKlass)},
            {"max_klass",   formatHex(s.maxKlass)},
        };
    });

    // Phase 18 — find method instances (IL2CPP). Returns every overload of
    // `method` on the supplied class, with the AOT native address read out
    // of MethodInfo->methodPointer (or il2cpp_method_get_pointer where
    // bound). No JIT step — IL2CPP is AOT.
    PipeServer::RegisterHandler(MagicStrings::CmdFindMethodInstances,
        [resolveClassFromArgs, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};
        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch = args.value("signatureMatch", std::string{});
        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        auto fr = IL2CPP::FindMethodInstances(k, methodName, sigMatch);
        if (!fr.ok) return json{{"error", fr.error}};

        json matches = json::array();
        for (const auto& ov : fr.matches) {
            json names = json::array();
            for (const auto& s : ov.paramNames) names.push_back(s);
            json types = json::array();
            for (const auto& s : ov.paramTypes) types.push_back(s);
            matches.push_back({
                {"name",          ov.name},
                {"token",         ov.token},
                {"flags",         ov.flags},
                {"iflags",        ov.iflags},
                {"isStatic",      ov.isStatic},
                {"isVirtual",     ov.isVirtual},
                {"isAbstract",    ov.isAbstract},
                {"compiled",      ov.compiled},
                {"address",       formatHex(reinterpret_cast<std::uintptr_t>(ov.address))},
                {"paramTypes",    std::move(types)},
                {"paramNames",    std::move(names)},
                {"returnType",    ov.returnType},
                {"declaringClass",     ov.declaringClass},
                {"declaringNamespace", ov.declaringNamespace},
                {"signature",     ov.signatureString},
                {"humanSuffix",   ov.humanSuffix},
            });
        }
        return json{
            {"method",  methodName},
            {"count",   matches.size()},
            {"matches", std::move(matches)},
        };
    });

    // Phase 19a — callMethod (IL2CPP, static-only). Parses typed args from
    // the JSON request, marshals into IL2CPP's void** params, invokes
    // il2cpp_runtime_invoke under SEH, captures managed exceptions, and
    // unboxes the return value per the resolved method's formal returnType.
    // 19c will lift the static-only / no-string restrictions; 19a stays
    // tight so the wire shape is locked in before instance dispatch lands.
    PipeServer::RegisterHandler(MagicStrings::CmdCallMethod,
        [resolveClassFromArgs, parseAddr, formatHex](const json& args) -> json {
        if (!IL2CPP::IsResolved()) return json{{"error", "il2cpp runtime not resolved"}};

        std::string methodName = args.value("method", std::string{});
        if (methodName.empty()) return json{{"error", "missing 'method' arg"}};
        std::string sigMatch   = args.value("signatureMatch", std::string{});

        std::string err;
        IL2CPP::Il2CppClass* k = resolveClassFromArgs(args, err);
        if (k == nullptr) return json{{"error", err}};

        // 19a: instance must be absent / null. Parse for forward-compat; the
        // invoker rejects non-zero with a distinct error code.
        std::uintptr_t instance = 0;
        if (args.contains("instance") && !args["instance"].is_null()) {
            if (!parseAddr(args["instance"], instance)) {
                return json{{"error", "invalid 'instance' (hex string or integer)"}};
            }
        }

        // Decode each {type, value} into a typed CallArg. Numeric values may
        // arrive as JSON int / uint / float / hex-string ("0xCAFEBABE") for
        // address args. Enforce the wire `type` tags listed in
        // docs/lua-invoke.md — unknown tags get rejected so a typo doesn't
        // silently coerce to null.
        using IL2CPP::CallArg;
        using IL2CPP::CallArgKind;
        std::vector<CallArg> typedArgs;

        const json& argsArr = args.contains("args") && args["args"].is_array()
                                ? args["args"] : json::array();
        typedArgs.reserve(argsArr.size());
        for (std::size_t i = 0; i < argsArr.size(); ++i) {
            const json& a = argsArr[i];
            if (!a.is_object()) {
                return json{{"error", std::string{"args["} + std::to_string(i) + "] not an object"}};
            }
            std::string ty = a.value("type", std::string{});
            CallArg out;

            if (ty == "null") {
                out.kind = CallArgKind::Null;
            } else if (ty == "bool" || ty == "boolean") {
                out.kind = CallArgKind::Bool;
                if (a.contains("value")) {
                    if      (a["value"].is_boolean())          out.i64 = a["value"].get<bool>() ? 1 : 0;
                    else if (a["value"].is_number_integer())    out.i64 = a["value"].get<std::int64_t>();
                    else if (a["value"].is_number_unsigned())   out.u64 = a["value"].get<std::uint64_t>();
                }
            } else if (ty == "byte"   || ty == "uint8")  { out.kind = CallArgKind::Byte;   out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "sbyte"  || ty == "int8")   { out.kind = CallArgKind::SByte;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "int16"  || ty == "short")  { out.kind = CallArgKind::Int16;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint16" || ty == "ushort") { out.kind = CallArgKind::UInt16; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int32"  || ty == "int")    { out.kind = CallArgKind::Int32;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint32" || ty == "uint")   { out.kind = CallArgKind::UInt32; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "int64"  || ty == "long")   { out.kind = CallArgKind::Int64;  out.i64 = a.value("value", std::int64_t{0}); }
              else if (ty == "uint64" || ty == "ulong")  { out.kind = CallArgKind::UInt64; out.u64 = a.value("value", std::uint64_t{0}); }
              else if (ty == "float"  || ty == "single") { out.kind = CallArgKind::Float;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "double") { out.kind = CallArgKind::Double;
                  if (a.contains("value") && a["value"].is_number()) out.f = a["value"].get<double>();
              }
              else if (ty == "char")   { out.kind = CallArgKind::Char;
                  if (a.contains("value")) {
                      if      (a["value"].is_string() && !a["value"].get<std::string>().empty())
                                                          out.u64 = static_cast<std::uint8_t>(a["value"].get<std::string>()[0]);
                      else if (a["value"].is_number())     out.u64 = a["value"].get<std::uint64_t>();
                  }
              }
              else if (ty == "string") {
                  out.kind = CallArgKind::String;          // rejected by invoker in 19a
                  out.s = a.value("value", std::string{});
              }
              else if (ty == "address" || ty == "intptr" || ty == "uintptr" || ty == "ptr") {
                  out.kind = CallArgKind::Address;
                  if (a.contains("value")) {
                      std::uintptr_t addr = 0;
                      if (parseAddr(a["value"], addr)) {
                          out.u64 = addr;
                      } else {
                          return json{{"error", std::string{"args["} + std::to_string(i) + "].value not a valid address"}};
                      }
                  }
              }
              else {
                  return json{{"error", std::string{"args["} + std::to_string(i) + "] unknown type '" + ty + "'"}};
              }
            typedArgs.push_back(std::move(out));
        }

        auto cr = IL2CPP::CallMethod(k, methodName, sigMatch, instance, typedArgs);

        // Render the typed return into a JSON-friendly value. The discriminator
        // chooses how returnValue is encoded — null for Void / Unsupported,
        // numeric for ints/floats, hex string for addresses.
        json returnValue = nullptr;
        switch (cr.ret.kind) {
        case IL2CPP::CallReturn::Kind::Void:        returnValue = nullptr; break;
        case IL2CPP::CallReturn::Kind::SignedInt:   returnValue = cr.ret.i64; break;
        case IL2CPP::CallReturn::Kind::UnsignedInt: returnValue = cr.ret.u64; break;
        case IL2CPP::CallReturn::Kind::Float:       returnValue = cr.ret.f; break;
        case IL2CPP::CallReturn::Kind::Address:     returnValue = formatHex(cr.ret.u64); break;
        case IL2CPP::CallReturn::Kind::String:      returnValue = cr.ret.s; break;
        case IL2CPP::CallReturn::Kind::Unsupported: returnValue = nullptr; break;
        }

        json result;
        if (!cr.error.empty()) {
            result["ok"]    = false;
            result["error"] = cr.error;
            return result;
        }
        result["ok"]          = cr.ok;
        result["returnType"]  = cr.ret.typeName;
        result["returnValue"] = std::move(returnValue);
        result["exception"]   = cr.exception.empty() ? json(nullptr) : json(cr.exception);
        result["elapsedMs"]   = cr.elapsedMs;
        result["method"] = json{
            {"address",     formatHex(reinterpret_cast<std::uintptr_t>(cr.addressCalled))},
            {"signature",   cr.methodSignature},
            {"humanSuffix", cr.humanSuffix},
        };
        if (cr.ret.kind == IL2CPP::CallReturn::Kind::Unsupported) {
            // Tell the caller why returnValue is null even though the call
            // succeeded. 19a doesn't decode multi-field value-type returns.
            result["note"] = "value-type-by-value returns are not decoded in 19a; pass an out-buffer in v2 or read the address";
        }
        return result;
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
