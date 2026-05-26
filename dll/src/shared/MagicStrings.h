// MagicStrings.h
// Single source of truth for all literal strings shared across the DLL.
// Keep every magic string here so the DLL and UI stay in sync (mirror in C# UI/Constants.cs).
#pragma once

namespace UnityCEDumper::MagicStrings {

// IPC ----------------------------------------------------------------------
inline constexpr const wchar_t* PipeName = L"\\\\.\\pipe\\UnityDumpBfx";

// Logging ------------------------------------------------------------------
inline constexpr const wchar_t* LogRootEnvVar = L"LOCALAPPDATA";
// Renamed 2026-05-20: %LOCALAPPDATA%\UnityCEDumper\Logs\ → %LOCALAPPDATA%\Discrete_UEDumper\Logs\
// to match the repo name (`discrete`) instead of the original working-title
// `UnityCEDumper`. Mirror const in `ui/UnityDumpUI.Core/Constants.cs`.
inline constexpr const wchar_t* LogRootSubpath = L"Discrete_UEDumper\\Logs";

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

// Phase 21a — reverse-property lookup. Given a class spec + a fixed
// byte offset from the instance base, scan the heap for live instances
// (same scan findInstances uses) and decode the field at each
// instance+offset. Inverse of walkInstance (one instance, all fields):
// this returns one field across all instances. Offset must already be
// the cumulative offset from instance base (UI computes it by walking
// the Inspector node's parent chain summing offsets, bailing if any
// ancestor crosses a pointer-traversal field).
inline constexpr const char* CmdListFieldValuesAcrossInstances =
    "listFieldValuesAcrossInstances";

// Phase 18 — find method instances + AOB pattern generator.
// findMethodInstances enumerates every overload of a named method on a class
// and returns each one's native address + decoded signature + a symbol-safe
// human-readable suffix used for AA script symbol naming.
// getMethodAobPattern reads N bytes at the supplied address and returns an
// AOB pattern string with rel32/RIP-relative byte slots wildcarded.
inline constexpr const char* CmdFindMethodInstances   = "findMethodInstances";
inline constexpr const char* CmdGetMethodAobPattern   = "getMethodAobPattern";

// Phase 19 — Lua-callable Unity methods (managed invocation).
// callMethod resolves a method by class + name (+ optional signatureMatch
// to disambiguate overloads), marshals primitive / null / address args
// into a void** params array, calls the runtime invoke entry point, and
// returns the unboxed primitive return value (or raw address for
// reference-type returns) plus any captured managed exception.
// 19a scope: IL2CPP, static methods only, primitive + null + address
// args, primitive + address returns, no string args/returns.
inline constexpr const char* CmdCallMethod            = "callMethod";

// Phase 21a — Inspector field write-back. Given an instance address,
// a field offset (cumulative from the instance base), a type tag (same
// taxonomy as Phase 19 callMethod arg builders), and a JSON-typed value,
// write sizeof(type) bytes to instance+offset via SEH-protected
// SafeWriteBytes. Backend-agnostic (no Mono/IL2CPP API calls — just
// process memory write). v1 scope: primitives + raw address. Reference-
// type writes (System.String, object pointer) deferred to v2 for GC
// root marshaling.
inline constexpr const char* CmdSetInstanceField      = "setInstanceField";

// Phase 25 — cross-class symbol finder. Walks every class in the given
// assembly, iterates its fields + methods, and returns rows whose name
// contains the supplied substring (case-insensitive). The UI exposes
// this as a Tab so the user no longer has to click each class in turn
// when hunting a property like "MaxHP" that could live anywhere.
// Both backends accept the same args:
//   assembly: string (required)
//   pattern : string (required, ASCII case-insensitive substring)
//   kinds   : optional ["field"|"method"] — default both
//   limit   : optional int — default 200, hard-cap 5000
inline constexpr const char* CmdSearchSymbols         = "searchSymbols";

// Phase 28 — Field → Methods cross-reference. Inverse of Phase 25
// `searchSymbols`: given a Unity field, list every method that reads
// and/or writes it.
//
// Mono path (28a): walks the IL bytecode of every method declared in
// the target assembly via `mono_method_get_header` + per-op decode,
// matches the IL operand against the target field's metadata token
// (0x04xxxxxx). Exact, no false positives. Generic methods scanned
// once at the open-generic level — instantiated versions share the
// same IL so a token match counts for every instantiation
// (mirrors dnSpy).
//
// IL2CPP path (28b): static fields ONLY in v1. Scans GameAssembly.dll
// `.text` for `mov reg, [rip+disp32]` / `lea reg, [rip+disp32]`
// instructions whose target equals the field's static-storage address
// (`il2cpp_class_get_static_data(klass) + field.offset`). Each hit is
// attributed to its enclosing method via binary search against a
// sorted method-pointer index built from the same `class_get_methods`
// walk the rest of the IL2CPP backend uses. Instance fields return
// `error: "field references for instance fields not supported on
// IL2CPP backend in v1"` so the UI can surface the limitation
// gracefully.
//
// Wire request:
//   { assembly: string, ns: string, class: string, fieldName: string,
//     kind?: "any"|"read"|"write" (default "any"), limit?: int (default 200,
//     cap 5000) }
//
// Wire response:
//   { ok: bool,
//     field: { name, offset, type, isStatic, token (Mono only),
//              staticAddress (IL2CPP static only) },
//     references: [
//       { assembly, namespace, class, method, signature?, methodToken,
//         address (hex; null when Mono JIT skipped), isStatic,
//         kind: "read"|"write", confidence: "exact",
//         ilOffset (Mono only) } ],
//     scanned_methods: int, truncated: bool,
//     error?: string }
inline constexpr const char* CmdFindFieldReferences   = "findFieldReferences";

// Phase 28b-v3 — investigation diagnostic. Given a class FQN, reports
// every storage slot (GameAssembly.dll non-exec sections + committed RW
// heap regions) holding the Il2CppClass* and its static_fields pointer,
// plus 128 bytes of surrounding context for each. Used to determine
// empirically how compiler-generated `<>c` lambda-cache classes are
// referenced from AOT code when the Phase 28b-v2 ClassPointerScanner
// finds 0 module slots.
//
// Request args:
//   assembly  : string (required)
//   ns        : string (default "")
//   class     : string (required)
//   heapScan  : bool   (default true — scan committed RW heap too)
//   heapLimit : int    (default 64; per-pointer cap on heap slots)
//
// Response (success):
//   { ok: true,
//     classFqn,
//     classPtr, staticFieldsPtr,
//     staticFieldsOffsetInClass,
//     module: { base, size, sections: [{ name, va, size, characteristics }] },
//     classStructDump : "hex...",
//     moduleClassPtrSlots:        [{ address, section, surroundingHex }],
//     moduleStaticFieldsSlots:    [{ address, section, surroundingHex }],
//     heapClassPtrSlots:          [{ address, regionBase, regionSize,
//                                    regionProtect, regionType,
//                                    surroundingHex }],
//     heapStaticFieldsSlots:      same as above,
//     stats: { moduleBytesScanned, heapRegionsScanned, heapBytesScanned,
//              heapRegionsSkipped, heapCapHit } }
inline constexpr const char* CmdDiagnoseClassReferences = "diagnoseClassReferences";

// Phase 27a — Unity-aware value scan (First Scan).
// Searches process memory (or a single class's live instances in
// class-scoped mode) for primitive values matching the request and
// filters non-Unity-managed hits via the Phase 17 whatIsAt class-range
// cache. Wire request fields:
//   dataType  : "int8"|"int16"|"int32"|"int64"
//               |"uint8"|"uint16"|"uint32"|"uint64"
//               |"float"|"double"  (string scan lives in Phase 27b)
//   scanType  : "exact"|"range"  (changed/unchanged/etc. live in 27b)
//   value     : number (exact match) or range lower bound
//   valueTo   : number (range upper bound, inclusive — required when
//               scanType == "range")
//   tolerance : number (float/double epsilon match; default 0.0001
//               matches CE's default float epsilon)
//   classScope: bool (true = class-scoped mode — requires assembly +
//               ns + class)
//   assembly  : string (class-scoped mode only)
//   ns        : string (class-scoped mode only — empty allowed)
//   class     : string (class-scoped mode only)
//   alignment : int (1/2/4/8 — defaults to sizeof(dataType); set to
//               1 for "fast scan" misaligned search)
//   limit     : int (default 50_000, hard-cap 50_000 — applies AFTER
//               whatIsAt filter so callers always see at most this many
//               managed candidates)
//   maxBackwardScan : int (default 16384, cap 16384 — passed through to
//               whatIsAt; bumped from the Phase 17 default 4096 because
//               value-scan candidates can land deep inside very large
//               MonoBehaviour subclasses)
// Response shape:
//   { ok: true, candidates: [ { addr, instAddr, asm, ns, class, field,
//                               fieldOffset, fieldKind, currentValue,
//                               nearestField? } ],
//     scannedBytes, droppedNonManaged, truncated, count, mode }
//   mode = "global"|"class-scoped".
inline constexpr const char* CmdScanFieldValue        = "scanFieldValue";

// Phase 27b — Session-state RPCs for CE-style First Scan / Next Scan
// workflow. `beginValueScan` takes the same args as `scanFieldValue`
// but additionally stores the candidate list server-side under a
// generated `sessionId`. Subsequent `refineValueScan` calls re-read
// each candidate's current value, compare against the session's
// previous-value snapshot per the supplied scanType, and prune the
// session in place. `endValueScan` releases the session buffer.
//
// `beginValueScan` request additions (over scanFieldValue):
//   (none — sessionId is server-allocated and returned)
// `beginValueScan` response additions:
//   sessionId (uint64 as decimal string for JSON-safe wire shape)
//
// `refineValueScan` request:
//   sessionId : string (decimal uint64) — required
//   scanType  : "exact"|"range"|"changed"|"unchanged"
//               |"increased"|"decreased"|"newValue"
//   value     : number (required for exact/range/newValue)
//   valueTo   : number (required for range; upper bound)
//   limit     : int (optional cap on returned candidate rows;
//               default 5000)
// `refineValueScan` response:
//   { ok, sessionId, scanType, count, totalCandidates, removed,
//     dataType, candidates: [...] }
//   `count` = candidates emitted this call (capped by limit).
//   `totalCandidates` = remaining session size after pruning.
//   `removed` = number pruned this round.
//
// `endValueScan` request: { sessionId }. Response: { ok, sessionId, dropped }.
//
// Sessions auto-expire 5 minutes after last use (cap on memory leaks
// from clients that crash without calling End). Auto-cleanup on pipe
// disconnect via the existing Phase 14a teardown hook.
inline constexpr const char* CmdBeginValueScan        = "beginValueScan";
inline constexpr const char* CmdRefineValueScan       = "refineValueScan";
inline constexpr const char* CmdEndValueScan          = "endValueScan";

// Phase 20-MT — diagnostic snapshot of the main-thread dispatcher state.
// Args: (none).
// Returns: { initialized, hook_installed, main_thread_id, tick_count,
//            submit_count, drain_count, timeout_count, reject_count,
//            last_tick_age_ms, queue_depth, is_healthy }
inline constexpr const char* CmdGetDispatcherStats    = "getDispatcherStats";

// Debugger symbol dump (IL2CPP only). Walks every class in the supplied
// assembly list and emits one row per class (Il2CppClass*) and/or per
// compiled method (AOT methodPointer in GameAssembly.dll). Drives the
// UI's "Export Symbols → x64dbg / Ghidra / IDA" actions.
//   assemblies         : string[] (required)
//   kinds              : optional ["class"|"method"] — default both
//   computeHumanSuffix : optional bool — default true; appended to
//                        method rows so overloads disambiguate.
//   limit              : optional int — 0 / unset = unlimited; hard
//                        safety cap 1_000_000 rows.
// Returns:
//   { moduleName, moduleBase, moduleSize, rows:[...], count,
//     scanned_classes, truncated, missing_assemblies? }
inline constexpr const char* CmdDumpIl2cppSymbols     = "dumpIl2cppSymbols";

// Phase C follow-up — bulk getClassInfo across every user-assembly class
// in ONE pipe RPC. Replaces the N×getClassInfo client-side loop that
// `Il2cppHeaderBatchExporter` used to do (and that caused the SaGa F2R
// >60s slow-quit documented in lessons-learned.md "Phase C follow-up").
// Backend-symmetric: both Mono and IL2CPP register the same name; Mono
// methods carry null `address` because the JIT addr doesn't survive
// restart. Request:
//   { assemblies: ["Assembly-CSharp", ...], limit?: int }
// Response:
//   { rows:[ {assembly, namespace, name, parent?, fields:[...],
//             methods:[...], error?} ],
//     count, scanned, truncated, missing_assemblies? }
inline constexpr const char* CmdDumpClassInfo         = "dumpClassInfo";

}  // namespace UnityCEDumper::MagicStrings
