// PlayerLocator.cs — Teleport auto-detect wave A. Multi-signal "which instance
// is the player?" candidate scorer. Unity has no engine-level player chain
// (see teleport.md), so instead of one universal resolver this runs several
// cheap, independent signals and ranks the union:
//
//   A  camera anchor    Camera.get_main -> transform -> parent chain (players
//                       parent the camera in FPS / many TPS rigs) + the camera
//                       position anchors distance scoring for every candidate
//   B  tag lookup       GameObject.FindWithTag("Player") — authoritative when
//                       the game uses the built-in tag; stripped on many
//                       IL2CPP builds -> any error just disables the signal
//   C  CharacterController enumeration — games that use a CC almost always
//                       have exactly one: the player
//   D  well-known names GameObject.Find over common player object names
//   E  user-class names offline class-list filter (Assembly-CSharp etc.) ->
//                       findInstances(aliveOnly, sane) -> classes with 1-3
//                       live instances resolve their Transform
//
// Pure composition over BackendAdapter (same idiom as SceneEnumerator) — zero
// DLL changes. Every signal degrades gracefully: a stripped / missing engine
// API contributes no candidates instead of failing the run. All engine icalls
// are mainThread:true (Phase 20-MT dispatcher); the run aborts early with the
// focus hint when the dispatcher drain-timeout fires (runInBackground pause).
//
// Calls are strictly sequential so the wire behaviour is deterministic and
// testable with the queued fake-pipe harness.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

/// <summary>One ranked auto-detect result. <see cref="TransformAddress"/> is
/// already resolved — feed it through the normal Set Target path with
/// targetIsGameObject=false (Component.get_transform on a Transform returns
/// itself, revalidating the pointer for free).</summary>
public sealed class PlayerCandidate
{
    public string  TransformAddress  { get; init; } = string.Empty;
    public string? GameObjectAddress { get; init; }
    /// <summary>The user-class instance behind signal E hits; null otherwise.</summary>
    public string? InstanceAddress   { get; init; }
    /// <summary>GameObject name ("" when unreadable).</summary>
    public string  Name              { get; init; } = string.Empty;
    /// <summary>Class behind the hit ("CharacterController", user class, …).</summary>
    public string? ClassName         { get; init; }
    /// <summary>Signals that produced this candidate (merged duplicates).</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    public int     Score             { get; init; }
    public double? DistanceToCamera  { get; init; }
    public (double X, double Y, double Z)? Position { get; init; }
    /// <summary>How much to trust this pick — see <see cref="PlayerConfidence"/>.</summary>
    public PlayerConfidence Confidence { get; init; }
    /// <summary>Displacement magnitude from a movement-detection run (null for
    /// the static scorer). The mover is the player.</summary>
    public double? MovedDistance { get; init; }
}

/// <summary>How trustworthy an auto-detect pick is. Camera-chain-only hits are
/// <see cref="Low"/> — usually a camera rig, not the player.</summary>
public enum PlayerConfidence
{
    /// <summary>Only the camera chain fired (or a demoted camera-rig name) —
    /// probably not the player. Prompt the user to move-detect or pick manually.</summary>
    Low = 0,
    /// <summary>One corroborating name/class signal, or a moderate score.</summary>
    Medium = 1,
    /// <summary>A strong signal (tag / CharacterController / single-instance user
    /// class), or movement-confirmed, or multiple signals agree.</summary>
    High = 2,
}

/// <summary>Per-signal toggles + caps. Defaults are the wave A shipping shape;
/// tests flip signals off to pin one wire sequence at a time.</summary>
public sealed class PlayerLocateOptions
{
    public bool EnableCameraChain         { get; init; } = true;
    public bool EnableTagLookup           { get; init; } = true;
    public bool EnableCharacterController { get; init; } = true;
    public bool EnableWellKnownNames      { get; init; } = true;
    public bool EnableUserClassScan       { get; init; } = true;

    /// <summary>How far up the camera's parent chain to walk.</summary>
    public int MaxParentChainDepth { get; init; } = 8;
    /// <summary>Heap-scan budget for signal E (findInstances is per-class).</summary>
    public int MaxUserClassScans   { get; init; } = 8;
    /// <summary>Game assemblies to getClasses over (Assembly-CSharp* first).</summary>
    public int MaxAssemblies       { get; init; } = 12;
    /// <summary>Ranked rows returned to the UI.</summary>
    public int MaxCandidates       { get; init; } = 12;
}

/// <summary>Result envelope. <see cref="Ok"/>=false only on a whole-run abort
/// (dispatcher drain timeout); "no candidates" is Ok=true + empty list.</summary>
public sealed class PlayerLocateResult
{
    public bool    Ok    { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<PlayerCandidate> Candidates { get; init; } = Array.Empty<PlayerCandidate>();
    public string? CameraTransformAddress { get; init; }
    public (double X, double Y, double Z)? CameraPosition { get; init; }
    /// <summary>Per-signal soft notes (unavailable APIs, hit counts) for the log.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Best candidate's confidence — drives the "maybe pick manually /
    /// move-detect" guidance. <see cref="PlayerConfidence.Low"/> when the only
    /// hit is the camera chain.</summary>
    public PlayerConfidence TopConfidence =>
        Candidates.Count == 0 ? PlayerConfidence.Low : Candidates[0].Confidence;
}

/// <summary>Options for movement-correlation detection (the robust disambiguator
/// for games where static signals don't fire).</summary>
public sealed class MovementDetectOptions
{
    /// <summary>Static signals that seed the sample pool before the broad
    /// scene sweep. Same defaults as <see cref="PlayerLocateOptions"/>.</summary>
    public PlayerLocateOptions Signals { get; init; } = new();

    /// <summary>Also fold in a capped <c>FindObjectsByType(Transform)</c> sweep
    /// so the player is in the pool even when no static signal named it. This
    /// is the expensive part (one get_position per pooled transform × 2 passes),
    /// so it's capped.</summary>
    public bool SweepAllTransforms { get; init; } = true;

    /// <summary>Max transforms to sample from the broad sweep. Each costs two
    /// main-thread get_position calls; keep it to a few seconds of budget. The
    /// scene may hold far more objects — a strided sample covers the whole
    /// array sparsely but can still miss a specific object (surfaced to the UI).</summary>
    public int MaxSweepTransforms { get; init; } = 600;

    /// <summary>Minimum displacement (world units) to count as "moved" — filters
    /// idle jitter / animation micro-motion.</summary>
    public double MinMoveDistance { get; init; } = 0.20;

    /// <summary>After ranking movers, walk their parent chains and surface the
    /// common-ancestor "root" of a moving cluster as the primary candidate.
    /// Animated characters expose their skeleton, so the raw top movers are the
    /// leaf bones (skirt / hair / foot) that swing MOST — teleporting a bone
    /// just deforms the mesh; the thing to teleport is the root above them.</summary>
    public bool ResolveToRoot { get; init; } = true;

    /// <summary>How many top movers to walk up when resolving roots.</summary>
    public int MaxRootWalkMovers { get; init; } = 8;

    /// <summary>Max parent-chain depth to climb (skeletons can be deep; a cache
    /// makes shared ancestors cheap).</summary>
    public int MaxRootWalkDepth { get; init; } = 64;

    /// <summary>How many rungs of the DCA→root ladder to present.</summary>
    public int MaxRootLadder { get; init; } = 6;

    /// <summary>Ranked rows returned.</summary>
    public int MaxCandidates { get; init; } = 12;
}

/// <summary>One tracked transform between the two movement samples.</summary>
public sealed class MovementTrack
{
    public string TransformAddress { get; init; } = string.Empty;
    public string? GameObjectAddress { get; init; }
    public string? InstanceAddress   { get; init; }
    public string? ClassName         { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    /// <summary>True for the main camera's transform — excluded from movers
    /// (it moves whenever the user rotates the view).</summary>
    public bool IsCamera { get; init; }
    public (double X, double Y, double Z) Start { get; init; }
}

/// <summary>Phase-1 output of movement detection: the pool + start positions.
/// Hold it, let the user move, then pass it to
/// <see cref="PlayerLocator.FinishMovementSampleAsync"/>.</summary>
public sealed class MovementSample
{
    public bool    Ok    { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<MovementTrack> Tracks { get; init; } = Array.Empty<MovementTrack>();
    public int     SceneTransformTotal { get; init; }
    public bool    Truncated { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    internal PlayerLocator? Owner { get; init; }
}

/// <summary>One raw signal hit before merge / enrichment.</summary>
public sealed class PlayerSignalHit
{
    public PlayerSignalHit(string transformAddress, string source, int score,
        string? gameObjectAddress = null, string? instanceAddress = null, string? className = null)
    {
        TransformAddress  = transformAddress;
        Source            = source;
        Score             = score;
        GameObjectAddress = gameObjectAddress;
        InstanceAddress   = instanceAddress;
        ClassName         = className;
    }

    public string  TransformAddress  { get; }
    public string  Source            { get; }
    public int     Score             { get; }
    public string? GameObjectAddress { get; }
    public string? InstanceAddress   { get; }
    public string? ClassName         { get; }
}

/// <summary>Pure merge / scoring rules — no pipe, fully unit-testable.</summary>
public static class PlayerLocatorRanking
{
    public const int ScoreTag              = 100;
    public const int ScoreCharController   = 80;
    public const int ScoreExactPlayerName  = 70;
    public const int ScoreCameraChain      = 40;   // weak: often a camera rig
    public const int ScoreWellKnownName    = 50;
    public const int ScoreSingleInstance   = 50;
    public const int ScoreFewInstances     = 30;

    /// <summary>Subtracted from a candidate whose GameObject name reads as a
    /// camera rig — enough to sink a camera-chain-only hit below any real
    /// player signal, but recoverable if a strong signal also tagged it.</summary>
    public const int CameraRigNamePenalty  = 45;

    /// <summary>Camera-proximity bonus: the player is nearly always the
    /// player-ish object closest to the main camera.</summary>
    public static int DistanceBonus(double? distance) => distance switch
    {
        null   => 0,
        < 2.0  => 30,
        < 10.0 => 20,
        < 30.0 => 10,
        _      => 0,
    };

    /// <summary>Merged (pre-enrichment) candidate: same Transform hit by
    /// several signal families sums their scores — combined evidence beats any
    /// single name hit. Within ONE family only the best hit counts: two
    /// GameObject.Find aliases (or two player-ish classes on one GameObject)
    /// are correlated evidence, not independent confirmation.</summary>
    public sealed class Merged
    {
        public string  TransformAddress  { get; init; } = string.Empty;
        public string? GameObjectAddress { get; set; }
        public string? InstanceAddress   { get; set; }
        public string? ClassName         { get; set; }
        public List<string> Sources      { get; } = new();
        public int     Score             { get; set; }

        internal Dictionary<string, int> FamilyBest { get; } = new(StringComparer.Ordinal);
    }

    public static IReadOnlyList<Merged> Merge(IEnumerable<PlayerSignalHit> hits)
    {
        var byTransform = new Dictionary<string, Merged>(StringComparer.OrdinalIgnoreCase);
        var ordered     = new List<Merged>();
        foreach (PlayerSignalHit h in hits)
        {
            if (string.IsNullOrEmpty(h.TransformAddress)) continue;
            if (!byTransform.TryGetValue(h.TransformAddress, out Merged? m))
            {
                m = new Merged { TransformAddress = h.TransformAddress };
                byTransform[h.TransformAddress] = m;
                ordered.Add(m);
            }

            string family = FamilyOf(h.Source);
            if (m.FamilyBest.TryGetValue(family, out int best))
            {
                if (h.Score > best)
                {
                    m.Score += h.Score - best;
                    m.FamilyBest[family] = h.Score;
                }
            }
            else
            {
                m.FamilyBest[family] = h.Score;
                m.Score += h.Score;
            }

            if (!m.Sources.Contains(h.Source)) m.Sources.Add(h.Source);
            m.GameObjectAddress ??= h.GameObjectAddress;
            m.InstanceAddress   ??= h.InstanceAddress;
            m.ClassName         ??= h.ClassName;
        }
        return ordered;
    }

    /// <summary>"name:\"Player\"" and "name:\"player\"" are one family
    /// ("name"); "camera-chain" / "CharacterController" are their own.</summary>
    private static string FamilyOf(string source)
    {
        int i = source.IndexOf(':');
        return i < 0 ? source : source[..i];
    }
}

/// <summary>Whole-word token matching, shared by the class-name keyword filter
/// and the camera-rig name detector. Short tokens (chr/cam/rig/…) must match a
/// whole tokenized word, not a fragment of a longer one — else "chr" hits
/// "Archer", "cam" hits "Cameron", "rig" hits "Frigate", "boom" hits "Boomer".
/// Tokenizing on separators AND camelCase / digit edges keeps "FollowCam" and
/// "PlayerRig" matching while "Cameron"/"Mountain" don't.</summary>
internal static class NameTokenMatch
{
    /// <summary>Split into lowercase alphanumeric tokens on any non-alphanumeric
    /// char, on lower→upper camelCase transitions, and on letter↔digit edges.
    /// "MainCameraRig" → [main,camera,rig]; "CM vcam1" → [cm,vcam,1];
    /// "Player_Chr02" → [player,chr,02].</summary>
    public static List<string> Tokenize(string name)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(name)) return tokens;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString().ToLowerInvariant()); sb.Clear(); }
                continue;
            }
            if (sb.Length > 0)
            {
                char prev = name[i - 1];
                bool camel     = char.IsLower(prev) && char.IsUpper(c);
                bool digitEdge = char.IsDigit(prev) != char.IsDigit(c);
                if (camel || digitEdge) { tokens.Add(sb.ToString().ToLowerInvariant()); sb.Clear(); }
            }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString().ToLowerInvariant());
        return tokens;
    }

    /// <summary>True when any whole token equals <paramref name="token"/>
    /// (which must already be lowercase).</summary>
    public static bool HasToken(string name, string token)
    {
        foreach (string t in Tokenize(name))
        {
            if (t == token) return true;
        }
        return false;
    }
}

/// <summary>Signal E's offline class-name filter — pure, unit-testable.</summary>
public static class PlayerClassNameFilter
{
    public sealed class ClassCandidate
    {
        public string  Assembly  { get; init; } = string.Empty;
        public string? Namespace { get; init; }
        public string  Name      { get; init; } = string.Empty;
        /// <summary>Lower = better name quality (0 = exact "Player").</summary>
        public int     Rank      { get; init; }
    }

    // Player-ish naming, incl. common Japanese-game conventions (chara/chr/unit
    // /actor). "playable" is dropped — it over-matches Unity's PlayableDirector
    // /PlayableGraph, not the pawn. Whole-word-ish short tokens (chr/pc) are
    // matched with boundary care in Select to avoid "Archer"/"Purchase" hits.
    private static readonly string[] Keywords =
        { "player", "hero", "protagonist", "avatar", "chara", "character",
          "actor", "pawn", "unit" };

    // Short tokens that need word-boundary matching (else "chr" hits "Archer",
    // "pc" hits "Purchase"). Matched as a standalone token or with a digit.
    private static readonly string[] ShortTokens = { "chr", "pc" };

    // Classes that NAME the player but aren't the pawn: managers, data bags,
    // UI, settings. Substring, case-insensitive.
    private static readonly string[] Exclusions =
    {
        "manager", "settings", "config", "data", "info", "save", "pref",
        "profile", "stats", "ui", "hud", "menu", "icon", "audio", "sound",
        "input", "network", "spawn", "factory", "pool", "event", "util",
        "helper", "editor", "debug", "test", "camera", "animation", "anim",
        "inventory", "achievement", "shop", "dialog", "state",
    };

    public static IReadOnlyList<ClassCandidate> Select(
        IEnumerable<(string Assembly, string? Namespace, string Name)> classes, int cap)
    {
        var picked = new List<ClassCandidate>();
        foreach ((string asm, string? ns, string name) in classes)
        {
            if (string.IsNullOrEmpty(name)) continue;
            // Compiler-generated / generic definitions can't be the pawn.
            if (name.IndexOfAny(new[] { '<', '`', '$' }) >= 0) continue;

            string lower = name.ToLowerInvariant();
            if (!MatchesKeyword(name, lower)) continue;

            bool excluded = false;
            foreach (string e in Exclusions)
            {
                if (lower.Contains(e, StringComparison.Ordinal)) { excluded = true; break; }
            }
            if (excluded) continue;

            // Rank: exact "Player" is the gold hit; then any player* name; then
            // the strong pawn synonyms; then the rest. Lower = better.
            int rank =
                string.Equals(name, "Player", StringComparison.OrdinalIgnoreCase) ? 0 :
                lower.StartsWith("player", StringComparison.Ordinal)              ? 1 :
                lower.Contains("player", StringComparison.Ordinal)                ? 2 :
                (lower.Contains("hero", StringComparison.Ordinal)
                 || lower.Contains("protagonist", StringComparison.Ordinal))     ? 3 : 4;
            picked.Add(new ClassCandidate { Assembly = asm, Namespace = ns, Name = name, Rank = rank });
        }

        return picked
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.Name.Length)            // "Player" beats "PlayerLocomotionDriver"
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, cap))
            .ToList();
    }

    // `name` is the original (for camelCase tokenization); `lower` is its
    // lower-cased form (for the substring keyword scan).
    private static bool MatchesKeyword(string name, string lower)
    {
        foreach (string k in Keywords)
        {
            if (lower.Contains(k, StringComparison.Ordinal)) return true;
        }
        // Short tokens (chr/pc) only match a whole tokenized word, else "chr"
        // hits "Archer", "pc" hits "Purchase". Tokenize the ORIGINAL name so a
        // camelCase boundary ("MapChr" → chr) is preserved.
        foreach (string t in ShortTokens)
        {
            if (NameTokenMatch.HasToken(name, t)) return true;
        }
        return false;
    }
}

/// <summary>
/// The wave A auto-detect runner. Construct per run or reuse — the only cached
/// state is the resolved engine assembly (CoreModule vs monolithic), which is
/// stable for a process lifetime.
/// </summary>
public sealed class PlayerLocator
{
    private static readonly string[] EngineAssemblyCandidates =
        { "UnityEngine.CoreModule", "UnityEngine" };

    private const string EngineNamespace = "UnityEngine";
    private const int    Vector3Bytes    = 12;

    // Signal C: CharacterController lives in PhysicsModule on module-split
    // Unity (5.4+), in the monolithic UnityEngine assembly before that.
    private static readonly string[] CharacterControllerFqns =
    {
        "UnityEngine.CharacterController, UnityEngine.PhysicsModule",
        "UnityEngine.CharacterController, UnityEngine",
    };

    // Signal D: exact GameObject names games commonly give the player pawn.
    // GameObject.Find is exact-match + active-only, so aliases cost one icall
    // each. "PlayerArmature" is Unity's official Starter Assets rig name.
    private static readonly string[] WellKnownNames =
    {
        "Player", "player", "PLAYER", "Player1", "Player 1",
        "PlayerObject", "PlayerArmature", "PlayerCharacter", "PlayerController",
        "Character", "MainCharacter", "Main Character", "Hero", "Avatar",
    };

    // Signal E: assemblies that can't contain game code.
    private static readonly string[] NonGameAssemblyPrefixes =
    {
        "mscorlib", "System", "netstandard", "Mono.", "Microsoft.",
        "UnityEngine", "Unity.", "UnityEditor", "Newtonsoft",
    };

    // Camera-rig object names — the camera-chain signal walks the camera's
    // parents, and its immediate parent is very often a rig / holder, NOT the
    // player. A candidate whose name matches this is demoted so it stops
    // winning by default when it's the only hit.
    //
    // Long tokens are safe as plain substrings; short tokens must match at a
    // word boundary (else "cam" hits "Cameron", "rig" hits "Frigate", "mount"
    // hits "Mountain", "eye" hits "Redeye", "boom" hits "Boomer").
    private static readonly string[] CameraRigLongTokens =
    {
        "camera", "cinemachine", "vcam", "cmvcam", "freelook", "viewpoint",
        "lookat", "holder", "dolly", "crane", "gimbal", "socket", "pivot",
    };
    private static readonly string[] CameraRigShortTokens =
    {
        "cam", "rig", "boom", "mount", "eye", "pov",
    };

    private static readonly IReadOnlyList<CallArgRequest> NoArgs = new List<CallArgRequest>();

    private readonly BackendAdapter _backend;
    private readonly FileLogger?    _logger;
    private readonly int            _mainThreadTimeoutMs;

    private string? _resolvedEngineAssembly;
    private bool    _drainTimeout;

    public PlayerLocator(BackendAdapter backend, FileLogger? logger = null,
                         int mainThreadTimeoutMs = 8000)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger  = logger;
        _mainThreadTimeoutMs = mainThreadTimeoutMs;
    }

    public async Task<PlayerLocateResult> LocateAsync(
        PlayerLocateOptions? options = null, CancellationToken ct = default)
    {
        PlayerLocateOptions opt = options ?? new PlayerLocateOptions();
        _drainTimeout = false;

        var hits  = new List<PlayerSignalHit>();
        var notes = new List<string>();

        (string? camTransform, (double X, double Y, double Z)? camPos, bool aborted) =
            await GatherSignalsAsync(opt, hits, notes, ct).ConfigureAwait(false);
        if (aborted) return Abort();

        // --- merge + enrich + rank ------------------------------------------
        IReadOnlyList<PlayerLocatorRanking.Merged> merged = PlayerLocatorRanking.Merge(hits);
        // Enrichment costs ~3 icalls per candidate — bound it to the plausible
        // top of the pre-score order (ranked rows cap lower still).
        var top = merged.OrderByDescending(m => m.Score).Take(24).ToList();

        var final = new List<PlayerCandidate>(top.Count);
        foreach (PlayerLocatorRanking.Merged m in top)
        {
            ct.ThrowIfCancellationRequested();
            PlayerCandidate c = await EnrichAsync(m, camPos, ct).ConfigureAwait(false);
            if (_drainTimeout) return Abort();
            final.Add(c);
        }

        var ranked = final
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.DistanceToCamera ?? double.MaxValue)
            .ThenBy(c => c.TransformAddress, StringComparer.Ordinal)
            .Take(Math.Max(1, opt.MaxCandidates))
            .ToList();

        PlayerConfidence topConf = ranked.Count == 0 ? PlayerConfidence.Low : ranked[0].Confidence;
        Log($"PlayerLocator: {hits.Count} raw hits -> {merged.Count} unique -> {ranked.Count} ranked "
            + $"(top confidence {topConf}). " + string.Join(" | ", notes));

        return new PlayerLocateResult
        {
            Ok = true,
            Candidates = ranked,
            CameraTransformAddress = camTransform,
            CameraPosition = camPos,
            Notes = notes,
        };
    }

    /// <summary>Guidance shown when static auto-detect can't confidently name the
    /// player (only the camera fired, or the top hit is a camera rig). Points at
    /// the movement-detection fallback.</summary>
    public const string LowConfidenceHint =
        "Low confidence — the only thing found looks like the camera, not the player. "
        + "This game doesn't expose the usual player markers (no \"Player\" tag / "
        + "CharacterController / player-named object). Use \"Detect by movement\" "
        + "(move your character while it samples) or pick the target manually.";

    // Run signals A-E into `hits`; returns the camera transform+position and an
    // abort flag (dispatcher drain timeout). Shared by LocateAsync and movement
    // detection (whose seed pool is the same signal set).
    private async Task<(string? camTransform, (double X, double Y, double Z)? camPos, bool aborted)>
        GatherSignalsAsync(PlayerLocateOptions opt, List<PlayerSignalHit> hits,
                           List<string> notes, CancellationToken ct)
    {
        string? camTransform = null;
        (double X, double Y, double Z)? camPos = null;

        if (opt.EnableCameraChain)
        {
            (camTransform, camPos) = await CameraAnchorAsync(hits, notes, opt, ct).ConfigureAwait(false);
            if (_drainTimeout) return (camTransform, camPos, true);
        }
        if (opt.EnableTagLookup)
        {
            await TagLookupAsync(hits, notes, ct).ConfigureAwait(false);
            if (_drainTimeout) return (camTransform, camPos, true);
        }
        if (opt.EnableCharacterController)
        {
            await CharacterControllerAsync(hits, notes, ct).ConfigureAwait(false);
            if (_drainTimeout) return (camTransform, camPos, true);
        }
        if (opt.EnableWellKnownNames)
        {
            await WellKnownNamesAsync(hits, notes, ct).ConfigureAwait(false);
            if (_drainTimeout) return (camTransform, camPos, true);
        }
        if (opt.EnableUserClassScan)
        {
            await UserClassScanAsync(hits, notes, opt, ct).ConfigureAwait(false);
            if (_drainTimeout) return (camTransform, camPos, true);
        }
        return (camTransform, camPos, false);
    }

    // ------------------------------------------------------------------
    // Movement-correlation detection (the robust disambiguator)
    // ------------------------------------------------------------------

    /// <summary>
    /// Phase 1 of movement detection: build a pool of transforms (the static
    /// signals' hits + the camera + a capped <c>FindObjectsByType(Transform)</c>
    /// sweep) and record each one's start position. The caller then lets the
    /// user move their character for a moment and calls
    /// <see cref="FinishMovementSampleAsync"/> with the returned sample.
    /// </summary>
    public async Task<MovementSample> BeginMovementSampleAsync(
        MovementDetectOptions? options = null, CancellationToken ct = default)
    {
        MovementDetectOptions opt = options ?? new MovementDetectOptions();
        _drainTimeout = false;

        var seedHits = new List<PlayerSignalHit>();
        var notes    = new List<string>();
        (string? camTransform, _, bool aborted) =
            await GatherSignalsAsync(opt.Signals, seedHits, notes, ct).ConfigureAwait(false);
        if (aborted) return new MovementSample { Ok = false, Error = DrainTimeoutHint };

        // Pool = every transform we can name from the signals, keyed by address.
        var poolMeta = new Dictionary<string, PlayerSignalHit>(StringComparer.OrdinalIgnoreCase);
        foreach (PlayerSignalHit h in seedHits)
            poolMeta.TryAdd(h.TransformAddress, h);

        int sceneTotal = 0;
        bool truncated = false;
        if (opt.SweepAllTransforms)
        {
            (IReadOnlyList<string> transforms, sceneTotal, truncated) =
                await EnumerateSceneTransformsAsync(opt.MaxSweepTransforms, ct).ConfigureAwait(false);
            if (_drainTimeout) return new MovementSample { Ok = false, Error = DrainTimeoutHint };
            foreach (string t in transforms)
                poolMeta.TryAdd(t, null!);   // sweep entries have no signal metadata
            notes.Add($"sweep: {transforms.Count} of {sceneTotal} scene transforms"
                + (truncated ? " (capped)" : string.Empty));
        }

        // Capture t0 for every pooled transform.
        var tracks = new List<MovementTrack>(poolMeta.Count);
        foreach (KeyValuePair<string, PlayerSignalHit> kv in poolMeta)
        {
            ct.ThrowIfCancellationRequested();
            (double, double, double)? p = await ReadTransformPositionAsync(kv.Key, ct).ConfigureAwait(false);
            if (_drainTimeout) return new MovementSample { Ok = false, Error = DrainTimeoutHint };
            if (p is not { } start) continue;
            PlayerSignalHit? meta = kv.Value;
            tracks.Add(new MovementTrack
            {
                TransformAddress  = kv.Key,
                GameObjectAddress = meta?.GameObjectAddress,
                InstanceAddress   = meta?.InstanceAddress,
                ClassName         = meta?.ClassName,
                Sources           = meta is null ? Array.Empty<string>() : new[] { meta.Source },
                IsCamera          = camTransform is not null
                                    && string.Equals(kv.Key, camTransform, StringComparison.OrdinalIgnoreCase),
                Start             = start,
            });
        }

        Log($"PlayerLocator movement: pool {tracks.Count} transforms "
            + $"(sweep {(opt.SweepAllTransforms ? sceneTotal.ToString(CultureInfo.InvariantCulture) : "off")}). "
            + string.Join(" | ", notes));

        return new MovementSample
        {
            Ok = true,
            Tracks = tracks,
            SceneTransformTotal = sceneTotal,
            Truncated = truncated,
            Notes = notes,
            Owner = this,
        };
    }

    /// <summary>
    /// Phase 2 of movement detection: re-read every tracked transform's position
    /// and rank by how far it moved. The camera transform is excluded (it moves
    /// on view rotation); movers below <see cref="MovementDetectOptions.MinMoveDistance"/>
    /// are dropped. The top movers are enriched with names + returned as
    /// High-confidence candidates.
    /// </summary>
    public async Task<PlayerLocateResult> FinishMovementSampleAsync(
        MovementSample sample, MovementDetectOptions? options = null, CancellationToken ct = default)
    {
        if (sample is null || !sample.Ok)
            return new PlayerLocateResult { Ok = false, Error = sample?.Error ?? "no movement sample" };

        MovementDetectOptions opt = options ?? new MovementDetectOptions();
        _drainTimeout = false;

        var movers = new List<(MovementTrack Track, double Dist, (double, double, double) End)>();
        foreach (MovementTrack track in sample.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (track.IsCamera) continue;   // camera moves on view rotation
            (double, double, double)? p = await ReadTransformPositionAsync(track.TransformAddress, ct).ConfigureAwait(false);
            if (_drainTimeout) return Abort();
            if (p is not { } end) continue;
            double dx = end.Item1 - track.Start.X;
            double dy = end.Item2 - track.Start.Y;
            double dz = end.Item3 - track.Start.Z;
            double dist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (dist >= opt.MinMoveDistance) movers.Add((track, dist, end));
        }

        // Take the top movers by raw displacement first (bounds how many get the
        // costly name enrichment), then re-rank by the penalty-adjusted score so
        // a rig-named follow-cam that swings a slightly wider arc can't out-rank
        // the real player it tracks.
        var ordered = movers
            .OrderByDescending(m => m.Dist)
            .Take(Math.Max(1, opt.MaxCandidates))
            .ToList();

        var candidates = new List<PlayerCandidate>(ordered.Count);
        foreach (var mv in ordered)
        {
            ct.ThrowIfCancellationRequested();
            (string? go, string name) = await ResolveNameAsync(
                mv.Track.GameObjectAddress, mv.Track.TransformAddress, ct).ConfigureAwait(false);
            if (_drainTimeout) return Abort();

            // A mover named like a camera rig is still suspicious (a follow-cam
            // that tracks the player moves too) — demote but keep it visible.
            bool rig = IsCameraRigName(name);
            candidates.Add(new PlayerCandidate
            {
                TransformAddress  = mv.Track.TransformAddress,
                GameObjectAddress = go,
                InstanceAddress   = mv.Track.InstanceAddress,
                Name              = name,
                ClassName         = mv.Track.ClassName,
                Sources           = mv.Track.Sources.Count > 0
                                    ? new List<string>(mv.Track.Sources) { "moved" }
                                    : new[] { "moved" },
                Score             = (int)Math.Round(mv.Dist * 10) + (rig ? -PlayerLocatorRanking.CameraRigNamePenalty : 0),
                Position          = mv.End,
                MovedDistance     = mv.Dist,
                Confidence        = rig ? PlayerConfidence.Medium : PlayerConfidence.High,
            });
        }

        candidates = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.MovedDistance ?? 0d)
            .ThenBy(c => c.TransformAddress, StringComparer.Ordinal)
            .ToList();

        // Resolve the moving bones up to their shared root — that's what you
        // teleport, not a leaf bone. Roots rank above the raw bones.
        string rootNote = string.Empty;
        if (opt.ResolveToRoot && candidates.Count > 0)
        {
            List<PlayerCandidate> roots =
                await ResolveMovementRootsAsync(candidates, opt, ct).ConfigureAwait(false);
            if (_drainTimeout) return Abort();
            if (roots.Count > 0)
            {
                var rootAddrs = new HashSet<string>(
                    roots.Select(r => r.TransformAddress), StringComparer.OrdinalIgnoreCase);
                // Drop any bone row that IS one of the surfaced roots (dedup),
                // keep the rest as evidence below the roots.
                var bones = candidates.Where(c => !rootAddrs.Contains(c.TransformAddress)).ToList();
                candidates = roots.Concat(bones)
                    .Take(Math.Max(1, opt.MaxCandidates))
                    .ToList();
                rootNote = $"; resolved {roots.Count} root(s) above the moving bones";
            }
        }

        Log($"PlayerLocator movement finish: {movers.Count} moved (of {sample.Tracks.Count} tracked), "
            + $"top {candidates.Count} returned{rootNote}.");

        return new PlayerLocateResult
        {
            Ok = true,
            Candidates = candidates,
            Notes = new[] { $"{movers.Count} object(s) moved of {sample.Tracks.Count} tracked{rootNote}" },
        };
    }

    /// <summary>
    /// Walk the top movers' parent chains and surface the common-ancestor
    /// "root" of the dominant moving cluster. Animated characters expose their
    /// skeleton, so the raw top movers are the leaf bones that swing most;
    /// their shared ancestors — the skeleton / character root — are the real
    /// teleport targets. Returns a DCA→root ladder (tightest common ancestor
    /// first), name-enriched and scored above the bones.
    /// </summary>
    private async Task<List<PlayerCandidate>> ResolveMovementRootsAsync(
        IReadOnlyList<PlayerCandidate> movers, MovementDetectOptions opt, CancellationToken ct)
    {
        var empty = new List<PlayerCandidate>();

        // A single moving object has no "shared ancestor" — resolving it would
        // just re-surface the object itself (or promote its scene container).
        // Leave the raw mover row in place.
        var top = movers.Take(Math.Max(1, opt.MaxRootWalkMovers)).ToList();
        if (top.Count < 2) return empty;

        var parentCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var chains = new List<(List<string> Addrs, double Dist)>();
        foreach (PlayerCandidate m in top)
        {
            ct.ThrowIfCancellationRequested();
            (List<string> addrs, bool ok) = await WalkParentChainAsync(m.TransformAddress, parentCache, opt, ct)
                .ConfigureAwait(false);
            if (_drainTimeout) return empty;
            // A walk that hit a get_parent ERROR (not a genuine 0x0 root) is
            // incomplete — its terminal node is a false root that would seed a
            // bogus cluster, so drop it from clustering.
            if (ok && addrs.Count >= 2) chains.Add((addrs, m.MovedDistance ?? 0));
        }
        if (chains.Count < 2) return empty;

        // Anchor on the highest-displacement chain (chains preserve the movers'
        // displacement order) — that's the object the user deliberately moved,
        // so we resolve ITS character rather than whichever unrelated hierarchy
        // happens to contribute the most sampled bones.
        (List<string> Addrs, double Dist) anchor = chains[0];
        string anchorRoot = anchor.Addrs[^1];
        var cluster = chains.Where(c => Eq(c.Addrs[^1], anchorRoot)).ToList();
        if (cluster.Count < 2) return empty;

        // Exclude LEAF movers (a mover not itself an ancestor of another mover)
        // from the ladder so the top rung is a genuine ancestor, never a swinging
        // bone. A mover that IS an ancestor of another mover stays eligible — it
        // may be the real (moving) character root.
        var moverAddrs  = new HashSet<string>(cluster.Select(c => c.Addrs[0]), StringComparer.OrdinalIgnoreCase);
        var internalMov = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((List<string> addrs, _) in cluster)
        {
            for (int i = 1; i < addrs.Count; i++)
                if (moverAddrs.Contains(addrs[i])) internalMov.Add(addrs[i]);
        }

        // Shared ancestors = nodes in EVERY cluster chain (minus leaf movers),
        // ordered by proximity to the movers (anchor index → DCA first, root last).
        var shared = anchor.Addrs
            .Where(a => (!moverAddrs.Contains(a) || internalMov.Contains(a))
                        && cluster.All(c => c.Addrs.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .Take(Math.Max(1, opt.MaxRootLadder))
            .ToList();
        if (shared.Count == 0) return empty;

        // A one-rung ladder whose only node is the top-level root often means a
        // flat hierarchy OR a container shared by several characters (the movers
        // only converge at the top) — surface it but at Medium confidence.
        bool containerish = shared.Count == 1 && Eq(shared[0], anchorRoot);

        int clusterSize = cluster.Count;
        var roots = new List<PlayerCandidate>(shared.Count);
        for (int i = 0; i < shared.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            (string? go, string name) = await ResolveNameAsync(null, shared[i], ct).ConfigureAwait(false);
            if (_drainTimeout) return empty;
            roots.Add(new PlayerCandidate
            {
                TransformAddress  = shared[i],
                GameObjectAddress = go,
                Name              = name,
                // DCA (i==0) is the tightest subtree containing all the moving
                // parts — recommend it first; climbing rungs are alternatives.
                Sources           = new[] { i == 0 ? $"moving-root ×{clusterSize}" : $"root ↑{i}" },
                Score             = RootScoreBase - i,
                Confidence        = containerish ? PlayerConfidence.Medium : PlayerConfidence.High,
            });
        }
        return roots;
    }

    private const int RootScoreBase = 1000;

    private static bool Eq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Walk [mover, p1, …, root]. Returns ok=false when a get_parent call FAILED
    // (as opposed to reaching a genuine 0x0 root) so the caller can drop the
    // incomplete chain instead of treating the failure point as a false root.
    private async Task<(List<string> addrs, bool ok)> WalkParentChainAsync(
        string moverAddr, Dictionary<string, string?> cache, MovementDetectOptions opt, CancellationToken ct)
    {
        var chain = new List<string> { moverAddr };
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { moverAddr };
        string cur = moverAddr;
        for (int d = 0; d < opt.MaxRootWalkDepth; d++)
        {
            (string? parent, bool ok) = await GetParentCachedAsync(cache, cur, ct).ConfigureAwait(false);
            if (_drainTimeout || !ok) return (chain, false);
            if (parent is null || !seen.Add(parent)) break;   // genuine root, or cycle guard
            chain.Add(parent);
            cur = parent;
        }
        return (chain, true);
    }

    // get_parent with a success/failure discriminator. ok=false = the call
    // errored (stale pointer / RPC hiccup); ok=true with parent==null = a
    // genuine hierarchy root (0x0). Only successful results are cached.
    private async Task<(string? parent, bool ok)> GetParentCachedAsync(
        Dictionary<string, string?> cache, string transformAddr, CancellationToken ct)
    {
        if (cache.TryGetValue(transformAddr, out string? cached)) return (cached, true);
        CallMethodResult r = await CallEngineAsync(
            "Transform", "get_parent", transformAddr, NoArgs, "()",
            wantStruct: false, ct).ConfigureAwait(false);
        if (!r.Ok || !string.IsNullOrEmpty(r.Error)) return (null, false);   // real error — don't cache
        string? parent = SuccessAddress(r);   // null here = genuine 0x0 root
        cache[transformAddr] = parent;
        return (parent, true);
    }

    // FindObjectsByType(Transform) -> capped list of transform addresses, plus
    // the true scene total and whether we truncated. Same probe order as the
    // CharacterController sweep (Unity 2022+ ByType -> 2-arg OfType -> 1-arg).
    private async Task<(IReadOnlyList<string> transforms, int total, bool truncated)>
        EnumerateSceneTransformsAsync(int cap, CancellationToken ct)
    {
        string? typeAddr = null;
        foreach (string fqn in new[]
                 { "UnityEngine.Transform, UnityEngine.CoreModule",
                   "UnityEngine.Transform, UnityEngine" })
        {
            CallMethodResult r = await _backend.CallMethodAsync(
                "mscorlib", "System", "Type", "GetType",
                new[] { CallArgFactory.String(fqn) },
                signatureMatch: "(System.String)",
                mainThread: true, mainThreadTimeoutMs: _mainThreadTimeoutMs,
                ct: ct).ConfigureAwait(false);
            NoteDrainTimeout(r);
            if (_drainTimeout) return (Array.Empty<string>(), 0, false);
            typeAddr = SuccessAddress(r);
            if (typeAddr is not null) break;
        }
        if (typeAddr is null) return (Array.Empty<string>(), 0, false);

        var attempts = new (string Method, string Signature, CallArgRequest[] Args)[]
        {
            ("FindObjectsByType",
             "(System.Type, UnityEngine.FindObjectsInactive, UnityEngine.FindObjectsSortMode)",
             new[] { CallArgFactory.Address(typeAddr), CallArgFactory.Int("int32", 1), CallArgFactory.Int("int32", 0) }),
            ("FindObjectsOfType",
             "(System.Type, System.Boolean)",
             new[] { CallArgFactory.Address(typeAddr), CallArgFactory.Bool(true) }),
            ("FindObjectsOfType",
             "(System.Type)",
             new[] { CallArgFactory.Address(typeAddr) }),
        };

        string? arrayAddress = null;
        foreach (var attempt in attempts)
        {
            CallMethodResult r = await CallEngineAsync(
                "Object", attempt.Method, instanceHex: null, attempt.Args, attempt.Signature,
                wantStruct: false, ct).ConfigureAwait(false);
            if (_drainTimeout) return (Array.Empty<string>(), 0, false);
            arrayAddress = SuccessAddress(r);
            if (arrayAddress is not null) break;
        }
        if (arrayAddress is null) return (Array.Empty<string>(), 0, false);

        // Fetch as wide a window as the DLL allows (hard cap 4096), then STRIDE
        // -select `cap` transforms evenly across it — sampling a contiguous
        // first-`cap` prefix would blind us to a player that sorts later in the
        // FindObjectsByType order. The header `length` is the true scene total
        // regardless of how many elements come back, so truncation is honest.
        int fetch = Math.Min(4096, Math.Max(cap, 1));
        WalkArrayResult array = await _backend.WalkArrayAsync(arrayAddress, limit: fetch, ct: ct)
                                              .ConfigureAwait(false);
        int total = array.Length > int.MaxValue ? int.MaxValue : (int)array.Length;

        var window = new List<string>();
        foreach (WalkedArrayElement e in array.Elements ?? new List<WalkedArrayElement>())
        {
            string? hex = ElementAddress(e);
            if (hex is not null) window.Add(hex);
        }

        IReadOnlyList<string> list;
        if (window.Count <= cap)
        {
            list = window;
        }
        else
        {
            // Even stride across the fetched window: indices 0, N/cap, 2N/cap, …
            var strided = new List<string>(cap);
            for (int i = 0; i < cap; i++)
                strided.Add(window[(int)((long)i * window.Count / cap)]);
            list = strided;
        }

        bool truncated = total > list.Count;
        return (list, total == 0 ? list.Count : total, truncated);
    }

    private async Task<(double, double, double)?> ReadTransformPositionAsync(
        string transformAddr, CancellationToken ct)
    {
        CallMethodResult pos = await CallEngineAsync(
            "Transform", "get_position", transformAddr, NoArgs, sigMatch: null,
            wantStruct: true, ct).ConfigureAwait(false);
        if (!pos.Ok || !string.IsNullOrEmpty(pos.Error)) return null;
        // A success with no/short struct payload decodes to all-zeros, which
        // would masquerade as world-origin (0,0,0). Treat it as unreadable so a
        // one-sided glitch across the two movement passes can't fabricate a
        // displacement equal to the object's distance from origin. Vector3 = 12
        // bytes = 24 hex chars.
        if (!HasVector3Payload(pos)) return null;
        float[] p = CallArgFactory.DecodeStructFloats(pos.ReturnStructBytes, 3);
        return (p[0], p[1], p[2]);
    }

    private async Task<(string? go, string name)> ResolveNameAsync(
        string? gameObject, string transformAddr, CancellationToken ct)
    {
        string? go = gameObject;
        if (go is null)
        {
            CallMethodResult r = await CallEngineAsync(
                "Component", "get_gameObject", transformAddr, NoArgs, "()",
                wantStruct: false, ct).ConfigureAwait(false);
            go = SuccessAddress(r);
        }
        if (go is null || _drainTimeout) return (go, string.Empty);

        CallMethodResult nr = await CallEngineAsync(
            "Object", "get_name", go, NoArgs, "()",
            wantStruct: false, ct).ConfigureAwait(false);
        string name = (nr.Ok && string.IsNullOrEmpty(nr.Error)
                       && nr.ReturnValue.ValueKind == JsonValueKind.String)
            ? nr.ReturnValue.GetString() ?? string.Empty
            : string.Empty;
        return (go, name);
    }

    // ------------------------------------------------------------------
    // Signals
    // ------------------------------------------------------------------

    private async Task<(string? camTransform, (double, double, double)? camPos)> CameraAnchorAsync(
        List<PlayerSignalHit> hits, List<string> notes, PlayerLocateOptions opt, CancellationToken ct)
    {
        CallMethodResult main = await CallEngineAsync(
            "Camera", "get_main", instanceHex: null, NoArgs, "()",
            wantStruct: false, ct).ConfigureAwait(false);
        string? camera = SuccessAddress(main);
        if (camera is null)
        {
            notes.Add($"camera: unavailable ({Describe(main)})");
            return (null, null);
        }

        CallMethodResult tr = await CallEngineAsync(
            "Component", "get_transform", camera, NoArgs, "()",
            wantStruct: false, ct).ConfigureAwait(false);
        string? camTransform = SuccessAddress(tr);
        if (camTransform is null)
        {
            notes.Add($"camera: transform unresolved ({Describe(tr)})");
            return (null, null);
        }

        (double, double, double)? camPos = null;
        CallMethodResult pos = await CallEngineAsync(
            "Transform", "get_position", camTransform, NoArgs, sigMatch: null,
            wantStruct: true, ct).ConfigureAwait(false);
        if (pos.Ok && HasVector3Payload(pos))
        {
            float[] p = CallArgFactory.DecodeStructFloats(pos.ReturnStructBytes, 3);
            camPos = (p[0], p[1], p[2]);
        }

        // Parent chain: FPS + many TPS rigs parent the camera under the player
        // root, so every ancestor is a candidate. The camera transform itself
        // is NOT — teleporting a follow-camera just gets snapped back.
        string current = camTransform;
        for (int depth = 0; depth < opt.MaxParentChainDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();
            CallMethodResult parent = await CallEngineAsync(
                "Transform", "get_parent", current, NoArgs, "()",
                wantStruct: false, ct).ConfigureAwait(false);
            string? parentAddr = SuccessAddress(parent);
            if (parentAddr is null) break;
            hits.Add(new PlayerSignalHit(parentAddr, "camera-chain",
                PlayerLocatorRanking.ScoreCameraChain));
            current = parentAddr;
        }

        notes.Add(camPos is null ? "camera: found (position unreadable)" : "camera: found");
        return (camTransform, camPos);
    }

    private async Task TagLookupAsync(
        List<PlayerSignalHit> hits, List<string> notes, CancellationToken ct)
    {
        // Stripped on many shipping IL2CPP builds (method_not_found) — that
        // just disables the signal. "Player" is a built-in tag, so no
        // UnityException-for-undefined-tag risk.
        CallMethodResult r = await CallEngineAsync(
            "GameObject", "FindWithTag", instanceHex: null,
            new[] { CallArgFactory.String("Player") }, "(System.String)",
            wantStruct: false, ct).ConfigureAwait(false);
        string? go = SuccessAddress(r);
        if (go is null)
        {
            notes.Add($"tag \"Player\": {(r.Ok ? "no object" : Describe(r))}");
            return;
        }

        string? transform = await ResolveGameObjectTransformAsync(go, ct).ConfigureAwait(false);
        if (transform is null) { notes.Add("tag \"Player\": transform unresolved"); return; }
        hits.Add(new PlayerSignalHit(transform, "tag:Player",
            PlayerLocatorRanking.ScoreTag, gameObjectAddress: go));
        notes.Add("tag \"Player\": hit");
    }

    private async Task CharacterControllerAsync(
        List<PlayerSignalHit> hits, List<string> notes, CancellationToken ct)
    {
        string? typeAddr = null;
        foreach (string fqn in CharacterControllerFqns)
        {
            CallMethodResult r = await _backend.CallMethodAsync(
                "mscorlib", "System", "Type", "GetType",
                new[] { CallArgFactory.String(fqn) },
                signatureMatch: "(System.String)",
                mainThread: true, mainThreadTimeoutMs: _mainThreadTimeoutMs,
                ct: ct).ConfigureAwait(false);
            NoteDrainTimeout(r);
            if (_drainTimeout) return;
            typeAddr = SuccessAddress(r);
            if (typeAddr is not null) break;
        }
        if (typeAddr is null)
        {
            notes.Add("CharacterController: type unresolved");
            return;
        }

        // Same 3-attempt probe order as SceneEnumerator (Unity 2022+ ByType ->
        // 2-arg OfType (stripping survivor) -> 1-arg OfType). Exact signatures
        // to dodge the close-paren substring trap.
        var attempts = new (string Method, string Signature, CallArgRequest[] Args)[]
        {
            ("FindObjectsByType",
             "(System.Type, UnityEngine.FindObjectsInactive, UnityEngine.FindObjectsSortMode)",
             new[] { CallArgFactory.Address(typeAddr), CallArgFactory.Int("int32", 1), CallArgFactory.Int("int32", 0) }),
            ("FindObjectsOfType",
             "(System.Type, System.Boolean)",
             new[] { CallArgFactory.Address(typeAddr), CallArgFactory.Bool(true) }),
            ("FindObjectsOfType",
             "(System.Type)",
             new[] { CallArgFactory.Address(typeAddr) }),
        };

        string? arrayAddress = null;
        foreach (var attempt in attempts)
        {
            CallMethodResult r = await CallEngineAsync(
                "Object", attempt.Method, instanceHex: null, attempt.Args, attempt.Signature,
                wantStruct: false, ct).ConfigureAwait(false);
            if (_drainTimeout) return;
            arrayAddress = SuccessAddress(r);
            if (arrayAddress is not null) break;
        }
        if (arrayAddress is null)
        {
            notes.Add("CharacterController: FindObjects* unavailable");
            return;
        }

        WalkArrayResult array = await _backend.WalkArrayAsync(arrayAddress, limit: 8, ct: ct)
                                              .ConfigureAwait(false);
        int found = 0;
        foreach (WalkedArrayElement e in array.Elements ?? new List<WalkedArrayElement>())
        {
            string? cc = ElementAddress(e);
            if (cc is null) continue;
            CallMethodResult tr = await CallEngineAsync(
                "Component", "get_transform", cc, NoArgs, "()",
                wantStruct: false, ct).ConfigureAwait(false);
            if (_drainTimeout) return;
            string? transform = SuccessAddress(tr);
            if (transform is null) continue;
            hits.Add(new PlayerSignalHit(transform, "CharacterController",
                PlayerLocatorRanking.ScoreCharController, className: "CharacterController"));
            found++;
        }
        notes.Add($"CharacterController: {found} hit(s)");
    }

    private async Task WellKnownNamesAsync(
        List<PlayerSignalHit> hits, List<string> notes, CancellationToken ct)
    {
        int found = 0;
        foreach (string name in WellKnownNames)
        {
            ct.ThrowIfCancellationRequested();
            CallMethodResult r = await CallEngineAsync(
                "GameObject", "Find", instanceHex: null,
                new[] { CallArgFactory.String(name) }, "(System.String)",
                wantStruct: false, ct).ConfigureAwait(false);
            if (_drainTimeout) return;
            if (!r.Ok && IsMethodNotFound(r.Error))
            {
                notes.Add("names: GameObject.Find unavailable");
                return;
            }
            string? go = SuccessAddress(r);
            if (go is null) continue;

            string? transform = await ResolveGameObjectTransformAsync(go, ct).ConfigureAwait(false);
            if (_drainTimeout) return;
            if (transform is null) continue;
            int score = string.Equals(name, "Player", StringComparison.Ordinal)
                ? PlayerLocatorRanking.ScoreExactPlayerName
                : PlayerLocatorRanking.ScoreWellKnownName;
            hits.Add(new PlayerSignalHit(transform, $"name:\"{name}\"", score, gameObjectAddress: go));
            found++;
        }
        notes.Add($"names: {found} hit(s)");
    }

    private async Task UserClassScanAsync(
        List<PlayerSignalHit> hits, List<string> notes, PlayerLocateOptions opt, CancellationToken ct)
    {
        GetAssembliesResult assemblies;
        try { assemblies = await _backend.GetAssembliesAsync(ct).ConfigureAwait(false); }
        catch (PipeProtocolException ex) { notes.Add($"classes: getAssemblies failed ({ex.Message})"); return; }

        // Assembly-CSharp(-firstpass) first — that's where the pawn class
        // lives in the overwhelming majority of games; asmdef assemblies after.
        var gameAssemblies = assemblies.Assemblies
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n) && !IsNonGameAssembly(n))
            .OrderBy(n => n.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, opt.MaxAssemblies))
            .ToList();

        var classTriples = new List<(string Assembly, string? Namespace, string Name)>();
        foreach (string asm in gameAssemblies)
        {
            ct.ThrowIfCancellationRequested();
            GetClassesResult classes;
            try { classes = await _backend.GetClassesAsync(asm, ct).ConfigureAwait(false); }
            catch (PipeProtocolException) { continue; }
            if (!string.IsNullOrEmpty(classes.Error)) continue;
            foreach (ClassEntry c in classes.Classes)
                classTriples.Add((asm, c.Namespace, c.Name));
        }

        IReadOnlyList<PlayerClassNameFilter.ClassCandidate> picked =
            PlayerClassNameFilter.Select(classTriples, opt.MaxUserClassScans);
        if (picked.Count > 0)
            Log("PlayerLocator signal-E picked: " + string.Join(", ", picked.Select(p => p.Name)));

        int candidateCount = 0;
        int skippedCount    = 0;
        foreach (PlayerClassNameFilter.ClassCandidate cls in picked)
        {
            ct.ThrowIfCancellationRequested();
            FindInstancesResult inst;
            try
            {
                // aliveOnly drops destroyed Unity shells, sane drops torn
                // objects (Phase 32) — both cut the heap-scan false positives
                // that would otherwise crash the get_transform probe. limit:5
                // (one past the accept gate's upper bound) so a shared/NPC class
                // with many instances reports count>4 and is rejected — the DLL
                // caps `matches` at `limit`, so limit:4 could never exceed 4 and
                // the "too many = not the player" branch was dead.
                inst = await _backend.FindInstancesAsync(
                    cls.Assembly, cls.Namespace, cls.Name,
                    limit: 5, aliveOnly: true, sane: true, ct: ct).ConfigureAwait(false);
            }
            catch (PipeProtocolException ex) { Log($"signal-E {cls.Name}: findInstances threw {ex.Message}"); continue; }
            if (!string.IsNullOrEmpty(inst.Error)) { Log($"signal-E {cls.Name}: error {inst.Error}"); continue; }

            // Some heap-scan matches parse into the header count but not the
            // matches[] array; fall back to the header count for the gate and
            // sample whatever addresses actually came through.
            int count = inst.Matches.Count > 0 ? inst.Matches.Count : inst.Count;
            // Exactly 1 live instance is the "this IS the player" shape; 2-4
            // could be player + prefab/pool copies. More = NPC / shared class.
            if (count is < 1 or > 4) { skippedCount++; Log($"signal-E {cls.Name}: count={count} (matches[]={inst.Matches.Count}) — skipped"); continue; }
            int score = count == 1
                ? PlayerLocatorRanking.ScoreSingleInstance
                : PlayerLocatorRanking.ScoreFewInstances;

            int resolved = 0, failed = 0;
            foreach (string instance in inst.Matches.Take(2))
            {
                // These are user classes that may or may not be Components.
                // Component.get_transform works for any Component/MonoBehaviour;
                // a plain data class returns 0x0 / errors → a clean skip.
                CallMethodResult tr = await CallEngineAsync(
                    "Component", "get_transform", instance, NoArgs, "()",
                    wantStruct: false, ct).ConfigureAwait(false);
                if (_drainTimeout) return;
                string? transform = SuccessAddress(tr);
                if (transform is null) { failed++; continue; }   // not a Component — skip
                hits.Add(new PlayerSignalHit(transform, $"class:{cls.Name}({count})",
                    score, instanceAddress: instance, className: cls.Name));
                candidateCount++;
                resolved++;
            }
            Log($"signal-E {cls.Name}: count={count}, transform resolved={resolved} failed={failed}");
        }
        notes.Add($"classes: {picked.Count} scanned, {candidateCount} hit(s)"
            + (skippedCount > 0 ? $", {skippedCount} count-skipped" : string.Empty));
    }

    // ------------------------------------------------------------------
    // Enrichment
    // ------------------------------------------------------------------

    private async Task<PlayerCandidate> EnrichAsync(
        PlayerLocatorRanking.Merged m, (double X, double Y, double Z)? camPos, CancellationToken ct)
    {
        // GameObject (for the display name): a Transform is a Component, so
        // get_gameObject resolves it when no signal recorded one.
        string? go = m.GameObjectAddress;
        if (go is null)
        {
            CallMethodResult r = await CallEngineAsync(
                "Component", "get_gameObject", m.TransformAddress, NoArgs, "()",
                wantStruct: false, ct).ConfigureAwait(false);
            go = SuccessAddress(r);
        }

        string name = string.Empty;
        if (go is not null && !_drainTimeout)
        {
            CallMethodResult r = await CallEngineAsync(
                "Object", "get_name", go, NoArgs, "()",
                wantStruct: false, ct).ConfigureAwait(false);
            if (r.Ok && string.IsNullOrEmpty(r.Error)
                && r.ReturnValue.ValueKind == JsonValueKind.String)
            {
                name = r.ReturnValue.GetString() ?? string.Empty;
            }
        }

        (double X, double Y, double Z)? position = null;
        double? distance = null;
        if (!_drainTimeout)
        {
            CallMethodResult pos = await CallEngineAsync(
                "Transform", "get_position", m.TransformAddress, NoArgs, sigMatch: null,
                wantStruct: true, ct).ConfigureAwait(false);
            if (pos.Ok && HasVector3Payload(pos))
            {
                float[] p = CallArgFactory.DecodeStructFloats(pos.ReturnStructBytes, 3);
                position = (p[0], p[1], p[2]);
                if (camPos is { } c)
                {
                    double dx = p[0] - c.X, dy = p[1] - c.Y, dz = p[2] - c.Z;
                    distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                }
            }
        }

        // Scoring adjustments (the wave-A version over-trusted the camera chain):
        //  * Distance-to-camera bonus applies ONLY to candidates with a
        //    non-camera signal. A camera-chain ancestor is near the camera by
        //    construction, so bonusing it is circular and let camera rigs win.
        //  * A camera-rig NAME (or a camera-chain-only candidate whose name says
        //    "camera"/"rig"/…) is heavily demoted so it stops out-ranking the
        //    real player when it's the only hit.
        bool cameraOnly = m.Sources.Count > 0
            && m.Sources.All(s => s.StartsWith("camera", StringComparison.Ordinal));
        int distanceBonus = cameraOnly ? 0 : PlayerLocatorRanking.DistanceBonus(distance);

        bool rigName = IsCameraRigName(name);
        int rigPenalty = rigName ? PlayerLocatorRanking.CameraRigNamePenalty : 0;

        int score = m.Score + distanceBonus - rigPenalty;

        PlayerConfidence confidence = ClassifyConfidence(m.Sources, rigName, cameraOnly);

        return new PlayerCandidate
        {
            TransformAddress  = m.TransformAddress,
            GameObjectAddress = go,
            InstanceAddress   = m.InstanceAddress,
            Name              = name,
            ClassName         = m.ClassName,
            Sources           = m.Sources,
            Score             = score,
            DistanceToCamera  = distance,
            Position          = position,
            Confidence        = confidence,
        };
    }

    /// <summary>True when a GameObject name looks like a camera rig / holder
    /// rather than the player (the camera-chain signal's classic false hit).
    /// Long tokens match as substrings; short tokens only at a word boundary.</summary>
    internal static bool IsCameraRigName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLowerInvariant();
        foreach (string t in CameraRigLongTokens)
        {
            if (lower.Contains(t, StringComparison.Ordinal)) return true;
        }
        // Short tokens tokenized off the ORIGINAL name so camelCase boundaries
        // ("FollowCam"/"PlayerRig") match but "Cameron"/"Frigate"/"Mountain" don't.
        foreach (string t in CameraRigShortTokens)
        {
            if (NameTokenMatch.HasToken(name, t)) return true;
        }
        return false;
    }

    // Confidence: strong signals (tag / CharacterController / single-instance
    // user class) => High; a name/class hit => Medium; camera-chain-only or a
    // camera-rig name => Low (probably not the player).
    //
    // A strong signal is checked FIRST — it recovers confidence the same way it
    // recovers score, so a real player whose GameObject name merely trips the
    // rig heuristic (e.g. a "PlayerRig" object with the Player tag) is not
    // force-demoted to Low. Only a lone camera-chain / rig-named hit stays Low.
    private static PlayerConfidence ClassifyConfidence(
        IReadOnlyList<string> sources, bool rigName, bool cameraOnly)
    {
        bool strong = sources.Any(s =>
            s.StartsWith("tag", StringComparison.Ordinal) ||
            s.StartsWith("CharacterController", StringComparison.Ordinal) ||
            s.StartsWith("class:", StringComparison.Ordinal));
        if (strong) return PlayerConfidence.High;

        if (rigName || cameraOnly) return PlayerConfidence.Low;

        // Corroboration across ≥2 distinct signal families also earns High.
        int families = sources.Select(FamilyPrefix).Distinct(StringComparer.Ordinal).Count();
        if (families >= 2) return PlayerConfidence.High;

        return PlayerConfidence.Medium;
    }

    private static string FamilyPrefix(string source)
    {
        int i = source.IndexOf(':');
        return i < 0 ? source : source[..i];
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    /// <summary>Focus-hint message returned when a mainThread call hits the
    /// Phase 20-MT drain timeout (runInBackground pause).</summary>
    public const string DrainTimeoutHint =
        "Auto-detect aborted: the game's Update() loop isn't ticking (main-thread "
        + "dispatcher drain timeout). Unity pauses when the game window loses focus "
        + "(Application.runInBackground = false default). Click \"Enable "
        + "runInBackground\" on the connection bar, or keep the game window focused, "
        + "then retry.";

    private PlayerLocateResult Abort() =>
        new() { Ok = false, Error = DrainTimeoutHint };

    private async Task<string?> ResolveGameObjectTransformAsync(string gameObject, CancellationToken ct)
    {
        CallMethodResult r = await CallEngineAsync(
            "GameObject", "get_transform", gameObject, NoArgs, "()",
            wantStruct: false, ct).ConfigureAwait(false);
        return SuccessAddress(r);
    }

    // Engine instance/static call with CoreModule -> monolithic fallback. The
    // survivor assembly is cached — every class this locator names lives in
    // CoreModule (Camera / Component / Transform / GameObject / Object), so
    // one resolution answers for all of them.
    private async Task<CallMethodResult> CallEngineAsync(
        string className, string method, string? instanceHex,
        IReadOnlyList<CallArgRequest> args, string? sigMatch, bool wantStruct,
        CancellationToken ct)
    {
        if (_resolvedEngineAssembly is string resolved)
        {
            CallMethodResult single = await _backend.CallMethodAsync(
                resolved, EngineNamespace, className, method, args,
                signatureMatch: sigMatch, instanceAddress: instanceHex,
                mainThread: true, mainThreadTimeoutMs: _mainThreadTimeoutMs,
                returnStruct: wantStruct, returnStructSize: wantStruct ? Vector3Bytes : 0,
                ct: ct).ConfigureAwait(false);
            NoteDrainTimeout(single);
            return single;
        }

        CallMethodResult? last = null;
        foreach (string asm in EngineAssemblyCandidates)
        {
            CallMethodResult r = await _backend.CallMethodAsync(
                asm, EngineNamespace, className, method, args,
                signatureMatch: sigMatch, instanceAddress: instanceHex,
                mainThread: true, mainThreadTimeoutMs: _mainThreadTimeoutMs,
                returnStruct: wantStruct, returnStructSize: wantStruct ? Vector3Bytes : 0,
                ct: ct).ConfigureAwait(false);
            last = r;
            NoteDrainTimeout(r);
            if (string.IsNullOrEmpty(r.Error) || !IsClassNotFound(r.Error))
            {
                // Any non-class-miss outcome means the assembly resolved the
                // class — lock it in for the rest of the run.
                _resolvedEngineAssembly = asm;
                return r;
            }
        }
        return last!;
    }

    private void NoteDrainTimeout(CallMethodResult r)
    {
        if (!string.IsNullOrEmpty(r.Error)
            && r.Error.Contains("main-thread queue drain timeout", StringComparison.Ordinal))
        {
            _drainTimeout = true;
        }
    }

    private static bool IsClassNotFound(string error) =>
        error.Contains("class not found", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("class_not_found", StringComparison.OrdinalIgnoreCase);

    private static bool IsMethodNotFound(string? error) =>
        !string.IsNullOrEmpty(error)
        && error.Contains("method_not_found", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonGameAssembly(string name)
    {
        foreach (string prefix in NonGameAssemblyPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Non-null "0x…" return address iff the call succeeded.</summary>
    private static string? SuccessAddress(CallMethodResult r)
    {
        if (!r.Ok || !string.IsNullOrEmpty(r.Error)) return null;
        if (r.ReturnValue.ValueKind != JsonValueKind.String) return null;
        string? addr = r.ReturnValue.GetString();
        if (string.IsNullOrEmpty(addr)) return null;
        return IsNullAddress(addr) ? null : addr;
    }

    private static string? ElementAddress(WalkedArrayElement e)
    {
        string? hex = e.Ptr;
        if (string.IsNullOrEmpty(hex)
            && e.Value is JsonElement v && v.ValueKind == JsonValueKind.String)
        {
            hex = v.GetString();
        }
        if (string.IsNullOrEmpty(hex)) return null;
        return IsNullAddress(hex) ? null : hex;
    }

    // A Vector3 returnStruct is 12 bytes = 24 hex chars. A success that came
    // back without (or with a short) struct payload would decode to all-zeros
    // and masquerade as world-origin — reject it.
    private static bool HasVector3Payload(CallMethodResult r) =>
        !string.IsNullOrEmpty(r.ReturnStructBytes) && r.ReturnStructBytes.Length >= 24;

    private static bool IsNullAddress(string hex)
    {
        // "0x0", "0x0000000000000000" and friends.
        int i = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        for (; i < hex.Length; i++)
        {
            if (hex[i] != '0') return false;
        }
        return true;
    }

    private static string Describe(CallMethodResult r) =>
        !string.IsNullOrEmpty(r.Error)     ? r.Error :
        !string.IsNullOrEmpty(r.Exception) ? r.Exception : "null";

    private void Log(string message) => _logger?.Write("Teleport", message);
}
