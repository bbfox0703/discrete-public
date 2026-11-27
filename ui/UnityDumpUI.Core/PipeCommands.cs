// PipeCommands.cs — single source of truth for pipe RPC command names on the
// C# UI side. Mirrors dll/src/shared/MagicStrings.h (Cmd* constants); keep
// both files in sync. Full wire contracts live in docs/pipe-protocol.md.

namespace UnityDumpUI.Core;

public static class PipeCommands
{
    // Connection / diagnostics
    public const string Ping                         = "ping";
    public const string Handshake                    = "handshake";
    public const string Scan                         = "scan";
    public const string GetDispatcherStats           = "getDispatcherStats";
    public const string SetKeepForeground            = "setKeepForeground";
    public const string GetKeepForeground            = "getKeepForeground";

    // Assembly / class introspection
    public const string GetAssemblies                = "getAssemblies";
    public const string GetClasses                   = "getClasses";
    public const string GetClassInfo                 = "getClassInfo";
    public const string GetFieldInfo                 = "getFieldInfo";
    public const string GetMethodInfo                = "getMethodInfo";
    public const string GetStaticFieldData           = "getStaticFieldData";
    public const string GetMetadataStats             = "getMetadataStats";
    public const string GetEnumValues                = "getEnumValues";
    public const string SearchSymbols                = "searchSymbols";
    public const string DumpClassInfo                = "dumpClassInfo";
    public const string FindFieldReferences          = "findFieldReferences";

    // Memory / symbols / pointers
    public const string ReadMemory                   = "readMemory";
    public const string WriteMemory                  = "writeMemory";
    public const string ResolvePointer               = "resolvePointer";
    public const string RegisterSymbol               = "registerSymbol";
    public const string LookupSymbol                 = "lookupSymbol";
    public const string ListSymbols                  = "listSymbols";

    // Instance discovery, walking, mutation, invocation
    public const string FindInstances                = "findInstances";
    public const string WalkInstance                 = "walkInstance";
    public const string WalkArray                    = "walkArray";
    public const string SetInstanceField             = "setInstanceField";
    public const string ListFieldValuesAcrossInstances = "listFieldValuesAcrossInstances";
    public const string WhatIsAt                     = "whatIsAt";
    public const string GetClassRangeStats           = "getClassRangeStats";
    public const string FindMethodInstances          = "findMethodInstances";
    public const string GetMethodAobPattern          = "getMethodAobPattern";
    public const string CallMethod                   = "callMethod";

    // Value & group scans
    public const string ScanFieldValue               = "scanFieldValue";
    public const string BeginValueScan               = "beginValueScan";
    public const string RefineValueScan              = "refineValueScan";
    public const string EndValueScan                 = "endValueScan";
    public const string CancelScan                   = "cancelScan";
    public const string BeginGroupScan               = "beginGroupScan";
    public const string RefineGroupScan              = "refineGroupScan";
    public const string QueryGroupCandidates         = "queryGroupCandidates";
    public const string EndGroupScan                 = "endGroupScan";

    // Snapshot & diff
    public const string SnapshotBegin                = "snapshotBegin";
    public const string SnapshotChunk                = "snapshotChunk";
    public const string SnapshotEnd                  = "snapshotEnd";

    // Mono-only
    public const string MonoCompileMethod            = "monoCompileMethod";
    public const string MonoGetVTable                = "monoGetVTable";

    // IL2CPP-only
    public const string DumpIl2cppSymbols            = "dumpIl2cppSymbols";
    public const string Il2cppGetMetadataStrings     = "il2cppGetMetadataStrings";
    public const string Il2cppGetMetadataTypeFields  = "il2cppGetMetadataTypeFields";
    public const string Il2cppGetMetadataRegistration = "il2cppGetMetadataRegistration";
    public const string DiagnoseClassReferences      = "diagnoseClassReferences";
    public const string DumpLazyInitSlots            = "dumpLazyInitSlots";
}
