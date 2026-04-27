namespace UnityDumpUI.Core;

/// <summary>
/// Mirror of dll/src/shared/MagicStrings.h. Keep both files in sync.
/// </summary>
public static class Constants
{
    public const string PipeName = @"UnityDumpBfx";
    public const string LogRootSubpath = @"UnityCEDumper\Logs";

    // UI log categories (3 per Architecture.md Rule "Log output").
    public const string LogCategoryInit = "init";
    public const string LogCategoryPipe = "pipe";
    public const string LogCategoryView = "view";

    public const long LogMaxFileBytes = 8L * 1024 * 1024;
    public const int LogRotationFileCount = 4;

    // Single-instance Mutex name.
    public const string SingleInstanceMutexName = @"Global\UnityCEDumper.UI.SingleInstance";
}
