// FileLogger.cs — UI-side counterpart of dll/src/shared/Logger.{h,cpp}.
//
// Three categories per Architecture.md ("init" / "pipe" / "view", plus any
// ad-hoc category a service passes, e.g. "snapshot"). Logs land under
// %LOCALAPPDATA%\discrete\Logs\ui\<category>\current.log.
//
// Rotation + retention (2026-11, modelled on UE5CEDumper; same policy as the
// DLL — see Constants.LogRetentionDays):
//   * current.log is ALWAYS the live file.
//   * At UI startup every category's previous current.log is archived to
//     <YYYYMMDD-HHMMSS>.log, and the same happens mid-session when a file
//     reaches Constants.LogMaxFileBytes (8 MB). The stamp is the file's OWN
//     last-write time, never "now" — archiving must not reset a stale log's
//     clock, or the age prune would keep it for another full window.
//   * Retention is by AGE, not a file count (RunRetentionSweep, at UI start):
//     archives older than LogRetentionDays are deleted, and so are DLL
//     per-PID folders whose newest file is that old. The old count-based
//     policies (5 rotated UI files, 15 newest PID folders) evicted by number,
//     so a handful of short game launches discarded everything before them
//     no matter how recent.
//   * The pre-2026-11 log root (%LOCALAPPDATA%\Discrete_Dumper\Logs) gets the
//     same age sweep and is deleted once empty (see AppDataMigration).
//
// AOT-safe: no reflection, just FileStream + UTF-8 writes. Thread-safe by a
// per-category lock so concurrent writers don't interleave lines.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityDumpUI.Core.Services;

/// <summary>What one <see cref="FileLogger.RunRetentionSweep"/> pass removed.</summary>
public readonly record struct LogRetentionResult(int PidFoldersDeleted, int FilesDeleted, bool LegacyRootRemoved);

public sealed class FileLogger : IDisposable
{
    private readonly string _root;
    private readonly long   _maxFileBytes;
    private readonly ConcurrentDictionary<string, Lazy<CategoryWriter>> _writers = new();
    private bool _disposed;

    public FileLogger(string? logRootOverride = null,
                      long maxFileBytes = Constants.LogMaxFileBytes)
    {
        string root = logRootOverride ?? DefaultRoot();
        // Best-effort: an unwritable root must not abort UI startup. Each
        // category writer recreates its folder on every open attempt.
        try { Directory.CreateDirectory(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* retried on first write */ }
        _root = root;
        _maxFileBytes = maxFileBytes;

        // One-shot startup archive: every existing category's current.log
        // becomes a dated archive so this session writes a fresh file.
        // No-op on a fresh install.
        ArchiveAllOnStartup();
    }

    public string Root => _root;

    public void Write(string category, string message)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(category)) throw new ArgumentException("category required", nameof(category));
        // Lazy, not a bare factory: ConcurrentDictionary.GetOrAdd may invoke its
        // value factory MORE THAN ONCE for the same key under contention and
        // keep only one result, and two writers would fight over current.log
        // (opened with FileShare.Read). Lazy with ExecutionAndPublication
        // guarantees the writer is constructed exactly once per category. The
        // CategoryWriter constructor never throws, so the Lazy never caches an
        // exception; the file is opened on the first Write.
        var writer = _writers.GetOrAdd(category, c => new Lazy<CategoryWriter>(
            () => new CategoryWriter(_root, c, _maxFileBytes),
            LazyThreadSafetyMode.ExecutionAndPublication));
        writer.Value.Write(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // IsValueCreated guard: touching .Value on a Lazy that was never
        // realised would CONSTRUCT a writer (opening a log file) purely to
        // dispose it.
        foreach (var w in _writers.Values) if (w.IsValueCreated) w.Value.Dispose();
        _writers.Clear();
    }

    /// <summary>
    /// At UI startup, archive every existing category's <c>current.log</c> so
    /// the new session starts with a fresh file. No CategoryWriter is
    /// constructed for categories that are not written this session.
    /// </summary>
    private void ArchiveAllOnStartup()
    {
        try
        {
            foreach (string catDir in Directory.EnumerateDirectories(_root))
                ArchiveByWriteTime(Path.Combine(catDir, Constants.LogCurrentFileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>
    /// Rename <paramref name="src"/> to <c>&lt;YYYYMMDD-HHMMSS&gt;.log</c> in the
    /// same folder, stamped from the file's own last-write time (local), with a
    /// <c>-N</c> suffix on a same-second clash. Best-effort: on failure the
    /// file stays where it is and the next rotation retries.
    /// </summary>
    /// <returns>The archive path, or null when nothing was archived.</returns>
    internal static string? ArchiveByWriteTime(string src)
    {
        try
        {
            if (!File.Exists(src)) return null;
            string dir = Path.GetDirectoryName(src)!;
            string stamp = File.GetLastWriteTime(src).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (int dup = 0; dup < 100; dup++)
            {
                string name = dup == 0 ? $"{stamp}.log" : $"{stamp}-{dup}.log";
                string dst = Path.Combine(dir, name);
                if (File.Exists(dst)) continue;
                File.Move(src, dst);
                return dst;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best-effort */ }
        return null;
    }

    private static string DefaultRoot() => Path.Combine(LogRootDirectory(), "ui");

    /// <summary>
    /// The log ROOT — <c>%LOCALAPPDATA%\discrete\Logs</c> — parent of the
    /// UI's <c>ui\</c> tree and the DLL's per-PID folders. The "Open log folder"
    /// toolbar button opens this so the user can reach both. The directory is NOT
    /// created here; the caller lazy-creates it before opening.
    /// </summary>
    public static string LogRootDirectory()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, Constants.LogRootSubpath);
    }

    /// <summary>The pre-2026-11 log root, <c>%LOCALAPPDATA%\Discrete_Dumper\Logs</c>.</summary>
    public static string LegacyLogRootDirectory()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, Constants.LegacyAppDataFolderName, "Logs");
    }

    /// <summary>
    /// Age-based retention over the whole log root. Run once at UI startup,
    /// OFF the UI thread (it walks every per-PID folder).
    /// <list type="bullet">
    /// <item><c>ui\&lt;category&gt;\</c>: archived <c>*.log</c> older than the window
    /// are deleted; <c>current.log</c> is never touched.</item>
    /// <item>Numeric per-PID folders: removed when EMPTY or when their newest
    /// file is older than the window — judged by the files inside, not the
    /// folder's own mtime (Windows does not bump it when an existing child is
    /// written), so a running game's folder can never be selected. In a folder
    /// that stays, aged archives are deleted like the UI's.</item>
    /// <item>The legacy root gets the same treatment for every sub-folder
    /// (none of its files are live for this build), and is deleted — with
    /// its parent app-data folder — once empty.</item>
    /// </list>
    /// Never throws; a locked file or folder is skipped and retried next start.
    /// The DLL runs the same sweep (minus <c>ui\</c> and the legacy root) on
    /// attach, so proxy-only users get retention without ever opening the UI.
    /// </summary>
    public static LogRetentionResult RunRetentionSweep(string? logRootOverride = null,
                                                       string? legacyLogRootOverride = null,
                                                       DateTime? nowUtc = null,
                                                       int retentionDays = Constants.LogRetentionDays)
    {
        DateTime cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-retentionDays);
        int folders = 0, files = 0;

        string root = logRootOverride ?? LogRootDirectory();
        if (Directory.Exists(root))
        {
            foreach (string sub in SafeEnumerateDirectories(root))
            {
                string name = Path.GetFileName(sub);
                if (string.Equals(name, "ui", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string cat in SafeEnumerateDirectories(sub))
                        files += PruneAgedArchives(cat, cutoff, skipCurrent: true);
                }
                else if (IsPidFolderName(name))
                {
                    SweepFolder(sub, cutoff, skipCurrent: true, ref folders, ref files);
                }
            }
        }

        bool legacyRemoved = false;
        string legacy = legacyLogRootOverride ?? LegacyLogRootDirectory();
        if (Directory.Exists(legacy))
        {
            foreach (string sub in SafeEnumerateDirectories(legacy))
                SweepFolder(sub, cutoff, skipCurrent: false, ref folders, ref files);
            files += PruneAgedArchives(legacy, cutoff, skipCurrent: false);
            if (TryDeleteIfEmpty(legacy))
            {
                legacyRemoved = true;
                string? legacyAppData = Path.GetDirectoryName(legacy);
                if (legacyAppData is not null) TryDeleteIfEmpty(legacyAppData);
            }
        }

        return new LogRetentionResult(folders, files, legacyRemoved);
    }

    private static bool IsPidFolderName(string name)
    {
        if (name.Length == 0) return false;
        foreach (char c in name) if (c is < '0' or > '9') return false;
        return true;
    }

    // Remove the whole folder when it is empty or its newest file is past the
    // cutoff; otherwise prune aged archives in each of its sub-folders.
    private static void SweepFolder(string folder, DateTime cutoff, bool skipCurrent,
                                    ref int folders, ref int files)
    {
        DateTime? newest;
        try
        {
            newest = null;
            foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (newest is null || t > newest) newest = t;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;   // half-read folder: never guess it is empty or stale
        }

        if (newest is null || newest < cutoff)
        {
            try { Directory.Delete(folder, recursive: true); folders++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* locked — retry next start */ }
            return;
        }

        foreach (string cat in SafeEnumerateDirectories(folder))
            files += PruneAgedArchives(cat, cutoff, skipCurrent);
    }

    /// <summary>
    /// Delete <c>*.log</c> files in <paramref name="dir"/> (not recursive) whose
    /// last-write time is before <paramref name="cutoffUtc"/>. With
    /// <paramref name="skipCurrent"/>, <c>current.log</c> is never touched.
    /// Per-file failures (a locked file) do not stop the sweep.
    /// </summary>
    internal static int PruneAgedArchives(string dir, DateTime cutoffUtc, bool skipCurrent)
    {
        int deleted = 0;
        string[] candidates;
        try { candidates = Directory.GetFiles(dir, "*.log"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }

        foreach (string f in candidates)
        {
            if (skipCurrent && string.Equals(Path.GetFileName(f), Constants.LogCurrentFileName,
                                             StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (File.GetLastWriteTimeUtc(f) >= cutoffUtc) continue;
                File.Delete(f);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* retry next start */ }
        }
        return deleted;
    }

    private static string[] SafeEnumerateDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static bool TryDeleteIfEmpty(string dir)
    {
        try
        {
            using var it = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            if (it.MoveNext()) return false;
            Directory.Delete(dir);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // Logging is best-effort: no member of this class throws into the caller.
    // An IO failure drops the line and closes the file; the next Write retries
    // the open.
    private sealed class CategoryWriter : IDisposable
    {
        private readonly object       _gate = new();
        private readonly string       _dir;
        private readonly long         _maxFileBytes;
        private FileStream?           _stream;
        private StreamWriter?         _writer;
        private long                  _bytesWritten;

        public CategoryWriter(string root, string category, long maxFileBytes)
        {
            _dir = Path.Combine(root, category);
            _maxFileBytes = maxFileBytes;
        }

        public void Write(string message)
        {
            lock (_gate)
            {
                try
                {
                    if (_stream is null || _writer is null) Open();
                    if (_bytesWritten >= _maxFileBytes) Rotate();

                    string line = string.Format(CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm:ss.fff} {1}",
                        DateTimeOffset.Now, message);
                    _writer!.WriteLine(line);
                    _writer.Flush();
                    _bytesWritten = _stream!.Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                              or ObjectDisposedException or NotSupportedException)
                {
                    // Full disk, locked or unreachable file: drop this line and
                    // leave the fields null so the next Write reopens.
                    Close();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate) { Close(); }
        }

        private void Open()
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, Constants.LogCurrentFileName);
            _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                     bufferSize: 4096, FileOptions.None);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false))
            {
                NewLine    = "\n",
                AutoFlush  = false,
            };
            _bytesWritten = _stream.Length;
        }

        // Never throws: Dispose re-flushes buffered data and can fail the same
        // way the write did, so every step is guarded and the fields are
        // always cleared.
        private void Close()
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
            try { _writer?.Dispose(); } catch { /* ignore */ }
            try { _stream?.Dispose(); } catch { /* ignore */ }
            _writer = null;
            _stream = null;
        }

        private void Rotate()
        {
            Close();
            ArchiveByWriteTime(Path.Combine(_dir, Constants.LogCurrentFileName));
            Open();
        }
    }
}
