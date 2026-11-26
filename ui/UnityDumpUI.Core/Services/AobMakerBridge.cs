// AobMakerBridge.cs — named-pipe client for AOBMaker's CE plugin.
//
// AOBMaker (https://github.com/.../AOBMaker) ships a Cheat Engine plugin
// that listens on \\.\pipe\AOBMakerCEBridge and forwards JSON-RPC requests
// into CE's runtime. Phase 18c started using CreateAAScript; Phase 22a
// extends to the full set the bridge needs:
//
//   * NavigateHexView / NavigateDisassembler — open Memory View on an addr
//   * CreateAAScript                          — append vtAutoAssembler record
//   * CreateMemoryRecord                      — append primitive record
//   * CreateAAScriptWithRecords               — parent AA + offset children
//   * InjectTableFile                         — embed raw UTF-8 file in CT
//   * CheckAvailability                       — probe the pipe + flip IsAvailable
//
// Wire format (verbatim from AOBMaker spec):
//   * 4-byte little-endian uint32 length prefix
//   * UTF-8 JSON payload, camelCase keys
//   * CE plugin disconnects after every request → reconnect per call
//   * Max payload 10 MiB (we send ≤ ~150 KB for full bridge lua injects)
//
// IsAvailable lifecycle:
//   * False on construction.
//   * Each successful request flips it true.
//   * Each connect / IO / timeout failure flips it false.
//   * AvailabilityChanged fires only on transitions, not every call.
//   * CheckAvailabilityAsync is the canonical "is CE+plugin running?" probe;
//     UI bind menu IsEnabled to IsAvailable + subscribe to the event for
//     live updates.
//
// Native AOT requirements:
//   * System.IO.Pipes is AOT-safe.
//   * Serialization is via the [JsonSerializable] source-gen context below
//     — no reflection-based fallback path.

using System;
using System.Collections.Generic;
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
/// Cheat Engine memory-record value types in CE's internal TVariableType
/// numbering (NOT SDK header order). Used by
/// <see cref="IAobMakerBridge.CreateMemoryRecordAsync"/> and the
/// <c>records[]</c> element type in
/// <see cref="IAobMakerBridge.CreateAAScriptWithRecordsAsync"/>.
/// </summary>
public enum CeValueType
{
    Byte           = 0,
    Word           = 1,
    Dword          = 2,
    Qword          = 3,
    Single         = 4,
    Double         = 5,
    String         = 6,
    UnicodeString  = 7,
    ByteArray      = 8,
    Binary         = 9,
}

/// <summary>
/// One child memory record under a parent AA script (the
/// <c>records[]</c> element of
/// <see cref="IAobMakerBridge.CreateAAScriptWithRecordsAsync"/>).
/// Mirrors the C++ <c>RecordDef</c> in
/// <c>AOBMaker/plugins/CEPlugin/src/pipe_server.cpp</c>.
/// </summary>
public sealed class AaScriptChildRecord
{
    /// <summary>Description shown in CE's address list (column 1).</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Base address — typically a registered symbol name (e.g.
    /// "PlayerBase") or a hex literal. CE resolves this each refresh.
    /// </summary>
    public required string Address { get; init; }

    /// <summary>CE TVariableType value.</summary>
    public required CeValueType ValueType { get; init; }

    /// <summary>True for signed integer rendering (ignored on float / string types).</summary>
    public bool IsSigned { get; init; }

    /// <summary>
    /// Pointer-chain offsets in bytes, in Cheat Engine <c>.CT</c> /
    /// <c>MemoryRecord.Offset[0..n-1]</c> order: <c>Offsets[0]</c> is
    /// assigned to <c>child.Offset[0]</c> (applied LAST, closest to the
    /// target value) and <c>Offsets[n-1]</c> is applied FIRST right after
    /// dereferencing <see cref="Address"/>. Empty / null → no pointer walk.
    /// </summary>
    public IReadOnlyList<int>? Offsets { get; init; }

    /// <summary>
    /// CE display color as 6-hex-digit RGB without the leading "#" or
    /// "0x" (e.g. "FF0000"). Null → CE default.
    /// </summary>
    public string? Color { get; init; }
}

/// <summary>
/// Bridge to AOBMaker CE Plugin. All methods return <c>false</c> /
/// <c>(false, …)</c> on connect / IO / timeout failure rather than
/// throwing — the per-row buttons are nice-to-haves and the rest of
/// the UI must keep working when CE / AOBMaker aren't present.
/// </summary>
public interface IAobMakerBridge
{
    /// <summary>
    /// Cached availability — true if the most recent pipe operation
    /// (request or <see cref="CheckAvailabilityAsync"/>) succeeded.
    /// Bind menu IsEnabled to this property, subscribe to
    /// <see cref="AvailabilityChanged"/> for live updates.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Fires when <see cref="IsAvailable"/> transitions (true→false or
    /// false→true), once per transition. The bool argument is the new
    /// value.
    /// </summary>
    event EventHandler<bool>? AvailabilityChanged;

    /// <summary>
    /// Connect / immediately disconnect to update <see cref="IsAvailable"/>.
    /// Cheap — no payload exchange, just the pipe handshake.
    /// </summary>
    Task<bool> CheckAvailabilityAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Phase 22a — push a primitive memory record (byte/word/dword/qword/
    /// single/double/string/byteArray/binary) into CE's address list.
    /// Used by Phase 21 field write-back exposure.
    /// </summary>
    /// <param name="description">CE entry description.</param>
    /// <param name="address">Address as hex literal or registered symbol name.</param>
    /// <param name="valueType">CE TVariableType.</param>
    /// <param name="isSigned">Signed integer rendering toggle (ignored on non-int types).</param>
    Task<bool> CreateMemoryRecordAsync(string description, string address,
                                       CeValueType valueType, bool isSigned = false,
                                       CancellationToken ct = default);

    /// <summary>
    /// Phase 22a — push one parent AA script with N pointer-chain child
    /// memory records under it. Used for Phase 17 Address Lookup hand-off
    /// ("register the instance pointer + expose each field as a child
    /// dereference").
    /// </summary>
    /// <param name="description">Parent AA script description.</param>
    /// <param name="script">Full parent AA script text (ENABLE/DISABLE).</param>
    /// <param name="records">Child records to append under the parent. Each carries its own description / address / value type / pointer offsets / color.</param>
    /// <param name="autoActivate">When true, CE flips the parent entry's checkbox on after creation.</param>
    /// <param name="hideChildren">When true, parent gets <c>[moHideChildren,moDeactivateChildrenAsWell]</c> options — child records collapse under the parent in CE's tree.</param>
    Task<bool> CreateAAScriptWithRecordsAsync(string description, string script,
                                              IReadOnlyList<AaScriptChildRecord> records,
                                              bool autoActivate = false,
                                              bool hideChildren = true,
                                              CancellationToken ct = default);

    /// <summary>
    /// Phase 22a — embed a UTF-8 file into the currently-open CE table's
    /// <c>&lt;Files&gt;</c> section. The CE plugin internally runs
    /// <c>findTableFile</c> (delete-if-exists) → <c>createTableFile</c> →
    /// <c>Stream.copyFrom(createStringStream(content))</c> → <c>Stream.Size</c>
    /// verification. No Ascii85 — content is raw UTF-8 over the pipe.
    /// </summary>
    /// <param name="fileName">Filename inside the .CT (case-sensitive — used by <c>findTableFile</c> from AA scripts).</param>
    /// <param name="content">Raw UTF-8 file content. Long-bracket level chosen automatically by the CE plugin so any payload is safe.</param>
    /// <returns>Tuple of (Ok, ErrorMessage). On success Ok=true and ErrorMessage=null. On failure Ok=false and ErrorMessage carries the plugin-side reason (e.g. "Stream size mismatch: …") or null on a UI-side connect/timeout — surface the message in status text so users can tell "wrong CE state" apart from "real plugin bug".</returns>
    Task<(bool Ok, string? ErrorMessage)> InjectTableFileAsync(string fileName, string content,
                                                               CancellationToken ct = default);
}

public sealed class AobMakerBridge : IAobMakerBridge, IDisposable
{
    private const string PipeName             = "AOBMakerCEBridge";
    private const int    ConnectTimeoutMs     = 1500;
    private const int    ResponseTimeoutMs    = 5000;
    // InjectTableFile ships up to ~150 KB and runs CE Lua via synchronize()
    // + a Stream.Size verification on the way out. The 5 s navigation
    // deadline is too tight for that — give it a roomier window so a
    // momentary CE pause doesn't read as a spurious failure.
    private const int    InjectResponseTimeoutMs = 15000;
    private const uint   MaxMessageSize       = 10u * 1024u * 1024u;

    private const string TypeNavigateHexView           = "NavigateHexView";
    private const string TypeNavigateDisassembler      = "NavigateDisassembler";
    private const string TypeCreateAAScript            = "CreateAAScript";
    private const string TypeCreateMemoryRecord        = "CreateMemoryRecord";
    private const string TypeCreateAAScriptWithRecords = "CreateAAScriptWithRecords";
    private const string TypeInjectTableFile           = "InjectTableFile";

    private readonly FileLogger? _logger;
    /// <summary>Serialises every connect → write → read → cleanup cycle.
    /// <see cref="_pipe"/> is a single shared field, so two overlapping calls
    /// would have the second ReconnectAsync overwrite the first's live stream
    /// (leaking it) and then both would read/write the SAME pipe — interleaved
    /// frames, or an ObjectDisposedException when one call's CleanupPipe fires
    /// under the other. Reachable from the UI: Copy/Push aobscan, the two
    /// Navigate commands and the availability probe are independent commands
    /// with no mutual exclusion of their own. Mirrors PipeClient's semaphore.
    /// Non-reentrant, so it is taken at exactly two places — ExchangeAsync and
    /// CheckAvailabilityAsync — which every public entry point funnels through
    /// without nesting.</summary>
    private readonly SemaphoreSlim _pipeGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private bool _disposed;
    private bool _isAvailable;
    private readonly string _pipeNameOverride;

    public bool IsAvailable => _isAvailable;
    public event EventHandler<bool>? AvailabilityChanged;

    public AobMakerBridge(FileLogger? logger = null) : this(logger, PipeName) { }

    /// <summary>
    /// Test-only ctor that lets the test rig spin up a fake server on a
    /// per-test pipe name (matches <c>PipeClientLifecycleTests</c>'s
    /// pattern of one-server-per-test on a unique GUID-suffixed pipe).
    /// </summary>
    internal AobMakerBridge(FileLogger? logger, string pipeNameOverride)
    {
        _logger = logger;
        _pipeNameOverride = pipeNameOverride;
    }

    // ---------- Public API ----------------------------------------------

    public async Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        // Under the same gate as ExchangeAsync: this probe both REPLACES _pipe
        // (ReconnectAsync) and DISPOSES it (CleanupPipe), so running it beside a
        // real exchange would pull the stream out from under it. The ↻ button
        // and the automatic availability refresh are exactly that scenario.
        await _pipeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool ok = await ReconnectAsync(ct).ConfigureAwait(false);
            if (ok)
            {
                CleanupPipe(); // probe only — release immediately
                SetAvailable(true);
                Log("AobMaker CheckAvailability: ok");
                return true;
            }
            SetAvailable(false);
            Log("AobMaker CheckAvailability: pipe unavailable");
            return false;
        }
        finally
        {
            _pipeGate.Release();
        }
    }

    public Task<bool> NavigateHexViewAsync(string hexAddress, CancellationToken ct = default)
        => SendAddressOnlyAsync(TypeNavigateHexView, StripHexPrefix(hexAddress), ct);

    public Task<bool> NavigateDisassemblerAsync(string hexAddress, CancellationToken ct = default)
        => SendAddressOnlyAsync(TypeNavigateDisassembler, StripHexPrefix(hexAddress), ct);

    public async Task<bool> CreateAAScriptAsync(string description, string script,
                                                bool autoActivate = false,
                                                CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(script))
        {
            Log($"AobMaker {TypeCreateAAScript}: empty script, skipping");
            return false;
        }
        var request = new AobMakerMessage
        {
            Type         = TypeCreateAAScript,
            Description  = description,
            Script       = script,
            AutoActivate = autoActivate,
        };
        return await ExchangeAsync(request, ResponseTimeoutMs,
            $"{TypeCreateAAScript} → '{description}' (scriptLen={script.Length}, autoActivate={autoActivate})",
            ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateMemoryRecordAsync(string description, string address,
                                                    CeValueType valueType, bool isSigned = false,
                                                    CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(address))
        {
            Log($"AobMaker {TypeCreateMemoryRecord}: empty address, skipping");
            return false;
        }
        var request = new AobMakerMessage
        {
            Type        = TypeCreateMemoryRecord,
            Description = description,
            Address     = address,
            ValueType   = (int)valueType,
            IsSigned    = isSigned,
        };
        return await ExchangeAsync(request, ResponseTimeoutMs,
            $"{TypeCreateMemoryRecord} → '{description}' @ {address} type={valueType} signed={isSigned}",
            ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateAAScriptWithRecordsAsync(string description, string script,
                                                           IReadOnlyList<AaScriptChildRecord> records,
                                                           bool autoActivate = false,
                                                           bool hideChildren = true,
                                                           CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(script))
        {
            Log($"AobMaker {TypeCreateAAScriptWithRecords}: empty script, skipping");
            return false;
        }
        var request = new AobMakerMessage
        {
            Type         = TypeCreateAAScriptWithRecords,
            Description  = description,
            Script       = script,
            AutoActivate = autoActivate,
            HideChildren = hideChildren,
            Records      = MapRecords(records),
        };
        return await ExchangeAsync(request, ResponseTimeoutMs,
            $"{TypeCreateAAScriptWithRecords} → '{description}' (scriptLen={script.Length}, records={records?.Count ?? 0})",
            ct).ConfigureAwait(false);
    }

    public async Task<(bool Ok, string? ErrorMessage)> InjectTableFileAsync(string fileName, string content,
                                                                            CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentException("fileName must not be empty", nameof(fileName));
        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("content must not be empty", nameof(content));

        if (!await ReconnectAsync(ct).ConfigureAwait(false))
        {
            SetAvailable(false);
            Log($"AobMaker {TypeInjectTableFile} → '{fileName}': pipe unavailable");
            return (false, null);
        }

        try
        {
            var request = new AobMakerMessage
            {
                Type     = TypeInjectTableFile,
                FileName = fileName,
                Content  = content,
            };
            await WriteMessageAsync(_pipe!, request, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(InjectResponseTimeoutMs);
            AobMakerMessage? response = await ReadMessageAsync(_pipe!, timeoutCts.Token).ConfigureAwait(false);
            CleanupPipe();

            if (response == null || !response.Success)
            {
                string msg = response?.Message ?? "no response";
                Log($"AobMaker {TypeInjectTableFile} → '{fileName}': failure ({msg})");
                // CE plugin reachable but command failed — keep IsAvailable=true.
                if (response != null) SetAvailable(true);
                return (false, response?.Message);
            }
            SetAvailable(true);
            Log($"AobMaker {TypeInjectTableFile} → '{fileName}': ok ({content.Length:N0} chars)");
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            Log($"AobMaker {TypeInjectTableFile} → '{fileName}': timed out");
            CleanupPipe();
            return (false, "timed out waiting for CE Plugin response");
        }
        catch (Exception ex)
        {
            Log($"AobMaker {TypeInjectTableFile} → '{fileName}': {ex.GetType().Name}: {ex.Message}");
            SetAvailable(false);
            CleanupPipe();
            return (false, ex.Message);
        }
    }

    // ---------- Internals -----------------------------------------------

    private async Task<bool> SendAddressOnlyAsync(string type, string hexAddress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(hexAddress))
        {
            Log($"AobMaker {type}: empty address, skipping");
            return false;
        }
        var request = new AobMakerMessage { Type = type, Address = hexAddress };
        return await ExchangeAsync(request, ResponseTimeoutMs,
            $"{type} → {hexAddress}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Connect → write request → read response → cleanup. Updates
    /// <see cref="IsAvailable"/> and logs the outcome. Returns
    /// <c>response.Success</c>, or false on any failure path.
    /// </summary>
    private async Task<bool> ExchangeAsync(AobMakerMessage request, int timeoutMs,
                                           string logTag, CancellationToken ct)
    {
        await _pipeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        if (!await ReconnectAsync(ct).ConfigureAwait(false))
        {
            SetAvailable(false);
            Log($"AobMaker {logTag}: pipe unavailable");
            return false;
        }
        try
        {
            await WriteMessageAsync(_pipe!, request, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            AobMakerMessage? response = await ReadMessageAsync(_pipe!, timeoutCts.Token).ConfigureAwait(false);
            CleanupPipe();

            if (response == null || !response.Success)
            {
                Log($"AobMaker {logTag}: failure ({response?.Message ?? "no response"})");
                if (response != null) SetAvailable(true); // plugin replied, command failed
                return false;
            }
            SetAvailable(true);
            Log($"AobMaker {logTag}: ok");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log($"AobMaker {logTag}: timed out");
            CleanupPipe();
            return false;
        }
        catch (Exception ex)
        {
            Log($"AobMaker {logTag}: {ex.GetType().Name}: {ex.Message}");
            SetAvailable(false);
            CleanupPipe();
            return false;
        }
        }
        finally
        {
            _pipeGate.Release();
        }
    }

    private async Task<bool> ReconnectAsync(CancellationToken ct)
    {
        try
        {
            CleanupPipe();
            _pipe = new NamedPipeClientStream(".", _pipeNameOverride,
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

    private static List<AaScriptChildRecordWire>? MapRecords(IReadOnlyList<AaScriptChildRecord>? records)
    {
        if (records == null || records.Count == 0) return null;
        var list = new List<AaScriptChildRecordWire>(records.Count);
        foreach (var r in records)
        {
            list.Add(new AaScriptChildRecordWire
            {
                Description = r.Description,
                Address     = r.Address,
                ValueType   = (int)r.ValueType,
                IsSigned    = r.IsSigned,
                Offsets     = r.Offsets is null ? null : new List<int>(r.Offsets),
                Color       = r.Color,
            });
        }
        return list;
    }

    private static string StripHexPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
    }

    private void Log(string line) => _logger?.Write(Constants.LogCategoryView, line);

    private void SetAvailable(bool value)
    {
        if (_isAvailable == value) return;
        _isAvailable = value;
        AvailabilityChanged?.Invoke(this, value);
    }

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
        _pipeGate.Dispose();
    }
}

/// <summary>
/// Wire model — every field optional except <c>type</c>. The CE Plugin
/// silently ignores extras, so the same message class serves all six
/// command types we send.
/// </summary>
internal sealed class AobMakerMessage
{
    [JsonPropertyName("type")]    public string  Type    { get; set; } = string.Empty;

    // Navigate*
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    // Response envelope
    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool    Success { get; set; }
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    // CreateAAScript / CreateAAScriptWithRecords / CreateMemoryRecord shared
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("script")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Script { get; set; }
    [JsonPropertyName("autoActivate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool    AutoActivate { get; set; }

    // CreateMemoryRecord-specific (ValueType also reused as per-record type
    // in CreateAAScriptWithRecords' nested array element). Default 0 = Byte
    // is a legitimate value, so we cannot use WhenWritingDefault — use
    // a nullable int instead and let the source-gen omit when null.
    [JsonPropertyName("valueType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int?    ValueType { get; set; }
    [JsonPropertyName("isSigned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool    IsSigned { get; set; }

    // CreateAAScriptWithRecords-specific
    [JsonPropertyName("hideChildren")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool    HideChildren { get; set; }
    [JsonPropertyName("records")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AaScriptChildRecordWire>? Records { get; set; }

    // InjectTableFile
    [JsonPropertyName("fileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; set; }
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }
}

/// <summary>
/// Per-record element of the <c>records[]</c> array sent with
/// <c>CreateAAScriptWithRecords</c>. Wire-only mirror of the public
/// <see cref="AaScriptChildRecord"/> — split so the public API can use
/// the strongly-typed <see cref="CeValueType"/> enum and an
/// <see cref="IReadOnlyList{T}"/> contract while the wire layer stays
/// tied to int + List for source-gen ergonomics.
/// </summary>
internal sealed class AaScriptChildRecordWire
{
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("address")]     public string Address     { get; set; } = string.Empty;
    [JsonPropertyName("valueType")]   public int    ValueType   { get; set; }
    [JsonPropertyName("isSigned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool   IsSigned { get; set; }
    [JsonPropertyName("offsets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Offsets { get; set; }
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }
}

[JsonSerializable(typeof(AobMakerMessage))]
[JsonSerializable(typeof(AaScriptChildRecordWire))]
[JsonSerializable(typeof(List<AaScriptChildRecordWire>))]
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
