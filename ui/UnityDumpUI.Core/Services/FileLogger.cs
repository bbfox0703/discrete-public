// FileLogger.cs — UI-side counterpart of dll/src/shared/Logger.{h,cpp}.
//
// Three categories per Architecture.md ("init" / "pipe" / "view") + 4-file
// rotation at 8 MB per file. Logs land under
// %LOCALAPPDATA%\UnityCEDumper\Logs\ui\<category>\current.log.
//
// AOT-safe: no reflection, just FileStream + UTF-8 writes. Thread-safe by a
// per-category lock so concurrent writers don't interleave lines.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
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

    private static string DefaultRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Match the DLL: %LOCALAPPDATA%\UnityCEDumper\Logs\ui (Logs/<pid>/<cat> for the DLL,
        // Logs/ui/<cat> for the UI — disjoint trees by design).
        return Path.Combine(local, Constants.LogRootSubpath, "ui");
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
            // current.log → .1.log; .1 → .2; …; .(N-1) → .N (.N drops off).
            string current = Path.Combine(_dir, "current.log");
            for (int i = _rotationFiles - 1; i >= 1; --i)
            {
                string from = i == 1 ? current : Path.Combine(_dir, $"current.{i - 1}.log");
                string to   = Path.Combine(_dir, $"current.{i}.log");
                if (!File.Exists(from)) continue;
                try
                {
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
                catch (IOException) { /* best-effort rotation; never throw from logger */ }
            }
            Open();
            _bytesWritten = 0;
        }
    }
}
