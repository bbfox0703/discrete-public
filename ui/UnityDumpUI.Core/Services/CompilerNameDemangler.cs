// CompilerNameDemangler — translate Roslyn / Mono / IL2CPP compiler-
// generated names back into something a human can recognise. The raw
// names (async state machines, closures, lambdas, backing fields) are
// preserved on the wire and in lookups; this helper only produces a
// friendlier *display* string, so the UI can show e.g.
//
//   <CreateUnitResult>d__41        →  "CreateUnitResult [async]"
//   <>c__DisplayClass5_0           →  "[closure 5_0]"
//   <>c                            →  "[lambda cache]"
//   <CreateUnitResult>b__41_0      →  "CreateUnitResult.lambda_41_0"
//   <Name>k__BackingField          →  "Name [backing]"
//
// Nested-class paths are demangled per segment, so
// "Outer/<Async>d__1/<>c__DisplayClass0_0" becomes
// "Outer / Async [async] / [closure 0_0]".
//
// The demangler is intentionally regex-free (AOT-safe) and allocates
// nothing for already-clean names — the hot path is just a couple of
// substring scans returning the input unchanged.

using System;
using System.Collections.Generic;
using System.Text;

namespace UnityDumpUI.Core.Services;

public static class CompilerNameDemangler
{
    /// <summary>
    /// Demangle a single class / method / field segment. Returns the
    /// input unchanged if it doesn't match any known pattern.
    /// </summary>
    public static string Demangle(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        // Quick reject: every pattern we care about contains '<' or starts
        // with '<>'. Skip the work otherwise.
        if (raw.IndexOf('<') < 0) return raw;

        // <>c          — static lambda cache class on enclosing type
        if (raw == "<>c") return "[lambda cache]";
        // <>O          — cached delegate cache (newer Roslyn)
        if (raw == "<>O") return "[delegate cache]";

        // <>c__DisplayClassN[_M] — closure capture class
        if (raw.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
        {
            string tail = raw.AsSpan(17).ToString();
            return tail.Length == 0 ? "[closure]" : $"[closure {tail}]";
        }

        // <>9__N_M     — cached delegate field on <>c
        // <>9          — older single-field form
        if (raw.StartsWith("<>9__", StringComparison.Ordinal))
            return $"[lambda field {raw.AsSpan(5).ToString()}]";
        if (raw == "<>9") return "[lambda field]";

        // <>1__state / <>2__current / <>4__this / <>5__N etc.
        // Generic "<>N__name" iterator / async helper fields.
        if (raw.Length > 3 && raw[0] == '<' && raw[1] == '>' && IsDigit(raw[2]))
        {
            int us = raw.IndexOf("__", 2, StringComparison.Ordinal);
            if (us > 0 && us + 2 < raw.Length)
            {
                string label = raw.Substring(us + 2);
                return label switch
                {
                    "state"   => "[state]",
                    "current" => "[current]",
                    "this"    => "[outer this]",
                    _         => $"[{label}]",
                };
            }
        }

        // Named patterns: <NAME>X..., where X identifies the kind:
        //   <Foo>d__N            — async / iterator state machine
        //   <Foo>b__N[_M]        — lambda body
        //   <Foo>k__BackingField — auto-property backing field
        //   <Foo>g__Local|N_M    — local function (Roslyn)
        if (raw[0] == '<')
        {
            int gt = raw.IndexOf('>', 1);
            if (gt > 1 && gt + 1 < raw.Length)
            {
                string inner = raw.Substring(1, gt - 1);
                string tail  = raw.Substring(gt + 1);
                string? friendly = TryDemangleNamed(inner, tail);
                if (friendly != null) return friendly;
            }
        }

        return raw;
    }

    /// <summary>
    /// Demangle a nested-class path. The DLL emits nested classes as
    /// "Outer/Inner" — we demangle each segment and rejoin with " / ".
    /// </summary>
    public static string DemangleClassPath(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        if (raw.IndexOf('/') < 0 && raw.IndexOf('+') < 0)
            return Demangle(raw);

        // Split on either '/' (our DLL convention) or '+' (reflection
        // convention — caller may pass either). Backticks (generic arity)
        // stay attached to their segment.
        var parts = new List<string>(4);
        int start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '/' || c == '+')
            {
                parts.Add(raw.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(raw.Substring(start));

        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(" / ");
            sb.Append(Demangle(parts[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// One-line origin description, or null if the name is not
    /// compiler-generated. Useful for tooltips / export comments.
    /// </summary>
    public static string? DescribeOrigin(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('<') < 0) return null;
        if (raw == "<>c") return "static lambda cache class";
        if (raw == "<>O") return "delegate cache class";
        if (raw.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
            return "closure capture class";
        if (raw.StartsWith("<>9__", StringComparison.Ordinal) || raw == "<>9")
            return "cached delegate field";
        if (raw.Length > 3 && raw[0] == '<' && raw[1] == '>' && IsDigit(raw[2]))
            return "compiler-generated state field";
        if (raw[0] == '<')
        {
            int gt = raw.IndexOf('>', 1);
            if (gt > 1 && gt + 1 < raw.Length)
            {
                string inner = raw.Substring(1, gt - 1);
                string tail  = raw.Substring(gt + 1);
                if (tail.StartsWith("d__", StringComparison.Ordinal))
                    return $"async / iterator state machine for '{inner}'";
                if (tail.StartsWith("b__", StringComparison.Ordinal))
                    return $"lambda body inside '{inner}'";
                if (tail.StartsWith("g__", StringComparison.Ordinal))
                    return $"local function inside '{inner}'";
                if (tail == "k__BackingField")
                    return $"auto-property backing field for '{inner}'";
            }
        }
        return null;
    }

    private static string? TryDemangleNamed(string inner, string tail)
    {
        // <Name>d__N — async / iterator state machine class
        if (tail.StartsWith("d__", StringComparison.Ordinal))
        {
            string n = tail.Substring(3);
            return n.Length == 0
                ? $"{NameOrPlaceholder(inner)} [async]"
                : $"{NameOrPlaceholder(inner)} [async {n}]";
        }
        // <Name>b__N[_M] — lambda body (method on closure or enclosing type)
        if (tail.StartsWith("b__", StringComparison.Ordinal))
            return $"{NameOrPlaceholder(inner)}.lambda_{tail.Substring(3)}";
        // <Name>g__Local|N_M — local function
        if (tail.StartsWith("g__", StringComparison.Ordinal))
        {
            string rest = tail.Substring(3);
            int bar = rest.IndexOf('|');
            string localName = bar > 0 ? rest.Substring(0, bar) : rest;
            return $"{NameOrPlaceholder(inner)}.{localName} [local fn]";
        }
        // <Name>k__BackingField — auto-property backing field
        if (tail == "k__BackingField")
            return $"{NameOrPlaceholder(inner)} [backing]";
        return null;
    }

    private static string NameOrPlaceholder(string s)
        => string.IsNullOrEmpty(s) ? "?" : s;

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
