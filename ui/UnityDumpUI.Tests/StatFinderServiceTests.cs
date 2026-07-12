// StatFinderServiceTests.cs — the score + live-confirm flow (UE5 DetectStats
// port). Drives the fake pipe with a bulk dumpClassInfo + findInstances +
// walkInstance response queue.

using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class StatFinderServiceTests
{
    // One class with a Health/MaxHealth stat pair + a non-numeric field.
    private const string Dump = """
        { "rows": [
            { "assembly": "Assembly-CSharp", "namespace": "Game", "name": "PlayerStats",
              "fields": [
                { "name": "Health",     "type": "System.Int32",  "offset": 16, "static": false },
                { "name": "MaxHealth",  "type": "System.Int32",  "offset": 20, "static": false },
                { "name": "PlayerName", "type": "System.String", "offset": 24, "static": false }
              ], "methods": [] }
          ], "count": 1, "scanned": 1, "truncated": false }
        """;

    private const string FindOne  = """ { "matches": ["0x5000"], "count": 1, "limit": 3 } """;
    private const string FindNone = """ { "matches": [], "count": 0, "limit": 3 } """;

    private const string WalkOk = """
        { "address": "0x5000", "depth": 1, "count": 2, "pointer_size": 8, "fields": [
            { "name": "Health",    "type": "System.Int32", "offset": 16, "value": 100 },
            { "name": "MaxHealth", "type": "System.Int32", "offset": 20, "value": 100 }
          ] }
        """;

    [Fact]
    public async Task ScoreAsync_RanksInterestingFields_WithPairBonus()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { Dump }, ct);
        try
        {
            var svc = new StatFinderService(fp.Adapter);
            var fields = await svc.ScoreAsync(new[] { "Assembly-CSharp" }, ct: ct).ConfigureAwait(true);

            // PlayerName isn't a gameplay keyword -> only the two stats surface.
            Assert.Equal(2, fields.Count);
            InterestingField top = fields[0];
            Assert.Equal("MaxHealth", top.Name);        // 2 keyword hits > 1
            Assert.All(fields, f => Assert.True(f.HasMaxSibling));  // Health/MaxHealth pair
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task DetectAsync_LiveInstance_PlausibleValue_Confirms()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // order: dumpClassInfo, findInstances(PlayerStats), walkInstance
        var fp = await TeleportFakePipe.StartAsync(new[] { Dump, FindOne, WalkOk }, ct);
        try
        {
            var svc = new StatFinderService(fp.Adapter);
            var findings = await svc.DetectAsync(new[] { "Assembly-CSharp" }, ct: ct).ConfigureAwait(true);

            Assert.Equal(2, findings.Count);
            Assert.All(findings, f =>
            {
                Assert.True(f.LiveInstanceExists);
                Assert.True(f.ValuePlausible);
                Assert.True(f.Confirmed);
                Assert.Equal("100", f.LiveValue);
            });
            // Confirmed + higher-confidence first (MaxHealth scores above Health).
            Assert.Equal("MaxHealth", findings[0].Field.Name);
            Assert.True(findings[0].Confidence >= findings[1].Confidence);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    private const string MethodDump = """
        { "rows": [
            { "assembly": "Assembly-CSharp", "namespace": "Game", "name": "Player",
              "fields": [],
              "methods": [
                { "name": "SetHealth", "static": false },
                { "name": "AddGold",   "static": false },
                { "name": "Update",    "static": false }
              ] }
          ], "count": 1, "scanned": 1, "truncated": false }
        """;

    [Fact]
    public async Task ScoreMethodsAsync_SurfacesGameplayInvokeTargets()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { MethodDump }, ct);
        try
        {
            var svc = new StatFinderService(fp.Adapter);
            var methods = await svc.ScoreMethodsAsync(new[] { "Assembly-CSharp" }, includeGameplayActions: false, ct: ct)
                                   .ConfigureAwait(true);

            Assert.Equal(2, methods.Count);   // Update has no gameplay keyword
            Assert.Contains(methods, m => m.Name == "SetHealth");
            Assert.Contains(methods, m => m.Name == "AddGold");
            Assert.DoesNotContain(methods, m => m.Name == "Update");
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task DetectAsync_NoLiveInstance_NotConfirmed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // findInstances returns nothing -> walk is skipped; not confirmed.
        var fp = await TeleportFakePipe.StartAsync(new[] { Dump, FindNone }, ct);
        try
        {
            var svc = new StatFinderService(fp.Adapter);
            var findings = await svc.DetectAsync(new[] { "Assembly-CSharp" }, ct: ct).ConfigureAwait(true);

            Assert.Equal(2, findings.Count);
            Assert.All(findings, f =>
            {
                Assert.False(f.LiveInstanceExists);
                Assert.False(f.Confirmed);
                Assert.Equal(f.Field.Score, f.Confidence);   // no live/plausible bonus
            });
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }
}
