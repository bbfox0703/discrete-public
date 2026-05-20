using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Single-slot integrity guardian. Computes SHA256 of the current process
/// binary (with the hash slot temporarily zeroed) and compares against the
/// hash that inject-hash.ps1 wrote at publish time. On mismatch
/// <see cref="GetIntegrityFactor"/> returns a non-zero int — call sites XOR
/// that factor into a load-bearing constant so the program keeps running but
/// produces subtly wrong output. Dev builds (slot still 0xCC) always return 0.
///
/// Two evasion behaviours:
///  - <c>DelayThreshold</c>: the first N calls after the factor is computed
///    return 0 even when tampered. A breakpoint on this method will see
///    "everything's fine" on first hit; the real factor leaks out only on
///    the (N+1)th call from any consumer.
///  - <c>Debugger.IsAttached</c>: published binary + attached debugger
///    returns its own poison factor. Stepping through dnSpy / WinDbg triggers
///    the same downstream corruption as a hex-edit would.
/// </summary>
public static class IntegrityGuardian
{
    // .rdata padding — fills space around the HashSlot so a binary diff
    // doesn't immediately spotlight the 48-byte sentinel block.
    private static readonly byte[] _pad =
    [
        0x4C, 0xA7, 0x19, 0xE3, 0x82, 0x5F, 0xD4, 0x6B, 0x30, 0x98, 0xAF, 0x71, 0xC6, 0x0D, 0x53, 0xB8,
        0x27, 0xF5, 0x6A, 0x8E, 0x14, 0xDB, 0x40, 0x9C, 0x73, 0x05, 0xEE, 0xB1, 0x3A, 0xC2, 0x67, 0x50,
        0xD9, 0x2E, 0x84, 0xFB, 0x16, 0xA0, 0x45, 0x7D, 0xE2, 0x39, 0x8C, 0x01, 0xCA, 0x5B, 0xB6, 0x63,
        0x1F, 0xA4, 0x78, 0xD0, 0x4E, 0x97, 0x22, 0xF8, 0x6D, 0x0B, 0xBE, 0x55, 0xE9, 0x34, 0x83, 0xAC,
        0x47, 0x1A, 0xDE, 0x60, 0x95, 0x3C, 0xF1, 0x79, 0x06, 0xBA, 0x52, 0xCB, 0x2D, 0xE7, 0x88, 0x4A,
        0x13, 0xFD, 0x6E, 0xA5, 0x31, 0xD6, 0x49, 0x9F, 0x7B, 0x0E, 0xC4, 0x58, 0xB3, 0x26, 0xEA, 0x64
    ];

    /// <summary>
    /// 16-byte sentinel followed by a 32-byte SHA256 placeholder. The bytes
    /// here MUST match <see cref="IntegrityCore.Sentinel"/>; inject-hash.ps1
    /// finds this 48-byte block by scanning for the sentinel + 0xCC tail
    /// and rewrites the trailing 32 bytes with the binary's real hash.
    /// </summary>
    private static readonly byte[] HashSlot =
    [
        0x9D, 0x4F, 0xC2, 0x18, 0x73, 0xAE, 0x36, 0xB1,
        0xED, 0x5A, 0x82, 0x07, 0xD4, 0x69, 0xF5, 0x2C,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC
    ];

    private static int? _cachedFactor;
    private static readonly object _lock = new();
    private static int _invocationCount;
    private const int DelayThreshold = 3;

    /// <summary>
    /// Returns 0 when the binary is intact (or dev mode). Non-zero factor
    /// means tampered / debugger attached — XOR it into a downstream
    /// constant to corrupt output without a visible "tamper detected"
    /// message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIntegrityFactor()
    {
        if (_cachedFactor.HasValue)
        {
            if (_cachedFactor.Value == 0) return 0;
            return Interlocked.Increment(ref _invocationCount) >= DelayThreshold
                ? _cachedFactor.Value : 0;
        }

        lock (_lock)
        {
            _cachedFactor ??= ComputeFactor();
            if (_cachedFactor.Value == 0) return 0;
            return Interlocked.Increment(ref _invocationCount) >= DelayThreshold
                ? _cachedFactor.Value : 0;
        }
    }

    private static int ComputeFactor()
    {
        if (IntegrityCore.IsDevMode(HashSlot))
        {
            _ = _pad[0]; // anti-trim
            return 0;
        }

        if (System.Diagnostics.Debugger.IsAttached)
            return 0x44434701; // "DCG\x01" — published + debugger attached

        var storedHash = new byte[IntegrityCore.HashLength];
        Array.Copy(HashSlot, IntegrityCore.SentinelLength, storedHash, 0, IntegrityCore.HashLength);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return 0;

        try
        {
            var binary = File.ReadAllBytes(exePath);
            int slotPos = IntegrityCore.FindSlot(binary, IntegrityCore.Sentinel, storedHash);
            if (slotPos < 0) return 0;

            IntegrityCore.ZeroHashSlot(binary, storedHash);
            var computedHash = SHA256.HashData(binary);

            for (int i = 0; i < IntegrityCore.HashLength; i++)
            {
                if (storedHash[i] != computedHash[i])
                {
                    int factor = BitConverter.ToInt32(computedHash, 0)
                               ^ BitConverter.ToInt32(storedHash, 0);
                    return factor == 0 ? 1 : factor;
                }
            }
            return 0;
        }
        catch
        {
            // I/O failure (locked exe, AV interference, etc.) — fail open
            // so we don't lock legitimate users out.
            return 0;
        }
    }
}
