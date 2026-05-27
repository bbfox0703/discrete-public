// UserSettingsStore.cs — Phase 29c-2.
//
// Atomic JSON persistence for UserSettings. Lives at
// %LOCALAPPDATA%\Discrete_UEDumper\settings.json by default; callers can
// override the path for tests.
//
// Design choices:
//   * Atomic write: serialise → write to ".tmp" → File.Move(overwrite:true).
//     If the process dies mid-write we lose the new value but the old
//     settings.json stays intact.
//   * Load failures (file missing, malformed JSON, IO error) ALL fall
//     back to fresh defaults + log warning — settings corruption must
//     never block app startup.
//   * The class is async-only at the public surface so it composes with
//     the existing async UI plumbing. Internal IO uses async APIs to
//     avoid blocking the UI thread on slow disks.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

public sealed class UserSettingsStore
{
    private readonly string      _settingsPath;
    private readonly FileLogger? _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public const string LogCategory = "snapshot";

    /// <summary>Absolute path of the settings file this store reads from
    /// and writes to.</summary>
    public string SettingsPath => _settingsPath;

    public UserSettingsStore(string? settingsPathOverride = null, FileLogger? logger = null)
    {
        _settingsPath = settingsPathOverride ?? DefaultSettingsPath();
        _logger       = logger;
    }

    /// <summary>Standard location used when no override is passed:
    /// <c>%LOCALAPPDATA%\Discrete_UEDumper\settings.json</c>. Sits in
    /// the same root as the FileLogger so a user inspecting one finds
    /// the other.</summary>
    public static string DefaultSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Constants.LogRootSubpath is "Discrete_UEDumper\Logs". Strip the
        // "Logs" tail to share the parent folder with the logger.
        string subRoot = Constants.LogRootSubpath;
        int slash = subRoot.IndexOf('\\');
        string appRoot = slash > 0 ? subRoot[..slash] : subRoot;
        return Path.Combine(localAppData, appRoot, "settings.json");
    }

    /// <summary>Read the settings file. Returns fresh defaults if the
    /// file is missing, malformed, or unreadable for any reason — never
    /// throws. Failure modes are written to the snapshot log with a
    /// recovery hint.</summary>
    public async Task<UserSettings> LoadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Log($"Settings file not found at {_settingsPath}; using defaults.");
                return new UserSettings();
            }

            try
            {
                await using var fs = new FileStream(
                    _settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: true);
                var parsed = await JsonSerializer.DeserializeAsync(
                    fs, UserSettingsJsonContext.Default.UserSettings, ct).ConfigureAwait(false);
                if (parsed is null)
                {
                    Log($"Settings file at {_settingsPath} deserialised to null; using defaults.");
                    return new UserSettings();
                }
                Log($"Settings loaded from {_settingsPath}.");
                return parsed;
            }
            catch (JsonException jx)
            {
                Log($"Settings file at {_settingsPath} is corrupt ({jx.Message}); using defaults. " +
                    $"To recover, delete the file — a fresh one will be written on next Save.");
                return new UserSettings();
            }
            catch (IOException io)
            {
                Log($"Settings file at {_settingsPath} unreadable ({io.Message}); using defaults.");
                return new UserSettings();
            }
            catch (UnauthorizedAccessException ux)
            {
                Log($"Settings file at {_settingsPath} permission-denied ({ux.Message}); using defaults.");
                return new UserSettings();
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Persist the given settings atomically. Writes to a
    /// sibling <c>.tmp</c> file then <see cref="File.Move(string, string, bool)"/>
    /// with overwrite — either the new content lands intact or the old
    /// content stays intact.</summary>
    public async Task SaveAsync(UserSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmpPath = _settingsPath + ".tmp";
            await using (var fs = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    fs, settings, UserSettingsJsonContext.Default.UserSettings, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            // File.Move with overwrite is atomic on NTFS for same-volume
            // moves (which .tmp adjacent to target always is).
            File.Move(tmpPath, _settingsPath, overwrite: true);
            Log($"Settings saved to {_settingsPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Settings save FAILED ({ex.GetType().Name}: {ex.Message}); " +
                $"in-memory values stay but next launch will see stale disk state.");
            throw;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private void Log(string msg) =>
        _logger?.Write(LogCategory, $"UserSettings: {msg}");
}
