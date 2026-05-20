using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Build-artifact integrity token (slot A). Self-checks the current
/// process binary against the SHA256 that the publish step wrote into
/// its private hash slot. <see cref="Read"/> returns 0 on intact / dev
/// builds and a non-zero "factor" on tampered binaries; downstream
/// consumers XOR that factor into a load-bearing constant so output
/// degrades silently instead of throwing a visible "tamper detected"
/// banner.
/// </summary>
public static class BuildArtifactToken
{
    // .rdata padding so the slot doesn't sit naked between unrelated data.
    private static readonly byte[] _pad =
    [
        0x4C, 0xA7, 0x19, 0xE3, 0x82, 0x5F, 0xD4, 0x6B, 0x30, 0x98, 0xAF, 0x71, 0xC6, 0x0D, 0x53, 0xB8,
        0x27, 0xF5, 0x6A, 0x8E, 0x14, 0xDB, 0x40, 0x9C, 0x73, 0x05, 0xEE, 0xB1, 0x3A, 0xC2, 0x67, 0x50,
        0xD9, 0x2E, 0x84, 0xFB, 0x16, 0xA0, 0x45, 0x7D, 0xE2, 0x39, 0x8C, 0x01, 0xCA, 0x5B, 0xB6, 0x63,
        0x1F, 0xA4, 0x78, 0xD0, 0x4E, 0x97, 0x22, 0xF8, 0x6D, 0x0B, 0xBE, 0x55, 0xE9, 0x34, 0x83, 0xAC,
        0x47, 0x1A, 0xDE, 0x60, 0x95, 0x3C, 0xF1, 0x79, 0x06, 0xBA, 0x52, 0xCB, 0x2D, 0xE7, 0x88, 0x4A,
        0x13, 0xFD, 0x6E, 0xA5, 0x31, 0xD6, 0x49, 0x9F, 0x7B, 0x0E, 0xC4, 0x58, 0xB3, 0x26, 0xEA, 0x64
    ];

    /// <summary>Sentinel A + 32-byte SHA256 placeholder (0xCC during dev).</summary>
    private static readonly byte[] HashSlot =
    [
        0x9D, 0x4F, 0xC2, 0x18, 0x73, 0xAE, 0x36, 0xB1,
        0xED, 0x5A, 0x82, 0x07, 0xD4, 0x69, 0xF5, 0x2C,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC
    ];

    private static int? _cached;
    private static readonly object _lock = new();
    private static int _calls;
    private const int DelayThreshold = 3;

    /// <summary>
    /// 0 = clean, non-zero = tampered. First <c>DelayThreshold</c> calls
    /// after computation return 0 even when tampered, so a breakpoint
    /// trace on this method observes "fine" and gives up.
    /// </summary>
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
            return 0x44434701;

        var storedHash = new byte[SchemaConstants.HashLength];
        Array.Copy(HashSlot, SchemaConstants.SentinelLength, storedHash, 0, SchemaConstants.HashLength);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return 0;

        try
        {
            var binary = File.ReadAllBytes(exePath);
            int slotPos = SchemaConstants.FindSlot(binary, SchemaConstants.SentinelA, storedHash);
            if (slotPos < 0) return 0;

            // Zero ALL three slots so the SHA256 cancels out the random
            // hash bytes that publish wrote into slots B + C.
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
