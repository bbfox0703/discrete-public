// Il2cppHeaderBatchExporter.cs — Phase C (Il2CppDumper feature parity).
//
// Whole-game `il2cpp.h` generator: walks every user assembly, every class
// in each, and concatenates one big C-style header. Reuses the single-
// class `SdkHeaderExporter`'s field-rendering rules (offsets, type map,
// static skip, pragma pack) but emits fully-qualified struct names so
// names don't collide across namespaces, and includes a trailing
// `// Methods:` block per class with native AOT addresses — IL2CPP only,
// since Mono's JIT addresses don't survive a restart.
//
// Trade-off vs Il2CppDumper's il2cpp.h:
//   * We DON'T flatten inheritance into the struct body (Il2CppDumper
//     does). Each struct only declares its own fields, with a `// inherits
//     <ParentNs.ParentName>` banner so the reader knows to chase the
//     ancestor for the full instance layout. This keeps the file linear
//     and avoids depth-first inheritance-walk explosions on the ~200K-
//     class IL2CPP games in our corpus.
//   * We DO inline real method native addresses (Il2CppDumper only puts
//     RVAs in dump.cs). That's the path-B value-add we picked over a
//     pure-offline (path-A) exporter.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services.Export;

public static class Il2cppHeaderBatchExporter
{
    /// <summary>Per-export tally returned to the caller — used by the
    /// ViewModel for status / log lines.</summary>
    public sealed record class BatchStats(
        int AssembliesScanned,
        int ClassesEmitted,
        int ClassesFailed,
        int MethodsEmitted,
        int FieldsEmitted);

    /// <summary>
    /// Bulk-RPC entry point (Phase C follow-up). Renders the same
    /// whole-game header from a single `dumpClassInfo` round-trip
    /// instead of N×getClassInfo. Fixes the slow-quit issue documented
    /// in lessons-learned.md "Phase C follow-up — IL2CPP equivalent"
    /// AND cuts export time from ~20-40s to ~2-5s on large games.
    /// Pass the same `assemblyNames` order you sent to
    /// <see cref="BackendAdapter.DumpClassInfoAsync"/>; the renderer
    /// uses it to emit per-assembly banners in input order.
    /// </summary>
    public static (string Text, BatchStats Stats) RenderFromBulk(
        IReadOnlyList<string>             assemblyNames,
        DumpClassInfoResult               bulkResult)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);
        ArgumentNullException.ThrowIfNull(bulkResult);

        var sb = new StringBuilder(64 * 1024);
        int classesEmitted = 0, classesFailed = 0, methodsEmitted = 0, fieldsEmitted = 0;

        WriteFileHeader(sb);

        // Group rows by assembly preserving input order. Server returns
        // rows in walked order (per assembly, per class) — but defensive
        // grouping makes the renderer order-independent.
        var rowsByAssembly = new Dictionary<string, List<DumpedClassRow>>(
            StringComparer.Ordinal);
        foreach (DumpedClassRow row in bulkResult.Rows)
        {
            if (!rowsByAssembly.TryGetValue(row.Assembly, out List<DumpedClassRow>? list))
            {
                list = new List<DumpedClassRow>();
                rowsByAssembly[row.Assembly] = list;
            }
            list.Add(row);
        }

        foreach (string assemblyName in assemblyNames)
        {
            if (!rowsByAssembly.TryGetValue(assemblyName, out List<DumpedClassRow>? rows))
            {
                // Assembly was requested but the server didn't return any rows
                // (image not loaded — already surfaced via MissingAssemblies).
                continue;
            }
            sb.Append("// ============================================================\n");
            sb.Append("// Assembly: ").Append(assemblyName)
              .Append("  (").Append(rows.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" classes)\n");
            sb.Append("// ============================================================\n\n");

            foreach (DumpedClassRow row in rows)
            {
                string ns   = row.Namespace ?? string.Empty;
                string name = row.Name;
                string fqn  = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

                if (!string.IsNullOrEmpty(row.Error))
                {
                    sb.Append("// SKIPPED ").Append(fqn)
                      .Append(" — ").Append(SafeOneLine(row.Error!)).Append("\n\n");
                    ++classesFailed;
                    continue;
                }

                // Project DumpedClassRow → GetClassInfoResult so EmitOneClass
                // can stay backend-agnostic (same projection both code paths use).
                var info = new GetClassInfoResult
                {
                    Namespace = ns,
                    Name      = name,
                    Parent    = row.Parent,
                    Fields    = row.Fields,
                    Methods   = row.Methods,
                };
                EmitOneClass(sb, info, fqn, ref methodsEmitted, ref fieldsEmitted);
                ++classesEmitted;
            }
        }

        // missing_assemblies + truncated surface to the caller via
        // BatchStats.ClassesFailed delta vs assemblyNames coverage; the
        // ViewModel formats the user-facing message. No need to bake
        // them into the header text itself.
        return (sb.ToString(),
                new BatchStats(assemblyNames.Count, classesEmitted, classesFailed,
                               methodsEmitted, fieldsEmitted));
    }

    private static void WriteFileHeader(StringBuilder sb)
    {
        sb.Append("// auto-generated by Discrete — whole-game IL2CPP header\n");
        sb.Append("// Source: live IL2CPP process (Tools → Export il2cpp.h…)\n");
        sb.Append("// Conventions:\n");
        sb.Append("//   * Struct names are fully qualified: Namespace_Class\n");
        sb.Append("//     (collision-safe across the whole game).\n");
        sb.Append("//   * Each struct declares ONLY its own instance fields.\n");
        sb.Append("//     Inheritance is not flattened — follow the\n");
        sb.Append("//     `// inherits <Parent>` banner to walk the chain.\n");
        sb.Append("//   * Static / literal fields are omitted from the struct body.\n");
        sb.Append("//   * Method AOT addresses (IL2CPP only) trail each struct.\n\n");
        sb.Append("#pragma once\n");
        sb.Append("#include <cstdint>\n\n");
    }

    /// <summary>
    /// Render the whole-game il2cpp.h. Each user assembly contributes a
    /// banner + N struct definitions. <paramref name="getClasses"/> /
    /// <paramref name="getClassInfo"/> are the only backend hooks — wire
    /// them to <see cref="BackendAdapter.GetClassesAsync"/> /
    /// <see cref="BackendAdapter.GetClassInfoAsync"/> from the call site.
    /// </summary>
    /// <param name="assemblyNames">Filtered user-assembly list (caller
    /// is expected to have already passed it through
    /// <see cref="SystemAssemblyFilter.SelectUserAssemblies"/>).</param>
    /// <param name="progress">Optional progress callback fired once per
    /// class. Args: (assembly, classQualifiedName, classIndex, totalSoFar).
    /// Useful for surfacing live counts in the UI without blocking the
    /// pipe loop.</param>
    public static async Task<(string Text, BatchStats Stats)> ExportAsync(
        IReadOnlyList<string>                                                            assemblyNames,
        Func<string, CancellationToken, Task<GetClassesResult>>                          getClasses,
        Func<string, string?, string, CancellationToken, Task<GetClassInfoResult?>>      getClassInfo,
        Action<string /*assembly*/, string /*classFqn*/, int /*assemblyClassIndex*/, int /*totalEmitted*/>? progress = null,
        CancellationToken                                                                ct = default)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);
        ArgumentNullException.ThrowIfNull(getClasses);
        ArgumentNullException.ThrowIfNull(getClassInfo);

        var sb = new StringBuilder(64 * 1024);

        int classesEmitted = 0, classesFailed = 0, methodsEmitted = 0, fieldsEmitted = 0;

        WriteFileHeader(sb);

        foreach (string assemblyName in assemblyNames)
        {
            ct.ThrowIfCancellationRequested();

            GetClassesResult classesResult;
            try
            {
                classesResult = await getClasses(assemblyName, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sb.Append("// ============================================================\n");
                sb.Append("// Assembly: ").Append(assemblyName).Append("\n");
                sb.Append("// ERROR: getClasses failed: ").Append(SafeOneLine(ex.Message)).Append('\n');
                sb.Append("// ============================================================\n\n");
                continue;
            }
            if (!string.IsNullOrEmpty(classesResult.Error))
            {
                sb.Append("// ============================================================\n");
                sb.Append("// Assembly: ").Append(assemblyName).Append("\n");
                sb.Append("// ERROR: ").Append(SafeOneLine(classesResult.Error!)).Append('\n');
                sb.Append("// ============================================================\n\n");
                continue;
            }

            sb.Append("// ============================================================\n");
            sb.Append("// Assembly: ").Append(assemblyName)
              .Append("  (").Append(classesResult.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" classes)\n");
            sb.Append("// ============================================================\n\n");

            int idxInAsm = 0;
            foreach (ClassEntry ce in classesResult.Classes)
            {
                ct.ThrowIfCancellationRequested();

                string ns   = ce.Namespace ?? string.Empty;
                string name = ce.Name;
                string fqn  = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

                GetClassInfoResult? info;
                try
                {
                    info = await getClassInfo(assemblyName, ns, name, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    info = null;
                }

                if (info is null)
                {
                    sb.Append("// SKIPPED ").Append(fqn).Append(" — getClassInfo failed\n\n");
                    ++classesFailed;
                    ++idxInAsm;
                    continue;
                }

                EmitOneClass(sb, info, fqn, ref methodsEmitted, ref fieldsEmitted);
                ++classesEmitted;
                ++idxInAsm;
                progress?.Invoke(assemblyName, fqn, idxInAsm, classesEmitted);
            }
        }

        return (sb.ToString(),
                new BatchStats(assemblyNames.Count, classesEmitted, classesFailed,
                               methodsEmitted, fieldsEmitted));
    }

    // Emit one class block: comment header + struct + trailing method addresses.
    private static void EmitOneClass(
        StringBuilder sb,
        GetClassInfoResult info,
        string classFqn,
        ref int methodsEmitted,
        ref int fieldsEmitted)
    {
        string structName = BuildStructName(info.Namespace, info.Name);

        sb.Append("// Class: ").Append(classFqn).Append('\n');
        if (info.Parent is { Name: { Length: > 0 } pn })
        {
            string pns      = info.Parent.Namespace ?? string.Empty;
            string parentFqn = string.IsNullOrEmpty(pns) ? pn : $"{pns}.{pn}";
            sb.Append("// inherits ").Append(parentFqn).Append('\n');
        }

        sb.Append("#pragma pack(push, 1)\n");
        sb.Append("struct ").Append(structName).Append(" {\n");

        var instanceFields = info.Fields?
            .Where(f => !f.Static)
            .OrderBy(f => f.Offset)
            .ToList() ?? new List<FieldEntry>();
        if (instanceFields.Count == 0)
        {
            sb.Append("    // (no instance fields declared on this class)\n");
        }
        else
        {
            foreach (FieldEntry f in instanceFields)
            {
                ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
                sb.Append("    ");
                sb.Append(shape.CHeaderType);
                sb.Append(' ');
                sb.Append(SanitizeIdentifier(f.Name));
                sb.Append(';');
                sb.Append("    // +0x");
                sb.Append(f.Offset.ToString("X", CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(f.Type))
                {
                    sb.Append("  ").Append(f.Type);
                }
                sb.Append('\n');
                ++fieldsEmitted;
            }
        }

        sb.Append("};\n");
        sb.Append("#pragma pack(pop)\n");

        // Method address trailer (IL2CPP only — Mono returns null Address).
        var methodsWithAddr = info.Methods?
            .Where(m => !string.IsNullOrEmpty(m.Address))
            .ToList() ?? new List<MethodEntry>();
        if (methodsWithAddr.Count > 0)
        {
            sb.Append("// Methods (AOT entry points, GameAssembly.dll absolute):\n");
            foreach (MethodEntry m in methodsWithAddr)
            {
                sb.Append("//   ");
                sb.Append(m.Static ? "[static] " : "         ");
                sb.Append(m.Name);
                sb.Append("    @ ").Append(m.Address);
                sb.Append("    token=0x").Append(m.Token.ToString("X8", CultureInfo.InvariantCulture));
                sb.Append('\n');
                ++methodsEmitted;
            }
        }
        sb.Append('\n');
    }

    /// <summary>
    /// Whole-game-safe struct identifier: <c>Namespace_Class</c>. Mirrors the
    /// debugger-symbol label convention from
    /// <see cref="SymbolExporter.BuildLabel"/> so a user can cross-reference
    /// the .h and the .dd64 / .idc / .py output. Public for unit testing.
    /// </summary>
    public static string BuildStructName(string? @namespace, string? name)
    {
        var sb = new StringBuilder(48);
        if (!string.IsNullOrEmpty(@namespace))
        {
            sb.Append(@namespace);
            sb.Append('_');
        }
        sb.Append(string.IsNullOrEmpty(name) ? "Anon" : name);
        return SanitizeIdentifier(sb.ToString());
    }

    private static string SanitizeIdentifier(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "_";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9') || c == '_';
            sb.Append(ok ? c : '_');
        }
        if (sb[0] >= '0' && sb[0] <= '9') sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string SafeOneLine(string s) =>
        s.Replace('\r', ' ').Replace('\n', ' ');
}
