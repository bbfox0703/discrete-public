// TeleportControllerTests.cs — Task 4. Integration tests (fake pipe) for the
// stateful action layer: the recall-auto-saves-last flow and guard paths.

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class TeleportControllerTests
{
    private const string TransformOk = """
        { "ok": true, "returnType": "UnityEngine.Transform", "returnValue": "0x2000",
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private const string VoidOk = """
        { "ok": true, "returnType": "System.Void", "returnValue": null,
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "(UnityEngine.Vector3)", "humanSuffix": "" } }
        """;

    // Euler read fails softly so the captured marker has HasRotation=false
    // (keeps the recall write to a single set_position call).
    private const string EulerErr = """ { "error": "no eulerAngles" } """;

    private static string PosOk(string hexBytes) => $$"""
        { "ok": true, "returnType": "UnityEngine.Vector3", "returnValue": null,
          "returnStructBytes": "{{hexBytes}}", "returnStructSize": 12,
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    [Fact]
    public async Task RecallMarker_AutoSavesLast_ThenWritesSavedCoords()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var responses = new[]
        {
            TransformOk,                                // SetTarget:   get_transform
            PosOk("0000c8420000484300009643"),          // SaveMarker:  get_position (100,200,300)
            EulerErr,                                   // SaveMarker:  get_eulerAngles (soft-fail)
            PosOk("0000803f0000004000004040"),          // RecallMarker save-last: get_position (1,2,3)
            EulerErr,                                   // RecallMarker save-last: get_eulerAngles (soft-fail)
            VoidOk,                                     // RecallMarker write:     set_position
        };
        var fp = await TeleportFakePipe.StartAsync(responses, ct);
        try
        {
            var controller = new TeleportController(new TeleportService(fp.Adapter));

            // detectDriver:false — these tests exercise the marker/recall write
            // flow with a plain Transform target; driver detection has its own
            // tests in TeleportServiceTests and would add unrelated RPCs here.
            Assert.Null(await controller.SetTargetAsync("0x1000", false, ct, detectDriver: false).ConfigureAwait(true));
            Assert.True(controller.HasTarget);
            Assert.Null(await controller.SaveMarkerAsync(0, ct).ConfigureAwait(true));
            Assert.Null(await controller.RecallMarkerAsync(0, ct).ConfigureAwait(true));

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal(6, reqs.Count);
            // The 4th call (index 3) is the pre-jump get_position — proof the
            // recall auto-saved the "last" slot before writing.
            Assert.Equal("get_position", TeleportFakePipe.Params(reqs[3]).GetProperty("method").GetString());
            // The write lands the SAVED marker coords (100,200,300), not the
            // current pose (1,2,3).
            JsonElement setPos = TeleportFakePipe.Params(reqs[5]);
            Assert.Equal("set_position", setPos.GetProperty("method").GetString());
            Assert.Equal("0000c8420000484300009643",
                         setPos.GetProperty("args")[0].GetProperty("bytes").GetString());
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task RecallMarker_EmptySlot_ReturnsErrorWithoutWriting()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { TransformOk }, ct);
        try
        {
            var controller = new TeleportController(new TeleportService(fp.Adapter));
            // detectDriver:false — these tests exercise the marker/recall write
            // flow with a plain Transform target; driver detection has its own
            // tests in TeleportServiceTests and would add unrelated RPCs here.
            Assert.Null(await controller.SetTargetAsync("0x1000", false, ct, detectDriver: false).ConfigureAwait(true));

            string? error = await controller.RecallMarkerAsync(1, ct).ConfigureAwait(true);
            Assert.Equal("marker slot is empty", error);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Single(reqs);   // only the SetTarget get_transform; no teleport RPCs
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task RecallLast_WithNothingSaved_ReturnsError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { TransformOk }, ct);
        try
        {
            var controller = new TeleportController(new TeleportService(fp.Adapter));
            // detectDriver:false — these tests exercise the marker/recall write
            // flow with a plain Transform target; driver detection has its own
            // tests in TeleportServiceTests and would add unrelated RPCs here.
            Assert.Null(await controller.SetTargetAsync("0x1000", false, ct, detectDriver: false).ConfigureAwait(true));

            Assert.Equal("nothing to undo", await controller.RecallLastAsync(ct).ConfigureAwait(true));
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }
}
