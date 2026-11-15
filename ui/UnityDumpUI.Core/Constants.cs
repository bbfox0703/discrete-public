namespace UnityDumpUI.Core;

/// <summary>
/// Mirror of dll/src/shared/MagicStrings.h. Keep both files in sync.
/// </summary>
public static class Constants
{
    public const string PipeName = @"DiscreteBfx";
    /// <summary>
    /// App-data folder under %LOCALAPPDATA% — holds Logs\ plus the UI's
    /// user-state files (settings.json, window-state.txt, bookmarks, …).
    /// Matches the repo name.
    /// </summary>
    public const string AppDataFolderName = "discrete";

    /// <summary>
    /// The app-data folder used until 2026-11. <c>AppDataMigration</c> moves
    /// its user-state files into <see cref="AppDataFolderName"/> at UI start;
    /// its old <c>Logs\</c> tree is left in place and aged out by the same
    /// retention sweep, and the folder is deleted once empty.
    /// </summary>
    public const string LegacyAppDataFolderName = "Discrete_Dumper";

    // Log root: discrete\Logs. The C++ DLL mirror is in
    // `dll/src/shared/MagicStrings.h` (LogRootSubpath). Both sides share
    // the same root; DLL writes to `<root>\<pid>\<category>\` and the UI
    // writes to `<root>\ui\<category>\`.
    public const string LogRootSubpath = AppDataFolderName + @"\Logs";

    // UI log categories (3 per Architecture.md Rule "Log output").
    public const string LogCategoryInit = "init";
    public const string LogCategoryPipe = "pipe";
    public const string LogCategoryView = "view";

    // Rotation + retention — same policy as the DLL (MagicStrings.h),
    // modelled on UE5CEDumper. The live file is always `current.log`; at UI
    // start and on reaching LogMaxFileBytes it is archived to
    // `<YYYYMMDD-HHMMSS>.log`, stamped from the file's OWN last-write time.
    // Retention is by AGE, not a file count: archives, and DLL per-PID
    // folders whose newest file is older than LogRetentionDays, are deleted.
    public const string LogCurrentFileName = "current.log";
    public const long LogMaxFileBytes = 8L * 1024 * 1024;
    public const int LogRetentionDays = 21;   // keep in sync with MagicStrings::LogRetentionDays

    // Single-instance Mutex name.
    public const string SingleInstanceMutexName = @"Global\Discrete_Dumper.UI.SingleInstance";
}
