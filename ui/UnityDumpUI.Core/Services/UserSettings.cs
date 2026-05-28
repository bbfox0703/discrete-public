// UserSettings.cs — Phase 29c-2.
//
// JSON-persisted per-user preferences. Currently scoped to the snapshot
// store (Phase 29c needs: custom DB directory, auto-save toggle, prune
// threshold), but the file is meant to be the single home for any future
// user-facing setting so we don't sprout one settings file per feature.
//
// Schema is intentionally loose: every property is nullable / has a
// default so a corrupt or partial JSON falls back cleanly. AOT-safe via
// the source-gen JsonSerializerContext at the bottom of this file.

using System;
using System.Text.Json.Serialization;

namespace UnityDumpUI.Core.Services;

/// <summary>Persisted user preferences. New keys MUST default to a
/// sensible "no opinion" value so loading an older JSON missing the key
/// behaves as if the user never set it.</summary>
public sealed class UserSettings
{
    /// <summary>Phase 29c: override directory for per-game snapshot DBs.
    /// <c>null</c> = use the platform default
    /// (<c>%LOCALAPPDATA%\Discrete_UEDumper\snapshots\</c> on Windows).</summary>
    [JsonPropertyName("snapshot_db_directory")]
    public string? SnapshotDbDirectory { get; set; }

    /// <summary>Phase 29c: auto-save every captured snapshot to disk
    /// without an explicit click. Default <c>false</c> — user opts in
    /// via Settings dialog so wide snaps don't fill the disk unexpectedly.</summary>
    [JsonPropertyName("auto_save_snapshots")]
    public bool AutoSaveSnapshots { get; set; }

    /// <summary>Phase 29c: auto-prune threshold in bytes. When the
    /// per-game DB exceeds this size, the oldest snapshots are deleted
    /// until under the threshold (always preserving at least 1).
    /// Default 200 MB. <c>0</c> = pruning disabled.
    /// <para/>
    /// Rationale for 200 MB (lowered from 1 GB on 2026-05-29 after live
    /// observation): gzipped per-class blobs compress ~50× vs raw JSON.
    /// A wide 活俠傳-class snapshot that's ~100 MB in-memory lands at
    /// &lt;2 MB on disk, so 200 MB comfortably holds 100+ snapshots of
    /// the largest target before pruning kicks in.</summary>
    [JsonPropertyName("auto_prune_threshold_bytes")]
    public long AutoPruneThresholdBytes { get; set; } = DefaultAutoPruneThresholdBytes;

    public const long DefaultAutoPruneThresholdBytes = 200L * 1024 * 1024;

    /// <summary>Deep clone for safe mutation in the Settings dialog
    /// without affecting the active in-memory copy until the user hits
    /// Apply / OK.</summary>
    public UserSettings Clone() => new()
    {
        SnapshotDbDirectory     = SnapshotDbDirectory,
        AutoSaveSnapshots       = AutoSaveSnapshots,
        AutoPruneThresholdBytes = AutoPruneThresholdBytes,
    };
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserSettings))]
internal partial class UserSettingsJsonContext : JsonSerializerContext { }
