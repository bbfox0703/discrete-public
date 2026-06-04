namespace UnityDumpUI.Core.Services;

/// <summary>
/// Build-time constant pool used by various token readers. Holds three
/// sentinel markers (A/B/C) plus search helpers. Each token reader picks
/// one sentinel and treats the 32 bytes that follow as its hash slot —
/// dev builds fill it with 0xCC, the publish step replaces it with a real
/// SHA256 of the binary (with all three slots zeroed first).
/// </summary>
internal static class SchemaConstants
{
    internal const int SentinelLength = 16;
    internal const int HashLength = 32;
    internal const int SlotLength = SentinelLength + HashLength; // 48
    internal const byte DevModeFill = 0xCC;

    /// <summary>Sentinel A — paired with <c>BuildArtifactToken</c> in Core.</summary>
    internal static readonly byte[] SentinelA =
    [
        0x9D, 0x4F, 0xC2, 0x18, 0x73, 0xAE, 0x36, 0xB1,
        0xED, 0x5A, 0x82, 0x07, 0xD4, 0x69, 0xF5, 0x2C
    ];

    /// <summary>Sentinel B — paired with <c>ExportFormatToken</c> in Core/Export.</summary>
    internal static readonly byte[] SentinelB =
    [
        0xF4, 0x8A, 0x21, 0xBE, 0x67, 0xD3, 0x09, 0x55,
        0xA8, 0x1C, 0xEB, 0x4F, 0x73, 0xC2, 0x96, 0x38
    ];

    /// <summary>Sentinel C — paired with <c>WindowsRuntimeMetadata</c> in the UI assembly.</summary>
    internal static readonly byte[] SentinelC =
    [
        0x52, 0xE7, 0xB9, 0x0D, 0xC4, 0x71, 0x36, 0xAF,
        0x82, 0x5B, 0xF9, 0x14, 0x6E, 0xD8, 0x47, 0xA1
    ];

    // .rdata layout filler — keeps the sentinels from sitting next to the
    // token readers' own padding, raising the cost of "diff binary against
    // a clean copy and look for the 48-byte block that changed".
    private static readonly byte[] _gap =
    [
        0x6B, 0xC4, 0x37, 0x9F, 0x52, 0xE1, 0x08, 0xBD, 0x46, 0x88, 0x21, 0xDA, 0x73, 0x95, 0x2E, 0xCC,
        0x14, 0xA7, 0x58, 0xEC, 0x39, 0xB2, 0x65, 0x80, 0x1B, 0xF7, 0x42, 0x9D, 0x6E, 0xC0, 0x53, 0x86,
        0xAE, 0x07, 0xD9, 0x32, 0xFB, 0x64, 0x91, 0x28, 0xBF, 0x4A, 0x77, 0xE0, 0x15, 0xCD, 0x58, 0x83,
        0x29, 0xE5, 0x73, 0xB1, 0x4C, 0xAF, 0x68, 0xD2, 0x14, 0x97, 0x3B, 0xFE, 0x65, 0x80, 0x42, 0xCD
    ];

    /// <summary>
    /// Locate a slot by sentinel + known stored hash (48-byte match).
    /// Used by the owning token reader to confirm its slot survived AOT.
    /// </summary>
    internal static int FindSlot(byte[] binary, byte[] sentinel, byte[] storedHash)
    {
        for (int i = 0; i <= binary.Length - SlotLength; i++)
        {
            bool match = true;
            for (int j = 0; j < SentinelLength; j++)
                if (binary[i + j] != sentinel[j]) { match = false; break; }
            if (!match) continue;

            bool hashMatch = true;
            for (int j = 0; j < HashLength; j++)
                if (binary[i + SentinelLength + j] != storedHash[j]) { hashMatch = false; break; }
            if (hashMatch) return i;
        }
        return -1;
    }

    /// <summary>
    /// Sentinel-only search — locates a slot regardless of its hash content.
    /// Used to extract the OTHER tokens' stored hashes so the verifying
    /// reader can zero them before computing SHA256.
    /// </summary>
    internal static int FindSentinel(byte[] binary, byte[] sentinel)
    {
        for (int i = 0; i <= binary.Length - SlotLength; i++)
        {
            bool match = true;
            for (int j = 0; j < SentinelLength; j++)
                if (binary[i + j] != sentinel[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    internal static byte[] ExtractStoredHash(byte[] binary, byte[] sentinel)
    {
        int pos = FindSentinel(binary, sentinel);
        if (pos < 0) return new byte[HashLength];
        var hash = new byte[HashLength];
        Array.Copy(binary, pos + SentinelLength, hash, 0, HashLength);
        return hash;
    }

    /// <summary>
    /// Zero out the 32-byte hash region of all three slots in
    /// <paramref name="binary"/>. The token readers call this before
    /// computing SHA256 so each guardian's stored hash equals
    /// SHA256(binary-with-all-three-slots-cleared) — i.e. tampering any
    /// of the three slots breaks all three checks.
    /// <para>
    /// Slots are located by <c>sentinel + <paramref name="injectedHash"/></c>
    /// (the 48-byte <see cref="FindSlot"/> match), NOT sentinel-only. The raw
    /// 16-byte sentinels also appear elsewhere in the AOT image (the sentinel
    /// DEFINITION arrays in this class), and a sentinel-only first match can
    /// land on one of those DECOYS instead of the real slot — zeroing the wrong
    /// bytes and leaving the injected hash in place, so the recompute no longer
    /// matches the stamped hash and the integrity check FALSE-POSITIVES on a
    /// clean publish. inject-hash.ps1 dodges this by matching sentinel + 32×0xCC
    /// (the dev fill); at runtime the slots hold the injected hash, so we match
    /// on that. All three slots carry the same injected hash, so the caller's
    /// own stored hash uniquely identifies every real slot.
    /// </para>
    /// </summary>
    internal static void ZeroAllSlots(byte[] binary, byte[] injectedHash)
    {
        ZeroOne(binary, SentinelA, injectedHash);
        ZeroOne(binary, SentinelB, injectedHash);
        ZeroOne(binary, SentinelC, injectedHash);
    }

    private static void ZeroOne(byte[] binary, byte[] sentinel, byte[] injectedHash)
    {
        int pos = FindSlot(binary, sentinel, injectedHash);
        if (pos < 0) return;
        for (int i = 0; i < HashLength; i++)
            binary[pos + SentinelLength + i] = 0x00;
    }

    /// <summary>
    /// True when the slot is still filled with 0xCC — meaning the build
    /// hasn't been through the publish-time hash injector yet.
    /// </summary>
    internal static bool IsDevMode(byte[] hashSlot)
    {
        // anti-trim
        _ = _gap[0];

        for (int i = SentinelLength; i < SlotLength; i++)
            if (hashSlot[i] != DevModeFill) return false;
        return true;
    }
}
