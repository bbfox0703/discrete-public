// PlayerLocatorTests.cs — Teleport auto-detect wave A. Pure tests pin the
// class-name filter + merge/scoring rules; wire tests drive one signal at a
// time through the queued fake pipe (PlayerLocator's calls are strictly
// sequential, so each scenario is a deterministic response queue).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class PlayerLocatorTests
{
    // ------------------------------------------------------------------
    // Canned callMethod responses
    // ------------------------------------------------------------------

    private static string AddrOk(string typeName, string hex) => $$"""
        { "ok": true, "returnType": "{{typeName}}", "returnValue": "{{hex}}",
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private static string NullAddrOk(string typeName) =>
        AddrOk(typeName, "0x0000000000000000");

    private static string StringOk(string value) => $$"""
        { "ok": true, "returnType": "System.String", "returnValue": "{{value}}",
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private static string Vector3Ok(float x, float y, float z)
    {
        Span<byte> b = stackalloc byte[12];
        BitConverter.TryWriteBytes(b.Slice(0, 4), x);
        BitConverter.TryWriteBytes(b.Slice(4, 4), y);
        BitConverter.TryWriteBytes(b.Slice(8, 4), z);
        string hex = Convert.ToHexString(b).ToLowerInvariant();
        return $$"""
            { "ok": true, "returnType": "UnityEngine.Vector3", "returnValue": null,
              "returnStructBytes": "{{hex}}", "returnStructSize": 12,
              "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
            """;
    }

    private static string ErrorResult(string error) =>
        $$"""{ "ok": false, "error": "{{error}}", "returnValue": null, "elapsedMs": 1 }""";

    private static string Cmd(string requestLine)
    {
        using var doc = JsonDocument.Parse(requestLine);
        return doc.RootElement.GetProperty("cmd").GetString() ?? string.Empty;
    }

    // ------------------------------------------------------------------
    // Pure: class-name filter
    // ------------------------------------------------------------------

    [Fact]
    public void ClassNameFilter_PicksRanksAndExcludes()
    {
        var classes = new (string, string?, string)[]
        {
            ("Assembly-CSharp", "Game", "EnemyAI"),            // no keyword
            ("Assembly-CSharp", "Game", "PlayerController"),   // rank 1
            ("Assembly-CSharp", "Game", "Player"),             // rank 0 — first
            ("Assembly-CSharp", "Game", "PlayerManager"),      // excluded (manager)
            ("Assembly-CSharp", "Game", "PlayerData"),         // excluded (data)
            ("Assembly-CSharp", "Game", "MyPlayerRig"),        // rank 2
            ("Assembly-CSharp", "Game", "NetworkPlayer"),      // excluded (network)
            ("Assembly-CSharp", "Game", "Hero"),               // rank 3
            ("Assembly-CSharp", "Game", "<PlayerState>d__12"), // compiler-generated
        };

        IReadOnlyList<PlayerClassNameFilter.ClassCandidate> picked =
            PlayerClassNameFilter.Select(classes, cap: 3);

        Assert.Equal(3, picked.Count);
        Assert.Equal("Player",           picked[0].Name);
        Assert.Equal("PlayerController", picked[1].Name);
        Assert.Equal("MyPlayerRig",      picked[2].Name);
    }

    [Fact]
    public void ClassNameFilter_CapIsRespected()
    {
        var classes = Enumerable.Range(0, 20)
            .Select(i => ("Assembly-CSharp", (string?)"Game", $"PlayerThing{i:00}"))
            .ToList();
        Assert.Equal(5, PlayerClassNameFilter.Select(classes, cap: 5).Count);
    }

    // ------------------------------------------------------------------
    // Pure: merge / scoring
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_SumsAcrossFamilies_TakesMaxWithinFamily()
    {
        var hits = new[]
        {
            new PlayerSignalHit("0x1000", "tag:Player",     PlayerLocatorRanking.ScoreTag),
            new PlayerSignalHit("0x1000", "name:\"Player\"", PlayerLocatorRanking.ScoreExactPlayerName),
            // Same family ("name") — must NOT add on top of the 70.
            new PlayerSignalHit("0x1000", "name:\"player\"", PlayerLocatorRanking.ScoreWellKnownName),
            new PlayerSignalHit("0x2000", "camera-chain",   PlayerLocatorRanking.ScoreCameraChain),
        };

        var merged = PlayerLocatorRanking.Merge(hits);

        Assert.Equal(2, merged.Count);
        var top = merged.Single(m => m.TransformAddress == "0x1000");
        Assert.Equal(PlayerLocatorRanking.ScoreTag + PlayerLocatorRanking.ScoreExactPlayerName, top.Score);
        Assert.Equal(3, top.Sources.Count);
        Assert.Equal(PlayerLocatorRanking.ScoreCameraChain,
            merged.Single(m => m.TransformAddress == "0x2000").Score);
    }

    [Fact]
    public void Merge_WithinFamilyHigherLaterScoreUpgrades()
    {
        var hits = new[]
        {
            new PlayerSignalHit("0x1000", "name:\"player\"", PlayerLocatorRanking.ScoreWellKnownName),
            new PlayerSignalHit("0x1000", "name:\"Player\"", PlayerLocatorRanking.ScoreExactPlayerName),
        };
        Assert.Equal(PlayerLocatorRanking.ScoreExactPlayerName,
            PlayerLocatorRanking.Merge(hits).Single().Score);
    }

    [Theory]
    [InlineData(null,  0)]
    [InlineData(1.0,  30)]
    [InlineData(5.0,  20)]
    [InlineData(15.0, 10)]
    [InlineData(99.0,  0)]
    public void DistanceBonus_Buckets(double? distance, int expected) =>
        Assert.Equal(expected, PlayerLocatorRanking.DistanceBonus(distance));

    // ------------------------------------------------------------------
    // Wire: camera anchor + parent chain
    // ------------------------------------------------------------------

    private static PlayerLocateOptions Only(
        bool camera = false, bool tag = false, bool cc = false,
        bool names = false, bool classes = false) => new()
    {
        EnableCameraChain         = camera,
        EnableTagLookup           = tag,
        EnableCharacterController = cc,
        EnableWellKnownNames      = names,
        EnableUserClassScan       = classes,
    };

    [Fact]
    public async Task CameraChain_AncestorBecomesCandidate_WithDistanceBonus()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            AddrOk("UnityEngine.Camera", "0x1000"),        // Camera.get_main
            AddrOk("UnityEngine.Transform", "0x1100"),     // Component.get_transform (camera)
            Vector3Ok(0f, 0f, 0f),                         // camera position
            AddrOk("UnityEngine.Transform", "0x1200"),     // get_parent -> ancestor
            NullAddrOk("UnityEngine.Transform"),           // get_parent -> chain ends
            // enrichment of 0x1200:
            AddrOk("UnityEngine.GameObject", "0x1300"),    // Component.get_gameObject
            StringOk("PlayerRoot"),                        // Object.get_name
            Vector3Ok(3f, 4f, 0f),                         // position -> distance 5 -> +20
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(camera: true), ct)
                                                .ConfigureAwait(true);

            Assert.True(r.Ok);
            Assert.Equal("0x1100", r.CameraTransformAddress);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal("0x1200", c.TransformAddress);
            Assert.Equal("PlayerRoot", c.Name);
            Assert.Equal(PlayerLocatorRanking.ScoreCameraChain + 20, c.Score);
            Assert.NotNull(c.DistanceToCamera);
            Assert.Equal(5.0, c.DistanceToCamera!.Value, 3);
            Assert.Equal(new[] { "camera-chain" }, c.Sources);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement first = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("UnityEngine.CoreModule", first.GetProperty("assembly").GetString());
            Assert.Equal("Camera",   first.GetProperty("name").GetString());
            Assert.Equal("get_main", first.GetProperty("method").GetString());
            Assert.Equal(JsonValueKind.True, first.GetProperty("main_thread").ValueKind);
            Assert.Equal("get_parent",
                TeleportFakePipe.Params(reqs[3]).GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task DrainTimeout_AbortsWholeRunWithFocusHint()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            ErrorResult("main-thread queue drain timeout after 8000 ms"),
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator
                .LocateAsync(Only(camera: true, tag: true, names: true), ct)
                .ConfigureAwait(true);

            Assert.False(r.Ok);
            Assert.Equal(PlayerLocator.DrainTimeoutHint, r.Error);
            Assert.Empty(r.Candidates);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    // ------------------------------------------------------------------
    // Wire: tag lookup
    // ------------------------------------------------------------------

    [Fact]
    public async Task TagLookup_HitScores100()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            AddrOk("UnityEngine.GameObject", "0x2000"),   // FindWithTag("Player")
            AddrOk("UnityEngine.Transform", "0x2100"),    // GameObject.get_transform
            // enrichment (GameObject already known -> name, then position):
            StringOk("Player"),
            Vector3Ok(1f, 2f, 3f),
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(tag: true), ct)
                                                .ConfigureAwait(true);

            Assert.True(r.Ok);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal(PlayerLocatorRanking.ScoreTag, c.Score);   // no camera -> no bonus
            Assert.Equal("Player", c.Name);
            Assert.Equal("0x2000", c.GameObjectAddress);
            Assert.Equal(new[] { "tag:Player" }, c.Sources);
            Assert.Null(c.DistanceToCamera);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement p0 = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("GameObject",  p0.GetProperty("name").GetString());
            Assert.Equal("FindWithTag", p0.GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task TagLookup_StrippedMethodDisablesSignalGracefully()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[] { ErrorResult("method_not_found") };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(tag: true), ct)
                                                .ConfigureAwait(true);

            Assert.True(r.Ok);
            Assert.Empty(r.Candidates);
            Assert.Contains(r.Notes, n => n.Contains("tag", StringComparison.Ordinal)
                                       && n.Contains("method_not_found", StringComparison.Ordinal));
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    // ------------------------------------------------------------------
    // Wire: CharacterController enumeration
    // ------------------------------------------------------------------

    [Fact]
    public async Task CharacterController_EnumeratesViaTypeAndFindObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            AddrOk("System.Type", "0x5000"),               // Type.GetType (PhysicsModule FQN)
            AddrOk("UnityEngine.Object[]", "0x5100"),      // FindObjectsByType
            // walkArray (pipe RPC, not callMethod):
            """
            { "count": 1, "length": 1, "is_pointer": true,
              "elements": [ { "index": 0, "ptr": "0x5200" } ] }
            """,
            AddrOk("UnityEngine.Transform", "0x5300"),     // Component.get_transform (the CC)
            // enrichment:
            AddrOk("UnityEngine.GameObject", "0x5400"),    // get_gameObject
            StringOk("Knight"),                            // get_name
            Vector3Ok(0f, 1f, 0f),                         // position
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(cc: true), ct)
                                                .ConfigureAwait(true);

            Assert.True(r.Ok);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal(PlayerLocatorRanking.ScoreCharController, c.Score);
            Assert.Equal("Knight", c.Name);
            Assert.Equal("CharacterController", c.ClassName);
            Assert.Equal("0x5300", c.TransformAddress);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement p0 = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("mscorlib", p0.GetProperty("assembly").GetString());
            Assert.Equal("GetType",  p0.GetProperty("method").GetString());
            JsonElement p1 = TeleportFakePipe.Params(reqs[1]);
            Assert.Equal("Object",            p1.GetProperty("name").GetString());
            Assert.Equal("FindObjectsByType", p1.GetProperty("method").GetString());
            Assert.Equal("walkArray", Cmd(reqs[2]));
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    // ------------------------------------------------------------------
    // Wire: well-known names (+ alias merge)
    // ------------------------------------------------------------------

    [Fact]
    public async Task WellKnownNames_AliasHitsMergeIntoOneCandidate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new List<string>
        {
            AddrOk("UnityEngine.GameObject", "0x4000"),   // Find("Player") hit
            AddrOk("UnityEngine.Transform", "0x4100"),    //   -> get_transform
            AddrOk("UnityEngine.GameObject", "0x4000"),   // Find("player") -> same object
            AddrOk("UnityEngine.Transform", "0x4100"),    //   -> get_transform
        };
        // The remaining 12 well-known names miss.
        for (int i = 0; i < 12; i++) responses.Add(NullAddrOk("UnityEngine.GameObject"));
        responses.Add(StringOk("Player"));                // enrichment: name
        responses.Add(Vector3Ok(9f, 9f, 9f));             // enrichment: position

        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(names: true), ct)
                                                .ConfigureAwait(true);

            Assert.True(r.Ok);
            PlayerCandidate c = Assert.Single(r.Candidates);
            // Alias hits are one family — exact-"Player" 70 wins, no summing.
            Assert.Equal(PlayerLocatorRanking.ScoreExactPlayerName, c.Score);
            Assert.Equal(2, c.Sources.Count);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    // ------------------------------------------------------------------
    // Wire: user-class scan
    // ------------------------------------------------------------------

    [Fact]
    public async Task UserClassScan_SingleInstanceComponentBecomesCandidate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            // getAssemblies — engine assemblies must be filtered out.
            """
            { "count": 2, "assemblies": [
              { "name": "Assembly-CSharp" }, { "name": "UnityEngine.CoreModule" } ] }
            """,
            // getClasses(Assembly-CSharp) — manager is excluded, enemy no keyword.
            """
            { "assembly": "Assembly-CSharp", "count": 3, "classes": [
              { "namespace": "Game", "name": "PlayerController" },
              { "namespace": "Game", "name": "EnemyAI" },
              { "namespace": "Game", "name": "PlayerManager" } ] }
            """,
            // findInstances(Game.PlayerController) — exactly one live instance.
            """{ "count": 1, "limit": 4, "matches": [ "0x3000" ] }""",
            AddrOk("UnityEngine.Transform", "0x3100"),     // Component.get_transform
            // enrichment:
            AddrOk("UnityEngine.GameObject", "0x3200"),
            StringOk("Hero"),
            Vector3Ok(1f, 0f, 1f),
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(classes: true), ct)
                                                .ConfigureAwait(true);

            Assert.True(r.Ok);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal(PlayerLocatorRanking.ScoreSingleInstance, c.Score);
            Assert.Equal("PlayerController", c.ClassName);
            Assert.Equal("0x3000", c.InstanceAddress);
            Assert.Equal("Hero", c.Name);
            Assert.Equal(new[] { "class:PlayerController(1)" }, c.Sources);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal("getAssemblies", Cmd(reqs[0]));
            Assert.Equal("getClasses",    Cmd(reqs[1]));
            Assert.Equal("findInstances", Cmd(reqs[2]));
            JsonElement fi = TeleportFakePipe.Params(reqs[2]);
            Assert.Equal("PlayerController", fi.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.True, fi.GetProperty("aliveOnly").ValueKind);
            Assert.Equal(JsonValueKind.True, fi.GetProperty("sane").ValueKind);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }
}
