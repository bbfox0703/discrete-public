// PathResolver.cpp — see PathResolver.h for the resolution contract.
//
// Two independent pieces:
//   * Filesystem probing — from a game install root (or any of its known
//     sub-directories), locate global-metadata.dat / GameAssembly.dll via
//     Unity's fixed layout rules.
//   * Steam discovery — registry -> libraryfolders.vdf -> per-library
//     appmanifest_*.acf + steamapps\common\*, so a bare store name resolves
//     to an install directory.
//
// The VDF handling mirrors ui/UnityDumpUI/Platform/SteamGameScanner.cs (same
// "extract the quoted value after a quoted key" tokeniser) — deliberately
// not a full Valve KeyValues parser, because the two keys we need
// ("path" in libraryfolders.vdf, "name"/"installdir" in appmanifest) are
// always simple quoted scalars.

#include "PathResolver.h"

#include <windows.h>

#include <algorithm>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <set>
#include <sstream>

namespace fs = std::filesystem;

namespace Discrete::CLI::PathResolve {

namespace {

// ---------------------------------------------------------------------------
// String helpers

bool IEqualsW(const std::wstring& a, const std::wstring& b) {
    return a.size() == b.size() && ::_wcsicmp(a.c_str(), b.c_str()) == 0;
}

bool EndsWithI(const std::wstring& s, const wchar_t* suffix) {
    std::size_t n = std::wcslen(suffix);
    if (s.size() < n) return false;
    return ::_wcsnicmp(s.c_str() + (s.size() - n), suffix, n) == 0;
}

// Case-fold only — for identity comparisons (paths, folder names) where
// punctuation is significant.
std::wstring LowerW(std::wstring s) {
    for (wchar_t& c : s) {
        if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c - L'A' + L'a');
    }
    return s;
}

// Fold to a comparison key: lowercase, alphanumerics only. Lets
// "songsofsilence", "Songs of Silence" and "Songs-of-Silence" all match the
// same install without needing fuzzy distance scoring.
std::wstring FoldName(const std::wstring& s) {
    std::wstring out;
    out.reserve(s.size());
    for (wchar_t c : s) {
        if ((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'z')) out.push_back(c);
        else if (c >= L'A' && c <= L'Z')                          out.push_back(static_cast<wchar_t>(c - L'A' + L'a'));
        else if (static_cast<unsigned>(c) > 0x7F)                 out.push_back(c);  // keep CJK / accented as-is
    }
    return out;
}

std::wstring TrimSpec(const std::wstring& raw) {
    std::size_t b = raw.find_first_not_of(L" \t\r\n\"");
    if (b == std::wstring::npos) return {};
    std::size_t e = raw.find_last_not_of(L" \t\r\n\"");
    return raw.substr(b, e - b + 1);
}

// ---------------------------------------------------------------------------
// Filesystem helpers — every call is error_code-based; a CLI has no business
// throwing on an unreadable directory.

bool IsFile(const fs::path& p) {
    std::error_code ec;
    return fs::is_regular_file(p, ec);
}

bool IsDir(const fs::path& p) {
    std::error_code ec;
    return fs::is_directory(p, ec);
}

// Bounded child-directory listing. Steam's common\ can hold hundreds of
// entries; a game root holds a handful. The cap only guards against a spec
// pointed at something pathological (C:\ , a network share root).
constexpr std::size_t kMaxChildDirs = 256;

std::vector<fs::path> ChildDirs(const fs::path& root) {
    std::vector<fs::path> out;
    std::error_code ec;
    fs::directory_iterator it(root, fs::directory_options::skip_permission_denied, ec);
    if (ec) return out;
    for (const auto& e : it) {
        std::error_code ec2;
        if (!e.is_directory(ec2)) continue;
        out.push_back(e.path());
        if (out.size() >= kMaxChildDirs) break;
    }
    return out;
}

fs::path Absolute(const fs::path& p) {
    std::error_code ec;
    fs::path a = fs::absolute(p, ec);
    if (ec) return p;
    // weakly_canonical resolves ".." / symlinks without requiring the whole
    // path to exist; on failure keep the absolute form.
    fs::path c = fs::weakly_canonical(a, ec);
    return ec ? a : c;
}

// ---------------------------------------------------------------------------
// Unity layout probing

const wchar_t* kMetadataLeaf = L"global-metadata.dat";
const wchar_t* kGameAssembly = L"GameAssembly.dll";

// A directory the user may plausibly point at that sits *below* the install
// root. Climb out of these so probing always starts from the root.
bool IsUnitySubDirName(const std::wstring& n) {
    return IEqualsW(n, L"Metadata") || IEqualsW(n, L"il2cpp_data") || EndsWithI(n, L"_Data");
}

fs::path NormalizeToGameRoot(fs::path d) {
    // At most three levels: Metadata -> il2cpp_data -> <Game>_Data -> root.
    for (int i = 0; i < 3; ++i) {
        std::wstring name = d.filename().wstring();
        if (name.empty() || !IsUnitySubDirName(name)) break;
        fs::path parent = d.parent_path();
        if (parent.empty() || parent == d) break;
        d = parent;
    }
    return d;
}

// Candidate layouts for global-metadata.dat, relative to `root`. Order is
// most-specific-first so a game root with several *_Data folders still lands
// on the il2cpp one.
std::optional<fs::path> ProbeMetadataAt(const fs::path& root, std::vector<std::wstring>* tried) {
    auto consider = [&](const fs::path& p) -> bool {
        if (tried) tried->push_back(p.wstring());
        return IsFile(p);
    };
    // root is a <Game>_Data directory.
    fs::path c = root / L"il2cpp_data" / L"Metadata" / kMetadataLeaf;
    if (consider(c)) return c;
    // root is an il2cpp_data directory.
    c = root / L"Metadata" / kMetadataLeaf;
    if (consider(c)) return c;
    // root is the Metadata directory itself.
    c = root / kMetadataLeaf;
    if (consider(c)) return c;
    // root is the game install root — walk its <Game>_Data children.
    for (const auto& child : ChildDirs(root)) {
        if (!EndsWithI(child.filename().wstring(), L"_Data")) continue;
        c = child / L"il2cpp_data" / L"Metadata" / kMetadataLeaf;
        if (consider(c)) return c;
    }
    return std::nullopt;
}

std::optional<fs::path> ProbeGameAssemblyAt(const fs::path& root, std::vector<std::wstring>* tried) {
    fs::path c = root / kGameAssembly;
    if (tried) tried->push_back(c.wstring());
    if (IsFile(c)) return c;
    return std::nullopt;
}

std::optional<fs::path> ProbeAt(const fs::path& root, Target t, std::vector<std::wstring>* tried) {
    return (t == Target::Metadata) ? ProbeMetadataAt(root, tried)
                                   : ProbeGameAssemblyAt(root, tried);
}

// Full probe from a directory spec: normalise to the root, probe there, fall
// back to the literal spec, then descend one level (some installs nest the
// real payload under steamapps\common\<Game>\<Game>\).
struct DirProbe {
    fs::path artefact;
    fs::path gameRoot;
};

std::optional<DirProbe> ProbeDirectory(const fs::path& spec, Target t,
                                       std::vector<std::wstring>* tried) {
    fs::path root = NormalizeToGameRoot(spec);
    if (auto hit = ProbeAt(root, t, tried)) return DirProbe{*hit, root};
    if (root != spec) {
        if (auto hit = ProbeAt(spec, t, tried)) return DirProbe{*hit, spec};
    }
    for (const auto& child : ChildDirs(root)) {
        // Skip the *_Data folders — ProbeAt already covered those shapes and
        // re-probing them just pads the "tried" list.
        if (IsUnitySubDirName(child.filename().wstring())) continue;
        if (auto hit = ProbeAt(child, t, tried)) return DirProbe{*hit, child};
    }
    return std::nullopt;
}

// ---------------------------------------------------------------------------
// Spec classification

// True when the spec is unambiguously meant as a filesystem path: a
// separator, a drive letter, a leading dot, or a known artefact extension.
// Such a spec that doesn't exist is a hard failure — we never reinterpret it
// as a Steam game name.
bool LooksLikePath(const std::wstring& s) {
    if (s.find(L'\\') != std::wstring::npos) return true;
    if (s.find(L'/')  != std::wstring::npos) return true;
    if (s.size() >= 2 && s[1] == L':')       return true;
    if (!s.empty() && s[0] == L'.')          return true;
    if (EndsWithI(s, L".dat") || EndsWithI(s, L".dll") || EndsWithI(s, L".exe")) return true;
    return false;
}

// ---------------------------------------------------------------------------
// VDF / registry

std::optional<std::string> ReadFileUtf8(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return std::nullopt;
    std::ostringstream ss;
    ss << f.rdbuf();
    if (!f.good() && !f.eof()) return std::nullopt;
    std::string s = ss.str();
    // Strip a UTF-8 BOM if Steam ever writes one.
    if (s.size() >= 3 && static_cast<unsigned char>(s[0]) == 0xEF
                      && static_cast<unsigned char>(s[1]) == 0xBB
                      && static_cast<unsigned char>(s[2]) == 0xBF) {
        s.erase(0, 3);
    }
    return s;
}

std::string VdfUnescape(const std::string& raw) {
    std::string out;
    out.reserve(raw.size());
    for (std::size_t i = 0; i < raw.size(); ++i) {
        if (raw[i] != '\\' || i + 1 >= raw.size()) { out.push_back(raw[i]); continue; }
        char n = raw[++i];
        switch (n) {
            case 'n':  out.push_back('\n'); break;
            case 't':  out.push_back('\t'); break;
            case '\\': out.push_back('\\'); break;
            case '"':  out.push_back('"');  break;
            default:   out.push_back('\\'); out.push_back(n); break;
        }
    }
    return out;
}

// Read the quoted string starting at `open` (which must index the opening
// quote). Returns the raw (still-escaped) body and advances `open` past the
// closing quote.
bool ReadQuoted(const std::string& text, std::size_t& open, std::string* body) {
    if (open >= text.size() || text[open] != '"') return false;
    std::size_t i = open + 1;
    std::string out;
    while (i < text.size()) {
        if (text[i] == '\\' && i + 1 < text.size()) { out.push_back(text[i]); out.push_back(text[i + 1]); i += 2; continue; }
        if (text[i] == '"') { open = i + 1; *body = out; return true; }
        out.push_back(text[i]);
        ++i;
    }
    return false;
}

// Every value whose key is exactly `key`. `key` is matched as a quoted token
// so "path" doesn't hit "mounted_path" etc.
std::vector<std::string> VdfValues(const std::string& text, const std::string& key, bool firstOnly) {
    std::vector<std::string> out;
    std::string quotedKey = "\"" + key + "\"";
    std::size_t i = 0;
    while (true) {
        std::size_t k = text.find(quotedKey, i);
        if (k == std::string::npos) break;
        std::size_t after = k + quotedKey.size();
        // The value is the next quoted token; anything between must be
        // whitespace (otherwise this was a key inside a longer token or the
        // start of a block, not a scalar assignment).
        std::size_t j = after;
        while (j < text.size() && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r' || text[j] == '\n')) ++j;
        std::string body;
        if (j < text.size() && text[j] == '"' && ReadQuoted(text, j, &body)) {
            out.push_back(VdfUnescape(body));
            if (firstOnly) return out;
            i = j;
            continue;
        }
        i = after;
    }
    return out;
}

std::optional<std::wstring> ReadRegString(HKEY hive, const wchar_t* subKey, const wchar_t* value,
                                          DWORD extraFlags) {
    DWORD cb = 0;
    DWORD flags = RRF_RT_REG_SZ | extraFlags;
    LSTATUS st = ::RegGetValueW(hive, subKey, value, flags, nullptr, nullptr, &cb);
    if (st != ERROR_SUCCESS || cb < sizeof(wchar_t)) return std::nullopt;
    std::wstring buf(cb / sizeof(wchar_t), L'\0');
    st = ::RegGetValueW(hive, subKey, value, flags, nullptr, buf.data(), &cb);
    if (st != ERROR_SUCCESS) return std::nullopt;
    buf.resize(::wcsnlen(buf.c_str(), buf.size()));
    if (buf.empty()) return std::nullopt;
    return buf;
}

std::wstring NormalizeSlashes(std::wstring p) {
    std::replace(p.begin(), p.end(), L'/', L'\\');
    while (!p.empty() && p.back() == L'\\') p.pop_back();
    return p;
}

std::optional<std::wstring> ResolveSteamDir() {
    // HKCU is the user-scoped install — by far the common case.
    if (auto p = ReadRegString(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath", 0))
        return NormalizeSlashes(*p);
    // System-wide installs land in the 32-bit view of HKLM.
    if (auto p = ReadRegString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Valve\\Steam", L"InstallPath",
                               RRF_SUBKEY_WOW6432KEY))
        return NormalizeSlashes(*p);
    if (auto p = ReadRegString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Valve\\Steam", L"InstallPath", 0))
        return NormalizeSlashes(*p);
    return std::nullopt;
}

std::vector<fs::path> EnumerateSteamLibraries() {
    std::vector<fs::path> libs;
    auto steamDir = ResolveSteamDir();
    if (!steamDir) return libs;
    if (IsDir(*steamDir)) libs.emplace_back(*steamDir);

    fs::path vdf = fs::path(*steamDir) / L"steamapps" / L"libraryfolders.vdf";
    auto text = ReadFileUtf8(vdf);
    if (!text) return libs;

    std::set<std::wstring> seen;
    for (const auto& l : libs) seen.insert(LowerW(l.wstring()));
    for (const std::string& raw : VdfValues(*text, "path", /*firstOnly=*/false)) {
        std::wstring w = NormalizeSlashes(Utf8ToWide(raw));
        if (w.empty() || !IsDir(w)) continue;
        if (!seen.insert(LowerW(w)).second) continue;
        libs.emplace_back(w);
    }
    return libs;
}

// ---------------------------------------------------------------------------
// Match scoring for a bare Steam name.

// Lower is better. -1 means "no match at all".
int ScoreMatch(const SteamGame& g, const std::wstring& queryFold, const std::wstring& queryRaw) {
    std::wstring folder = fs::path(g.installDir).filename().wstring();
    if (IEqualsW(g.name, queryRaw) || IEqualsW(folder, queryRaw)) return 0;
    std::wstring nameFold   = FoldName(g.name);
    std::wstring folderFold = FoldName(folder);
    if (nameFold == queryFold || folderFold == queryFold) return 1;
    if (queryFold.empty()) return -1;
    if (nameFold.find(queryFold)   != std::wstring::npos) return 2;
    if (folderFold.find(queryFold) != std::wstring::npos) return 2;
    return -1;
}

}  // namespace

// ---------------------------------------------------------------------------
// Encoding helpers (public — main.cpp uses them too)

std::string WideToUtf8(const std::wstring& w) {
    if (w.empty()) return {};
    int n = ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                                  nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(static_cast<std::size_t>(n), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                          out.data(), n, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return {};
    int n = ::MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    if (n <= 0) return {};
    std::wstring out(static_cast<std::size_t>(n), L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), out.data(), n);
    return out;
}

// ---------------------------------------------------------------------------
// Steam enumeration

std::vector<std::wstring> EnumerateSteamLibraryRoots() {
    std::vector<std::wstring> out;
    for (const fs::path& p : EnumerateSteamLibraries()) out.push_back(p.wstring());
    return out;
}

std::vector<SteamGame> EnumerateSteamGames(bool includeMissing) {
    std::vector<SteamGame> games;
    for (const fs::path& lib : EnumerateSteamLibraries()) {
        fs::path steamapps = lib / L"steamapps";
        if (!IsDir(steamapps)) continue;
        fs::path common = steamapps / L"common";

        // appmanifest_*.acf gives us the *store* name, which is what a user
        // types — it often differs from the install folder ("SaGa Frontier 2
        // Remastered" vs "SaGaF2R").
        std::set<std::wstring> fromManifest;
        {
            std::error_code ec;
            fs::directory_iterator it(steamapps, fs::directory_options::skip_permission_denied, ec);
            if (!ec) {
                for (const auto& e : it) {
                    std::error_code ec2;
                    if (!e.is_regular_file(ec2)) continue;
                    std::wstring fn = e.path().filename().wstring();
                    if (::_wcsnicmp(fn.c_str(), L"appmanifest_", 12) != 0) continue;
                    if (!EndsWithI(fn, L".acf")) continue;
                    auto text = ReadFileUtf8(e.path());
                    if (!text) continue;
                    auto installVals = VdfValues(*text, "installdir", /*firstOnly=*/true);
                    if (installVals.empty()) continue;
                    std::wstring installDir = Utf8ToWide(installVals[0]);
                    if (installDir.empty()) continue;
                    fs::path dir = common / installDir;
                    bool exists = IsDir(dir);
                    if (!exists && !includeMissing) continue;
                    auto nameVals = VdfValues(*text, "name", /*firstOnly=*/true);
                    std::wstring name = nameVals.empty() ? installDir : Utf8ToWide(nameVals[0]);
                    if (name.empty()) name = installDir;
                    // appmanifest_<appid>.acf — take the id from the filename
                    // rather than the "appid" key, which some manifests omit.
                    std::wstring appId =
                        (fn.size() >= 16) ? fn.substr(12, fn.size() - 16) : std::wstring();
                    fromManifest.insert(LowerW(installDir));
                    games.push_back(SteamGame{name, dir.wstring(), lib.wstring(), appId, exists});
                }
            }
        }

        // Anything under common\ without a readable manifest (manual copies,
        // partially-uninstalled entries) still gets indexed by folder name.
        for (const fs::path& d : ChildDirs(common)) {
            std::wstring folder = d.filename().wstring();
            if (fromManifest.count(LowerW(folder))) continue;
            games.push_back(SteamGame{folder, d.wstring(), lib.wstring(), L"", true});
        }
    }
    return games;
}

// ---------------------------------------------------------------------------
// Resolution

namespace {

// Shared tail: given a directory that should be a game install, probe it and
// build the Resolved. `originNote` describes how we got the directory.
std::optional<Resolved> ResolveFromDirectory(const fs::path& dir, Target target, Origin origin,
                                             const std::string& originNote, std::string* err) {
    std::vector<std::wstring> tried;
    auto hit = ProbeDirectory(dir, target, &tried);
    if (!hit) {
        std::string what = (target == Target::Metadata) ? "global-metadata.dat" : "GameAssembly.dll";
        std::string msg = "could not locate " + what + " under " + WideToUtf8(dir.wstring());
        if (!originNote.empty()) msg += " (" + originNote + ")";
        msg += "\n  probed:";
        // Cap the echo — a *_Data-rich root can produce a long list.
        std::size_t shown = 0;
        for (const std::wstring& t : tried) {
            if (shown++ >= 8) { msg += "\n    ... and " + std::to_string(tried.size() - 8) + " more"; break; }
            msg += "\n    " + WideToUtf8(t);
        }
        if (err) *err = msg;
        return std::nullopt;
    }
    Resolved r;
    r.path     = Absolute(hit->artefact).wstring();
    r.gameRoot = Absolute(hit->gameRoot).wstring();
    r.origin   = origin;
    r.note     = originNote.empty()
                     ? ("resolved under " + WideToUtf8(r.gameRoot))
                     : originNote;
    return r;
}

// Resolve a bare name against Steam. On miss / ambiguity fills *err and
// returns nullopt.
std::optional<SteamGame> MatchSteamGame(const std::wstring& query, std::string* err) {
    std::vector<SteamGame> games = EnumerateSteamGames();
    if (games.empty()) {
        if (err) {
            *err = "'" + WideToUtf8(query) + "' is not an existing path, and no Steam "
                   "installation was found to look it up in (checked HKCU\\Software\\Valve\\Steam "
                   "and HKLM\\SOFTWARE\\Valve\\Steam). Pass a game install directory or a full "
                   "file path instead.";
        }
        return std::nullopt;
    }

    std::wstring queryFold = FoldName(query);
    int bestScore = -1;
    std::vector<const SteamGame*> best;
    for (const SteamGame& g : games) {
        int s = ScoreMatch(g, queryFold, query);
        if (s < 0) continue;
        if (bestScore < 0 || s < bestScore) { bestScore = s; best.clear(); }
        if (s == bestScore) best.push_back(&g);
    }

    if (best.empty()) {
        if (err) {
            *err = "no Steam game matched '" + WideToUtf8(query) + "' (scanned "
                 + std::to_string(games.size()) + " installs). "
                   "Run `metadata-cli steam-vdf` to list name -> install folder.";
        }
        return std::nullopt;
    }
    if (best.size() > 1) {
        if (err) {
            std::string msg = "'" + WideToUtf8(query) + "' is ambiguous — " +
                              std::to_string(best.size()) + " Steam games match:";
            std::size_t shown = 0;
            for (const SteamGame* g : best) {
                if (shown++ >= 10) { msg += "\n    ... and " + std::to_string(best.size() - 10) + " more"; break; }
                msg += "\n    " + WideToUtf8(g->name) + "  ->  " + WideToUtf8(g->installDir);
            }
            msg += "\n  Pass the install directory explicitly.";
            *err = msg;
        }
        return std::nullopt;
    }
    return *best.front();
}

std::string SteamNote(const SteamGame& g) {
    return "steam '" + WideToUtf8(g.name) + "' -> " + WideToUtf8(g.installDir);
}

}  // namespace

std::optional<Resolved> Resolve(const std::wstring& rawSpec, Target target, std::string* err) {
    std::wstring spec = TrimSpec(rawSpec);
    if (spec.empty()) {
        if (err) *err = "empty path/name argument";
        return std::nullopt;
    }

    // (1) An existing file is used verbatim — including when it isn't named
    // what we'd expect. The user asked for that exact file.
    if (IsFile(spec)) {
        fs::path abs = Absolute(spec);
        Resolved r;
        r.path     = abs.wstring();
        r.gameRoot = NormalizeToGameRoot(abs.parent_path()).wstring();
        r.origin   = Origin::ExplicitFile;
        r.note     = "explicit file";
        return r;
    }

    // (2) An existing directory is a game root (or a sub-directory of one).
    if (IsDir(spec)) {
        return ResolveFromDirectory(spec, target, Origin::GameRoot, "", err);
    }

    // (3) Path-shaped but absent — hard failure, never reinterpreted.
    if (LooksLikePath(spec)) {
        if (err) *err = "no such file or directory: " + WideToUtf8(spec);
        return std::nullopt;
    }

    // (4) Bare name — ask Steam.
    auto game = MatchSteamGame(spec, err);
    if (!game) return std::nullopt;
    return ResolveFromDirectory(game->installDir, target, Origin::SteamName, SteamNote(*game), err);
}

std::optional<ResolvedPair> ResolvePair(const std::wstring& rawSpec, std::string* err) {
    std::wstring spec = TrimSpec(rawSpec);
    if (spec.empty()) {
        if (err) *err = "empty path/name argument";
        return std::nullopt;
    }

    // Establish the install root first, then resolve both artefacts from it.
    fs::path root;
    Origin origin = Origin::GameRoot;
    std::string note;
    // An explicitly-named file wins for its own slot; only the *other*
    // artefact is auto-located.
    std::optional<fs::path> pinnedMetadata;
    std::optional<fs::path> pinnedGameAssembly;

    if (IsFile(spec)) {
        fs::path abs = Absolute(spec);
        root   = NormalizeToGameRoot(abs.parent_path());
        origin = Origin::ExplicitFile;
        note   = "install root derived from " + WideToUtf8(abs.wstring());
        std::wstring ext = LowerW(abs.extension().wstring());
        if      (ext == L".dat") pinnedMetadata     = abs;
        else if (ext == L".dll") pinnedGameAssembly = abs;
    } else if (IsDir(spec)) {
        root = NormalizeToGameRoot(Absolute(spec));
    } else if (LooksLikePath(spec)) {
        if (err) *err = "no such file or directory: " + WideToUtf8(spec);
        return std::nullopt;
    } else {
        auto game = MatchSteamGame(spec, err);
        if (!game) return std::nullopt;
        root   = game->installDir;
        origin = Origin::SteamName;
        note   = SteamNote(*game);
    }

    ResolvedPair p;
    p.origin   = origin;
    p.gameRoot = Absolute(root).wstring();

    if (pinnedMetadata) {
        p.metadata = pinnedMetadata->wstring();
    } else {
        auto meta = ResolveFromDirectory(root, Target::Metadata, origin, note, err);
        if (!meta) return std::nullopt;
        p.metadata = meta->path;
        p.gameRoot = meta->gameRoot;   // may have descended a level
    }

    // GameAssembly.dll sits at the install root — which for an explicit-file
    // spec is derived from that file, so passing global-metadata.dat alone
    // still finds the sibling binary.
    if (pinnedGameAssembly) {
        p.gameAssembly = pinnedGameAssembly->wstring();
    } else {
        auto ga = ResolveFromDirectory(fs::path(p.gameRoot), Target::GameAssembly, origin, note, err);
        if (!ga) return std::nullopt;
        p.gameAssembly = ga->path;
    }

    p.note = note.empty() ? ("install root " + WideToUtf8(p.gameRoot)) : note;
    return p;
}

std::optional<MetadataWithBinary> ResolveMetadataWithBinary(const std::wstring& spec,
                                                            std::string*        err) {
    auto meta = Resolve(spec, Target::Metadata, err);
    if (!meta) return std::nullopt;

    MetadataWithBinary out;
    out.metadata = meta->path;
    out.gameRoot = meta->gameRoot;
    out.origin   = meta->origin;
    out.note     = meta->note;

    // An explicitly-named file is honoured for whatever target was asked, so
    // a spec pointing at global-metadata.dat comes back as `meta` verbatim.
    // The binary must then be located from the derived install root instead of
    // by re-running the spec, which would just hand back the same .dat.
    std::wstring binarySpec = out.gameRoot.empty() ? spec : out.gameRoot;

    std::string binErr;
    if (auto ga = Resolve(binarySpec, Target::GameAssembly, &binErr)) {
        out.gameAssembly = ga->path;
    } else {
        out.binaryError = binErr.empty()
            ? ("GameAssembly.dll not found alongside " + WideToUtf8(out.metadata))
            : binErr;
    }
    return out;
}

}  // namespace Discrete::CLI::PathResolve
