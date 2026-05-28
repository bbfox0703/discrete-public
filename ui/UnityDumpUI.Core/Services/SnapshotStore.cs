// SnapshotStore.cs — Phase 29c.
//
// Per-game SQLite-backed persistence for SnapshotEnvelope. One DB file per
// game build (keyed by FNV-1a `game_build_id` already produced by the DLL),
// named `{exe_basename}_{build_id[0:8]}.db` for human readability when the
// user opens the snapshots folder.
//
// Schema (hybrid: normalised metadata + per-class gzipped JSON blob):
//   snapshots          — one row per envelope
//   snapshot_classes   — one row per SnapshotClassEntry, payload_gzip = gzip(JSON)
//   meta               — schema_version, target_process, game_build_id seed
//
// Why per-class blob vs fully normalised?
//   * 활俠傳 wide snap = 3,647 classes × ~30 fields × ~120 instances ≈ 1M
//     field-values. Fully normalised → 1M INSERTs per save (slow even
//     batched). Per-class blob → 3,647 INSERTs and the existing
//     SnapshotClassEntry JSON serialiser does the heavy lifting.
//   * Reuses the AOT source-gen JsonSerializerContext already wired up
//     in ProtocolJson.cs — zero new mapping code.
//   * Gzip cuts payload ~5× on real-world data (lots of repeated class
//     FQN / field name strings).
//
// AOT: Microsoft.Data.Sqlite.Core + SQLitePCLRaw.bundle_e_sqlite3 — both
// reflection-free. Caller must run SQLitePCL.Batteries_V2.Init() once at
// process startup before any SnapshotStore use.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UnityDumpUI.Core.Services;

/// <summary>SQLite-backed cross-session snapshot persistence. One
/// instance per open per-game DB. Thread-safe at the Open/Save/List
/// boundary via an internal async semaphore (SQLite itself only allows
/// one writer at a time anyway).</summary>
public sealed class SnapshotStore : IAsyncDisposable
{
    /// <summary>Current schema version. Bump when the table layout
    /// changes + add a migrator branch in <see cref="MigrateAsync"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Log category shared by every Phase 29c service so all
    /// snapshot-persistence activity ends up in one rotating log file
    /// the user can inspect from %LOCALAPPDATA%\Discrete_UEDumper\Logs\ui\snapshot\.</summary>
    public const string LogCategory = "snapshot";

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim    _gate = new(1, 1);
    private readonly FileLogger?      _logger;

    /// <summary>Absolute path to the DB file this store is bound to.
    /// Surfaced in the UI status line.</summary>
    public string DatabasePath { get; }

    /// <summary>The <c>game_build_id</c> this DB is keyed against —
    /// every snapshot saved here must match (we don't enforce in the
    /// schema beyond storing it for sanity check + display).</summary>
    public string GameBuildId { get; }

    /// <summary>Human-readable exe basename baked into the filename.
    /// Stored in <c>meta</c> so the UI can show it even when only the
    /// DB path is known.</summary>
    public string TargetProcess { get; }

    private SnapshotStore(SqliteConnection conn, string dbPath, string buildId, string targetProcess, FileLogger? logger)
    {
        _connection   = conn;
        DatabasePath  = dbPath;
        GameBuildId   = buildId;
        TargetProcess = targetProcess;
        _logger       = logger;
    }

    private void Log(string msg) =>
        _logger?.Write(LogCategory, $"SnapshotStore[{Path.GetFileName(DatabasePath)}]: {msg}");

    /// <summary>Resolve the canonical per-game DB filename for the given
    /// build identity. Filename pattern is
    /// <c>{sanitised_exe_basename}_{build_id_prefix8}.db</c>.</summary>
    public static string ResolveDatabaseFileName(string targetProcess, string gameBuildId)
    {
        string baseName = string.IsNullOrWhiteSpace(targetProcess) ? "unknown" : targetProcess;
        // Strip any path components callers accidentally pass in.
        baseName = Path.GetFileNameWithoutExtension(baseName);
        baseName = SanitiseForFilename(baseName);

        string idPrefix = (gameBuildId ?? string.Empty).Length >= 8
            ? gameBuildId![..8]
            : (gameBuildId ?? "00000000").PadRight(8, '0');
        idPrefix = SanitiseForFilename(idPrefix);

        return $"{baseName}_{idPrefix}.db";
    }

    /// <summary>Open (or create) the DB at <paramref name="dbPath"/> and
    /// run any pending schema migrations. The directory is created if
    /// missing.</summary>
    public static async Task<SnapshotStore> OpenAsync(
        string dbPath, string gameBuildId, string targetProcess,
        FileLogger? logger = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("dbPath required", nameof(dbPath));
        if (string.IsNullOrWhiteSpace(gameBuildId))
            throw new ArgumentException("gameBuildId required", nameof(gameBuildId));

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            // Shared cache keeps the in-process semantics consistent when
            // multiple SnapshotStore instances reference the same path
            // (e.g. two windows open against the same game) — though our
            // single-instance Mutex makes that mostly hypothetical.
            Cache      = SqliteCacheMode.Shared,
        }.ConnectionString;

        var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // WAL + NORMAL synchronous: crash-safe AND fast enough for the
        // chunky per-class blob writes. Default DELETE journal mode is
        // ~5× slower on bulk INSERT under transaction.
        await ExecuteAsync(conn, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await ExecuteAsync(conn, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        // Cascade DELETEs from snapshots → snapshot_classes are wired in
        // the schema as ON DELETE CASCADE, but SQLite requires the FK
        // enforcement to be explicitly enabled per-connection.
        await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);

        bool freshSchema = await MigrateAsync(conn, gameBuildId, targetProcess, ct).ConfigureAwait(false);

        var store = new SnapshotStore(conn, dbPath, gameBuildId, targetProcess, logger);
        store.Log(freshSchema
            ? $"opened (fresh schema v{CurrentSchemaVersion}) buildId={gameBuildId} target={targetProcess}"
            : $"opened (existing schema) buildId={gameBuildId} target={targetProcess}");
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    // ─── Public ops ────────────────────────────────────────────────────

    /// <summary>Persist a snapshot envelope. Returns the auto-assigned
    /// row id (the <see cref="PersistedSnapshotMeta.Id"/> for later
    /// load / delete).</summary>
    public async Task<long> SaveSnapshotAsync(
        SnapshotPersistRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            long snapshotId;
            long payloadBytes = 0;

            // Two-pass write: serialize + gzip each class blob first (so we
            // know the total payload_bytes for prune accounting), then
            // INSERT the snapshot row + class rows under the same tx.
            var classBlobs = new List<(string Asm, string Fqn, int Inst, int Stat, byte[] Blob)>(request.Classes.Count);
            foreach (var c in request.Classes)
            {
                byte[] blob = SerializeAndGzip(c);
                payloadBytes += blob.LongLength;
                string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                classBlobs.Add((
                    c.Assembly,
                    fqn,
                    c.Instances?.Count ?? 0,
                    c.Statics?.Count ?? 0,
                    blob));
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO snapshots
                        (label, captured_at_utc, game_build_id, game_session_id,
                         target_process, backend, unity_version, ptr_size,
                         total_classes, total_instances, field_value_count,
                         instance_cap, array_cap, include_unchanged, asm_filter,
                         payload_bytes)
                    VALUES
                        ($label, $captured_at, $build_id, $session_id,
                         $target_proc, $backend, $unity, $ptr_size,
                         $total_classes, $total_instances, $field_value_count,
                         $instance_cap, $array_cap, $include_unchanged, $asm_filter,
                         $payload_bytes);
                    SELECT last_insert_rowid();
                    """;
                Bind(cmd, "$label",             request.Label);
                Bind(cmd, "$captured_at",       request.CapturedAtUtc);
                Bind(cmd, "$build_id",          request.GameBuildId);
                Bind(cmd, "$session_id",        request.GameSessionId);
                Bind(cmd, "$target_proc",       request.TargetProcess);
                Bind(cmd, "$backend",           request.Backend);
                Bind(cmd, "$unity",             request.UnityVersion);
                Bind(cmd, "$ptr_size",          request.PointerSize);
                Bind(cmd, "$total_classes",     request.TotalClasses);
                Bind(cmd, "$total_instances",   request.TotalInstances);
                Bind(cmd, "$field_value_count", request.FieldValueCount);
                Bind(cmd, "$instance_cap",      request.InstanceCap);
                Bind(cmd, "$array_cap",         request.ArrayCap);
                Bind(cmd, "$include_unchanged", request.IncludeUnchanged ? 1 : 0);
                Bind(cmd, "$asm_filter",        request.AssemblyFilter);
                Bind(cmd, "$payload_bytes",     payloadBytes);

                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                snapshotId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO snapshot_classes
                        (snapshot_id, assembly, class_fqn, instance_count, static_count, payload_gzip)
                    VALUES
                        ($snap, $asm, $fqn, $inst, $stat, $blob);
                    """;
                var pSnap = cmd.Parameters.Add("$snap", SqliteType.Integer);
                var pAsm  = cmd.Parameters.Add("$asm",  SqliteType.Text);
                var pFqn  = cmd.Parameters.Add("$fqn",  SqliteType.Text);
                var pInst = cmd.Parameters.Add("$inst", SqliteType.Integer);
                var pStat = cmd.Parameters.Add("$stat", SqliteType.Integer);
                var pBlob = cmd.Parameters.Add("$blob", SqliteType.Blob);
                pSnap.Value = snapshotId;
                foreach (var (asm, fqn, inst, stat, blob) in classBlobs)
                {
                    pAsm.Value  = asm;
                    pFqn.Value  = fqn;
                    pInst.Value = inst;
                    pStat.Value = stat;
                    pBlob.Value = blob;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            Log($"saved snapshot id={snapshotId} label=\"{request.Label}\" " +
                $"classes={request.Classes.Count} payload={payloadBytes:N0}B " +
                $"sessionId={request.GameSessionId}");
            return snapshotId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>List metadata for every snapshot in the DB, ordered
    /// newest-first. Cheap — no blobs read.</summary>
    public async Task<List<PersistedSnapshotMeta>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        var results = new List<PersistedSnapshotMeta>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, label, captured_at_utc, game_build_id, game_session_id,
                       target_process, backend, unity_version, ptr_size,
                       total_classes, total_instances, field_value_count,
                       instance_cap, array_cap, include_unchanged, asm_filter,
                       payload_bytes
                  FROM snapshots
                 ORDER BY captured_at_utc DESC, id DESC;
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new PersistedSnapshotMeta
                {
                    Id               = reader.GetInt64(0),
                    Label            = reader.IsDBNull(1) ? null : reader.GetString(1),
                    CapturedAtUtc    = reader.GetString(2),
                    GameBuildId      = reader.GetString(3),
                    GameSessionId    = reader.GetString(4),
                    TargetProcess    = reader.GetString(5),
                    Backend          = reader.GetString(6),
                    UnityVersion     = reader.IsDBNull(7) ? null : reader.GetString(7),
                    PointerSize      = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8),
                    TotalClasses     = reader.GetInt32(9),
                    TotalInstances   = reader.GetInt32(10),
                    FieldValueCount  = reader.GetInt32(11),
                    InstanceCap      = reader.IsDBNull(12) ? (int?)null : reader.GetInt32(12),
                    ArrayCap         = reader.IsDBNull(13) ? (int?)null : reader.GetInt32(13),
                    IncludeUnchanged = reader.GetInt32(14) != 0,
                    AssemblyFilter   = reader.IsDBNull(15) ? null : reader.GetString(15),
                    PayloadBytes     = reader.GetInt64(16),
                });
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    /// <summary>Load every <c>SnapshotClassEntry</c> for a snapshot.
    /// Decompresses + JsonSerializer-deserialises each blob via the
    /// source-gen <see cref="ProtocolJsonContext"/>.</summary>
    public async Task<List<SnapshotClassEntry>> LoadClassesAsync(
        long snapshotId, CancellationToken ct = default)
    {
        var results = new List<SnapshotClassEntry>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT payload_gzip
                  FROM snapshot_classes
                 WHERE snapshot_id = $id
                 ORDER BY class_fqn ASC;
                """;
            Bind(cmd, "$id", snapshotId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                using var blobStream = reader.GetStream(0);
                var entry = DeserializeGzipped(blobStream);
                if (entry is not null) results.Add(entry);
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    /// <summary>Distinct (assembly, class FQN) pairs across every saved
    /// snapshot in this DB. Reads only the indexed metadata columns —
    /// the gzipped <c>payload_gzip</c> blobs are never touched — so it's
    /// cheap enough to run on connect and seed the Class Pivot tab's
    /// autocomplete before the user hydrates anything. 활俠傳-scale
    /// (~3,647 classes × N snaps → ~3,647 distinct rows) returns in a
    /// few ms.</summary>
    public async Task<List<PersistedClassRef>> ListDistinctClassesAsync(CancellationToken ct = default)
    {
        var results = new List<PersistedClassRef>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT assembly, class_fqn
                  FROM snapshot_classes
                 ORDER BY class_fqn ASC;
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new PersistedClassRef
                {
                    Assembly = reader.GetString(0),
                    ClassFqn = reader.GetString(1),
                });
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    /// <summary>Delete one snapshot + its class rows (cascaded). Returns
    /// the number of rows deleted from the <c>snapshots</c> table (0 or 1).</summary>
    public async Task<int> DeleteSnapshotAsync(long snapshotId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM snapshots WHERE id = $id;";
            Bind(cmd, "$id", snapshotId);
            int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Log($"deleted snapshot id={snapshotId} rows={rows}");
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sum of <c>payload_bytes</c> across all snapshots. Used by
    /// the prune algorithm + UI status line. Cheap — single aggregate.</summary>
    public async Task<long> GetTotalPayloadBytesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(payload_bytes), 0) FROM snapshots;";
            var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return v is null or DBNull ? 0L : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Phase 29c-6: enforce the size budget by deleting the
    /// oldest snapshots until total payload bytes fall at or under
    /// <paramref name="thresholdBytes"/>. Always leaves at least
    /// <paramref name="minRetain"/> snapshot(s) regardless of size, so
    /// a single wide snap that exceeds the budget doesn't self-destruct.
    /// <para/>
    /// Returns the count of snapshots removed + bytes freed (pre-VACUUM).
    /// VACUUM is decided by the caller — <see cref="ShouldVacuumAsync"/>
    /// provides the heuristic.
    /// <para/>
    /// <c>thresholdBytes &lt;= 0</c> short-circuits (pruning disabled).</summary>
    public async Task<PruneResult> PruneAsync(
        long thresholdBytes, int minRetain = 1, CancellationToken ct = default)
    {
        if (thresholdBytes <= 0)
        {
            Log("prune skipped (threshold disabled)");
            return new PruneResult(0, 0);
        }
        if (minRetain < 0) minRetain = 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long currentBytes = await SumPayloadBytesUnlockedAsync(ct).ConfigureAwait(false);
            if (currentBytes <= thresholdBytes)
            {
                Log($"prune no-op (current={currentBytes:N0}B ≤ threshold={thresholdBytes:N0}B)");
                return new PruneResult(0, 0);
            }

            int total = await CountSnapshotsUnlockedAsync(ct).ConfigureAwait(false);
            int removable = Math.Max(0, total - minRetain);
            if (removable == 0)
            {
                Log($"prune blocked (current={currentBytes:N0}B > threshold but minRetain={minRetain} reserves all {total} snapshots)");
                return new PruneResult(0, 0);
            }

            int removed = 0;
            long freed = 0;
            using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            while (currentBytes > thresholdBytes && removed < removable)
            {
                long? oldestId = null;
                long  oldestBytes = 0;
                using (var probe = _connection.CreateCommand())
                {
                    probe.Transaction = tx;
                    probe.CommandText = """
                        SELECT id, payload_bytes
                          FROM snapshots
                         ORDER BY captured_at_utc ASC, id ASC
                         LIMIT 1;
                        """;
                    using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        oldestId    = reader.GetInt64(0);
                        oldestBytes = reader.GetInt64(1);
                    }
                }
                if (oldestId is null) break;
                using (var del = _connection.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM snapshots WHERE id = $id;";
                    Bind(del, "$id", oldestId.Value);
                    await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                currentBytes -= oldestBytes;
                freed        += oldestBytes;
                removed++;
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);

            Log($"prune removed={removed} freed={freed:N0}B newTotal={currentBytes:N0}B threshold={thresholdBytes:N0}B minRetain={minRetain}");
            return new PruneResult(removed, freed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Heuristic: returns <c>true</c> when the on-disk file
    /// has > 30% freelist pages (i.e. lots of dead space that VACUUM
    /// would reclaim). Cheap — one PRAGMA query.</summary>
    public async Task<bool> ShouldVacuumAsync(double freePageRatio = 0.30, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long pageCount = await ReadPragmaIntAsync("page_count", ct).ConfigureAwait(false);
            if (pageCount == 0) return false;
            long freelist  = await ReadPragmaIntAsync("freelist_count", ct).ConfigureAwait(false);
            double ratio = (double)freelist / pageCount;
            return ratio > freePageRatio;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Run the on-disk <c>VACUUM</c> to actually reclaim
    /// freelist pages after a delete burst. Slow on large DBs — caller
    /// decides when to trigger (typically via <see cref="ShouldVacuumAsync"/>).</summary>
    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long before = new FileInfo(DatabasePath).Length;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "VACUUM;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            long after = new FileInfo(DatabasePath).Length;
            Log($"VACUUM reclaimed {Math.Max(0L, before - after):N0}B (before={before:N0}, after={after:N0})");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ─── Migration ─────────────────────────────────────────────────────

    /// <summary>Returns <c>true</c> when a fresh schema was just created
    /// (caller logs the difference), <c>false</c> when the DB was already
    /// at the current version.</summary>
    private static async Task<bool> MigrateAsync(
        SqliteConnection conn, string buildId, string targetProcess, CancellationToken ct)
    {
        int existing = await ReadSchemaVersionAsync(conn, ct).ConfigureAwait(false);
        if (existing == CurrentSchemaVersion) return false;
        if (existing > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Snapshot DB schema version {existing} is newer than this build supports " +
                $"(max {CurrentSchemaVersion}). Open with a newer UI build, or move the DB aside.");
        }

        // v0 → v1 (first install): create everything.
        if (existing == 0)
        {
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(conn, SchemaV1, ct, tx).ConfigureAwait(false);

            // Seed meta with schema version + identity.
            await SetMetaAsync(conn, tx, "schema_version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            await SetMetaAsync(conn, tx, "target_process", targetProcess ?? string.Empty, ct).ConfigureAwait(false);
            await SetMetaAsync(conn, tx, "game_build_id",  buildId, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        // Future: if (existing < 2) { … migrate to v2 … }
        return false;
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS snapshots (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            label               TEXT,
            captured_at_utc     TEXT    NOT NULL,
            game_build_id       TEXT    NOT NULL,
            game_session_id     TEXT    NOT NULL,
            target_process      TEXT    NOT NULL,
            backend             TEXT    NOT NULL,
            unity_version       TEXT,
            ptr_size            INTEGER,
            total_classes       INTEGER NOT NULL,
            total_instances     INTEGER NOT NULL,
            field_value_count   INTEGER NOT NULL,
            instance_cap        INTEGER,
            array_cap           INTEGER,
            include_unchanged   INTEGER NOT NULL DEFAULT 0,
            asm_filter          TEXT,
            payload_bytes       INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_snapshots_captured ON snapshots(captured_at_utc DESC);

        CREATE TABLE IF NOT EXISTS snapshot_classes (
            snapshot_id     INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
            assembly        TEXT    NOT NULL,
            class_fqn       TEXT    NOT NULL,
            instance_count  INTEGER NOT NULL,
            static_count    INTEGER NOT NULL,
            payload_gzip    BLOB    NOT NULL,
            PRIMARY KEY (snapshot_id, assembly, class_fqn)
        );
        CREATE INDEX IF NOT EXISTS ix_classes_fqn ON snapshot_classes(class_fqn);

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        """;

    /// <summary>Single-aggregate SUM of payload_bytes. <b>Caller must
    /// hold _gate</b> — used by PruneAsync inside its own lock so it
    /// doesn't recursively re-enter the semaphore.</summary>
    private async Task<long> SumPayloadBytesUnlockedAsync(CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(payload_bytes), 0) FROM snapshots;";
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0L : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Row count of snapshots table. Caller must hold _gate.</summary>
    private async Task<int> CountSnapshotsUnlockedAsync(CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Read a single-integer PRAGMA value. Caller must hold _gate.</summary>
    private async Task<long> ReadPragmaIntAsync(string pragmaName, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragmaName};";
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0L : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Quick existence check: if `meta` table is missing we're on a
        // fresh DB (effective version 0).
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='meta';";
            var name = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (name is null) return 0;
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (v is null or DBNull) return 0;
        return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static async Task SetMetaAsync(
        SqliteConnection conn, SqliteTransaction tx, string key, string value, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES ($k, $v)
              ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        Bind(cmd, "$k", key);
        Bind(cmd, "$v", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static byte[] SerializeAndGzip(SnapshotClassEntry entry)
    {
        // Use the source-gen JsonSerializerContext so this stays AOT-safe.
        // Default options match what the wire deserialiser used — no extra
        // shape change, blob round-trips back into the same DTO.
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            entry, ProtocolJsonContext.Default.SnapshotClassEntry);

        using var ms  = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gz.Write(jsonBytes, 0, jsonBytes.Length);
        }
        return ms.ToArray();
    }

    private static SnapshotClassEntry? DeserializeGzipped(Stream gzipped)
    {
        using var gz = new GZipStream(gzipped, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gz, ProtocolJsonContext.Default.SnapshotClassEntry);
    }

    private static Task ExecuteAsync(
        SqliteConnection conn, string sql, CancellationToken ct, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        return cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static string SanitiseForFilename(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                sb.Append(c);
            else
                sb.Append('_');
        }
        if (sb.Length == 0) sb.Append("unknown");
        return sb.ToString();
    }
}

/// <summary>Inputs for <see cref="SnapshotStore.SaveSnapshotAsync"/>.
/// Plain DTO so the caller can populate without touching SQLite types.</summary>
public sealed class SnapshotPersistRequest
{
    public string?                   Label            { get; init; }
    public required string           CapturedAtUtc    { get; init; }
    public required string           GameBuildId      { get; init; }
    public required string           GameSessionId    { get; init; }
    public required string           TargetProcess    { get; init; }
    public required string           Backend          { get; init; }   // "mono" | "il2cpp"
    public string?                   UnityVersion     { get; init; }
    public int?                      PointerSize      { get; init; }
    public required int              TotalClasses     { get; init; }
    public required int              TotalInstances   { get; init; }
    public required int              FieldValueCount  { get; init; }
    public int?                      InstanceCap      { get; init; }
    public int?                      ArrayCap         { get; init; }
    public bool                      IncludeUnchanged { get; init; }
    public string?                   AssemblyFilter   { get; init; }
    public required List<SnapshotClassEntry> Classes  { get; init; }
}

/// <summary>Outcome of <see cref="SnapshotStore.PruneAsync"/>. Surfaced
/// in the snapshot tab status line so the user sees the prune fire.</summary>
public readonly record struct PruneResult(int Removed, long FreedBytes);

/// <summary>One row of the <c>snapshots</c> table — every column except
/// the per-class blobs. Drives the Snapshot tab's lazy metadata list.</summary>
/// <summary>A class identity (assembly + FQN) read straight from the
/// <c>snapshot_classes</c> metadata columns — no gzipped blob load.
/// Powers the Class Pivot tab's class / assembly autocomplete before
/// any snapshot is hydrated (Phase 29e-7).</summary>
public sealed class PersistedClassRef
{
    public required string Assembly { get; init; }
    public required string ClassFqn { get; init; }
}

public sealed class PersistedSnapshotMeta
{
    public required long    Id               { get; init; }
    public string?          Label            { get; init; }
    public required string  CapturedAtUtc    { get; init; }
    public required string  GameBuildId      { get; init; }
    public required string  GameSessionId    { get; init; }
    public required string  TargetProcess    { get; init; }
    public required string  Backend          { get; init; }
    public string?          UnityVersion     { get; init; }
    public int?             PointerSize      { get; init; }
    public required int     TotalClasses     { get; init; }
    public required int     TotalInstances   { get; init; }
    public required int     FieldValueCount  { get; init; }
    public int?             InstanceCap      { get; init; }
    public int?             ArrayCap         { get; init; }
    public required bool    IncludeUnchanged { get; init; }
    public string?          AssemblyFilter   { get; init; }
    public required long    PayloadBytes     { get; init; }
}
