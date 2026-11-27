// ScanCancellationProtocolTests.cs — C-M1 regression tests.
//
// Verifies the cooperative scan-cancellation contract across:
//   * AOT source-generated ProtocolJsonContext deserialization of
//     {"ok":false,"cancelled":true,"error":"cancelled"} for snapshotBegin,
//     findInstances, listFieldValuesAcrossInstances, and findFieldReferences.
//   * Live in-process NamedPipeServerStream + BackendAdapter end-to-end flow:
//     a long-running snapshotBegin on the bulk pipe is cancelled out-of-band
//     via BackendAdapter.CancelScanAsync() on a second pipe instance.
//   * ViewModel handling of cancelled RPC responses (LiveInspectionViewModel,
//     FieldReferencesViewModel, FieldAcrossInstancesViewModel).

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ScanCancellationProtocolTests : IDisposable
{
    private readonly string _tempDir;

    public ScanCancellationProtocolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScanCancelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string UniquePipeName() =>
        "DiscreteCancelTestPipe_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void CancelledPayloads_DeserializeViaAotContext_ForAllLongScanDtos()
    {
        const string json = "{\"id\":1,\"result\":{\"ok\":false,\"cancelled\":true,\"error\":\"cancelled\"}}";

        var snap = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcEnvelopeSnapshotBeginResult);
        Assert.NotNull(snap?.Result);
        Assert.False(snap!.Result!.Ok);
        Assert.True(snap.Result.Cancelled);
        Assert.Equal("cancelled", snap.Result.Error);

        var inst = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcEnvelopeFindInstancesResult);
        Assert.NotNull(inst?.Result);
        Assert.True(inst!.Result!.Cancelled);
        Assert.Equal("cancelled", inst.Result.Error);
        Assert.Empty(inst.Result.Matches);

        var list = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcEnvelopeListFieldValuesResult);
        Assert.NotNull(list?.Result);
        Assert.True(list!.Result!.Cancelled);
        Assert.Equal("cancelled", list.Result.Error);

        var refs = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcEnvelopeFindFieldReferencesResult);
        Assert.NotNull(refs?.Result);
        Assert.False(refs!.Result!.Ok);
        Assert.True(refs.Result.Cancelled);
        Assert.Equal("cancelled", refs.Result.Error);
        Assert.Empty(refs.Result.References);
    }

    [Fact]
    public async Task BackendAdapter_SnapshotBegin_CancelledOutOfBandViaCancelScan()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;

        var beginReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var bulkServer = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var cancelServer = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task bulkTask = Task.Run(async () =>
        {
            await bulkServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(bulkServer, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(bulkServer, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            string? req = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            Assert.NotNull(req);
            Assert.Contains("\"snapshotBegin\"", req);
            beginReceived.TrySetResult();

            // Hold until the out-of-band cancelScan arrives on the second pipe.
            await cancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            await writer.WriteLineAsync(
                "{\"id\":1,\"result\":{\"ok\":false,\"cancelled\":true,\"error\":\"cancelled\"}}")
                .ConfigureAwait(false);
        }, ct);

        Task cancelTask = Task.Run(async () =>
        {
            await cancelServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(cancelServer, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(cancelServer, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            string? req = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            Assert.NotNull(req);
            Assert.Contains("\"cancelScan\"", req);
            cancelReceived.TrySetResult();
            await writer.WriteLineAsync("{\"id\":1,\"result\":{\"ok\":true}}").ConfigureAwait(false);
        }, ct);

        await using var interactivePipe = new PipeClient(pipeName);
        await using var bulkPipe = new PipeClient(pipeName);
        await bulkPipe.ConnectAsync(5000, ct).ConfigureAwait(true);

        var backend = new BackendAdapter(interactivePipe, bulkPipe);

        Task<SnapshotBeginResult> snapTask = backend.SnapshotBeginAsync(ct: ct);
        await beginReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);

        await backend.CancelScanAsync(ct).ConfigureAwait(true);

        SnapshotBeginResult res = await snapTask.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
        Assert.False(res.Ok);
        Assert.True(res.Cancelled);
        Assert.Equal("cancelled", res.Error);

        await Task.WhenAll(bulkTask, cancelTask).ConfigureAwait(true);
    }

    [Fact]
    public async Task ViewModels_SurfaceCancelledResponsesCleanly_WithoutSilentEmptySuccess()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            for (int id = 1; id <= 3; id++)
            {
                string? req = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                Assert.NotNull(req);
                await writer.WriteLineAsync(
                    $"{{\"id\":{id},\"result\":{{\"ok\":false,\"cancelled\":true,\"error\":\"cancelled\"}}}}")
                    .ConfigureAwait(false);
            }
        }, ct);

        await using var pipe = new PipeClient(pipeName);
        await pipe.ConnectAsync(5000, ct).ConfigureAwait(true);
        var backend = new BackendAdapter(pipe);
        var logger = new FileLogger(Path.Combine(_tempDir, "logs"));

        // 1. LiveInspectionViewModel.FindInstancesCommand
        var inspector = new LiveInspectionViewModel(backend, logger);
        inspector.SetTarget("Assembly-CSharp", "Game", "Player");
        await inspector.FindInstancesCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal("Instance search cancelled.", inspector.ErrorMessage);
        Assert.Empty(inspector.Instances);

        // 2. FieldReferencesViewModel.RefreshCommand
        var refsVm = new FieldReferencesViewModel(
            backend, logger, "Assembly-CSharp", "Game", "Player", "hp", "System.Int32", false);
        await refsVm.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal("Field reference search cancelled.", refsVm.ErrorMessage);
        Assert.Empty(refsVm.Rows);
        Assert.Equal(0, refsVm.MatchCount);

        // 3. FieldAcrossInstancesViewModel.RefreshCommand
        var acrossVm = new FieldAcrossInstancesViewModel(
            backend, logger, "Assembly-CSharp", "Game", "Player", "hp", "System.Int32", 0x10);
        await acrossVm.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal("Field scan cancelled.", acrossVm.ErrorMessage);
        Assert.Empty(acrossVm.Rows);
        Assert.Equal(0, acrossVm.MatchCount);

        await serverTask.ConfigureAwait(true);
    }
}
