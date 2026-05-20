namespace UnityDumpUI.Core.Services;

/// <summary>
/// Rolling-XOR encoded sensitive strings. Defeats casual hex-editor /
/// strings.exe scans for the author handle + project URL in the published
/// binary. key_i = (byte)(seed + i * step). Pairs with IntegrityGuardian:
/// callers pass GetIntegrityFactor() into GetCopyright; on a tampered
/// binary the factor is non-zero and the decoded string degrades to garbage.
/// </summary>
public static class StringShield
{
    private const byte Seed = 0xA7;
    private const byte Step = 31;

    private static readonly byte[] EncAuthor = [0xC5, 0xA4, 0x83, 0x6B, 0x5B];

    private static readonly byte[] EncUrl =
    [
        0xCF, 0xB2, 0x91, 0x74, 0x50, 0x78, 0x4E, 0xAF,
        0xF0, 0xCE, 0xB8, 0x92, 0x78, 0x52, 0x3C, 0x19,
        0xE3, 0xC2, 0xB4, 0x96, 0x7F, 0x57, 0x22, 0x5E,
        0xEC, 0xC1, 0xA0
    ];

    private static readonly byte[] EncCredit =
    [
        0xC5, 0xA4, 0x83, 0x6B, 0x5B, 0x62, 0x21, 0xA0,
        0xF7, 0xCA, 0xA9, 0x8C, 0x68, 0x00, 0x76, 0x57,
        0xF8, 0xC6, 0xB0, 0x9A, 0x70, 0x5A, 0x34, 0x11,
        0xFB, 0xDA, 0xAC, 0x8E, 0x67, 0x4F, 0x3A, 0x46,
        0xE4, 0xC9, 0xA8
    ];

    public static string GetAuthor() => Decode(EncAuthor, Seed);

    public static string GetUrl() => Decode(EncUrl, Seed);

    /// <summary>
    /// Decode the footer credit. keyOffset==0 returns the canonical
    /// "bbfox @ https://opencheattables.com"; any non-zero offset (i.e.
    /// IntegrityGuardian flagged tampering) decodes to gibberish so the
    /// surface UI text on a patched binary is visibly wrong.
    /// </summary>
    public static string GetCopyright(int keyOffset = 0)
        => Decode(EncCredit, (byte)(Seed ^ (byte)(keyOffset & 0xFF)));

    private static string Decode(byte[] data, byte effectiveSeed)
    {
        var chars = new char[data.Length];
        for (int i = 0; i < data.Length; i++)
            chars[i] = (char)(data[i] ^ (byte)(effectiveSeed + i * Step));
        return new string(chars);
    }
}
