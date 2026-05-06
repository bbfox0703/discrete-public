// MagicStrings.h
// Single source of truth for all literal strings shared across the DLL.
// Keep every magic string here so the DLL and UI stay in sync (mirror in C# UI/Constants.cs).
#pragma once

namespace UnityCEDumper::MagicStrings {

// IPC ----------------------------------------------------------------------
inline constexpr const wchar_t* PipeName = L"\\\\.\\pipe\\UnityDumpBfx";

// Logging ------------------------------------------------------------------
inline constexpr const wchar_t* LogRootEnvVar = L"LOCALAPPDATA";
inline constexpr const wchar_t* LogRootSubpath = L"UnityCEDumper\\Logs";

// DLL log categories (one subfolder each, per Architecture.md)
inline constexpr const wchar_t* LogCategoryInit     = L"init";
inline constexpr const wchar_t* LogCategoryScan     = L"scan";
inline constexpr const wchar_t* LogCategoryMetadata = L"metadata";
inline constexpr const wchar_t* LogCategoryPipe     = L"pipe";
inline constexpr const wchar_t* LogCategoryWalk     = L"walk";

// Log rotation
inline constexpr unsigned long long LogMaxFileBytes = 8ull * 1024ull * 1024ull;  // 8 MB
inline constexpr int LogRotationFileCount = 4;

// Unity module names (backend detection)
inline constexpr const wchar_t* ModuleMonoBdwgc   = L"mono-2.0-bdwgc.dll";
inline constexpr const wchar_t* ModuleMonoLegacy  = L"mono.dll";
inline constexpr const wchar_t* ModuleIL2CPP      = L"GameAssembly.dll";

// Pipe protocol commands (mirror in C# UI)
inline constexpr const char* CmdHandshake          = "handshake";
inline constexpr const char* CmdScan               = "scan";
inline constexpr const char* CmdGetAssemblies      = "getAssemblies";
inline constexpr const char* CmdGetClasses         = "getClasses";
inline constexpr const char* CmdGetClassInfo       = "getClassInfo";
inline constexpr const char* CmdGetFieldInfo       = "getFieldInfo";
inline constexpr const char* CmdGetMethodInfo      = "getMethodInfo";
inline constexpr const char* CmdGetStaticFieldData = "getStaticFieldData";
inline constexpr const char* CmdFindInstances      = "findInstances";
inline constexpr const char* CmdReadMemory         = "readMemory";
inline constexpr const char* CmdWriteMemory        = "writeMemory";
inline constexpr const char* CmdRegisterSymbol     = "registerSymbol";
inline constexpr const char* CmdExportCeXml        = "exportCeXml";
inline constexpr const char* CmdExportCsx          = "exportCsx";
inline constexpr const char* CmdExportSdkHeader    = "exportSdkHeader";
inline constexpr const char* CmdGetEnumValues      = "getEnumValues";
inline constexpr const char* CmdResolvePointer     = "resolvePointer";
inline constexpr const char* CmdWalkInstance       = "walkInstance";
inline constexpr const char* CmdWalkArray          = "walkArray";
inline constexpr const char* CmdMonoCompileMethod  = "monoCompileMethod";
inline constexpr const char* CmdMonoGetVTable      = "monoGetVTable";
inline constexpr const char* CmdIL2CPPGetMetadataStrings = "il2cppGetMetadataStrings";
inline constexpr const char* CmdIL2CPPGetGenericInstances = "il2cppGetGenericInstances";

// Phase 17a — address finder (reverse lookup): identify which managed
// instance + class an arbitrary process address belongs to. Inverse of
// findInstances.
inline constexpr const char* CmdWhatIsAt              = "whatIsAt";
inline constexpr const char* CmdGetClassRangeStats    = "getClassRangeStats";

}  // namespace UnityCEDumper::MagicStrings
