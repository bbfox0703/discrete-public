// AobMakerBridge.cs — named-pipe client for AOBMaker's CE plugin.
//
// AOBMaker (https://github.com/.../AOBMaker) ships a Cheat Engine plugin
// that listens on \\.\pipe\AOBMakerCEBridge and forwards JSON-RPC requests
// into CE's runtime — most usefully, "navigate the open Memory View /
// Disassembler to <address>". Inspector rows expose one button per action;
// when AOBMaker isn't running the calls fail-soft (bool false + a status
// log line) so the rest of the inspector keeps working.
//
// Wire format (verbatim from AOBMaker spec):
//   * 4-byte little-endian uint32 length prefix
//   * UTF-8 JSON payload, camelCase keys
//   * CE plugin disconnects after every request → reconnect per call
//   * Max payload 10 MiB (we only ever send <100 bytes here)
//
// Native AOT requirements:
//   * System.IO.Pipes is AOT-safe.
//   * Serialization is via the [JsonSerializable] source-gen context below
//     — no reflection-based fallback path.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Inspector-side facade for the AOBMaker CE-plugin bridge. All methods
/// return <c>false</c> on connect / IO / timeout failure rather than
/// throwing — the per-row buttons are nice-to-haves, the rest of the UI
/// must keep working when CE / AOBMaker aren't present.
/// </summary>
public interface IAobMakerBridge
{
    /// <summary>
    /// Open / move CE's Memory View hex pane to <paramref name="hexAddress"/>.
    /// Pass the address as bare hex (no <c>0x</c> prefix); the bridge
    /// trims a leading <c>0x</c> defensively.
    /// </summary>
    Task<bool> NavigateHexViewAsync(string hexAddress, CancellationToken ct = default);

    /// <summary>Open / move CE's Disassembler pane to the given address.</summary>
    Task<bool> NavigateDisassemblerAsync(string hexAddress, CancellationToken ct = default);

    /// <summary>
    /// Phase 18c — push a complete AA script (ENABLE/DISABLE pair) into
    /// CE's address list as a new memory record of type vtAutoAssembler.
    /// CE plugin runs <c>al.createMemoryRecord()</c> + sets
    /// <c>mr.Script = [==[…]==]</c> on a synchronize callback.
    /// </summary>
    /// <param name="description">CE entry description (column 1 of the address list).</param>
    /// <param name="script">Full AA script text — both <c>[ENABLE]</c> and <c>[DISABLE]</c> blocks.</param>
    /// <param name="autoActivate">When true, CE flips the entry's checkbox on after creation.</param>
    Task<bool> CreateAAScriptAsync(string description, string script,
                                   bool autoActivate = false,
                                   CancellationToken ct = default);
}

public sealed class AobMakerBridge : IAobMakerBridge, IDisposable
{
    private const string PipeName            = "AOBMakerCEBridge";
    private const int    ConnectTimeoutMs    = 1500;
    private const int    ResponseTimeoutMs   = 5000;
    private const uint   MaxMessageSize      = 10u * 1024u * 1024u;

    private const string TypeNavigateHexView      = "NavigateHexView";
    private const string TypeNavigateDisassembler = "NavigateDisassembler";
    private const string TypeCreateAAScript       = "CreateAAScript";

    private readonly FileLogger? _logger;
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    public AobMakerBridge(FileLogger? logger = null) => _logger = logger;

    public Task<bool> NavigateHexViewAsync(string hexAddress, CancellationToken ct = default)
        => SendAsync(TypeNavigateHexView, StripHexPrefix(hexAddress), ct);

    public Task<bool> NavigateDisassemblerAsync(string hexAddress, CancellationToken ct = default)
        => SendAsync(TypeNavigateDisassembler, StripHexPrefix(hexAddress), ct);

    public async Task<bool> CreateAAScriptAsync(string description, string script,
                                                bool autoActivate = false,
                                                CancellationToken ct = default)
    {
        const string type = TypeCreateAAScript;
        if (string.IsNullOrEmpty(script))
        {
            Log($"AobMaker {type}: empty script, skipping");
            return false;
        }
        if (!await ReconnectAsync(ct).ConfigureAwait(false))
        {
            Log($"AobMaker {type}: pipe unavailable (CE / AOBMaker not running?)");
            return false;
        }

        try
        {
            var request = new AobMakerMessage
            {
                Type         = type,
                Description  = description,
                Script       = script,
                AutoActivate = autoActivate,
            };
            await WriteMessageAsync(_pipe!, request, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ResponseTimeoutMs);
            AobMakerMessage? response = await ReadMessageAsync(_pipe!, timeoutCts.Token)
                .ConfigureAwait(false);
            CleanupPipe();

            if (response == null || !response.Success)
            {
                Log($"AobMaker {type} → '{description}': failure ({response?.Message ?? "no response"})");
                return false;
            }
            Log($"AobMaker {type} → '{description}': ok (scriptLen={script.Length}, autoActivate={autoActivate})");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log($"AobMaker {type} → '{description}': timed out");
            CleanupPipe();
            return false;
        }
        catch (Exception ex)
        {
            Log($"AobMaker {type} → '{description}': {ex.GetType().Name}: {ex.Message}");
            CleanupPipe();
            return false;
        }
    }

    // ---------- Internals ------------------------------------------------

    private async Task<bool> SendAsync(string type, string hexAddress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(hexAddress))
        {
            Log($"AobMaker {type}: empty address, skipping");
            return false;
        }
        if (!await ReconnectAsync(ct).ConfigureAwait(false))
        {
            Log($"AobMaker {type}: pipe unavailable (CE / AOBMaker not running?)");
            return false;
        }

        try
        {
            var request = new AobMakerMessage { Type = type, Address = hexAddress };
            await WriteMessageAsync(_pipe!, request, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ResponseTimeoutMs);
            AobMakerMessage? response = await ReadMessageAsync(_pipe!, timeoutCts.Token)
                .ConfigureAwait(false);
            CleanupPipe();

            if (response == null || !response.Success)
            {
                Log($"AobMaker {type} → {hexAddress}: failure ({response?.Message ?? "no response"})");
                return false;
            }
            Log($"AobMaker {type} → {hexAddress}: ok");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log($"AobMaker {type} → {hexAddress}: timed out");
            CleanupPipe();
            return false;
        }
        catch (Exception ex)
        {
            Log($"AobMaker {type} → {hexAddress}: {ex.GetType().Name}: {ex.Message}");
            CleanupPipe();
            return false;
        }
    }

    private async Task<bool> ReconnectAsync(CancellationToken ct)
    {
        try
        {
            CleanupPipe();
            _pipe = new NamedPipeClientStream(".", PipeName,
                                              PipeDirection.InOut,
                                              PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            CleanupPipe();
            return false;
        }
    }

    private static async Task WriteMessageAsync(Stream stream, AobMakerMessage msg, CancellationToken ct)
    {
        // Relaxed encoder: avoids \uXXXX escapes for single quotes / angle
        // brackets / non-ASCII, since CE Plugin's Lua JSON parser doesn't
        // handle those (matches UE5CEDumper / AOBMaker's encoder choice).
        string json = JsonSerializer.Serialize(msg, AobMakerJsonContext.Relaxed.AobMakerMessage);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] lengthBuf = BitConverter.GetBytes((uint)payload.Length);
        await stream.WriteAsync(lengthBuf, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<AobMakerMessage?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        byte[] lengthBuf = new byte[4];
        if (await ReadExactAsync(stream, lengthBuf, 4, ct).ConfigureAwait(false) < 4) return null;
        uint length = BitConverter.ToUInt32(lengthBuf, 0);
        if (length == 0 || length > MaxMessageSize) return null;
        byte[] payload = new byte[length];
        if (await ReadExactAsync(stream, payload, (int)length, ct).ConfigureAwait(false) < (int)length)
            return null;
        string json = Encoding.UTF8.GetString(payload);
        return JsonSerializer.Deserialize(json, AobMakerJsonContext.Default.AobMakerMessage);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, count - total), ct)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static string StripHexPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
    }

    private void Log(string line) => _logger?.Write(Constants.LogCategoryView, line);

    private void CleanupPipe()
    {
        if (_pipe is null) return;
        try { _pipe.Dispose(); } catch { /* best effort */ }
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupPipe();
    }
}

/// <summary>
/// Wire model — every field optional except <c>type</c>. Subset we actually
/// exchange: navigate (Address) + create AA script (Description + Script +
/// AutoActivate) + the response envelope (Success / Message). The CE Plugin
/// silently ignores extra fields, so the same message class serves every
/// command direction.
/// </summary>
internal sealed class AobMakerMessage
{
    [JsonPropertyName("type")]    public string  Type    { get; set; } = string.Empty;
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("script")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Script { get; set; }
    [JsonPropertyName("autoActivate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool    AutoActivate { get; set; }
    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool    Success { get; set; }
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

[JsonSerializable(typeof(AobMakerMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy   = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AobMakerJsonContext : JsonSerializerContext
{
    private static AobMakerJsonContext? _relaxed;
    /// <summary>UnsafeRelaxedJsonEscaping context — see the writer comment in
    /// <see cref="AobMakerBridge.WriteMessageAsync"/>.</summary>
    public static AobMakerJsonContext Relaxed => _relaxed ??= new(new JsonSerializerOptions
    {
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}
