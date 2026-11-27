r"""Fail the build if a machine-local user path leaked into a tracked file.

Ported from the sibling project UE5CEDumper (tools/check_no_local_paths.py);
the dated incidents quoted below happened there, and are kept because they
explain why each rule exists.

    py tools/check_no_local_paths.py
    py tools/check_no_local_paths.py --staged    # scan staged index blobs only (for pre-commit hook)
    py tools/check_no_local_paths.py --list      # show every hit, do not exit 1
    py tools/check_no_local_paths.py --selftest  # the cannot-run controls only (every run does them first)

Exit codes: 0 nothing found; 1 (EXIT_FINDING) a tracked/staged file or an unpushed commit
message/diff carries a path or an identifier; 2 (EXIT_CANNOT_RUN) the tree was never
looked at -- git missing, this copy not a git checkout, git listing nothing, or the
checker itself failing. 2 is neither a finding nor a pass, and its first line says
why; callers tell the two failures apart by the code alone.

The checker always scans the repository it lives in (the parent of tools/),
whatever the current directory.

Why this exists: the repo is written on more than one PC and read by people who
are not the maintainer, so a literal home directory in a doc is both wrong for
every other reader and an unnecessary disclosure of the account name. It leaks
easily -- a path pasted out of a log, a scheduled-task location, a tool's usage
example -- and nothing else in CI notices.

Use the ENVIRONMENT-VARIABLE form instead, which is correct on every machine:

    C:\Users\<name>\AppData\Local\...   ->  %LOCALAPPDATA%\...
    C:\Users\<name>\AppData\Roaming\... ->  %APPDATA%\...
    C:\Users\<name>\...                 ->  %USERPROFILE%\...

Placeholders are allowed (`C:\Users\<you>`), because they cannot be mistaken for
a real account, as are the shared system accounts. Only concrete names fail.
"""
import contextlib
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile
import traceback

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Python exits 1 on an uncaught exception, the code a finding uses, so a crash would read as a
# leak: guarded() turns every crash into EXIT_CANNOT_RUN and leaves 1 meaning a finding only.
EXIT_OK, EXIT_FINDING, EXIT_CANNOT_RUN = 0, 1, 2

# The argv that runs git. A list so the self-test can put a stand-in in its place.
GIT = ["git"]

# A Windows user directory followed by a CONCRETE name.
#
# The separator class is built from chr(92) rather than written as a literal
# backslash: this file is generated/edited through tooling that has been seen to
# collapse "\\" to "\", which silently turns [\\/] into "/ only" -- a regex that
# then matches nothing and reports a clean tree forever. Assembling it here means
# the class cannot be damaged in transit.
_SEP = "[" + chr(92) + chr(92) + "/]"
PATTERN = re.compile(r"[A-Za-z]:" + _SEP + "Users" + _SEP +
                     r"(?!<|\{|%)([A-Za-z0-9._-]+)", re.IGNORECASE)

# Generic stand-ins that carry no account name.
ALLOWED = {"public", "default", "all", "username", "user", "you", "youruser",
           "name", "yourname", "someone"}

# A second, NARROWER pattern that ALLOWED does not get to excuse.
#
# ⚠ Found 2026-09-06 in docs/archive/todo-completed-build-937.md, which had carried
# `…/C:/Users/user/.claude/projects/<project>/memory/<file>.md` since it was archived. PATTERN
# matched it and then waved it through, because `user` is a legitimate generic stand-in and the
# path genuinely reads like one.
#
# The account name is the wrong thing to key on. What makes that path certainly-real and certainly
# wrong is where it CONTINUES: `.claude/projects/` is a per-session Claude Code directory, and a
# drive-absolute path into one cannot be meaningful in a tracked file whatever the account is
# called. So this rule ignores ALLOWED entirely.
#
# ⚠ It must NOT fire on the correct form: `%USERPROFILE%\.claude\projects\…` has no drive
# letter, so this pattern does not match, which is exactly the distinction being drawn.
# Exclude path separators from each intermediate segment so the regex has a
# unique decomposition per path and runs in linear time (A-M12: without
# excluding `/` and `\`, non-matching long paths like `"C:/" + "a/"*25 + "x"`
# trigger exponential backtracking).
_NO_SEP = r'[^\s:*?"<>|' + chr(92) + chr(92) + r'/]'
SESSION_DIR_PATTERN = re.compile(
    r"[A-Za-z]:" + _SEP + r"(?:" + _NO_SEP + r"{0,120}" + _SEP +
    r")*?\.claude" + _SEP + "projects",
    re.IGNORECASE,
)

SKIP = ("tools/check_no_local_paths.py",)

# ─────────────────────────────────────────────────────────────────────────────
# THIS MACHINE's identifiers — derived at run time, stored nowhere.
#
# ⭐ THE PROBLEM THIS SOLVES, and it is a real chicken-and-egg: to flag the account name "Xyz" a
# gate must know "Xyz", and writing it into a tracked file publishes exactly what is being
# protected. The patterns above sidestep it by matching a SHAPE (`C:\Users\<something>` that is not
# a known placeholder) and never needing the value. But a bare hostname has no shape — it is just a
# word — so shape matching cannot see it, and on 2026-09-06 the machine name was found in 13
# tracked files, six of them in docs/archive/, with two rigs HARD-CODING it (which also broke them
# on the second PC).
#
# Resolution: read the identifiers from the ENVIRONMENT of whichever machine is running the gate —
# which is precisely the machine that could leak them. Nothing is committed and nothing is hashed.
#
# ⛔ HASHING WOULD BE SECURITY THEATRE, so it was rejected: usernames and hostnames come from a tiny
# search space, and a stored sha256 of one is brute-forced in seconds. A CI secret was rejected too
# — secrets are unavailable to pull requests from forks, and the leak is authored
# LOCALLY, long before CI ever runs.
#
# ⚠ ITS LIMIT, STATED RATHER THAN HIDDEN: it only knows the CURRENT machine. If PC A commits PC B's
# name, A's gate cannot see it. This COMPLEMENTS the shape patterns; those remain the load-bearing
# half because they need no knowledge at all.
MIN_IDENTIFIER_LEN = 4

# A-L18: Common OS / CI / generic words that frequently appear as USERNAME,
# COMPUTERNAME, USERDOMAIN, or $HOME leaf on CI runners (e.g. GitHub Actions
# `runner` matching `xunit.runner.visualstudio`, or `admin` / `build` / `corp`).
# Users can also opt out individual identifiers via DISCRETE_IDENTITY_ALLOW
# (comma/semicolon-separated, case-insensitive) without disabling the guard.
COMMON_WORDS = {
    "admin", "administrator", "root", "system", "local", "localhost",
    "runner", "runneradmin", "builder", "build", "actions", "github",
    "gitlab", "jenkins", "agent", "worker", "container", "docker",
    "guest", "service", "workgroup", "corp", "domain", "home", "test",
    "owner", "desktop", "laptop", "server", "windows", "ubuntu",
    "linux", "macos", "vscode", "codespace", "devcontainer",
}


def _env_allowed_identifiers():
    raw = os.environ.get("DISCRETE_IDENTITY_ALLOW") or ""
    out = set()
    for part in re.split(r"[,;]", raw):
        s = part.strip().lower()
        if s:
            out.add(s)
    return out


def runtime_identifiers():
    """{value: where it came from} for this machine. Nothing here is ever persisted."""
    ignored = ALLOWED | COMMON_WORDS | _env_allowed_identifiers()
    found = {}
    for var in ("USERNAME", "COMPUTERNAME", "USERDOMAIN"):
        v = (os.environ.get(var) or "").strip()
        if len(v) >= MIN_IDENTIFIER_LEN and v.lower() not in ignored:
            found.setdefault(v, var)
    home = (os.environ.get("USERPROFILE") or os.environ.get("HOME") or "").rstrip("\\/")
    leaf = os.path.basename(home)
    if len(leaf) >= MIN_IDENTIFIER_LEN and leaf.lower() not in ignored:
        found.setdefault(leaf, "USERPROFILE")
    return found


def mask_account(m):
    """`C:\\Users\\Alice` -> `C:\\Users\\A****`. The path stays readable; the identity does not.

    ⚠ The example name here is FICTIONAL on purpose. An earlier version used this
    machine's real account, and the gate could not see it because it skipped its own
    file -- see SKIP below.
    """
    acct = m.group(1)
    return m.group(0).replace(acct, redact(acct))


def mask_account_line(line):
    """Same masking applied to the quoted source line, so the fragment and the line agree."""
    out = line.strip()
    for m in PATTERN.finditer(out):
        if m.group(1).lower() in ALLOWED:
            continue
        out = out.replace(m.group(1), redact(m.group(1)))
    return out[:110]


def redact(value):
    """⚠ THE GATE'S OWN OUTPUT MUST NOT PRINT THE NAME. CI logs on a public repo are public, so a
    failure that echoed the identifier would publish the very string it exists to keep out."""
    return value[0] + "*" * (len(value) - 1)


def ident_pattern(value):
    """Word-bounded, so a name that is a substring of an ordinary word does not fire."""
    return re.compile(r"(?<![A-Za-z0-9_])" + re.escape(value) + r"(?![A-Za-z0-9_])", re.IGNORECASE)


def scrub(text):
    """`text` with each concrete account segment and this machine's identifiers redacted. For what
    the gate says about itself: git's error text and a traceback carry paths too, and redact()'s
    rule holds for every line the gate prints."""
    out = PATTERN.sub(lambda m: m.group(0) if m.group(1).lower() in ALLOWED else mask_account(m),
                      text)
    for value in runtime_identifiers():
        out = ident_pattern(value).sub(redact(value), out)
    return out


class CannotRun(Exception):
    """The tree could not be listed, so nothing was checked."""


def cannot_run(reason):
    """Say that the tree was not checked, and why, under the exit code no caller reads as a finding
    or a pass."""
    print("check_no_local_paths: CANNOT RUN -- " + scrub(reason))
    print("Nothing was checked: this is not a finding, and not a pass.")
    return EXIT_CANNOT_RUN



def unpushed_commit_messages(git=GIT, root=ROOT):
    """(sha, message) for every local commit not yet on any remote branch.

    ⭐ WHY MESSAGES, AND WHY ONLY THE UNPUSHED ONES. A file-level gate reads `git ls-files` and can
    never see `%B`. That gap is not theoretical: on 2026-09-06 the account name was found in the
    message of 4b4d6775 -- the commit that REMOVED the same string from a file restated it in its
    own message, so the file was cleaned and the leak stayed.

    Only unpushed commits are checked, because those are the only ones still fixable: an unpushed
    commit can be amended or rebased, a pushed one cannot be recalled. That is also exactly the
    maintainer's own framing -- a commit always precedes a push, so the build is the last cheap
    moment.

    Raises CannotRun when git cannot list them: no messages read is not "no leak in them".
    """
    try:
        rng = subprocess.run(git + ["log", "--branches", "--not", "--remotes",
                                    "--format=%H%x01%B%x02"],
                             cwd=root, capture_output=True, text=True, encoding="utf-8",
                             errors="replace")
    except OSError as e:
        raise CannotRun("git could not be started (%s: %s) -- is git on PATH?"
                        % (type(e).__name__, e)) from None
    if rng.returncode != 0:
        why = [ln.strip() for ln in (rng.stderr or "").splitlines() if ln.strip()][:1]
        raise CannotRun("`git log --branches --not --remotes` exited %d%s"
                        % (rng.returncode, (": " + why[0]) if why else ""))
    out = []
    for chunk in (rng.stdout or "").split("\x02"):
        chunk = chunk.strip()
        if not chunk or "\x01" not in chunk:
            continue
        sha, msg = chunk.split("\x01", 1)
        out.append((sha.strip()[:8], msg))
    return out


def unpushed_commit_added_lines(git=GIT, root=ROOT):
    """(sha, path, line) for added lines in every local commit not yet on any remote branch.

    A-L18: Scanning only the working tree and commit messages misses a leak that
    was committed locally and then cleaned up in a subsequent commit (or only in
    the working tree) before push. Inspecting the added patch lines of every
    unpushed commit closes that hole before `git push` publishes history.
    """
    try:
        rng = subprocess.run(
            git + ["log", "--branches", "--not", "--remotes",
                   "--format=COMMIT:%H", "-p", "--no-color", "--unified=0"],
            cwd=root, capture_output=True, text=True, encoding="utf-8",
            errors="replace",
        )
    except OSError as e:
        raise CannotRun("git could not be started (%s: %s) -- is git on PATH?"
                        % (type(e).__name__, e)) from None
    if rng.returncode != 0:
        why = [ln.strip() for ln in (rng.stderr or "").splitlines() if ln.strip()][:1]
        raise CannotRun("`git log --branches --not --remotes -p` exited %d%s"
                        % (rng.returncode, (": " + why[0]) if why else ""))
    out = []
    cur_sha = ""
    cur_path = ""
    for raw_line in (rng.stdout or "").splitlines():
        if raw_line.startswith("COMMIT:"):
            cur_sha = raw_line[len("COMMIT:"):].strip()[:8]
            cur_path = ""
            continue
        if raw_line.startswith("+++ b/"):
            cur_path = raw_line[len("+++ b/"):].strip()
            continue
        if raw_line.startswith("+++ /dev/null"):
            cur_path = ""
            continue
        if raw_line.startswith("+") and not raw_line.startswith("+++"):
            out.append((cur_sha, cur_path, raw_line[1:]))
    return out


def staged_files(git=GIT, root=ROOT):
    """Staged added/copied/modified/renamed paths relative to `root` (A-L18)."""
    try:
        r = subprocess.run(
            git + ["diff", "--cached", "--name-only", "-z", "--diff-filter=ACMR"],
            cwd=root, capture_output=True,
        )
    except OSError as e:
        raise CannotRun("git could not be started (%s: %s) -- is git on PATH?"
                        % (type(e).__name__, e)) from None
    if r.returncode != 0:
        err = r.stderr.decode("utf-8", errors="replace")
        why = [ln.strip() for ln in err.splitlines() if ln.strip()][:1]
        raise CannotRun("`git diff --cached` exited %d%s"
                        % (r.returncode, (": " + why[0]) if why else ""))
    try:
        out = r.stdout.decode("utf-8")
    except UnicodeDecodeError:
        raise CannotRun("`git diff --cached -z` listed a path that is not UTF-8") from None
    return [f for f in out.split("\0") if f]


def read_staged_blob(path, git=GIT, root=ROOT):
    """Read staged index contents for `path` via `git show :<path>` (A-L18)."""
    try:
        r = subprocess.run(
            git + ["show", ":" + path],
            cwd=root, capture_output=True,
        )
    except OSError as e:
        raise CannotRun("git could not be started (%s: %s) -- is git on PATH?"
                        % (type(e).__name__, e)) from None
    if r.returncode != 0:
        err = r.stderr.decode("utf-8", errors="replace")
        why = [ln.strip() for ln in err.splitlines() if ln.strip()][:1]
        raise CannotRun("`git show :%s` exited %d%s"
                        % (path, r.returncode, (": " + why[0]) if why else ""))
    return r.stdout.decode("utf-8", errors="ignore")


def _scan_line(line, ident_patterns, self_scan=False):
    """Return [(redacted_fragment, redacted_line)] matches on a single text line."""
    found = []
    if not self_scan:
        for m in PATTERN.finditer(line):
            if m.group(1).lower() in ALLOWED:
                continue
            # ⚠ REDACT THE ACCOUNT SEGMENT. This branch used to print m.group(0) and
            # the raw line, i.e. it echoed the account name it had just caught -- which
            # is how a caught leak gets laundered into a commit message or a CI log.
            found.append((mask_account(m), mask_account_line(line)))
        # ALLOWED deliberately does NOT apply here -- see SESSION_DIR_PATTERN.
        for m in SESSION_DIR_PATTERN.finditer(line):
            found.append((scrub(m.group(0)), scrub(line.strip())[:110]))
    for value, rx in ident_patterns.items():
        if rx.search(line):
            # Redact in BOTH the fragment and the quoted line.
            found.append((redact(value), rx.sub(redact(value), line.strip())[:110]))
    return found


def tracked_files(git=GIT, root=ROOT):
    """Every tracked path, relative to `root`. Raises CannotRun when git does not list the tree:
    a scan of no files would print CHECK OK over a tree it never saw."""
    # -z: NUL-separated and unquoted, so a non-ASCII name arrives as its real UTF-8 path
    # (the default listing quotes it, and opening the quoted form fails).
    try:
        r = subprocess.run(git + ["ls-files", "-z"], cwd=root, capture_output=True)
    except OSError as e:
        raise CannotRun("git could not be started (%s: %s) -- is git on PATH?"
                        % (type(e).__name__, e)) from None
    if r.returncode != 0:
        err = r.stderr.decode("utf-8", errors="replace")
        why = [ln.strip() for ln in err.splitlines() if ln.strip()][:1]
        raise CannotRun("`git ls-files` exited %d%s" % (r.returncode, (": " + why[0]) if why else ""))
    try:
        out = r.stdout.decode("utf-8")
    except UnicodeDecodeError:
        raise CannotRun("`git ls-files -z` listed a path that is not UTF-8") from None
    if not out.strip():
        raise CannotRun("`git ls-files` listed no file")
    # ⚠ Everything is returned, INCLUDING this file. It used to be dropped here, which made the
    # gate blind to a real account name sitting in one of its own docstrings (found on the public
    # tip, not by the gate). The two REGEX branches are suppressed for it in run() instead --
    # that is the narrow thing SKIP was ever needed for.
    return [f for f in out.split("\0") if f]


def _run_staged(list_only, git, root):
    """Scan staged index blobs only (invoked by .githooks/pre-commit -- A-L18)."""
    try:
        files = staged_files(git, root)
    except CannotRun as e:
        return cannot_run(str(e))
    if not files:
        print("CHECK OK: no staged files to check.")
        return EXIT_OK

    ident_patterns = {v: ident_pattern(v) for v in runtime_identifiers()}
    hits = []
    for path in files:
        self_scan = path.replace(chr(92), "/").startswith(SKIP)
        try:
            content = read_staged_blob(path, git, root)
        except CannotRun as e:
            return cannot_run(str(e))
        for n, line in enumerate(content.splitlines(), 1):
            for frag, redacted in _scan_line(line, ident_patterns, self_scan=self_scan):
                hits.append((path, n, frag, redacted))

    if not hits:
        print(f"CHECK OK: no machine-local user path in {len(files)} staged file(s).")
        return EXIT_OK

    print(f"FOUND {len(hits)} machine-local path(s) in STAGED file(s):")
    for path, n, frag, line in hits:
        print(f"  {path}:{n}: {frag}")
        print(f"      {line}")
    print()
    print("Replace with %LOCALAPPDATA% / %APPDATA% / %USERPROFILE%, or a <placeholder>.")
    return EXIT_OK if list_only else EXIT_FINDING


def run(argv, git=GIT, root=ROOT):
    """The scan of the tree at `root`; `git` is the argv that lists it."""
    list_only = "--list" in argv
    if "--staged" in argv:
        return _run_staged(list_only, git, root)

    try:
        files = tracked_files(git, root)
    except CannotRun as e:
        return cannot_run(str(e))
    # Built once.
    ident_patterns = {v: ident_pattern(v) for v in runtime_identifiers()}

    hits = []
    unread = []
    for path in files:
        # This file DEFINES the two regexes, so its own source matches them. Suppress just those
        # two branches for it; the identifier branch below still applies, and must.
        self_scan = path.replace(chr(92), "/").startswith(SKIP)
        try:
            with open(os.path.join(root, path), "r", encoding="utf-8", errors="ignore") as fh:
                for n, line in enumerate(fh, 1):
                    for frag, redacted in _scan_line(line, ident_patterns, self_scan=self_scan):
                        hits.append((path, n, frag, redacted))
        except (OSError, UnicodeError):
            # A tracked file that cannot be read was not checked; never count it as clean.
            unread.append(path)
    if unread:
        return cannot_run("%d tracked file(s) could not be read: %s"
                          % (len(unread), ", ".join(unread[:5])))

    # Commit MESSAGES of unpushed commits -- see unpushed_commit_messages().
    try:
        messages = unpushed_commit_messages(git, root)
    except CannotRun as e:
        return cannot_run(str(e))
    msg_hits = []
    for sha, msg in messages:
        for line in msg.splitlines():
            for frag, redacted in _scan_line(line, ident_patterns, self_scan=False):
                msg_hits.append((sha, frag, redacted))

    # Added DIFF lines of unpushed commits (A-L18) -- catches leaks that were
    # committed locally and later overwritten only in the working tree or a
    # follow-up commit before push.
    try:
        added_lines = unpushed_commit_added_lines(git, root)
    except CannotRun as e:
        return cannot_run(str(e))
    patch_hits = []
    for sha, path, line in added_lines:
        self_scan = path.replace(chr(92), "/").startswith(SKIP)
        for frag, redacted in _scan_line(line, ident_patterns, self_scan=self_scan):
            patch_hits.append((sha, path or "<unknown>", frag, redacted))

    if not hits and not msg_hits and not patch_hits:
        print(f"CHECK OK: no machine-local user path in {len(files)} tracked files, "
              f"nor in any unpushed commit message or diff.")
        return EXIT_OK

    # A-L18: Print ALL non-empty finding groups (never early-return on msg_hits
    # before showing file/diff hits) and honor --list across every branch.
    if msg_hits:
        print(f"FOUND {len(msg_hits)} leak(s) in UNPUSHED commit MESSAGE(S):")
        for sha, frag, line in msg_hits:
            print(f"  {sha}: {frag}")
            print(f"      {line}")
        print()
        print("These commits are NOT pushed, so they are still fixable:")
        print("  git commit --amend        (the most recent one)")
        print("  git rebase -i <base>      (an older one; reword it)")
        print("A pushed commit message cannot be recalled, which is why only unpushed ones fail here.")
        print()

    if patch_hits:
        print(f"FOUND {len(patch_hits)} leak(s) in UNPUSHED commit DIFF(S):")
        for sha, path, frag, line in patch_hits:
            print(f"  {sha} ({path}): {frag}")
            print(f"      {line}")
        print()
        print("Rewrite or squash the unpushed commit(s) before pushing so the leak never enters remote history.")
        print()

    if hits:
        print(f"FOUND {len(hits)} machine-local path(s) in tracked files:")
        for path, n, frag, line in hits:
            print(f"  {path}:{n}: {frag}")
            print(f"      {line}")
        print()
        print("Replace with %LOCALAPPDATA% / %APPDATA% / %USERPROFILE%, or a <placeholder>.")

    return EXIT_OK if list_only else EXIT_FINDING


def guarded(entry, argv):
    """`entry(argv)`, with a crash reported as EXIT_CANNOT_RUN: see EXIT_FINDING."""
    try:
        return entry(argv)
    except Exception as e:
        print(scrub(traceback.format_exc()).rstrip())
        return cannot_run("the checker raised %s (traceback above)" % type(e).__name__)


# ─────────────────────────────────────────────────────────────────────────────
# Self-test. Each case is a way the check can fail to look at the tree, and each must come out
# EXIT_CANNOT_RUN: never EXIT_FINDING, which says a name leaked, and never EXIT_OK, which says the
# tree is clean. A finding and a clean tree are the controls, so "always 2" cannot pass. Git itself
# is never run: a stand-in answers in its place, so the cases hold on a machine without git and
# touch no repository.

# A concrete user home that names nobody, assembled so no line of this file spells one out.
_FIXTURE_HOME = "/".join(("C:", "Users", "Alice"))


def _stand_in_git(listing=(), rc=0, stderr="", log_rc=0, log_out="",
                  patch_rc=0, patch_out="", staged=(), blobs=None):
    """An argv that answers like git: `ls-files` writes the NUL-separated `listing` and `stderr`
    and exits `rc`; `log` (with or without `-p`) writes `patch_out`/`log_out`; `diff --cached`
    writes NUL-separated `staged`; `show :<path>` writes `blobs[path]`."""
    listed = "".join(p + "\0" for p in listing)
    staged_listed = "".join(p + "\0" for p in staged)
    blobs_map = dict(blobs or {})
    code = (
        "import sys\n"
        "args = sys.argv[1:]\n"
        "c = args[0] if args else ''\n"
        "if c == 'ls-files':\n"
        "    sys.stdout.buffer.write(%r.encode('utf-8'))\n"
        "    sys.stderr.buffer.write(%r.encode('utf-8'))\n"
        "    sys.exit(%d)\n"
        "elif c == 'log' and '-p' in args:\n"
        "    sys.stdout.buffer.write(%r.encode('utf-8'))\n"
        "    sys.exit(%d)\n"
        "elif c == 'log':\n"
        "    sys.stdout.buffer.write(%r.encode('utf-8'))\n"
        "    sys.exit(%d)\n"
        "elif c == 'diff':\n"
        "    sys.stdout.buffer.write(%r.encode('utf-8'))\n"
        "    sys.exit(0)\n"
        "elif c == 'show' and len(args) >= 2:\n"
        "    key = args[1][1:] if args[1].startswith(':') else args[1]\n"
        "    blobs = %r\n"
        "    if key not in blobs:\n"
        "        sys.stderr.buffer.write(b'fatal: path not in index')\n"
        "        sys.exit(128)\n"
        "    sys.stdout.buffer.write(blobs[key].encode('utf-8'))\n"
        "    sys.exit(0)\n"
        "sys.exit(0)\n"
        % (listed, stderr, rc, patch_out, patch_rc, log_out, log_rc, staged_listed, blobs_map)
    )
    return [sys.executable, "-I", "-c", code]


def _crash(_argv):
    raise RuntimeError("cannot open " + _FIXTURE_HOME + "/notes.txt")


def _expect(what, want, call, must_say=(), must_not_say=()):
    """-> how `call()` missed: its exit code, and what its output says or must not say."""
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            got = call()
    except Exception as e:
        return ["%s: raised %s instead of exiting %d" % (what, type(e).__name__, want)]
    text = out.getvalue()
    missed = [] if got == want else ["%s: exit %r, wanted %d" % (what, got, want)]
    missed += ["%s: the output does not say %r" % (what, s) for s in must_say if s not in text]
    missed += ["%s: the output says %r" % (what, s) for s in must_not_say if s in text]
    return missed


def _test_runtime_ident_filters():
    """Verify A-L18 common-word deny-list and DISCRETE_IDENTITY_ALLOW opt-out."""
    keys = ("USERNAME", "COMPUTERNAME", "USERDOMAIN", "USERPROFILE", "HOME", "DISCRETE_IDENTITY_ALLOW")
    saved = {k: os.environ.get(k) for k in keys}
    try:
        os.environ["USERNAME"] = "runner"
        os.environ["COMPUTERNAME"] = "BUILD"
        os.environ["USERDOMAIN"] = "CORP"
        os.environ["USERPROFILE"] = "/home/runner"
        os.environ["HOME"] = "/home/runner"
        os.environ["DISCRETE_IDENTITY_ALLOW"] = ""
        if runtime_identifiers():
            return EXIT_FINDING
        os.environ["USERNAME"] = "UniqueHostUser99"
        os.environ["DISCRETE_IDENTITY_ALLOW"] = "uniquehostuser99"
        if runtime_identifiers():
            return EXIT_FINDING
        os.environ["DISCRETE_IDENTITY_ALLOW"] = ""
        if "UniqueHostUser99" not in runtime_identifiers():
            return EXIT_FINDING
        return EXIT_OK
    finally:
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v


def selftest():
    """-> (cases run, [how each missed])."""
    tmp = tempfile.mkdtemp(prefix="check_no_local_paths_")
    try:
        with open(os.path.join(tmp, "leak.txt"), "w", encoding="utf-8") as fh:
            fh.write("see " + _FIXTURE_HOME + "/notes.txt\n")
        with open(os.path.join(tmp, "clean.txt"), "w", encoding="utf-8") as fh:
            fh.write("see %LOCALAPPDATA%/notes.txt\n")
        with open(os.path.join(tmp, "session.txt"), "w", encoding="utf-8") as fh:
            fh.write("see " + _FIXTURE_HOME + "/.claude/projects/x\n")
        with open(os.path.join(tmp, "日誌.txt"), "w", encoding="utf-8") as fh:
            fh.write("see " + _FIXTURE_HOME + "/notes.txt\n")
        not_a_repo = "fatal: not a git repository (or any of the parent directories): .git"
        not_owned = "fatal: detected dubious ownership in repository at '" + _FIXTURE_HOME + "/repo'"
        no_git = [os.path.join(tmp, "no-such-git.exe")]
        leak_msg = "deadbeef12345678\x01leaked " + _FIXTURE_HOME + "/msg.txt\x02"
        leak_patch = (
            "COMMIT:1234567890abcdef\n"
            "+++ b/clean.txt\n"
            "+see " + _FIXTURE_HOME + "/committed.txt\n"
        )
        cases = [
            ("git is not on PATH", EXIT_CANNOT_RUN,
             lambda: run([], no_git, tmp), ["CANNOT RUN"], []),
            ("this copy is not a git checkout", EXIT_CANNOT_RUN,
             lambda: run([], _stand_in_git(rc=128, stderr=not_a_repo), tmp),
             ["CANNOT RUN", "not a git repository"], []),
            ("git lists no file", EXIT_CANNOT_RUN,
             lambda: run([], _stand_in_git(), tmp), ["CANNOT RUN"], []),
            ("git's reason names an account, which is redacted", EXIT_CANNOT_RUN,
             lambda: run([], _stand_in_git(rc=128, stderr=not_owned), tmp),
             ["CANNOT RUN", "dubious ownership"], ["Alice"]),
            ("the checker crashes, and its traceback is redacted", EXIT_CANNOT_RUN,
             lambda: guarded(_crash, []), ["CANNOT RUN", "RuntimeError"], ["Alice"]),
            ("git cannot list the unpushed commits", EXIT_CANNOT_RUN,
             lambda: run([], _stand_in_git(["clean.txt"], log_rc=128), tmp),
             ["CANNOT RUN", "git log"], []),
            ("a listed file cannot be read", EXIT_CANNOT_RUN,
             lambda: run([], _stand_in_git(["clean.txt", "gone.txt"]), tmp),
             ["CANNOT RUN", "gone.txt"], ["CHECK OK"]),
            ("control: a concrete user home is a finding", EXIT_FINDING,
             lambda: run([], _stand_in_git(["leak.txt"]), tmp), ["FOUND"], ["Alice"]),
            ("control: a session folder is a finding, its account masked", EXIT_FINDING,
             lambda: run([], _stand_in_git(["session.txt"]), tmp), ["FOUND"], ["Alice"]),
            ("control: a non-ASCII file name is read", EXIT_FINDING,
             lambda: run([], _stand_in_git(["clean.txt", "日誌.txt"]), tmp), ["FOUND"], ["Alice"]),
            ("control: a clean tree passes", EXIT_OK,
             lambda: run([], _stand_in_git(["clean.txt"]), tmp), ["CHECK OK"], []),
            ("A-L18: unpushed commit diff leak is caught even when working tree is clean", EXIT_FINDING,
             lambda: run([], _stand_in_git(["clean.txt"], patch_out=leak_patch), tmp),
             ["UNPUSHED commit DIFF", "12345678"], ["Alice"]),
            ("A-L18: --list prints BOTH commit-message and file hits and exits 0", EXIT_OK,
             lambda: run(["--list"], _stand_in_git(["leak.txt"], log_out=leak_msg), tmp),
             ["UNPUSHED commit MESSAGE", "in tracked files"], ["Alice"]),
            ("A-L18: --staged catches a leak in index blob", EXIT_FINDING,
             lambda: run(["--staged"], _stand_in_git(
                 staged=["staged.txt"], blobs={"staged.txt": "x " + _FIXTURE_HOME + "/y\n"}), tmp),
             ["STAGED file", "staged.txt"], ["Alice"]),
            ("A-L18: --staged passes when index blob is clean", EXIT_OK,
             lambda: run(["--staged"], _stand_in_git(
                 staged=["staged.txt"], blobs={"staged.txt": "see %LOCALAPPDATA%/x\n"}), tmp),
             ["CHECK OK"], []),
            ("A-L18: common CI words and DISCRETE_IDENTITY_ALLOW are filtered", EXIT_OK,
             _test_runtime_ident_filters, [], []),
        ]
        missed = []
        for what, want, call, say, not_say in cases:
            missed += _expect(what, want, call, say, not_say)
        return len(cases), missed
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main(argv):
    n, missed = selftest()
    if missed:
        for m in missed:
            print("SELFTEST FAILED: " + m)
        return cannot_run("the checker misses its own controls (above), so its verdict on the "
                          "tree would mean nothing")
    if "--selftest" in argv:
        print("check_no_local_paths: selftest OK (%d cases)" % n)
        return EXIT_OK
    return run(argv)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(guarded(main, sys.argv[1:]))
