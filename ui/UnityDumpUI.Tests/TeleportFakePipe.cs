// TeleportFakePipe.cs — shared fake-named-pipe harness for the Teleport wire
// tests. Answers a QUEUE of canned callMethod responses in order (so a
// multi-RPC orchestration can be driven end-to-end) and captures every request
// line for assertions. Same idiom as CallMethodMainThreadTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Tests;

internal sealed class TeleportFakePipe : IAsyncDisposable
{
    public BackendAdapter Adapter { get; private set; } = null!;
    public Task<List<string>> ServerTask { get; private set; } = null!;
    private NamedPipeServerStream _server = null!;
    private PipeClient _client = null!;

    public static async Task<TeleportFakePipe> StartAsync(IReadOnlyList<string> responses, CancellationToken ct)
    {
        var fp = new TeleportFakePipe();
        string pipeName = $"DiscreteUITest_TP_{Guid.NewGuid():N}";
        fp._server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        fp._client = new PipeClient(pipeName);

        Task waitConn = fp._server.WaitForConnectionAsync(ct);
        await fp._client.ConnectAsync(timeoutMs: 2000, ct).ConfigureAwait(true);
        await waitConn.ConfigureAwait(true);

        fp.ServerTask = Task.Run(async () =>
        {
            var requests = new List<string>();
            using var reader = new StreamReader(fp._server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(fp._server, new UTF8Encoding(false), 4096, leaveOpen: true)
            { NewLine = "\n", AutoFlush = true };
            foreach (string resultJson in responses)
            {
                string requestLine = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
                requests.Add(requestLine);
                await writer.WriteLineAsync(WrapEnvelope(requestLine, resultJson)).ConfigureAwait(false);
            }
            return requests;
        }, ct);

        fp.Adapter = new BackendAdapter(fp._client);
        return fp;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(true);
        await _server.DisposeAsync().ConfigureAwait(true);
    }

    public static string WrapEnvelope(string requestLine, string resultJson)
    {
        using var reqDoc = JsonDocument.Parse(requestLine);
        int id = reqDoc.RootElement.GetProperty("id").GetInt32();
        using var resultDoc = JsonDocument.Parse(resultJson);
        return $"{{\"id\":{id},\"result\":{JsonSerializer.Serialize(resultDoc.RootElement)}}}";
    }

    /// <summary>The RPC params object ({ assembly, namespace, name, method, args, ... }).</summary>
    public static JsonElement Params(string requestLine)
    {
        using var doc = JsonDocument.Parse(requestLine);
        return doc.RootElement.GetProperty("args").Clone();
    }
}
