// PathResolver.h — argument-path simplification for metadata-cli.
//
// Every subcommand used to demand the full path to the artefact it parses
// (`...\game_Data\il2cpp_data\Metadata\global-metadata.dat`, or
// `...\GameAssembly.dll`). Both live at fixed, well-known offsets from a
// Unity game's install root, so the CLI can find them itself.
//
// A <spec> argument is now resolved in this order:
//
//   1. Existing FILE            → used verbatim. If the user names a file
//                                 that isn't there, that's a hard failure —
//                                 we never silently fall back to searching.
//   2. Existing DIRECTORY       → treated as a game root (or a *_Data /
//                                 il2cpp_data / Metadata sub-directory of
//                                 one) and probed for the artefact.
//   3. Path-shaped but missing  → hard failure (contains a separator, a
//                                 drive letter, or a .dat/.dll/.exe suffix).
//   4. Bare name                → Steam game lookup. Steam install dir from
//                                 the registry → libraryfolders.vdf → every
//                                 library's appmanifest_*.acf (store name)
//                                 and steamapps\common\* (folder name). The
//                                 matched install dir is then probed as (2).
//
// Any failure along the way is a hard failure with a diagnostic on stderr —
// the tool never guesses at a different file than the one asked for.

#pragma once

#include <optional>
#include <string>
#include <vector>

namespace Discrete::CLI::PathResolve {

// Which artefact a subcommand needs from a game install.
enum class Target {
    Metadata,       // <root>\<Game>_Data\il2cpp_data\Metadata\global-metadata.dat
    GameAssembly,   // <root>\GameAssembly.dll
};

// How a spec was interpreted — surfaced on stderr so a user who typed a
// bare game name can see which install actually got picked.
enum class Origin {
    ExplicitFile,   // spec was an existing file, used as-is
    GameRoot,       // spec was a directory, artefact found by the fixed rules
    SteamName,      // spec matched a Steam game, then resolved as GameRoot
};

struct Resolved {
    std::wstring path;      // absolute path to the artefact
    std::wstring gameRoot;  // install root it was found under (empty for ExplicitFile
                            // when the root can't be derived)
    Origin       origin = Origin::ExplicitFile;
    std::string  note;      // one-line UTF-8 provenance for stderr, e.g.
                            // "steam 'Songs of Silence' -> D:\SteamLibrary\..."
};

// Both artefacts of one install — for `symbols`, which needs the pair.
struct ResolvedPair {
    std::wstring metadata;
    std::wstring gameAssembly;
    std::wstring gameRoot;
    Origin       origin = Origin::GameRoot;
    std::string  note;
};

// Resolve one artefact. Returns std::nullopt on any failure; *err (UTF-8)
// carries a diagnostic suitable for printing verbatim to stderr.
std::optional<Resolved> Resolve(const std::wstring& spec, Target target, std::string* err);

// Resolve both artefacts from a single spec. The spec may be a game root, a
// Steam game name, or either of the two files (the other is then located
// relative to the derived install root).
std::optional<ResolvedPair> ResolvePair(const std::wstring& spec, std::string* err);

// ---------------------------------------------------------------------------
// Steam discovery — exposed for the `resolve` subcommand's diagnostics and
// for the "did you mean" list emitted when a name lookup misses.

struct SteamGame {
    std::wstring name;        // store name from appmanifest_*.acf, or the folder name
    std::wstring installDir;  // <library>\steamapps\common\<installdir>
    std::wstring library;     // library root that owns it
    std::wstring appId;       // from appmanifest_<appid>.acf; empty for folder-only entries
    bool         exists = true;  // false only when includeMissing surfaced a stale manifest
};

// Every game Steam knows about on this machine. Empty when Steam isn't
// installed or the registry / vdf files aren't readable — callers treat that
// as "no match" rather than an error.
//
// `includeMissing` keeps manifests whose steamapps\common\<installdir> folder
// is gone (uninstalled but not cleaned up, moved by hand). Name *matching*
// always runs with it off — a stale manifest must never win a lookup — but
// the `steam-vdf` listing shows them, flagged, because they're really in the
// VDF and explain otherwise-confusing gaps.
std::vector<SteamGame> EnumerateSteamGames(bool includeMissing = false);

// Steam library roots, in probe order: the Steam install itself first, then
// every `"path"` entry in steamapps\libraryfolders.vdf that exists on disk.
std::vector<std::wstring> EnumerateSteamLibraryRoots();

// UTF-8 <-> UTF-16 helpers shared with main.cpp.
std::string  WideToUtf8(const std::wstring& w);
std::wstring Utf8ToWide(const std::string& s);

}  // namespace Discrete::CLI::PathResolve
