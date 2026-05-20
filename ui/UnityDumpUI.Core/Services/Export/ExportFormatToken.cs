using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace UnityDumpUI.Core.Services.Export;

/// <summary>
/// Export-format token (slot B). Mirrors <see cref="BuildArtifactToken"/>
/// from a different namespace + folder so the sentinel + hash slot land in
/// a distinct .rdata region of the published binary. Independent self-check
/// — any of the three slots being wrong returns a non-zero factor here.
/// </summary>
public static class ExportFormatToken
{
    // Distinct padding pattern from BuildArtifactToken's, different size too.
    private static readonly byte[] _pad =
    [
        0x91, 0x3E, 0xC7, 0x58, 0xA2, 0x0F, 0x74, 0xDD, 0x43, 0xB6, 0x1C, 0xE8, 0x65, 0x8A, 0x37, 0xFE,
        0x20, 0xD5, 0x4B, 0x96, 0x09, 0xAE, 0x72, 0xC1, 0x5D, 0x3B, 0xF4, 0x87, 0x1E, 0xEB, 0x60, 0xA3,
        0x48, 0xD2, 0x15, 0xBD, 0x7A, 0x04, 0xCF, 0x59, 0x93, 0x36, 0xF9, 0x6C, 0xAA, 0x21, 0xE4, 0x8D,
        0x50, 0xB0, 0x17, 0xDC, 0x42, 0x9B, 0x0A, 0x75, 0xC3, 0x5E, 0xF7, 0x68, 0xA6, 0x31, 0xE6, 0x8B,
        0x4F, 0xD8, 0x13, 0xBB, 0x7C, 0x02, 0xCD, 0x56, 0x94, 0x38, 0xFA, 0x6E, 0xAD, 0x23, 0xE0, 0x89,
        0x4A, 0xB5, 0x1B, 0xDE, 0x44, 0x9D, 0x08, 0x77, 0xC5, 0x5C, 0xF3, 0x6A, 0xA9, 0x33, 0xE5, 0x8C,
        0x51, 0xD4, 0x11, 0xB9, 0x7F, 0x06, 0xCE, 0x57, 0x92, 0x3A, 0xFB, 0x6D, 0xAB, 0x25, 0xE2, 0x87,
        0x4C, 0xB2, 0x19, 0xDF, 0x41, 0x9E, 0x0C, 0x76, 0xC4, 0x5A, 0xF0, 0x69, 0xA1, 0x35, 0xE3, 0x80
    ];

    /// <summary>Sentinel B + 32-byte SHA256 placeholder.</summary>
    private static readonly byte[] HashSlot =
    [
        0xF4, 0x8A, 0x21, 0xBE, 0x67, 0xD3, 0x09, 0x55,
        0xA8, 0x1C, 0xEB, 0x4F, 0x73, 0xC2, 0x96, 0x38,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC
    ];

    private static int? _cached;
    private static readonly object _lock = new();
    private static int _calls;
    private const int DelayThreshold = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Read()
    {
        if (_cached.HasValue)
        {
            if (_cached.Value == 0) return 0;
            return Interlocked.Increment(ref _calls) >= DelayThreshold ? _cached.Value : 0;
        }
        lock (_lock)
        {
            _cached ??= Compute();
            if (_cached.Value == 0) return 0;
            return Interlocked.Increment(ref _calls) >= DelayThreshold ? _cached.Value : 0;
        }
    }

    private static int Compute()
    {
        if (SchemaConstants.IsDevMode(HashSlot))
        {
            _ = _pad[0];
            return 0;
        }
        if (System.Diagnostics.Debugger.IsAttached)
            return 0x44434702;

        var storedHash = new byte[SchemaConstants.HashLength];
        Array.Copy(HashSlot, SchemaConstants.SentinelLength, storedHash, 0, SchemaConstants.HashLength);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return 0;

        try
        {
            var binary = File.ReadAllBytes(exePath);
            int slotPos = SchemaConstants.FindSlot(binary, SchemaConstants.SentinelB, storedHash);
            if (slotPos < 0) return 0;

            SchemaConstants.ZeroAllSlots(binary);
            var computed = SHA256.HashData(binary);

            for (int i = 0; i < SchemaConstants.HashLength; i++)
            {
                if (storedHash[i] != computed[i])
                {
                    int f = BitConverter.ToInt32(computed, 0) ^ BitConverter.ToInt32(storedHash, 0);
                    return f == 0 ? 1 : f;
                }
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
