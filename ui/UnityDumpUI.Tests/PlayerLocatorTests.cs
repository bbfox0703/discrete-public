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
    public async Task CameraChain_OnlyHit_NoDistanceBonus_LowConfidence()
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
            StringOk("PlayerRoot"),                        // Object.get_name (not a rig name)
            Vector3Ok(3f, 4f, 0f),                         // position -> distance 5
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
            // Camera-chain-only: distance bonus is suppressed (circular) and
            // confidence is Low even though the name isn't a rig.
            Assert.Equal(PlayerLocatorRanking.ScoreCameraChain, c.Score);
            Assert.Equal(PlayerConfidence.Low, c.Confidence);
            Assert.Equal(PlayerConfidence.Low, r.TopConfidence);
            Assert.NotNull(c.DistanceToCamera);
            Assert.Equal(5.0, c.DistanceToCamera!.Value, 3);
            Assert.Equal(new[] { "camera-chain" }, c.Sources);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement first = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("get_main", first.GetProperty("method").GetString());
            Assert.Equal("get_parent",
                TeleportFakePipe.Params(reqs[3]).GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task CameraChain_RigNamedAncestor_IsDemoted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            AddrOk("UnityEngine.Camera", "0x1000"),
            AddrOk("UnityEngine.Transform", "0x1100"),
            Vector3Ok(0f, 0f, 0f),
            AddrOk("UnityEngine.Transform", "0x1200"),
            NullAddrOk("UnityEngine.Transform"),
            AddrOk("UnityEngine.GameObject", "0x1300"),
            StringOk("Main Camera Rig"),                   // rig name -> penalty
            Vector3Ok(1f, 0f, 0f),
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(camera: true), ct)
                                                .ConfigureAwait(true);
            PlayerCandidate c = Assert.Single(r.Candidates);
            // 40 (camera-chain) - 45 (rig penalty) = -5.
            Assert.Equal(PlayerLocatorRanking.ScoreCameraChain
                         - PlayerLocatorRanking.CameraRigNamePenalty, c.Score);
            Assert.Equal(PlayerConfidence.Low, c.Confidence);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Theory]
    // True: long tokens (safe substrings) + camelCase / boundary short tokens.
    [InlineData("Main Camera", true)]
    [InlineData("CM vcam1", true)]
    [InlineData("CameraRig", true)]
    [InlineData("PlayerPivot", true)]
    [InlineData("FollowCamHolder", true)]
    [InlineData("FollowCam", true)]        // camelCase short token
    [InlineData("PlayerRig", true)]        // camelCase short token
    [InlineData("Boom", true)]
    // False: short tokens embedded in a real word must NOT match.
    [InlineData("Cameron", false)]         // not "cam"
    [InlineData("Frigate", false)]         // not "rig"
    [InlineData("Trigger", false)]         // not "rig"
    [InlineData("Mountain", false)]        // not "mount"
    [InlineData("Redeye", false)]          // not "eye"
    [InlineData("Boomer", false)]          // not "boom"
    [InlineData("PlayerRoot", false)]
    [InlineData("Knight", false)]
    [InlineData("", false)]
    public void IsCameraRigName_Classifies(string name, bool expected) =>
        Assert.Equal(expected, PlayerLocator.IsCameraRigName(name));

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

    // ------------------------------------------------------------------
    // Movement-correlation detection
    // ------------------------------------------------------------------

    // Sweep-only movement options (static signals off, root resolution off) so
    // the wire is the pure Transform-sweep + displacement path.
    private static MovementDetectOptions SweepOnly() => new()
    {
        Signals = Only(),               // all static signals disabled
        SweepAllTransforms = true,
        ResolveToRoot = false,          // tested separately (adds parent walks)
    };

    [Fact]
    public async Task Movement_MoverIsRankedTop()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Begin: enumerate 2 transforms, capture t0. A stays put, B will move.
        var begin = new[]
        {
            AddrOk("System.Type", "0x9000"),               // Type.GetType(Transform)
            AddrOk("UnityEngine.Transform[]", "0x9100"),   // FindObjectsByType
            """
            { "count": 2, "length": 2, "is_pointer": true,
              "elements": [ { "index": 0, "ptr": "0xA000" }, { "index": 1, "ptr": "0xB000" } ] }
            """,
            Vector3Ok(0f, 0f, 0f),                         // t0 A
            Vector3Ok(5f, 0f, 0f),                         // t0 B
        };
        var fp = await TeleportFakePipe.StartAsync(begin, ct);
        MovementSample sample;
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            sample = await locator.BeginMovementSampleAsync(SweepOnly(), ct).ConfigureAwait(true);
            Assert.True(sample.Ok);
            Assert.Equal(2, sample.Tracks.Count);
            Assert.False(sample.Truncated);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }

        // Finish: A unchanged, B moved +3 on Y. Then enrich B (get_gameObject + name).
        var finish = new[]
        {
            Vector3Ok(0f, 0f, 0f),                         // t1 A -> moved 0
            Vector3Ok(5f, 3f, 0f),                         // t1 B -> moved 3
            AddrOk("UnityEngine.GameObject", "0xB100"),    // enrich B: get_gameObject
            StringOk("Chr01"),                             // enrich B: get_name
        };
        var fp2 = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            // A fresh locator over the second pipe; the sample carries all state.
            var locator2 = new PlayerLocator(fp2.Adapter);
            PlayerLocateResult r = await locator2.FinishMovementSampleAsync(sample, SweepOnly(), ct)
                                                 .ConfigureAwait(true);

            Assert.True(r.Ok);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal("0xB000", c.TransformAddress);
            Assert.Equal("Chr01", c.Name);
            Assert.NotNull(c.MovedDistance);
            Assert.Equal(3.0, c.MovedDistance!.Value, 3);
            Assert.Equal(30, c.Score);                     // round(3 * 10)
            Assert.Equal(PlayerConfidence.High, c.Confidence);
            Assert.Contains("moved", c.Sources);
        }
        finally { await fp2.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_NothingMoves_NoCandidates()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var begin = new[]
        {
            AddrOk("System.Type", "0x9000"),
            AddrOk("UnityEngine.Transform[]", "0x9100"),
            """
            { "count": 1, "length": 1, "is_pointer": true,
              "elements": [ { "index": 0, "ptr": "0xA000" } ] }
            """,
            Vector3Ok(7f, 7f, 7f),                         // t0 A
        };
        var fp = await TeleportFakePipe.StartAsync(begin, ct);
        MovementSample sample;
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            sample = await locator.BeginMovementSampleAsync(SweepOnly(), ct).ConfigureAwait(true);
            Assert.True(sample.Ok);
            Assert.Single(sample.Tracks);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }

        var finish = new[] { Vector3Ok(7f, 7f, 7f) };      // t1 A -> no movement
        var fp2 = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator2 = new PlayerLocator(fp2.Adapter);
            PlayerLocateResult r = await locator2.FinishMovementSampleAsync(sample, SweepOnly(), ct)
                                                 .ConfigureAwait(true);
            Assert.True(r.Ok);
            Assert.Empty(r.Candidates);
        }
        finally { await fp2.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_DrainTimeoutDuringBegin_ReturnsError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var begin = new[] { ErrorResult("main-thread queue drain timeout after 8000 ms") };
        var fp = await TeleportFakePipe.StartAsync(begin, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            MovementSample sample = await locator.BeginMovementSampleAsync(SweepOnly(), ct)
                                                 .ConfigureAwait(true);
            Assert.False(sample.Ok);
            Assert.Equal(PlayerLocator.DrainTimeoutHint, sample.Error);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_CameraExcluded_EvenIfLargestMover()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Hand-built sample: the camera (IsCamera) is skipped, so its t1 is never
        // read; only the player's t1 is consumed from the queue. If the skip were
        // removed, the player would read the camera's queued position and the
        // player/camera assertions would fail.
        var sample = new MovementSample
        {
            Ok = true,
            Tracks = new[]
            {
                new MovementTrack { TransformAddress = "0xCA00", IsCamera = true,
                                    Start = (0, 0, 0) },
                new MovementTrack { TransformAddress = "0xB00B", IsCamera = false,
                                    Start = (0, 0, 0) },
            },
        };
        var finish = new[]
        {
            Vector3Ok(0f, 0f, 5f),                         // t1 player -> moved 5
            AddrOk("UnityEngine.GameObject", "0xB010"),    // enrich player: get_gameObject
            StringOk("Hero"),                              // enrich player: get_name
        };
        var fp = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.FinishMovementSampleAsync(sample, SweepOnly(), ct)
                                                .ConfigureAwait(true);
            Assert.True(r.Ok);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal("0xB00B", c.TransformAddress);   // player, NOT the camera
            Assert.Equal("Hero", c.Name);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_TruncatedSweep_ReportsTrueTotal()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var opt = new MovementDetectOptions { Signals = Only(), SweepAllTransforms = true, MaxSweepTransforms = 1 };
        var begin = new[]
        {
            AddrOk("System.Type", "0x9000"),
            AddrOk("UnityEngine.Transform[]", "0x9100"),
            // Header says 5, but only 1 element returned -> truncated.
            """
            { "count": 1, "length": 5, "is_pointer": true,
              "elements": [ { "index": 0, "ptr": "0xA000" } ] }
            """,
            Vector3Ok(0f, 0f, 0f),                         // t0 for the single sampled transform
        };
        var fp = await TeleportFakePipe.StartAsync(begin, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            MovementSample sample = await locator.BeginMovementSampleAsync(opt, ct).ConfigureAwait(true);
            Assert.True(sample.Ok);
            Assert.Single(sample.Tracks);
            Assert.True(sample.Truncated);
            Assert.Equal(5, sample.SceneTransformTotal);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_RigMoverDemotedBelowPlayer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Two movers: a rig-named object moves FARTHER (8) than the player (5).
        // The rig penalty must re-rank the player above it.
        var begin = new[]
        {
            AddrOk("System.Type", "0x9000"),
            AddrOk("UnityEngine.Transform[]", "0x9100"),
            """
            { "count": 2, "length": 2, "is_pointer": true,
              "elements": [ { "index": 0, "ptr": "0xC000" }, { "index": 1, "ptr": "0xB000" } ] }
            """,
            Vector3Ok(0f, 0f, 0f),                         // t0 C000 (rig)
            Vector3Ok(0f, 0f, 0f),                         // t0 B000 (player)
        };
        var fp = await TeleportFakePipe.StartAsync(begin, ct);
        MovementSample sample;
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            sample = await locator.BeginMovementSampleAsync(SweepOnly(), ct).ConfigureAwait(true);
            Assert.Equal(2, sample.Tracks.Count);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }

        var finish = new[]
        {
            Vector3Ok(0f, 0f, 8f),                         // t1 C000 -> moved 8 (farther)
            Vector3Ok(0f, 0f, 5f),                         // t1 B000 -> moved 5
            AddrOk("UnityEngine.GameObject", "0xC100"),    // enrich C000
            StringOk("CameraRig"),                         // rig name -> penalty
            AddrOk("UnityEngine.GameObject", "0xB100"),    // enrich B000
            StringOk("Hero"),
        };
        var fp2 = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator2 = new PlayerLocator(fp2.Adapter);
            PlayerLocateResult r = await locator2.FinishMovementSampleAsync(sample, SweepOnly(), ct)
                                                 .ConfigureAwait(true);
            Assert.Equal(2, r.Candidates.Count);
            // Player (moved 5, score 50) ranks above the rig (moved 8, score 80-45=35).
            Assert.Equal("0xB000", r.Candidates[0].TransformAddress);
            Assert.Equal("Hero", r.Candidates[0].Name);
            Assert.Equal("0xC000", r.Candidates[1].TransformAddress);
            Assert.True(r.Candidates[0].Score > r.Candidates[1].Score);
        }
        finally { await fp2.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_StructlessPositionSuccess_TreatedUnreadable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // t0 is far from origin (10,0,0); t1 comes back Ok but WITHOUT struct
        // bytes. Without the payload guard that would decode to (0,0,0) and
        // fabricate a displacement of 10; with the guard the object is skipped.
        var begin = new[]
        {
            AddrOk("System.Type", "0x9000"),
            AddrOk("UnityEngine.Transform[]", "0x9100"),
            """
            { "count": 1, "length": 1, "is_pointer": true,
              "elements": [ { "index": 0, "ptr": "0xA000" } ] }
            """,
            Vector3Ok(10f, 0f, 0f),                        // t0 -> far from origin
        };
        var fp = await TeleportFakePipe.StartAsync(begin, ct);
        MovementSample sample;
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            sample = await locator.BeginMovementSampleAsync(SweepOnly(), ct).ConfigureAwait(true);
            Assert.Single(sample.Tracks);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }

        // Ok success but no returnStructBytes.
        var finish = new[]
        {
            """
            { "ok": true, "returnType": "UnityEngine.Vector3", "returnValue": null,
              "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
            """,
        };
        var fp2 = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator2 = new PlayerLocator(fp2.Adapter);
            PlayerLocateResult r = await locator2.FinishMovementSampleAsync(sample, SweepOnly(), ct)
                                                 .ConfigureAwait(true);
            Assert.True(r.Ok);
            Assert.Empty(r.Candidates);   // struct-less success must not fabricate a mover
        }
        finally { await fp2.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_ResolvesBonesToSharedRoot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Two moving "bones" (0xB100, 0xB200) share parent 0xA110 (hips) whose
        // parent is 0xA000 (character root). Root resolution must surface the
        // hips (DCA) + root ABOVE the raw bones.
        var sample = new MovementSample
        {
            Ok = true,
            Tracks = new[]
            {
                new MovementTrack { TransformAddress = "0xB100", Start = (0, 0, 0) },
                new MovementTrack { TransformAddress = "0xB200", Start = (0, 0, 0) },
            },
        };
        var finish = new[]
        {
            Vector3Ok(0f, 0f, 10f),                        // t1 0xB100 -> moved 10
            Vector3Ok(0f, 0f, 8f),                         // t1 0xB200 -> moved 8
            AddrOk("UnityEngine.GameObject", "0xB1F0"),    // enrich B100: get_gameObject
            StringOk("env_bone:skirt"),                    // enrich B100: get_name
            AddrOk("UnityEngine.GameObject", "0xB2F0"),    // enrich B200: get_gameObject
            StringOk("env_bone:belt"),                     // enrich B200: get_name
            // root walk: B100 -> A110 -> A000 -> null; B200 -> A110 (cached rest)
            AddrOk("UnityEngine.Transform", "0xA110"),     // get_parent(0xB100)
            AddrOk("UnityEngine.Transform", "0xA000"),     // get_parent(0xA110)
            NullAddrOk("UnityEngine.Transform"),           // get_parent(0xA000) -> root
            AddrOk("UnityEngine.Transform", "0xA110"),     // get_parent(0xB200)
            // ladder enrich: A110 (DCA), then A000 (root)
            AddrOk("UnityEngine.GameObject", "0xA1F0"),    // enrich A110: get_gameObject
            StringOk("Hips"),                              // enrich A110: get_name
            AddrOk("UnityEngine.GameObject", "0xA0F0"),    // enrich A000: get_gameObject
            StringOk("CharacterRoot"),                     // enrich A000: get_name
        };
        var opt = new MovementDetectOptions { Signals = Only(), ResolveToRoot = true };
        var fp = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.FinishMovementSampleAsync(sample, opt, ct)
                                                .ConfigureAwait(true);
            Assert.True(r.Ok);
            // The character root (non-bone name) is #1; "Hips" is a joint bone,
            // demoted below it even though it is the DCA.
            Assert.Equal("0xA000", r.Candidates[0].TransformAddress);
            Assert.Equal("CharacterRoot", r.Candidates[0].Name);
            Assert.Contains("moving-root", r.Candidates[0].Sources[0]);
            Assert.Equal(PlayerConfidence.High, r.Candidates[0].Confidence);
            Assert.Equal("0xA110", r.Candidates[1].TransformAddress);
            Assert.Equal("Hips", r.Candidates[1].Name);
            Assert.Equal(PlayerConfidence.Medium, r.Candidates[1].Confidence);   // skeleton bone
            // The leaf bones survive as evidence, ranked below the roots.
            Assert.Contains(r.Candidates, c => c.TransformAddress == "0xB100");
            Assert.True(r.Candidates[0].Score > r.Candidates.First(c => c.TransformAddress == "0xB100").Score);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_CharacterRoot_LiftedAboveSkeletonBones()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Mirrors the live Utawarerumono ladder: three env_bone: leaves whose DCA
        // is a spine bone; the ladder climbs env_bone bones up to the character
        // GameObject "P_Chara_00109@fld" (non-bone name) then a container. The
        // character rung must be #1, not the DCA spine bone.
        var sample = new MovementSample
        {
            Ok = true,
            Tracks = new[]
            {
                new MovementTrack { TransformAddress = "0xB100", Start = (0, 0, 0) },
                new MovementTrack { TransformAddress = "0xB200", Start = (0, 0, 0) },
            },
        };
        var finish = new[]
        {
            Vector3Ok(0f, 0f, 10f),                        // t1 B100
            Vector3Ok(0f, 0f, 8f),                         // t1 B200
            AddrOk("UnityEngine.GameObject", "0xB1F0"), StringOk("env_bone:mabutaR"),  // enrich B100
            AddrOk("UnityEngine.GameObject", "0xB2F0"), StringOk("env_bone:RightPinky"), // enrich B200
            // walk B100: Spine1 -> Hips -> P_Chara -> AttachPoint -> null
            AddrOk("UnityEngine.Transform", "0xC001"),     // get_parent(B100) -> Spine1
            AddrOk("UnityEngine.Transform", "0xC002"),     // get_parent(Spine1) -> Hips
            AddrOk("UnityEngine.Transform", "0xC003"),     // get_parent(Hips) -> P_Chara
            AddrOk("UnityEngine.Transform", "0xC004"),     // get_parent(P_Chara) -> AttachPoint
            NullAddrOk("UnityEngine.Transform"),           // get_parent(AttachPoint) -> root
            AddrOk("UnityEngine.Transform", "0xC001"),     // get_parent(B200) -> Spine1 (cached rest)
            // ladder enrich in DCA->root order: Spine1, Hips, P_Chara, AttachPoint
            AddrOk("UnityEngine.GameObject", "0xC01F"), StringOk("env_bone:Spine1"),
            AddrOk("UnityEngine.GameObject", "0xC02F"), StringOk("env_bone:Hips"),
            AddrOk("UnityEngine.GameObject", "0xC03F"), StringOk("P_Chara_00109@fld"),
            AddrOk("UnityEngine.GameObject", "0xC04F"), StringOk("AttachPoint"),
        };
        var opt = new MovementDetectOptions { Signals = Only(), ResolveToRoot = true };
        var fp = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.FinishMovementSampleAsync(sample, opt, ct)
                                                .ConfigureAwait(true);
            // Character GameObject #1, its container next, skeleton bones after.
            Assert.Equal("P_Chara_00109@fld", r.Candidates[0].Name);
            Assert.Equal("0xC003", r.Candidates[0].TransformAddress);
            Assert.Equal(PlayerConfidence.High, r.Candidates[0].Confidence);
            Assert.Equal("AttachPoint", r.Candidates[1].Name);                     // container above
            // The DCA spine bone is demoted below the character.
            var spine = r.Candidates.First(c => c.Name == "env_bone:Spine1");
            Assert.Equal(PlayerConfidence.Medium, spine.Confidence);
            Assert.True(r.Candidates[0].Score > spine.Score);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Theory]
    [InlineData("env_bone:Spine1", "env_bone:", true)]
    [InlineData("env_bone:GlobalSRT", "env_bone:", true)]
    [InlineData("Hips", "", true)]                 // joint token, no prefix
    [InlineData("RightPinky", "", true)]
    [InlineData("Bip01_Spine", "", true)]
    [InlineData("P_Chara_00109@fld", "env_bone:", false)]   // the character
    [InlineData("AttachPoint", "env_bone:", false)]
    [InlineData("PlayerRoot", "", false)]          // "root" is not a joint token
    public void IsBoneName_Classifies(string name, string prefix, bool expected) =>
        Assert.Equal(expected, PlayerLocator.IsBoneName(name, prefix));

    [Theory]
    [InlineData(new[] { "env_bone:mabutaR", "env_bone:Spine1_2p", "env_bone:RightPinky" }, "env_bone:")]
    [InlineData(new[] { "Bip01 L Hand", "Bip01 Spine", "Bip01 Head" }, "Bip01 ")]
    [InlineData(new[] { "foot", "hand" }, "")]     // no common prefix
    public void CommonBonePrefix_Computes(string[] names, string expected) =>
        Assert.Equal(expected, PlayerLocator.CommonBonePrefix(names));

    [Fact]
    public async Task Movement_SingleMover_NotPromotedToRoot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Only one object moved — there is no "shared ancestor", so root
        // resolution must NOT run (else it would re-surface the mover itself or
        // promote its scene container). The raw mover row survives.
        var sample = new MovementSample
        {
            Ok = true,
            Tracks = new[] { new MovementTrack { TransformAddress = "0xB100", Start = (0, 0, 0) } },
        };
        var finish = new[]
        {
            Vector3Ok(0f, 0f, 10f),                        // t1 -> moved 10
            AddrOk("UnityEngine.GameObject", "0xB1F0"),
            StringOk("env_bone:foot"),
        };
        var opt = new MovementDetectOptions { Signals = Only(), ResolveToRoot = true };
        var fp = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.FinishMovementSampleAsync(sample, opt, ct)
                                                .ConfigureAwait(true);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal("0xB100", c.TransformAddress);
            Assert.Contains("moved", c.Sources);           // raw mover, not a resolved root
            Assert.DoesNotContain(c.Sources, s => s.Contains("moving-root", StringComparison.Ordinal));
            Assert.NotNull(c.MovedDistance);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_ContainerRoot_MediumConfidence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Both movers sit directly under one root (they converge only at the
        // top) — a one-rung ladder == the top root, which reads as a flat /
        // container hierarchy → Medium, not High.
        var sample = new MovementSample
        {
            Ok = true,
            Tracks = new[]
            {
                new MovementTrack { TransformAddress = "0xB100", Start = (0, 0, 0) },
                new MovementTrack { TransformAddress = "0xB200", Start = (0, 0, 0) },
            },
        };
        var finish = new[]
        {
            Vector3Ok(0f, 0f, 10f),                        // t1 B100
            Vector3Ok(0f, 0f, 8f),                         // t1 B200
            AddrOk("UnityEngine.GameObject", "0xB1F0"), StringOk("thingA"),   // enrich B100
            AddrOk("UnityEngine.GameObject", "0xB2F0"), StringOk("thingB"),   // enrich B200
            AddrOk("UnityEngine.Transform", "0xR000"),     // get_parent(B100) -> root
            NullAddrOk("UnityEngine.Transform"),           // get_parent(R000) -> null
            AddrOk("UnityEngine.Transform", "0xR000"),     // get_parent(B200) -> root
            AddrOk("UnityEngine.GameObject", "0xR0F0"), StringOk("Characters"),  // enrich R000
        };
        var opt = new MovementDetectOptions { Signals = Only(), ResolveToRoot = true };
        var fp = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.FinishMovementSampleAsync(sample, opt, ct)
                                                .ConfigureAwait(true);
            Assert.Equal("0xR000", r.Candidates[0].TransformAddress);
            Assert.Equal("Characters", r.Candidates[0].Name);
            Assert.Equal(PlayerConfidence.Medium, r.Candidates[0].Confidence);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Movement_ParentWalkError_FallsBackToRawBones()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // A get_parent ERROR (not a 0x0 root) must not seed a false cluster: the
        // errored chain is dropped, leaving <2 chains → no roots, raw bones kept.
        var sample = new MovementSample
        {
            Ok = true,
            Tracks = new[]
            {
                new MovementTrack { TransformAddress = "0xB100", Start = (0, 0, 0) },
                new MovementTrack { TransformAddress = "0xB200", Start = (0, 0, 0) },
            },
        };
        var finish = new[]
        {
            Vector3Ok(0f, 0f, 10f),                        // t1 B100
            Vector3Ok(0f, 0f, 8f),                         // t1 B200
            AddrOk("UnityEngine.GameObject", "0xB1F0"), StringOk("boneA"),
            AddrOk("UnityEngine.GameObject", "0xB2F0"), StringOk("boneB"),
            ErrorResult("native_crash_in_runtime_invoke"), // get_parent(B100) FAILS
            AddrOk("UnityEngine.Transform", "0xA110"),     // get_parent(B200) -> A110
            NullAddrOk("UnityEngine.Transform"),           // get_parent(A110) -> root
        };
        var opt = new MovementDetectOptions { Signals = Only(), ResolveToRoot = true };
        var fp = await TeleportFakePipe.StartAsync(finish, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.FinishMovementSampleAsync(sample, opt, ct)
                                                .ConfigureAwait(true);
            Assert.True(r.Ok);
            // No root row (errored chain dropped -> single valid chain -> no DCA).
            Assert.DoesNotContain(r.Candidates, c => c.Sources.Any(s => s.Contains("moving-root", StringComparison.Ordinal)));
            Assert.Contains(r.Candidates, c => c.TransformAddress == "0xB100");
            Assert.Contains(r.Candidates, c => c.TransformAddress == "0xB200");
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    // ------------------------------------------------------------------
    // Signal-E gate + strong-signal confidence
    // ------------------------------------------------------------------

    [Fact]
    public async Task UserClassScan_ManyInstances_CountSkipped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            """{ "count": 1, "assemblies": [ { "name": "Assembly-CSharp" } ] }""",
            """
            { "assembly": "Assembly-CSharp", "count": 1, "classes": [
              { "namespace": "Game", "name": "Character" } ] }
            """,
            // 5 live instances -> count 5 > 4 -> shared/NPC class, must be skipped.
            """{ "count": 5, "limit": 5, "matches": [ "0x10", "0x20", "0x30", "0x40", "0x50" ] }""",
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(classes: true), ct)
                                                .ConfigureAwait(true);
            Assert.True(r.Ok);
            Assert.Empty(r.Candidates);   // no get_transform, no candidate
            Assert.Contains(r.Notes, n => n.Contains("count-skipped", StringComparison.Ordinal));
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task UserClassScan_PlayerSingleton_FieldWalkSurfacesRealTransform()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // The "Player" singleton isn't a Component (get_transform fails), so its
        // fields are mined: a Transform-typed field (_character) points at the
        // real controllable object; a Vector3 field is logged, not surfaced.
        var responses = new[]
        {
            """{ "count": 1, "assemblies": [ { "name": "Assembly-CSharp" } ] }""",
            """
            { "assembly": "Assembly-CSharp", "count": 1, "classes": [
              { "namespace": "Game", "name": "Player" } ] }
            """,
            """{ "count": 1, "limit": 5, "matches": [ "0xE100" ] }""",   // findInstances(Player)
            NullAddrOk("UnityEngine.Transform"),                          // get_transform -> not a Component
            // walkInstance(0xE100): a Transform ref, a Vector3 position, a scalar
            """
            { "address": "0xE100", "count": 3, "fields": [
              { "name": "_character", "type": "UnityEngine.Transform", "offset": 24, "ptr": "0xC100" },
              { "name": "_logicalPos", "type": "UnityEngine.Vector3", "offset": 32 },
              { "name": "hp", "type": "System.Int32", "offset": 40, "value": 100 } ] }
            """,
            AddrOk("UnityEngine.GameObject", "0xC1E0"),                   // enrich: get_gameObject
            StringOk("MapChara"),                                        // enrich: get_name
            Vector3Ok(5f, 0f, 5f),                                       // enrich: get_position
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(classes: true), ct)
                                                .ConfigureAwait(true);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal("0xC100", c.TransformAddress);       // the field's Transform, not the singleton
            Assert.Equal("MapChara", c.Name);
            Assert.Equal(new[] { "class:Player._character" }, c.Sources);
            Assert.Equal(PlayerConfidence.High, c.Confidence);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal("walkInstance", Cmd(reqs[4]));
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task StrongSignal_OnRigName_StaysHighConfidence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // A Player-tagged object whose name trips the rig heuristic must NOT be
        // force-demoted to Low — the strong tag signal recovers confidence.
        var responses = new[]
        {
            AddrOk("UnityEngine.GameObject", "0x2000"),    // FindWithTag("Player")
            AddrOk("UnityEngine.Transform", "0x2100"),     // GameObject.get_transform
            StringOk("PlayerCameraTarget"),                // rig name (contains "camera")
            Vector3Ok(1f, 2f, 3f),
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var locator = new PlayerLocator(fp.Adapter);
            PlayerLocateResult r = await locator.LocateAsync(Only(tag: true), ct)
                                                .ConfigureAwait(true);
            PlayerCandidate c = Assert.Single(r.Candidates);
            Assert.Equal(PlayerConfidence.High, c.Confidence);
            Assert.NotEqual(PlayerConfidence.Low, r.TopConfidence);
            // Score still eats the rig penalty (100 - 45) but confidence is High.
            Assert.Equal(PlayerLocatorRanking.ScoreTag - PlayerLocatorRanking.CameraRigNamePenalty, c.Score);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }
}
