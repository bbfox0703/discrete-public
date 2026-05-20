namespace UnityDumpUI.Core.Services;

/// <summary>
/// Combines all three token readers into one factor. The third slot
/// (<c>WindowsRuntimeMetadata</c>) lives in the UI assembly which Core
/// cannot reference directly, so the UI registers its reader via
/// <see cref="RegisterExternal"/> at app startup. Returns the bitwise OR
/// of all known token factors — non-zero if any guardian sees tampering.
/// </summary>
public static class TokenAggregator
{
    private static Func<int>? _external;

    /// <summary>
    /// Wire a UI-side token reader. Called once during app startup.
    /// Subsequent calls overwrite (last-wins) — only used in tests.
    /// </summary>
    public static void RegisterExternal(Func<int> reader) => _external = reader;

    /// <summary>
    /// Combined factor across all wired token readers. 0 = everything clean,
    /// non-zero = at least one slot is bad.
    /// </summary>
    public static int Combined()
        => BuildArtifactToken.Read()
         | Export.ExportFormatToken.Read()
         | (_external?.Invoke() ?? 0);
}
