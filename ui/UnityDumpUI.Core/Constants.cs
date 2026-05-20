namespace UnityDumpUI.Core;

/// <summary>
/// Mirror of dll/src/shared/MagicStrings.h. Keep both files in sync.
/// </summary>
public static class Constants
{
    public const string PipeName = @"UnityDumpBfx";
    // Renamed 2026-05-20: UnityCEDumper → Discrete_UEDumper to match the
    // repo name (`discrete`). The C++ DLL mirror is in
    // `dll/src/shared/MagicStrings.h` (LogRootSubpath). Both sides share
    // the same root; DLL writes to `<root>\<pid>\<category>\` and the UI
    // writes to `<root>\ui\<category>\`.
    public const string LogRootSubpath = @"Discrete_UEDumper\Logs";

    // UI log categories (3 per Architecture.md Rule "Log output").
    public const string LogCategoryInit = "init";
    public const string LogCategoryPipe = "pipe";
    public const string LogCategoryView = "view";

    public const long LogMaxFileBytes = 8L * 1024 * 1024;

    // UI side keeps 5 rotated copies — bumped from 4 to 5 alongside the
    // 2026-05-20 "rotate on every UI startup" policy change. The DLL
    // per-PID logger still uses its own LogRotationFileCount (in
    // MagicStrings.h) and is unaffected — each game launch is short
    // enough that DLL rotation rarely fires anyway.
    public const int LogRotationFileCount = 5;

    /// <summary>
    /// Per-PID DLL log directories older than this many days are removed
    /// on UI startup (`FileLogger.PruneOldDllPidDirectories`). 15 days
    /// gives the user plenty of time to dig back into a recent session
    /// for forensics, while keeping the on-disk footprint bounded.
    /// </summary>
    public const int LogPruneDaysToKeep = 15;

    // Single-instance Mutex name.
    public const string SingleInstanceMutexName = @"Global\Discrete_UEDumper.UI.SingleInstance";
}
