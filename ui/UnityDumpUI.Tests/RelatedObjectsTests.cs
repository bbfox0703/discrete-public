// RelatedObjectsTests.cs — SceneEnumerator.GetRelatedObjectsAsync (feature-diff
// #10). Drives the multi-RPC scene-graph walk over the fake-pipe queue
// (Type.GetType + get_gameObject/get_name/get_transform/get_parent + children).

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class RelatedObjectsTests
{
    private static string SR(string val) => $$"""
        { "ok": true, "returnType": "System.Object", "returnValue": "{{val}}",
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private static string Num(int n) => $$"""
        { "ok": true, "returnType": "System.Int32", "returnValue": {{n}},
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private const string Null = "0x0000000000000000";

    private static SceneEnumerator NewScene(BackendAdapter adapter) =>
        new(adapter, new FileLogger(Path.Combine(Path.GetTempPath(), "RelTestLogs_" + System.Guid.NewGuid().ToString("N"))));

    [Fact]
    public async Task GetRelated_Component_ResolvesGameObjectAndChild()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            SR("0x00000000DEAD01"),  // Type.GetType(GameObject) — engine resolve
            SR("0x00000000000000A0"),// Component.get_gameObject(instance) -> GO
            SR("Player"),            // Object.get_name(GO)
            SR("0x00000000000000B0"),// Component.get_transform(GO)
            SR(Null),                // Transform.get_parent -> none
            SR("0x00000000000000B0"),// GetChildren: GameObject.get_transform(GO)
            Num(1),                  // Transform.get_childCount -> 1
            SR("0x00000000000000C0"),// Transform.GetChild(0) -> child transform
            SR("0x00000000000000D0"),// Component.get_gameObject(childTr) -> child GO
            SR("Weapon"),            // Object.get_name(childGO)
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var scene = NewScene(fp.Adapter);
            RelatedObjectsResult r = await scene
                .GetRelatedObjectsAsync("0x00000000000000A5", "PlayerController", ct).ConfigureAwait(true);

            Assert.Null(r.Error);
            Assert.Equal(2, r.Objects.Count);
            Assert.Equal(RelatedKind.GameObject, r.Objects[0].Kind);
            Assert.Equal("Player", r.Objects[0].Label);
            Assert.Equal("0x00000000000000A0", r.Objects[0].Address);
            Assert.Equal("GameObject", r.Objects[0].Class);
            Assert.Equal(RelatedKind.Child, r.Objects[1].Kind);
            Assert.Equal("Weapon", r.Objects[1].Label);
            Assert.Equal("0x00000000000000D0", r.Objects[1].Address);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task GetRelated_GameObjectInstance_SkipsGetGameObject()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            SR("0x00000000DEAD01"),  // Type.GetType
            SR("Root"),              // Object.get_name(instance) — instance IS the GO
            SR("0x00000000000000B0"),// get_transform
            SR(Null),                // get_parent -> none
            SR("0x00000000000000B0"),// children: get_transform
            Num(0),                  // childCount 0
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var scene = NewScene(fp.Adapter);
            RelatedObjectsResult r = await scene
                .GetRelatedObjectsAsync("0x00000000000000A0", "GameObject", ct).ConfigureAwait(true);

            Assert.Null(r.Error);
            RelatedObject only = Assert.Single(r.Objects);
            Assert.Equal(RelatedKind.GameObject, only.Kind);
            Assert.Equal("Root", only.Label);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task GetRelated_NoGameObject_ReturnsError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            SR("0x00000000DEAD01"),  // Type.GetType
            SR(Null),                // get_gameObject -> managed null
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var scene = NewScene(fp.Adapter);
            RelatedObjectsResult r = await scene
                .GetRelatedObjectsAsync("0xDeadInstance", "SomeScript", ct).ConfigureAwait(true);

            Assert.NotNull(r.Error);
            Assert.Empty(r.Objects);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }
}
