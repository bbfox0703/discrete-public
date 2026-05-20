namespace UnityDumpUI.Core.Services;

/// <summary>
/// Shared sentinel + slot constants for IntegrityGuardian. Sentinel is a
/// 16-byte random marker that inject-hash.ps1 searches for after AOT
/// publish; the 32 bytes immediately following are the SHA256 hash slot
/// (0xCC during development, real hash after publish). Kept in a separate
/// file so the byte arrays don't end up adjacent to the guardian's own
/// padding in .rdata — small layout obstacle for someone diffing the PE.
/// </summary>
internal static class IntegrityCore
{
    internal const int SentinelLength = 16;
    internal const int HashLength = 32;
    internal const int SlotLength = SentinelLength + HashLength; // 48
    internal const byte DevModeFill = 0xCC;

    /// <summary>Sentinel marker — distinct from the AOBMaker A/B/C sentinels.</summary>
    internal static readonly byte[] Sentinel =
    [
        0x9D, 0x4F, 0xC2, 0x18, 0x73, 0xAE, 0x36, 0xB1,
        0xED, 0x5A, 0x82, 0x07, 0xD4, 0x69, 0xF5, 0x2C
    ];

    // .rdata layout filler — keeps the sentinel from sitting next to the
    // guardian's _pad block, so a hex editor diff against an unpatched
    // copy can't trivially home in on "the 48 bytes that changed".
    private static readonly byte[] _gap =
    [
        0x6B, 0xC4, 0x37, 0x9F, 0x52, 0xE1, 0x08, 0xBD, 0x46, 0x88, 0x21, 0xDA, 0x73, 0x95, 0x2E, 0xCC,
        0x14, 0xA7, 0x58, 0xEC, 0x39, 0xB2, 0x65, 0x80, 0x1B, 0xF7, 0x42, 0x9D, 0x6E, 0xC0, 0x53, 0x86,
        0xAE, 0x07, 0xD9, 0x32, 0xFB, 0x64, 0x91, 0x28, 0xBF, 0x4A, 0x77, 0xE0, 0x15, 0xCD, 0x58, 0x83
    ];

    /// <summary>
    /// Zero out the 32-byte hash slot in <paramref name="binary"/>. Caller
    /// invokes this with the slot's stored hash bytes already extracted —
    /// the binary is then re-hashed to verify integrity.
    /// </summary>
    internal static void ZeroHashSlot(byte[] binary, byte[] storedHash)
    {
        int pos = FindSlot(binary, Sentinel, storedHash);
        if (pos < 0) return;
        for (int i = 0; i < HashLength; i++)
            binary[pos + SentinelLength + i] = 0x00;
    }

    /// <summary>Locate a 48-byte sentinel+hash slot in the binary.</summary>
    internal static int FindSlot(byte[] binary, byte[] sentinel, byte[] storedHash)
    {
        for (int i = 0; i <= binary.Length - SlotLength; i++)
        {
            bool match = true;
            for (int j = 0; j < SentinelLength; j++)
            {
                if (binary[i + j] != sentinel[j]) { match = false; break; }
            }
            if (!match) continue;

            bool hashMatch = true;
            for (int j = 0; j < HashLength; j++)
            {
                if (binary[i + SentinelLength + j] != storedHash[j]) { hashMatch = false; break; }
            }
            if (hashMatch) return i;
        }
        return -1;
    }

    /// <summary>
    /// True when the slot is still filled with 0xCC — meaning this build
    /// hasn't been through inject-hash.ps1 yet (Debug / Release iteration).
    /// </summary>
    internal static bool IsDevMode(byte[] hashSlot)
    {
        // anti-trim: prevents the linker from dropping _gap (it has no
        // observable side effect otherwise).
        _ = _gap[0];

        for (int i = SentinelLength; i < SlotLength; i++)
        {
            if (hashSlot[i] != DevModeFill) return false;
        }
        return true;
    }
}
