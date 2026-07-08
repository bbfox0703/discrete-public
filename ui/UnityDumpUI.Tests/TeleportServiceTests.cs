// TeleportServiceTests.cs — Task 3 (Teleport wave 1). Pins the wire behaviour
// of the Unity teleport primitive: which engine method/class/assembly each
// step targets, that reads request returnStruct + decode Vector3 bytes, and
// that writes pack a value-struct arg with the exact little-endian layout.
//
// Uses the same fake-named-pipe idiom as CallMethodMainThreadTests, extended
// to answer a QUEUE of requests so the multi-RPC orchestration (resolve ->
// get_position -> get_eulerAngles) can be driven end-to-end.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class TeleportServiceTests
{
    private const string AddrOk = """
        { "ok": true, "returnType": "UnityEngine.Transform", "returnValue": "0x2000",
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private const string VoidOk = """
        { "ok": true, "returnType": "System.Void", "returnValue": null,
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "(UnityEngine.Vector3)", "humanSuffix": "Vector3" } }
        """;

    private static string StructOk(string typeName, string hexBytes) => $$"""
        { "ok": true, "returnType": "{{typeName}}", "returnValue": null,
          "returnStructBytes": "{{hexBytes}}", "returnStructSize": 12,
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    [Fact]
    public async Task ResolveTransform_Component_TargetsComponentGetTransform()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { AddrOk }, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (transform, error) = await svc.ResolveTransformAsync("0x1000", targetIsGameObject: false, ct)
                                              .ConfigureAwait(true);

            Assert.Null(error);
            Assert.Equal("0x2000", transform);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement p = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("UnityEngine.CoreModule", p.GetProperty("assembly").GetString());
            Assert.Equal("UnityEngine",            p.GetProperty("namespace").GetString());
            Assert.Equal("Component",              p.GetProperty("name").GetString());
            Assert.Equal("get_transform",          p.GetProperty("method").GetString());
            Assert.Equal("0x1000",                 p.GetProperty("instance").GetString());
            Assert.Equal(JsonValueKind.True,       p.GetProperty("main_thread").ValueKind);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task ResolveTransform_GameObject_TargetsGameObjectGetTransform()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { AddrOk }, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (transform, error) = await svc.ResolveTransformAsync("0x1000", targetIsGameObject: true, ct)
                                              .ConfigureAwait(true);

            Assert.Null(error);
            Assert.Equal("0x2000", transform);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal("GameObject", TeleportFakePipe.Params(reqs[0]).GetProperty("name").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task ReadPose_DecodesPositionAndEuler_RequestsReturnStruct()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // position (10,20,30) and eulerAngles (5,45,0) as little-endian floats.
        var responses = new[]
        {
            StructOk("UnityEngine.Vector3", "000020410000a0410000f041"),
            StructOk("UnityEngine.Vector3", "0000a0400000344200000000"),
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            TeleportPose pose = await svc.ReadPoseAsync("0x2000", ct).ConfigureAwait(true);

            Assert.True(pose.Ok);
            Assert.Equal(10.0, pose.X, 3);
            Assert.Equal(20.0, pose.Y, 3);
            Assert.Equal(30.0, pose.Z, 3);
            Assert.True(pose.HasRotation);
            Assert.Equal(5.0,  pose.EulerX, 3);
            Assert.Equal(45.0, pose.EulerY, 3);
            Assert.Equal(0.0,  pose.EulerZ, 3);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement p0 = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("get_position", p0.GetProperty("method").GetString());
            Assert.Equal(JsonValueKind.True, p0.GetProperty("returnStruct").ValueKind);
            Assert.Equal("get_eulerAngles", TeleportFakePipe.Params(reqs[1]).GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task WritePose_PacksVector3ValueStructArg()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk }, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (ok, error) = await svc.WritePoseAsync("0x2000", 1.0, 2.0, 3.0, euler: null, ct: ct)
                                       .ConfigureAwait(true);

            Assert.True(ok);
            Assert.Null(error);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement p = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("set_position", p.GetProperty("method").GetString());
            JsonElement arg0 = p.GetProperty("args")[0];
            Assert.Equal("value-struct", arg0.GetProperty("type").GetString());
            // 1f,2f,3f little-endian, contiguous.
            Assert.Equal("0000803f0000004000004040", arg0.GetProperty("bytes").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task WritePose_WithRotation_SetsPositionThenEuler()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk, VoidOk }, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (ok, error) = await svc.WritePoseAsync("0x2000", 1.0, 2.0, 3.0, (10.0, 20.0, 30.0), ct: ct)
                                       .ConfigureAwait(true);

            Assert.True(ok);
            Assert.Null(error);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal(2, reqs.Count);
            Assert.Equal("set_position",    TeleportFakePipe.Params(reqs[0]).GetProperty("method").GetString());
            Assert.Equal("set_eulerAngles", TeleportFakePipe.Params(reqs[1]).GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task CallEngine_FallsBackToUnityEngineAssemblyOnClassNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string notFound = """ { "error": "class not found: UnityEngine.Component" } """;
        var fp = await TeleportFakePipe.StartAsync(new[] { notFound, AddrOk }, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (transform, error) = await svc.ResolveTransformAsync("0x1000", targetIsGameObject: false, ct)
                                              .ConfigureAwait(true);

            Assert.Null(error);
            Assert.Equal("0x2000", transform);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal(2, reqs.Count);
            Assert.Equal("UnityEngine.CoreModule", TeleportFakePipe.Params(reqs[0]).GetProperty("assembly").GetString());
            Assert.Equal("UnityEngine",            TeleportFakePipe.Params(reqs[1]).GetProperty("assembly").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task TeleportDirectional_MovesAlongForwardByDistance()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // pos (0,0,0), forward (0,0,1) [Unity +Z], distance 5, 3D mode.
        var responses = new[]
        {
            StructOk("UnityEngine.Vector3", "000000000000000000000000"), // get_position
            StructOk("UnityEngine.Vector3", "00000000000000000000803f"), // get_forward
            VoidOk,                                                       // set_position
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (ok, error) = await svc.TeleportDirectionalAsync("0x2000", 5.0, horizontal: false, ct: ct)
                                       .ConfigureAwait(true);

            Assert.True(ok);
            Assert.Null(error);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal("get_position", TeleportFakePipe.Params(reqs[0]).GetProperty("method").GetString());
            Assert.Equal("get_forward",  TeleportFakePipe.Params(reqs[1]).GetProperty("method").GetString());
            JsonElement setPos = TeleportFakePipe.Params(reqs[2]);
            Assert.Equal("set_position", setPos.GetProperty("method").GetString());
            // dest = (0,0,0) + (0,0,1)*5 = (0,0,5).
            Assert.Equal("00000000000000000000a040",
                         setPos.GetProperty("args")[0].GetProperty("bytes").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public void CallArgFactory_Vector3_RoundTripsThroughDecode()
    {
        CallArgRequest arg = CallArgFactory.Vector3(-1.5f, 0.25f, 42.0f);
        Assert.Equal("value-struct", arg.Type);
        float[] back = CallArgFactory.DecodeStructFloats(arg.Bytes, 3);
        Assert.Equal(-1.5f, back[0]);
        Assert.Equal(0.25f, back[1]);
        Assert.Equal(42.0f, back[2]);
    }

    [Fact]
    public void DecodeStructFloats_BadHex_ReturnsZeros()
    {
        float[] r = CallArgFactory.DecodeStructFloats("nothex", 3);
        Assert.Equal(new float[] { 0, 0, 0 }, r);
        Assert.Equal(new float[] { 0, 0, 0 }, CallArgFactory.DecodeStructFloats(null, 3));
    }

    // --- driver-aware teleport ---

    // An address-returning success (Type / component / transform pointer).
    private static string Ref(string hex) => $$"""
        { "ok": true, "returnType": "System.Object", "returnValue": "{{hex}}",
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    [Fact]
    public async Task DetectDriver_CharacterController_RetargetsToItsTransform()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // CC on the target itself — nearest walk stops immediately (no get_parent).
        var responses = new[]
        {
            Ref("0xCC7E"),   // Type.GetType(CharacterController)
            Ref("0xCC00"),   // walk d0: GetComponent(target) -> the CC (stop)
            Ref("0xCC01"),   // Component.get_transform(cc) -> the CC's own transform
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (driver, effective, note) = await svc.DetectMovementDriverAsync("0x2000", ct)
                                                     .ConfigureAwait(true);

            Assert.Equal(MovementDriverKind.CharacterController, driver.Kind);
            Assert.True(driver.IsCharacterController);
            Assert.Equal("0xCC00", driver.ComponentAddress);
            Assert.Equal("0xCC01", effective);                 // retargeted to the CC's transform
            Assert.Contains("self", note);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal("GetType",       TeleportFakePipe.Params(reqs[0]).GetProperty("method").GetString());
            JsonElement p1 = TeleportFakePipe.Params(reqs[1]);
            Assert.Equal("Component",     p1.GetProperty("name").GetString());
            Assert.Equal("GetComponent",  p1.GetProperty("method").GetString());
            Assert.Equal("get_transform", TeleportFakePipe.Params(reqs[2]).GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task DetectDriver_NoCharacterController_FallsBackToTransform()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            Ref("0xCC7E"),   // CharacterController Type resolves
            // walk: GetComponent(self) -> null, get_parent -> root (stop)
            Ref("0x0"),      // GetComponent(self) -> null
            Ref("0x0"),      // get_parent -> null (top of hierarchy) -> stop
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (driver, effective, note) = await svc.DetectMovementDriverAsync("0x2000", ct)
                                                     .ConfigureAwait(true);

            Assert.Equal(MovementDriverKind.Transform, driver.Kind);
            Assert.False(driver.IsCharacterController);
            Assert.Equal("0x2000", effective);                 // unchanged
            Assert.NotNull(note);                              // "No CharacterController …"

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal(3, reqs.Count);
            Assert.Equal("GetComponent", TeleportFakePipe.Params(reqs[1]).GetProperty("method").GetString());
            Assert.Equal("get_parent",   TeleportFakePipe.Params(reqs[2]).GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task DetectDriver_CharacterControllerOnAncestor_NearestFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // No CC on the picked object or its parent; the nearest one is on the
        // grandparent — the walk climbs to it and STOPS (doesn't over-climb).
        var responses = new[]
        {
            Ref("0xCC7E"),   // Type.GetType(CharacterController)
            Ref("0x0"),      // walk d0: GetComponent(target) -> null
            Ref("0xA100"),   // walk d0: get_parent -> parent
            Ref("0x0"),      // walk d1: GetComponent(parent) -> null
            Ref("0xA200"),   // walk d1: get_parent -> grandparent
            Ref("0xCC00"),   // walk d2: GetComponent(grandparent) -> the CC (stop)
            Ref("0xCC01"),   // get_transform(cc) -> its transform
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (driver, effective, note) = await svc.DetectMovementDriverAsync("0x2000", ct)
                                                     .ConfigureAwait(true);

            Assert.Equal(MovementDriverKind.CharacterController, driver.Kind);
            Assert.Equal("0xCC00", driver.ComponentAddress);
            Assert.Equal("0xCC01", effective);
            Assert.Contains("ancestor +2", note);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task DetectDriver_FullWidthNullComponent_ClimbsToRealAncestorCc()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Live Utawarerumono bug: GetComponent returns the FULL-WIDTH managed null
        // "0x0000000000000000" (not "0x0") when the object has no
        // CharacterController. The old guard only caught "0x0", so it accepted the
        // full-width zero as a FOUND component and stopped at a phantom CC
        // (instance 0 -> set_enabled instance_required_for_non_static), never
        // climbing to the REAL CC on the ancestor rig. It must read as null.
        var responses = new[]
        {
            Ref("0xCC7E"),                 // Type.GetType(CharacterController)
            Ref("0x0000000000000000"),     // GetComponent(target) -> full-width null (NOT a CC)
            Ref("0xA100"),                 // get_parent(target) -> ancestor rig
            Ref("0xCC00"),                 // GetComponent(ancestor) -> the REAL CC (stop)
            Ref("0xCC01"),                 // get_transform(cc) -> its transform
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (driver, effective, note) = await svc.DetectMovementDriverAsync("0x2000", ct)
                                                     .ConfigureAwait(true);

            // The phantom self-CC is rejected; the walk finds the real ancestor CC.
            Assert.Equal(MovementDriverKind.CharacterController, driver.Kind);
            Assert.True(driver.IsCharacterController);
            Assert.Equal("0xCC00", driver.ComponentAddress);
            Assert.Equal("0xCC01", effective);
            Assert.Contains("ancestor +1", note);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0x0", true)]
    [InlineData("0x", true)]
    [InlineData("0x0000000000000000", true)]     // full-width managed null
    [InlineData("0X0000000000000000", true)]     // case-insensitive prefix
    [InlineData("0xCC00", false)]
    [InlineData("0x0000000000000001", false)]
    public void IsNullAddress_NormalizesZeroForms(string? hex, bool expected) =>
        Assert.Equal(expected, TeleportService.IsNullAddress(hex));

    [Fact]
    public async Task WritePose_CharacterControllerDriver_DisablesSetsReEnables()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var driver = new MovementDriver
        {
            Kind = MovementDriverKind.CharacterController,
            ComponentAddress = "0xCC00",
        };
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk, VoidOk, VoidOk }, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (ok, error) = await svc.WritePoseAsync("0xCC01", 1.0, 2.0, 3.0, euler: null, driver, ct)
                                       .ConfigureAwait(true);
            Assert.True(ok);
            Assert.Null(error);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal(3, reqs.Count);
            // disable → set_position → re-enable, and the CC calls hit PhysicsModule.
            JsonElement dis = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("set_enabled", dis.GetProperty("method").GetString());
            Assert.Equal("Collider",    dis.GetProperty("name").GetString());
            Assert.Equal("UnityEngine.PhysicsModule", dis.GetProperty("assembly").GetString());
            Assert.Equal(JsonValueKind.False, dis.GetProperty("args")[0].GetProperty("value").ValueKind);
            Assert.Equal("set_position", TeleportFakePipe.Params(reqs[1]).GetProperty("method").GetString());
            JsonElement en = TeleportFakePipe.Params(reqs[2]);
            Assert.Equal("set_enabled", en.GetProperty("method").GetString());
            Assert.Equal(JsonValueKind.True, en.GetProperty("args")[0].GetProperty("value").ValueKind);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task WritePose_NoDriver_WritesDirectly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk }, ct);
        try
        {
            var svc = new TeleportService(fp.Adapter);
            var (ok, _) = await svc.WritePoseAsync("0x2000", 1.0, 2.0, 3.0,
                euler: null, driver: MovementDriver.Transform, ct: ct).ConfigureAwait(true);
            Assert.True(ok);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Single(reqs);   // just set_position — no disable/enable
            Assert.Equal("set_position", TeleportFakePipe.Params(reqs[0]).GetProperty("method").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }
}
