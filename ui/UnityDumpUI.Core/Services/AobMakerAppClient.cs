// AobMakerAppClient.cs — named-pipe client for the AOBMaker *standalone app*.
//
// Distinct from AobMakerBridge (which talks to the AOBMaker *CE plugin* on
// \\.\pipe\AOBMakerCEBridge for pushing AA scripts into CE). The standalone
// AOBMaker.exe listens on \\.\pipe\AOBMaker and exposes a single command we
// use here:
//
//   GenerateAob — module + address → a UNIQUE AOB (Mode D). The app attaches
//                 to the target game (usermode ReadProcessMemory), seeds Iced
//                 on the requested instruction, and expands the pattern until
//                 it matches exactly once in the module. Takes a few seconds.
//
// Spec: AOBMaker/docs/GenerateAob-Pipe-Command.md. Wire format is IDENTICAL to
// AobMakerBridge:
//   * 4-byte little-endian uint32 length prefix
//   * UTF-8 JSON payload, camelCase keys
//   * single request/response per connection (server is single-instance, so
//     a Mode-D scan in flight makes concurrent callers queue — expected)
//
// IsAvailable lifecycle / AOT requirements mirror AobMakerBridge: System.IO.Pipes
// is AOT-safe and serialization rides the [JsonSerializable] source-gen context
// below — no reflection.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Result of a <see cref="IAobMakerAppClient.GenerateAobAsync"/> call. Mirrors
/// the <c>GenerateAobResult</c> response documented in AOBMaker's pipe spec.
/// </summary>
public sealed class GenerateAobResult
{
    /// <summary>True when the app found a unique AOB.</summary>
    public bool    Success { get; init; }

    /// <summary>The AOB pattern (space-separated hex bytes / <c>??</c> wildcards). Null on failure.</summary>
    public string? Aob { get; init; }

    /// <summary>
    /// Byte offset from the AOB start to the requested address: Mode D may
    /// extend the pattern backwards to cover the function prologue, so the
    /// requested (method-entry) address can sit inside the pattern. Recover it
    /// as <c>matchAddress + InjectionOffset</c>.
    /// </summary>
    public int     InjectionOffset { get; init; }

    /// <summary>Number of module hits for the pattern (<c>1</c> = unique).</summary>
    public int     MatchCount { get; init; }

    /// <summary>Resolved module name (e.g. <c>GameAssembly.dll</c>).</summary>
    public string? Module { get; init; }

    /// <summary>Human-readable message / error reason.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Convenience factory for a UI-side failure (pipe down / timeout) where
    /// the app never replied — keeps the call sites tidy.
    /// </summary>
    public static GenerateAobResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>
/// Client for the AOBMaker standalone app's <c>GenerateAob</c> pipe command.
/// Fail-soft: probe / generate return availability=false / a failure result on
/// connect / IO / timeout rather than throwing — the app is an OPTIONAL
/// enhancement (the DLL method-prologue pattern is the fallback).
/// </summary>
public interface IAobMakerAppClient
{
    /// <summary>Cached availability — true if the most recent pipe op succeeded.</summary>
    bool IsAvailable { get; }

    /// <summary>Fires once per <see cref="IsAvailable"/> transition; arg is the new value.</summary>
    event EventHandler<bool>? AvailabilityChanged;

    /// <summary>Connect / immediately disconnect to refresh <see cref="IsAvailable"/>.</summary>
    Task<bool> CheckAvailabilityAsync(CancellationToken ct = default);

    /// <summary>
    /// Ask the app for a unique AOB at <paramref name="module"/> +
    /// <paramref name="address"/>. Provide <paramref name="processId"/> (or
    /// <paramref name="processName"/>) so the app attaches to the right game;
    /// when both are null the app falls back to CE's currently-attached process.
    /// </summary>
    Task<GenerateAobResult> GenerateAobAsync(string module, string address,
                                             uint? processId = null,
                                             string? processName = null,
                                             CancellationToken ct = default);
}

public sealed class AobMakerAppClient : IAobMakerAppClient, IDisposable
{
    private const string PipeName          = "AOBMaker";
    private const int    ConnectTimeoutMs  = 1500;
    // Mode D expands ± around the seed instruction scanning the whole module
    // each step — comfortably multi-second on a large GameAssembly.dll. Give
    // the response a roomy deadline so a real scan isn't read as a timeout.
    private const int    GenerateTimeoutMs = 30000;
    private const uint   MaxMessageSize    = 10u * 1024u * 1024u;

    private const string TypeGenerateAob = "GenerateAob";

    private readonly FileLogger? _logger;
    private readonly string _pipeNameOverride;
    private NamedPipeClientStream? _pipe;
    private bool _disposed;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable;
    public event EventHandler<bool>? AvailabilityChanged;

    public AobMakerAppClient(FileLogger? logger = null) : this(logger, PipeName) { }

    /// <summary>Test-only ctor — per-test pipe name (matches AobMakerBridge's pattern).</summary>
    internal AobMakerAppClient(FileLogger? logger, string pipeNameOverride)
    {
        _logger = logger;
        _pipeNameOverride = pipeNameOverride;
    }

    // ---------- Public API ----------------------------------------------

    public async Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        bool ok = await ReconnectAsync(ct).ConfigureAwait(false);
        if (ok)
        {
            CleanupPipe(); // probe only — release immediately
            SetAvailable(true);
            Log("AobMakerApp CheckAvailability: ok");
            return true;
        }
        SetAvailable(false);
        Log("AobMakerApp CheckAvailability: pipe unavailable");
        return false;
    }

    public async Task<GenerateAobResult> GenerateAobAsync(string module, string address,
                                                          uint? processId = null,
                                                          string? processName = null,
                                                          CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(address))
            return GenerateAobResult.Fail("address must not be empty");

        string logTag = $"{TypeGenerateAob} → {module}!{address} pid={processId?.ToString() ?? "-"}";

        if (!await ReconnectAsync(ct).ConfigureAwait(false))
        {
            SetAvailable(false);
            Log($"AobMakerApp {logTag}: pipe unavailable");
            return GenerateAobResult.Fail("AOBMaker app not running (\\\\.\\pipe\\AOBMaker)");
        }

        try
        {
            var request = new AobMakerAppMessage
            {
                Type        = TypeGenerateAob,
                Module      = string.IsNullOrEmpty(module) ? null : module,
                Address     = address,
                ProcessId   = processId,
                ProcessName = string.IsNullOrEmpty(processName) ? null : processName,
            };
            await WriteMessageAsync(_pipe!, request, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(GenerateTimeoutMs);
            AobMakerAppMessage? response = await ReadMessageAsync(_pipe!, timeoutCts.Token).ConfigureAwait(false);
            CleanupPipe();

            if (response == null)
            {
                Log($"AobMakerApp {logTag}: no response");
                SetAvailable(false);
                return GenerateAobResult.Fail("no response from AOBMaker app");
            }

            // App replied — pipe is alive even if the command failed.
            SetAvailable(true);
            if (!response.Success)
            {
                Log($"AobMakerApp {logTag}: failure ({response.Message ?? "unknown"})");
                return new GenerateAobResult
                {
                    Success = false,
                    Module  = response.Module ?? module,
                    Message = response.Message ?? "AOBMaker app could not generate a unique AOB",
                };
            }

            Log($"AobMakerApp {logTag}: ok (matchCount={response.MatchCount}, injectionOffset={response.InjectionOffset})");
            return new GenerateAobResult
            {
                Success         = true,
                Aob             = response.Aob,
                InjectionOffset = response.InjectionOffset,
                MatchCount      = response.MatchCount,
                Module          = response.Module ?? module,
                Message         = response.Message,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log($"AobMakerApp {logTag}: cancelled by caller");
            CleanupPipe();
            return GenerateAobResult.Fail("cancelled");
        }
        catch (OperationCanceledException)
        {
            Log($"AobMakerApp {logTag}: timed out");
            CleanupPipe();
            return GenerateAobResult.Fail("timed out waiting for AOBMaker app (Mode D scan)");
        }
        catch (Exception ex)
        {
            Log($"AobMakerApp {logTag}: {ex.GetType().Name}: {ex.Message}");
            SetAvailable(false);
            CleanupPipe();
            return GenerateAobResult.Fail(ex.Message);
        }
    }

    // ---------- Internals -----------------------------------------------

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

    private static async Task WriteMessageAsync(Stream stream, AobMakerAppMessage msg, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(msg, AobMakerAppJsonContext.Default.AobMakerAppMessage);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] lengthBuf = BitConverter.GetBytes((uint)payload.Length);
        await stream.WriteAsync(lengthBuf, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<AobMakerAppMessage?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        byte[] lengthBuf = new byte[4];
        if (await ReadExactAsync(stream, lengthBuf, 4, ct).ConfigureAwait(false) < 4) return null;
        uint length = BitConverter.ToUInt32(lengthBuf, 0);
        if (length == 0 || length > MaxMessageSize) return null;
        byte[] payload = new byte[length];
        if (await ReadExactAsync(stream, payload, (int)length, ct).ConfigureAwait(false) < (int)length)
            return null;
        string json = Encoding.UTF8.GetString(payload);
        return JsonSerializer.Deserialize(json, AobMakerAppJsonContext.Default.AobMakerAppMessage);
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
    }
}

/// <summary>
/// Wire model for the GenerateAob request/response — every field optional
/// except <c>type</c>. The app ignores unknown extras, so one class serves
/// both directions.
/// </summary>
internal sealed class AobMakerAppMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    // ----- request -----
    [JsonPropertyName("module")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Module { get; set; }

    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    [JsonPropertyName("processId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ProcessId { get; set; }

    [JsonPropertyName("processName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessName { get; set; }

    // ----- response -----
    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Success { get; set; }

    [JsonPropertyName("aob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Aob { get; set; }

    [JsonPropertyName("injectionOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int InjectionOffset { get; set; }

    [JsonPropertyName("matchCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MatchCount { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    // pos / aoblen are only present for RIP-relative seeds (symbol-scan helpers).
    // Carried for completeness; not consumed by the AA-script generator yet.
    [JsonPropertyName("pos")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pos { get; set; }

    [JsonPropertyName("aoblen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AobLen { get; set; }
}

[JsonSerializable(typeof(AobMakerAppMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy   = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AobMakerAppJsonContext : JsonSerializerContext
{
}
