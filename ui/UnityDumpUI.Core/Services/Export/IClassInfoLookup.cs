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
