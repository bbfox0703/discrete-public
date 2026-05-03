// IClassInfoLookup.cs — async lookup that exporters use for recursive
// type drilldown.
//
// Exporters cannot reach into BackendAdapter directly (it lives in the same
// project but exporters should stay testable + injectable). Implementations
// should swallow soft errors (return null) so exporter recursion can fall
// through to "opaque pointer" gracefully.

using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services.Export;

public interface IClassInfoLookup
{
    Task<GetClassInfoResult?> GetClassInfoAsync(
        string assembly, string @namespace, string name,
        CancellationToken ct = default);
}

/// <summary>
/// Adds live-process introspection on top of <see cref="IClassInfoLookup"/>.
/// CE XML's live-data export mode uses these to fetch concrete addresses
/// per array slot and per pointer field, so the generated CE table opens
/// at fully-resolved addresses instead of templated `+offset` chains the
/// user has to anchor manually.
/// </summary>
public interface ILiveExportSource : IClassInfoLookup
{
    Task<ReadMemoryResult> ReadMemoryAsync(
        string address, int size, CancellationToken ct = default);

    Task<WalkArrayResult> WalkArrayAsync(
        string address,
        string? elementType,
        int?    limit,
        int?    elementSize,
        string? elementAssembly,
        string? elementNamespace,
        string? elementName,
        CancellationToken ct = default);
}
