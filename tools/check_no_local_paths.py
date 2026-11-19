r"""Fail the build if a machine-local user path leaked into a tracked file.

Ported from the sibling project UE5CEDumper (tools/check_no_local_paths.py);
the dated incidents quoted below happened there, and are kept because they
explain why each rule exists.

    py tools/check_no_local_paths.py
    py tools/check_no_local_paths.py --list      # show every hit, do not exit 1
    py tools/check_no_local_paths.py --selftest  # the cannot-run controls only (every run does them first)

Exit codes: 0 nothing found; 1 (EXIT_FINDING) a tracked file or an unpushed commit
message carries a path or an identifier; 2 (EXIT_CANNOT_RUN) the tree was never
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
SESSION_DIR_PATTERN = re.compile(r"[A-Za-z]:" + _SEP + r"(?:[^\s:*?\"<>|]{0,120}" + _SEP +
                                 r")*?\.claude" + _SEP + "projects", re.IGNORECASE)

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


def runtime_identifiers():
    """{value: where it came from} for this machine. Nothing here is ever persisted."""
    found = {}
    for var in ("USERNAME", "COMPUTERNAME", "USERDOMAIN"):
        v = (os.environ.get(var) or "").strip()
        if len(v) >= MIN_IDENTIFIER_LEN and v.lower() not in ALLOWED:
            found.setdefault(v, var)
    home = (os.environ.get("USERPROFILE") or os.environ.get("HOME") or "").rstrip("\\/")
    leaf = os.path.basename(home)
    if len(leaf) >= MIN_IDENTIFIER_LEN and leaf.lower() not in ALLOWED:
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


def run(argv, git=GIT, root=ROOT):
    """The scan of the tree at `root`; `git` is the argv that lists it."""
    list_only = "--list" in argv
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
                    for m in (() if self_scan else PATTERN.finditer(line)):
                        if m.group(1).lower() in ALLOWED:
                            continue
                        # ⚠ REDACT THE ACCOUNT SEGMENT. This branch used to print m.group(0) and
                        # the raw line, i.e. it echoed the account name it had just caught -- which
                        # is how a caught leak gets laundered into a commit message or a CI log.
                        hits.append((path, n, mask_account(m), mask_account_line(line)))
                    # ALLOWED deliberately does NOT apply here -- see SESSION_DIR_PATTERN.
                    for m in (() if self_scan else SESSION_DIR_PATTERN.finditer(line)):
                        hits.append((path, n, scrub(m.group(0)), scrub(line.strip())[:110]))
                    for value, rx in ident_patterns.items():
                        if rx.search(line):
                            # Redact in BOTH the fragment and the quoted line.
                            hits.append((path, n, redact(value),
                                         rx.sub(redact(value), line.strip())[:110]))
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
            for m in PATTERN.finditer(line):
                if m.group(1).lower() in ALLOWED:
                    continue
                msg_hits.append((sha, mask_account(m), mask_account_line(line)))
            for m in SESSION_DIR_PATTERN.finditer(line):
                msg_hits.append((sha, scrub(m.group(0)), scrub(line.strip())[:110]))
            for value, rx in ident_patterns.items():
                if rx.search(line):
                    msg_hits.append((sha, redact(value),
                                     rx.sub(redact(value), line.strip())[:110]))

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
        return EXIT_FINDING

    if not hits:
        print(f"CHECK OK: no machine-local user path in {len(files)} tracked files, "
              f"nor in any unpushed commit message.")
        return EXIT_OK

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


def _stand_in_git(listing=(), rc=0, stderr="", log_rc=0, log_out=""):
    """An argv that answers like git: `ls-files` writes the NUL-separated `listing` and `stderr`
    and exits `rc`; `log` writes `log_out` and exits `log_rc`; anything else exits 0."""
    listed = "".join(p + "\0" for p in listing)
    code = ("import sys; c = sys.argv[1:2]; a = c == ['ls-files']; g = c == ['log']; "
            "sys.stdout.buffer.write((%r if a else (%r if g else '')).encode('utf-8')); "
            "sys.stderr.buffer.write((%r if a else '').encode('utf-8')); "
            "sys.exit(%d if a else (%d if g else 0))" % (listed, log_out, stderr, rc, log_rc))
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
