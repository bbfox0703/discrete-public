namespace UnityDumpUI.Core;

/// <summary>
/// Mirror of dll/src/shared/MagicStrings.h. Keep both files in sync.
/// </summary>
public static class Constants
{
    public const string PipeName = @"DiscreteBfx";
    // Log root: Discrete_Dumper\Logs — matches the repo name (`discrete`);
    // the original working-title was `UnityCEDumper`. The C++ DLL mirror is
    // in `dll/src/shared/MagicStrings.h` (LogRootSubpath). Both sides share
    // the same root; DLL writes to `<root>\<pid>\<category>\` and the UI
    // writes to `<root>\ui\<category>\`.
    public const string LogRootSubpath = @"Discrete_Dumper\Logs";

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
    /// Keep only the most-recent this-many per-PID DLL log directories on UI
    /// startup (`FileLogger.PruneOldDllPidDirectories`); older ones are removed.
    /// Count-based, not age-based — an old-but-recent-enough PID still gets
    /// dropped once 15 newer ones exist, since keeping them longer serves no
    /// purpose. The most recent 15 sessions cover any realistic forensics need
    /// while keeping the on-disk footprint bounded. The `ui` directory is
    /// preserved separately and never counted here.
    /// </summary>
    public const int LogKeepRecentPidDirs = 15;

    // Single-instance Mutex name.
    public const string SingleInstanceMutexName = @"Global\Discrete_Dumper.UI.SingleInstance";
}
