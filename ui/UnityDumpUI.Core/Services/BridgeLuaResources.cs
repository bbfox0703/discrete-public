// BridgeLuaResources.cs — Phase 22b accessor for the 5 CE Lua bridge
// scripts embedded in UnityDumpUI.Core.dll's manifest.
//
// Each script is shipped as an <EmbeddedResource> in the project file
// with a stable LogicalName (UnityDumpUI.Core.Resources.Bridge.<file>),
// so this lookup works regardless of where the source physically lives
// on disk (scripts/ is outside the project tree, but Include="..\..\…"
// in the csproj wires it up at build time).
//
// Used by Phase 22d's "Inject Discrete Bridge into CE Table" Tools-menu
// command: iterate LoadOrder, for each call AobMakerBridge
// .InjectTableFileAsync(fileName, content). The 8th call is the
// bootstrap AA script (22c) that loads the embedded files at runtime.
//
// Why this lives in Core (not UI):
//   * Test project only references Core — putting the accessor here
//     keeps the test project's dependency graph clean.
//   * Pure data access: no Avalonia / UI dependencies, no Native AOT
//     analyzer concerns. GetManifestResourceStream is AOT-safe.
//   * Mirrors UE5CEDumper's HelperLuaResource.cs layout, scaled from 1
//     to 5 files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UnityDumpUI.Core.Services;

public static class BridgeLuaResources
{
    /// <summary>
    /// Logical-name prefix shared by every embedded bridge file. See the
    /// <c>&lt;LogicalName&gt;</c> entries in <c>UnityDumpUI.Core.csproj</c>.
    /// </summary>
    private const string ResourcePrefix = "UnityDumpUI.Core.Resources.Bridge.";

    /// <summary>
    /// Bridge lua filenames in the order Phase 22c's bootstrap AA script
    /// will load them. utils.lua first because <c>unity_dump.lua</c> calls
    /// <c>require 'utils'</c>; the rest are independent (verified 2026-05-12
    /// via grep of all <c>require</c>/<c>dofile</c> sites). Order is also
    /// what <see cref="ReadAll"/> yields.
    /// <para>
    /// <c>mono_registersymbol_helpers.lua</c> is the DLL-free Mono symbol
    /// register helpers — bundled into the CT here so the
    /// MethodOverloads-tab "Copy mono_register* AA script" output can
    /// findTableFile() + chunk-load it on enable. The file is
    /// side-effect-free at load time (only function defs land in _G);
    /// running it at bridge boot is harmless to users who never hit the
    /// DLL-free path.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LoadOrder { get; } = new[]
    {
        // Core — injected by "Inject Discrete Bridge" (DLL inject + pipe):
        "utils.lua",
        "unity_dump.lua",
        // Action deps — embedded on demand by the Send-to-CE-Table / Create-AA
        // actions whose generated AA self-loads them (NOT injected by the
        // bridge). Kept here so Read() can serve them to those call sites.
        "unity_invoke.lua",                 // CallMethod AA   → callUnityMethod
        "unity_symbols.lua",                // Mono-JIT AA     → registerMethodSymbol
        "mono_registersymbol_helpers.lua",  // DLL-free Mono   → discrete_mono_registerSymbolEx
    };

    /// <summary>
    /// The only files "Inject Discrete Bridge" embeds + the bootstrap loads:
    /// <c>utils.lua</c> (required by the bootstrap's
    /// <c>package.preload['utils']</c> trick) + <c>unity_dump.lua</c> (injects
    /// the backend DLL + starts the pipe). Everything else is an action dep
    /// embedded on demand by the action that needs it. Listed in load order.
    /// </summary>
    public static IReadOnlyList<string> CoreFiles { get; } = new[]
    {
        "utils.lua",
        "unity_dump.lua",
    };

    /// <summary>
    /// Read one bridge lua file by filename (case-sensitive — the
    /// LogicalName entries in csproj are case-sensitive too).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not in <see cref="LoadOrder"/>.</exception>
    /// <exception cref="InvalidOperationException">The resource is missing
    /// from the assembly. Indicates a packaging bug — the
    /// <c>EmbeddedResource</c> link was lost or the LogicalName drifted
    /// from <see cref="ResourcePrefix"/> + <paramref name="fileName"/>.</exception>
    public static string Read(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentException("fileName must not be empty", nameof(fileName));
        if (!LoadOrder.Contains(fileName))
            throw new ArgumentException(
                $"'{fileName}' is not a known bridge lua file. " +
                $"Expected one of: {string.Join(", ", LoadOrder)}",
                nameof(fileName));

        string resourceName = ResourcePrefix + fileName;
        var asm = typeof(BridgeLuaResources).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found in " +
                $"{asm.GetName().Name}. Check UnityDumpUI.Core.csproj " +
                $"<EmbeddedResource> link for scripts/{fileName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Enumerate all 5 bridge lua files in <see cref="LoadOrder"/>,
    /// yielding <c>(fileName, content)</c> pairs. The fileName is the
    /// CE-table-side identifier (passed to
    /// <c>IAobMakerBridge.InjectTableFileAsync</c>'s <c>fileName</c>
    /// argument and used by the bootstrap script's
    /// <c>findTableFile(name)</c> calls), NOT the LogicalName.
    /// </summary>
    public static IReadOnlyList<(string FileName, string Content)> ReadAll()
        => ReadAll(LoadOrder);

    /// <summary>
    /// Read a specific subset of bridge lua files (e.g. the output of
    /// <see cref="ResolveFiles"/>) as <c>(fileName, content)</c> pairs, in
    /// the order given. Each name must be a known bridge file.
    /// </summary>
    /// <exception cref="ArgumentException">A name is not a known bridge file.</exception>
    public static IReadOnlyList<(string FileName, string Content)> ReadAll(
        IReadOnlyList<string> fileNames)
    {
        if (fileNames is null) throw new ArgumentNullException(nameof(fileNames));
        var list = new List<(string, string)>(fileNames.Count);
        foreach (string fileName in fileNames)
            list.Add((fileName, Read(fileName)));
        return list;
    }

    /// <summary>
    /// Diagnostic — return all manifest resource names with the bridge
    /// prefix. Useful when the manifest is suspected to have drifted
    /// (e.g. someone renamed a script but forgot the LogicalName).
    /// </summary>
    public static IReadOnlyList<string> ListEmbeddedNames()
    {
        var asm = typeof(BridgeLuaResources).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }
}
