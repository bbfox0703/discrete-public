// ProtocolJson.cs — source-gen JSON contexts for the pipe protocol.
//
// Native AOT requires every (de)serialized type to be reachable through a
// JsonSerializerContext. Add new entries below as new pipe commands ship.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityDumpUI.Core.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy   = JsonKnownNamingPolicy.Unspecified,  // we use [JsonPropertyName]
    WriteIndented          = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RpcEnvelope<HandshakeResult>))]
[JsonSerializable(typeof(RpcEnvelope<PingResult>))]
[JsonSerializable(typeof(RpcEnvelope<CancelScanResult>))]
[JsonSerializable(typeof(RpcEnvelope<GetAssembliesResult>))]
[JsonSerializable(typeof(RpcEnvelope<GetClassesResult>))]
[JsonSerializable(typeof(RpcEnvelope<GetClassInfoResult>))]
[JsonSerializable(typeof(RpcEnvelope<FindInstancesResult>))]
[JsonSerializable(typeof(RpcEnvelope<WalkInstanceResult>))]
[JsonSerializable(typeof(RpcEnvelope<GetStaticFieldDataResult>))]
[JsonSerializable(typeof(RpcEnvelope<ReadMemoryResult>))]
[JsonSerializable(typeof(RpcEnvelope<WalkArrayResult>))]
[JsonSerializable(typeof(RpcEnvelope<MetadataStatsResult>))]
[JsonSerializable(typeof(RpcEnvelope<WhatIsAtResult>))]
[JsonSerializable(typeof(RpcEnvelope<ClassRangeStatsResult>))]
[JsonSerializable(typeof(RpcEnvelope<FindMethodInstancesResult>))]
[JsonSerializable(typeof(RpcEnvelope<GetMethodAobPatternResult>))]
[JsonSerializable(typeof(RpcEnvelope<CallMethodResult>))]
[JsonSerializable(typeof(RpcEnvelope<GetEnumValuesResult>))]
[JsonSerializable(typeof(RpcEnvelope<ListFieldValuesResult>))]
[JsonSerializable(typeof(RpcEnvelope<SetInstanceFieldResult>))]
[JsonSerializable(typeof(RpcEnvelope<SearchSymbolsResult>))]
[JsonSerializable(typeof(RpcEnvelope<DumpIl2cppSymbolsResult>))]
[JsonSerializable(typeof(RpcEnvelope<DumpClassInfoResult>))]
[JsonSerializable(typeof(RpcEnvelope<BeginValueScanResult>))]
[JsonSerializable(typeof(RpcEnvelope<RefineValueScanResult>))]
[JsonSerializable(typeof(RpcEnvelope<EndValueScanResult>))]
[JsonSerializable(typeof(RpcEnvelope<SnapshotBeginResult>))]
[JsonSerializable(typeof(RpcEnvelope<SnapshotChunkResult>))]
[JsonSerializable(typeof(RpcEnvelope<SnapshotEndResult>))]
[JsonSerializable(typeof(RpcEnvelope<FindFieldReferencesResult>))]
[JsonSerializable(typeof(RpcEnvelope<DispatcherStatsResult>))]
[JsonSerializable(typeof(HandshakeResult))]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(GetAssembliesResult))]
[JsonSerializable(typeof(GetClassesResult))]
[JsonSerializable(typeof(GetClassInfoResult))]
[JsonSerializable(typeof(FindInstancesResult))]
[JsonSerializable(typeof(WalkInstanceResult))]
[JsonSerializable(typeof(GetStaticFieldDataResult))]
[JsonSerializable(typeof(ReadMemoryResult))]
[JsonSerializable(typeof(WalkArrayResult))]
[JsonSerializable(typeof(WalkedArrayElement))]
[JsonSerializable(typeof(AssemblyEntry))]
[JsonSerializable(typeof(ClassEntry))]
[JsonSerializable(typeof(ClassRef))]
[JsonSerializable(typeof(FieldEntry))]
[JsonSerializable(typeof(MethodEntry))]
[JsonSerializable(typeof(WalkedField))]
[JsonSerializable(typeof(MetadataStatsResult))]
[JsonSerializable(typeof(WhatIsAtResult))]
[JsonSerializable(typeof(WhatIsAtField))]
[JsonSerializable(typeof(WhatIsAtScanRange))]
[JsonSerializable(typeof(ClassRangeStatsResult))]
[JsonSerializable(typeof(FindMethodInstancesResult))]
[JsonSerializable(typeof(MethodOverloadEntry))]
[JsonSerializable(typeof(GetMethodAobPatternResult))]
[JsonSerializable(typeof(AobPatternStats))]
[JsonSerializable(typeof(CallMethodResult))]
[JsonSerializable(typeof(CallMethodMethodInfo))]
[JsonSerializable(typeof(ListFieldValuesResult))]
[JsonSerializable(typeof(ListFieldValueRow))]
[JsonSerializable(typeof(SetInstanceFieldResult))]
[JsonSerializable(typeof(SearchSymbolsResult))]
[JsonSerializable(typeof(SearchSymbolMatch))]
[JsonSerializable(typeof(DumpIl2cppSymbolsResult))]
[JsonSerializable(typeof(SymbolDumpRow))]
[JsonSerializable(typeof(DumpClassInfoResult))]
[JsonSerializable(typeof(DumpedClassRow))]
[JsonSerializable(typeof(BeginValueScanResult))]
[JsonSerializable(typeof(RefineValueScanResult))]
[JsonSerializable(typeof(EndValueScanResult))]
[JsonSerializable(typeof(SnapshotBeginResult))]
[JsonSerializable(typeof(SnapshotChunkResult))]
[JsonSerializable(typeof(SnapshotEndResult))]
[JsonSerializable(typeof(SnapshotPlanEntry))]
[JsonSerializable(typeof(SnapshotScanStats))]
[JsonSerializable(typeof(SnapshotDecodeStats))]
[JsonSerializable(typeof(SnapshotClassEntry))]
[JsonSerializable(typeof(SnapshotClassParent))]
[JsonSerializable(typeof(SnapshotInstanceEntry))]
[JsonSerializable(typeof(SnapshotFieldEntry))]
[JsonSerializable(typeof(SnapshotFieldSchema))]
[JsonSerializable(typeof(SnapshotArrayElements))]
[JsonSerializable(typeof(SnapshotArrayElement))]
[JsonSerializable(typeof(System.Collections.Generic.List<SnapshotPlanEntry>))]
[JsonSerializable(typeof(System.Collections.Generic.List<SnapshotClassEntry>))]
[JsonSerializable(typeof(System.Collections.Generic.List<SnapshotInstanceEntry>))]
[JsonSerializable(typeof(System.Collections.Generic.List<SnapshotFieldEntry>))]
[JsonSerializable(typeof(System.Collections.Generic.List<SnapshotFieldSchema>))]
[JsonSerializable(typeof(System.Collections.Generic.List<SnapshotArrayElement>))]
[JsonSerializable(typeof(FindFieldReferencesResult))]
[JsonSerializable(typeof(FieldReferenceFieldInfo))]
[JsonSerializable(typeof(FieldReferenceRow))]
[JsonSerializable(typeof(DispatcherStatsResult))]
[JsonSerializable(typeof(ValueScanCandidate))]
[JsonSerializable(typeof(System.Collections.Generic.List<ValueScanCandidate>))]
[JsonSerializable(typeof(CallArgRequest))]
[JsonSerializable(typeof(System.Collections.Generic.List<CallArgRequest>))]
[JsonSerializable(typeof(GetEnumValuesResult))]
[JsonSerializable(typeof(EnumValueEntry))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(RpcError))]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(bool))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Wire request envelope. `args` is serialized as a raw JSON dictionary so
/// each handler can carry its own arg shape without forcing a polymorphic
/// hierarchy on the source generator.
/// </summary>
internal sealed class PipeRequest
{
    [JsonPropertyName("id")]   public int                                Id   { get; set; }
    [JsonPropertyName("cmd")]  public string                             Cmd  { get; set; } = string.Empty;
    [JsonPropertyName("args")] public Dictionary<string, JsonElement>?   Args { get; set; }
}
