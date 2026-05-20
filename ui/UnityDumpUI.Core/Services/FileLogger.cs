// FileLogger.cs — UI-side counterpart of dll/src/shared/Logger.{h,cpp}.
//
// Three categories per Architecture.md ("init" / "pipe" / "view") + 5-file
// rotation. Logs land under
// %LOCALAPPDATA%\Discrete_UEDumper\Logs\ui\<category>\current.log.
//
// Rotation policy (2026-05-20):
//   * UI side rotates on EVERY UI startup — `current.log` → `current.1.log`
//     → `current.2.log` → … → `current.5.log` (oldest drops). The size-
//     based safety net is still present (defends against a runaway long-
//     running UI session writing > LogMaxFileBytes between launches), but
//     practical rotation is driven by the startup-time pass.
//   * DLL per-PID side stays size-based (rare to fire — game session usually
//     ends well under 8 MB per category). Implemented in C++ side.
//   * Cleanup: DLL per-PID directories older than `Constants.LogPruneDaysToKeep`
//     days are removed at UI startup via `PruneOldDllPidDirectories`.
//
// AOT-safe: no reflection, just FileStream + UTF-8 writes. Thread-safe by a
// per-category lock so concurrent writers don't interleave lines.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityDumpUI.Core.Services;

public sealed class FileLogger : IDisposable
{
    private readonly string _root;
    private readonly long   _maxFileBytes;
    private readonly int    _rotationFiles;
    private readonly ConcurrentDictionary<string, CategoryWriter> _writers = new();
    private bool _disposed;

    public FileLogger(string? logRootOverride = null,
                      long maxFileBytes = Constants.LogMaxFileBytes,
                      int rotationFiles = Constants.LogRotationFileCount)
    {
        string root = logRootOverride ?? DefaultRoot();
        Directory.CreateDirectory(root);
        _root = root;
        _maxFileBytes = maxFileBytes;
        _rotationFiles = rotationFiles;

        // One-shot startup rotation: chain every existing category's
        // current.log out to .1, .2, … so the new session writes a fresh
        // current.log. Unlike size-based rotation this fires regardless
        // of how big the previous log was. Safe to call on a fresh
        // install — no-op when no files exist.
        RotateAllOnStartup();
    }

    public string Root => _root;

    public void Write(string category, string message)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(category)) throw new ArgumentException("category required", nameof(category));
        var writer = _writers.GetOrAdd(category, c => new CategoryWriter(_root, c, _maxFileBytes, _rotationFiles));
        writer.Write(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _writers.Values) w.Dispose();
        _writers.Clear();
    }

    /// <summary>
    /// At UI startup, rotate every existing category's `current.log` so
    /// the new session starts with a fresh file. Scans <see cref="Root"/>
    /// for subdirectories that hold a `current.log` and chains it out to
    /// `current.1.log` etc. — same shape as the size-based <see cref="CategoryWriter.Rotate"/>
    /// path but unconditional. No CategoryWriter is constructed for
    /// categories that don't yet exist (those rotate naturally on first
    /// Write next session).
    /// </summary>
    private void RotateAllOnStartup()
    {
        DirectoryInfo rootDir;
        try { rootDir = new DirectoryInfo(_root); }
        catch (Exception) { return; }
        if (!rootDir.Exists) return;

        foreach (DirectoryInfo catDir in rootDir.EnumerateDirectories())
        {
            string current = Path.Combine(catDir.FullName, "current.log");
            if (!File.Exists(current)) continue;
            RotateChain(catDir.FullName, _rotationFiles);
        }
    }

    /// <summary>
    /// Shared rotation helper. Cycles <c>current.log → current.1.log → …
    /// → current.(N-1).log</c>, dropping <c>current.N.log</c> when it
    /// exists. Called by both the startup pass (<see cref="RotateAllOnStartup"/>)
    /// and the per-write size guard (<see cref="CategoryWriter.Rotate"/>).
    /// Best-effort: IO failures swallowed so logger faults never block
    /// the rest of the app.
    /// </summary>
    private static void RotateChain(string dir, int rotationFiles)
    {
        string current = Path.Combine(dir, "current.log");
        for (int i = rotationFiles - 1; i >= 1; --i)
        {
            string from = i == 1 ? current : Path.Combine(dir, $"current.{i - 1}.log");
            string to   = Path.Combine(dir, $"current.{i}.log");
            if (!File.Exists(from)) continue;
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    private static string DefaultRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Match the DLL: %LOCALAPPDATA%\Discrete_UEDumper\Logs\ui (Logs/<pid>/<cat> for the DLL,
        // Logs/ui/<cat> for the UI — disjoint trees by design).
        return Path.Combine(local, Constants.LogRootSubpath, "ui");
    }

    /// <summary>
    /// Remove DLL-side per-PID log directories whose last-write time is
    /// older than <paramref name="daysToKeep"/> days. Each game injection
    /// (Pick &amp; inject or proxy auto-load) creates a new PID folder under
    /// <c>%LOCALAPPDATA%\Discrete_UEDumper\Logs\&lt;pid&gt;\</c>; without
    /// cleanup these accumulate to hundreds of dead folders. Run on UI
    /// startup.
    ///
    /// The <c>ui</c> directory is preserved (only numeric PID folders are
    /// candidates). Anything that isn't a numeric PID folder is left
    /// alone. Best-effort: failures (file locked by another running game,
    /// permission denied) are swallowed so logging itself never faults
    /// the UI start path.
    /// </summary>
    /// <returns>Number of PID directories successfully removed.</returns>
    public static int PruneOldDllPidDirectories(int daysToKeep = Constants.LogPruneDaysToKeep,
                                                string? logRootOverride = null)
    {
        if (daysToKeep < 0) daysToKeep = 0;
        string root = logRootOverride ?? Path.GetDirectoryName(DefaultRoot()) ?? string.Empty;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;

        DateTime cutoff = DateTime.UtcNow.AddDays(-daysToKeep);

        List<DirectoryInfo> stalePidDirs;
        try
        {
            stalePidDirs = Directory.EnumerateDirectories(root)
                .Select(p => new DirectoryInfo(p))
                .Where(d => int.TryParse(d.Name, out _))   // numeric PID only — skips "ui"
                .Where(d => d.LastWriteTimeUtc < cutoff)
                .ToList();
        }
        catch (Exception)
        {
            return 0;
        }

        int deleted = 0;
        foreach (DirectoryInfo d in stalePidDirs)
        {
            try
            {
                d.Delete(recursive: true);
                ++deleted;
            }
            catch (Exception)
            {
                // Best-effort. Stale PID might still be locked by a
                // running game (proxy auto-loaded a sibling install at
                // the same PID). Skip + retry next launch.
            }
        }
        return deleted;
    }

    private sealed class CategoryWriter : IDisposable
    {
        private readonly object       _gate = new();
        private readonly string       _dir;
        private readonly long         _maxFileBytes;
        private readonly int          _rotationFiles;
        private FileStream?           _stream;
        private StreamWriter?         _writer;
        private long                  _bytesWritten;

        public CategoryWriter(string root, string category, long maxFileBytes, int rotationFiles)
        {
            _dir = Path.Combine(root, category);
            _maxFileBytes = maxFileBytes;
            _rotationFiles = rotationFiles;
            Directory.CreateDirectory(_dir);
            Open();
        }

        public void Write(string message)
        {
            lock (_gate)
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
        }

        public void Dispose()
        {
            lock (_gate) { Close(); }
        }

        private void Open()
        {
            string path = Path.Combine(_dir, "current.log");
            _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                     bufferSize: 4096, FileOptions.None);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false))
            {
                NewLine    = "\n",
                AutoFlush  = false,
            };
            _bytesWritten = _stream.Length;
        }

        private void Close()
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
            _writer?.Dispose();
            _writer = null;
            _stream?.Dispose();
            _stream = null;
        }

        private void Rotate()
        {
            Close();
            RotateChain(_dir, _rotationFiles);
            Open();
            _bytesWritten = 0;
        }
    }
}
