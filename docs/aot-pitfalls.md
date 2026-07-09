# Avalonia + .NET Native AOT — Landmine Catalogue

> **Audience**: anyone touching a `src/ui/` in this repo (or any other repo
> in this family) for the first time. Every entry below has been paid for
> at least once across **UE5CEDumper, RE-UNION, CrimsonAtomtic, discrete,
> ZoltDump, AOBMaker**. Read top to bottom before writing your first
> `.axaml` or `csproj` — you will save days.
>
> **Date**: 2026-05-25.  **Stack**: Avalonia 12.0.3, .NET 10,
> `CommunityToolkit.Mvvm` 8.x, `MicroCom.Runtime` 0.11.6, Native AOT on
> `win-x64`.
>
> **How to use**: each section ends with a citation table pointing at the
> source repo + file where the lesson was originally paid for. Drill down
> when you need code to copy.

---

## §0  Day-one checklist (the hot 25)

The minimum set every new Avalonia AOT project must do before opening
the first `.axaml`. Skipping any of these will cost a session.

### Build configuration

1. **`PublishAot=false` by default**; enable via `-p:PublishAot=true` on
   publish only. Debug/Release stays JIT for fast iteration.
2. **`TrimMode=partial`** — combined with `TrimmerRootAssembly`, trims
   first-party code while keeping Avalonia internals whole.
3. **`BuiltInComInteropSupport=false`** — Avalonia 12's MicroCom needs
   this off for AOT.
4. **`AvaloniaUseCompiledBindingsByDefault=true`** — compiled bindings
   require `x:DataType` everywhere; reflection bindings die under AOT.
5. **`NoWarn=IL2104;IL3053`** — `IL2104` (DataGrid 12.0.0 trim rollup)
   and `IL3053` (Avalonia/MicroCom AOT analysis rollup) are noise
   covered by `TrimmerRootAssembly`. Leave **`IL2026` and `IL3050`
   visible** — those are real live-code issues you want to see.
6. **`InvariantGlobalization=true`** for AOT binary size — but be aware
   that `CultureInfo.CurrentUICulture.Name` returns `""`; any locale
   detection must P/Invoke Win32 `GetUserDefaultUILanguage`.
7. **`TrimmerRootAssembly`**: the canonical 13-assembly list
   ([§3.1](#31-trimmerrootassembly-canonical-list)).
8. **Static CRT linker args** (`/NODEFAULTLIB:msvcrt.lib` etc.) so the
   produced exe runs without VC++ Redistributable on bare Windows.
   UCRT stays dynamic. See [§3.4](#34-static-crt-no-vc-redist).

### Avalonia setup

9. **Force `Win32CompositionMode.RedirectionSurface`** in
   `AppBuilder.Configure<App>()`. WinUI Composition via MicroCom CCW
   vtable init crashes under AOT — this falls back to GDI compositor.
10. **Hand-rolled `IDataTemplate` ViewLocator** — never `Type.GetType`,
    never `Activator.CreateInstance`. Switch expression by type.
11. **`x:DataType="vm:..."` on every** `Window` / `UserControl` /
    `DataTemplate` root. Compiled bindings refuse without it.
12. **`StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"`**
    in `App.axaml` — the DataGrid Fluent style is not auto-pulled by
    `FluentTheme`.
13. **Don't reference `Avalonia.Desktop`** on `win-x64` — drags
    `Avalonia.X11` + `Avalonia.FreeDesktop` + `Tmds.DBus.Protocol`,
    ILC emits "will always throw" diagnostics for Linux entrypoints
    whose DBus types were trimmed away. Reference `Avalonia.Win32` +
    `Avalonia.Skia` + `Avalonia.HarfBuzz` explicitly. **Without
    `.UseHarfBuzz()` the process fast-fails with `0xC0000409` BEFORE
    any log writes**, looking identical to "EXE doesn't run".

### Library choices

14. **`CommunityToolkit.Mvvm` only**, never `ReactiveUI` (reflection +
    Expression.Compile, no v12). Use field form
    `[ObservableProperty] private T _name;` — partial-property syntax
    is broken in CTMvvm 8.4.0 on .NET 10.
15. **No `Microsoft.Extensions.DependencyInjection`** — hand-roll the
    composition root in `MainWindowViewModel`'s ctor (or
    `App.OnFrameworkInitializationCompleted`). Reflection-based
    service location is AOT-hostile.
16. **`System.Text.Json` source generators ONLY** — every
    `JsonSerializer.Serialize/Deserialize` call passes
    `JsonSerializerContext.Default.MyType`, never the `Type`-argument
    overload. Multiple small contexts per feature area beat one
    mega-context.

### Runtime patterns

17. **Top-level try/catch in `Program.Main`** that writes a crash log
    to `%LOCALAPPDATA%\<app>\crash.log` — AOT stack traces are
    gutted; this is your only debugging signal.
18. **`[LibraryImport]` not `[DllImport]`** — but be aware
    `LibraryImport` doesn't auto-suffix A/W. Spell `EntryPoint =
    "GetModuleHandleW"` literally. Set `DefaultDllImportSearchPaths(System32)`
    for system DLLs.
19. **`Assembly.GetEntryAssembly()` not `GetExecutingAssembly()`** —
    the latter is trimmed in single-file AOT publishes.
20. **`[GeneratedRegex(...)]` partial method** for all regex — never
    runtime `Regex.Compile`.

### XAML patterns

21. **Modal dialogs go code-behind** — no `.axaml`, no
    `CompiledBindings`. Read controls directly. The MVVM corner cases
    in dialog `Window`s under AOT aren't worth fighting.
22. **DataGrid context menus in code-behind** — XAML-bound
    `ContextMenu` on a DataGrid breaks because the popup is
    disconnected from the visual tree; `DataContext` becomes
    `object?`. ListBox + commands bound to outer VM stays in XAML
    fine.
23. **`AutoGenerateColumns="False"` on every DataGrid** — auto-gen
    walks properties via reflection.
24. **DataGrid sorting on `DataGridTemplateColumn` needs all three of**:
    `SortMemberPath="ClrPropName"` + `CanUserSort="True"` +
    `CustomSortComparer` set in code-behind. Simple
    `DataGridTextColumn` with `Binding="{Binding Foo}"` works
    out-of-box. ([§4.5](#45-datagrid-sort-the-three-layer-trap))
25. **`<Run Text="{Binding ...}"/>` inside `<TextBlock>` →
    StackOverflow under AOT**. Use `<TextBlock Text="{Binding ...}"/>`
    directly.

---

## §1  The full csproj template

Synthesized from `UE5CEDumper/ui/UE5DumpUI/UE5DumpUI.csproj`,
`ZoltDump/ui/ZoltDumpUI/ZoltDumpUI.csproj`,
`RE-UNION/ui/REUnionUI/REUnionUI.csproj`,
`CrimsonAtomtic/src/CrimsonAtomtic.Ui/CrimsonAtomtic.Ui.csproj`,
`discrete/ui/UnityDumpUI/UnityDumpUI.csproj`,
`AOBMaker/src/AOBMaker.UI/AOBMaker.UI.csproj`.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- IL3053: third-party AOT analysis from Avalonia/MicroCom (covered by TrimmerRootAssembly).
         IL2104: Avalonia.Controls.DataGrid 12.0.0 trim rollup (covered by TrimmerRootAssembly).
         Leave IL2026 + IL3050 visible — those are real reflection/runtime-codegen issues. -->
    <NoWarn>$(NoWarn);IL3053;IL2104</NoWarn>
  </PropertyGroup>

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>

    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>

    <!-- AOT off by default for fast dev iteration; -p:PublishAot=true on publish. -->
    <PublishAot Condition="'$(PublishAot)' == ''">false</PublishAot>
    <!-- partial trim + TrimmerRootAssembly: trim our code, keep Avalonia whole. -->
    <TrimMode>partial</TrimMode>
    <!-- Strips globalization data; CultureInfo.CurrentUICulture.Name == "" — handle via Win32. -->
    <InvariantGlobalization>true</InvariantGlobalization>

    <!-- Avalonia 12 requirement: must be false for Native AOT. -->
    <BuiltInComInteropSupport>false</BuiltInComInteropSupport>
    <!-- Compiled bindings required everywhere; per-View opt-out per element if needed. -->
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>

    <!-- LibraryImport source generator emits unsafe blocks. -->
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>Assets\Mainicon.ico</ApplicationIcon>
  </PropertyGroup>

  <!-- Static CRT for AOT publish: produced exe runs without VC++ Redistributable.
       UCRT (ucrtbase.dll) stays dynamic — it's a Windows 10/11 OS component, and
       .NET AOT targets pin /DEFAULTLIB:ucrt.lib at the end of link.rsp anyway. -->
  <ItemGroup Condition="'$(PublishAot)' == 'true'">
    <LinkerArg Include="/NODEFAULTLIB:msvcrt.lib" />
    <LinkerArg Include="/NODEFAULTLIB:msvcrtd.lib" />
    <LinkerArg Include="/NODEFAULTLIB:vcruntime.lib" />
    <LinkerArg Include="/NODEFAULTLIB:vcruntimed.lib" />
    <LinkerArg Include="/DEFAULTLIB:libvcruntime.lib" />
    <LinkerArg Include="/DEFAULTLIB:libcmt.lib" />
  </ItemGroup>

  <!-- Avalonia loads platform/render backends via reflection — without these the
       trimmer drops them and the app NREs in the compositor thread at startup. -->
  <ItemGroup>
    <TrimmerRootAssembly Include="Avalonia" />
    <TrimmerRootAssembly Include="Avalonia.Base" />
    <TrimmerRootAssembly Include="Avalonia.Markup" />
    <TrimmerRootAssembly Include="Avalonia.Markup.Xaml" />
    <TrimmerRootAssembly Include="Avalonia.MicroCom" />
    <TrimmerRootAssembly Include="Avalonia.Controls" />
    <TrimmerRootAssembly Include="Avalonia.Controls.DataGrid" />
    <TrimmerRootAssembly Include="Avalonia.Themes.Fluent" />
    <TrimmerRootAssembly Include="Avalonia.Win32" />
    <TrimmerRootAssembly Include="Avalonia.Win32.Automation" />
    <TrimmerRootAssembly Include="Avalonia.Skia" />
    <TrimmerRootAssembly Include="Avalonia.Fonts.Inter" />
    <TrimmerRootAssembly Include="MicroCom.Runtime" />
  </ItemGroup>

  <!-- Don't reference Avalonia.Desktop on win-x64 — drags X11/FreeDesktop/DBus.
       Reference each backend explicitly so dead Linux paths never enter the graph. -->
  <ItemGroup>
    <PackageReference Include="Avalonia"                   Version="12.0.3" />
    <PackageReference Include="Avalonia.Controls.DataGrid" Version="12.0.0" /> <!-- lags core -->
    <PackageReference Include="Avalonia.Themes.Fluent"     Version="12.0.3" />
    <PackageReference Include="Avalonia.Fonts.Inter"       Version="12.0.3" />
    <PackageReference Include="Avalonia.Win32"             Version="12.0.3" />
    <PackageReference Include="Avalonia.Skia"              Version="12.0.3" />
    <PackageReference Include="Avalonia.HarfBuzz"          Version="12.0.3" /> <!-- skip → 0xC0000409 -->
    <PackageReference Include="MicroCom.Runtime"           Version="0.11.6" />
    <PackageReference Include="CommunityToolkit.Mvvm"      Version="8.4.2"  />

    <!-- Debug-only diagnostics; keep out of AOT publish. -->
    <PackageReference Include="Avalonia.Diagnostics" Version="11.3.16">
      <IncludeAssets Condition="'$(Configuration)' != 'Debug'">None</IncludeAssets>
      <PrivateAssets Condition="'$(Configuration)' != 'Debug'">All</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

**Notable absences** — knobs we deliberately did NOT set:

- No `JsonSerializerIsReflectionEnabledByDefault` (AOT defaults it off,
  which is what we want).
- No `EnableConfigurationBindingGenerator` (no
  `Microsoft.Extensions.Configuration` in any of these repos).
- No `EventSourceSupport=false` (not needed for these payload sizes).
- No `TrimmerSingleWarn=false` — keeps each callsite's IL warning
  separately visible.
- No `IsAotCompatible=true` on the executable project (it's `PublishAot`
  already). Library projects DO set `IsAotCompatible=true` so trim
  warnings surface at build time.

---

## §2  The canonical `Program.cs`

```csharp
using Avalonia;
using Avalonia.Win32;

namespace MyApp;

internal static class Program
{
    // STAThread is required by Avalonia's Win32 backend on classic desktop.
    [System.STAThread]
    public static int Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (System.Exception ex)
        {
            // AOT stack traces are limited — without this catch you'll have no
            // signal at all when the renderer dies in the compositor thread.
            try
            {
                var logDir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "MyApp");
                System.IO.Directory.CreateDirectory(logDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(logDir, "crash.log"),
                    $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n");
            }
            catch { /* best effort */ }
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            // Don't UsePlatformDetect() — reference Win32/Skia/HarfBuzz directly
            // so dead Linux paths never enter the assembly graph.
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()      // omit → InvalidOperationException: No text
                                // shaping system; process fast-fails 0xC0000409
                                // BEFORE any log writes
            .With(new Win32PlatformOptions
            {
                // AOT: MicroCom CCW vtable init fails → QueryInterface returns
                // null vtable → NullReferenceException at compositor startup.
                // RedirectionSurface forces the legacy GDI compositor path.
                // Remove when Avalonia ships a MicroCom fix.
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
            })
            .WithInterFont()
            .LogToTrace();
}
```

---

## §3  Build configuration deep dive

### §3.1  `TrimmerRootAssembly` canonical list

Avalonia loads platform/render backends via reflection. Without these
roots, ILC drops the relevant types and you NRE in the compositor
thread at startup. The list converged across all 6 repos:

```
Avalonia
Avalonia.Base
Avalonia.Markup
Avalonia.Markup.Xaml
Avalonia.MicroCom
Avalonia.Controls
Avalonia.Controls.DataGrid
Avalonia.Themes.Fluent
Avalonia.Win32
Avalonia.Win32.Automation        ← UI Automation; keep even if unused (avoids accessibility-stack NRE)
Avalonia.Skia                    ← Rendering backend (loaded by UsePlatformDetect via reflection)
Avalonia.Fonts.Inter
MicroCom.Runtime                 ← COM interop runtime
```

Some repos add `Avalonia.Desktop` and `MyApp` (the app itself) to the
roots — RE-UNION does so and pays with a 49 MB exe. Discrete and
CrimsonAtomtic don't add `Avalonia.Desktop` (they don't reference it
at all) and end up at ~27-30 MB.

### §3.2  Warning suppression strategy

Three layers:

| Code | Severity | What to do |
|---|---|---|
| **`IL2026`** | Live trim warning ("uses Members which has `RequiresUnreferencedCodeAttribute`") | **Keep visible.** Fix at call site by switching to a source-gen API. |
| **`IL3050`** | Live AOT warning ("uses Members which has `RequiresDynamicCodeAttribute`") | **Keep visible.** Same — there's a source-gen alternative. |
| **`IL3053`** | Rollup ("assembly produced AOT analysis warnings") | Suppress via `<NoWarn>` once `TrimmerRootAssembly` covers the offending library. |
| **`IL2104`** | Rollup ("assembly produced trim warnings") | Same — suppress for `Avalonia.Controls.DataGrid`. |
| **`IL3052`** | Reserved | Not seen in any of the 6 repos. |

`AOBMaker` uses `[UnconditionalSuppressMessage(... "IL2026" ...)]` to
silence per-callsite warnings when DataGrid rows need
`x:CompileBindings="False"` (a corner case — see [§4.6](#46-datagrid-x-compilebindings-false-island)).

### §3.3  `TrimmerSingleWarn` — leave default

`TrimmerSingleWarn` (default `true`) collapses per-callsite trim
warnings into one per assembly. **All 6 repos leave the default.** The
per-callsite warnings IL2026/IL3050 are visible because the rollup
codes are suppressed — exactly the inverse of `TrimmerSingleWarn`'s
purpose. Don't touch it.

### §3.4  Static CRT (no VC++ Redist)

Add these `<LinkerArg>` entries — but **only when `PublishAot=true`**.
Force-static-linking the CRT in Debug/Release single-file publishes
breaks the loader.

```xml
<ItemGroup Condition="'$(PublishAot)' == 'true'">
  <LinkerArg Include="/NODEFAULTLIB:msvcrt.lib"     />
  <LinkerArg Include="/NODEFAULTLIB:msvcrtd.lib"    />
  <LinkerArg Include="/NODEFAULTLIB:vcruntime.lib"  />
  <LinkerArg Include="/NODEFAULTLIB:vcruntimed.lib" />
  <LinkerArg Include="/DEFAULTLIB:libvcruntime.lib" />
  <LinkerArg Include="/DEFAULTLIB:libcmt.lib"       />
</ItemGroup>
```

**UCRT stays dynamic** — `ucrtbase.dll` is a Windows 10/11 OS
component, and .NET AOT targets pin `/DEFAULTLIB:ucrt.lib` at the end
of `link.rsp` anyway. Any `/NODEFAULTLIB:ucrt.lib` is overridden.

If your Rust core (or any other staticlib) uses `std::env::home_dir`,
ILC's default Windows linker input list does NOT include
`userenv.lib`. Add it explicitly:

```xml
<ItemGroup Condition="'$(PublishAot)' == 'true'">
  <NativeLibrary Include="Userenv.lib" />
</ItemGroup>
```

Otherwise you'll see `unresolved external symbol __imp_GetUserProfileDirectoryW`
during the ILC link.

### §3.5  Publish command shape

```powershell
dotnet publish src/MyApp/MyApp.csproj `
    -c Release `
    -r win-x64 `
    -p:PublishAot=true `
    -o dist/win-x64 `
    --nologo
```

Don't add `-p:PublishTrimmed=true` — `PublishAot=true` implies
trimming, and adding the explicit switch flips some AOT analyzers
into error mode on Avalonia 12.x DataGrid trim warnings.

If `dotnet publish` chokes on Visual Studio 18 Insiders with
`vswhere.exe` output pollution, the workaround is to invoke MSBuild
with `IlcUseEnvironmentalTools=true` after manually importing
`vcvarsall.bat` — wrap in a `publish.ps1`.

### §3.6  Post-publish hygiene

Three cleanup tasks worth automating in your publish script
(`CrimsonAtomtic`'s `package_aot.ps1` is the reference):

1. **Strip managed PDBs**: managed assemblies aren't shipped after AOT;
   the PDBs are ~200 MB of dead weight. Add an `<MSBuild Target>`:
   ```xml
   <Target Name="StripPdbsAfterPublish" AfterTargets="Publish">
     <ItemGroup>
       <_PublishPdbs Include="$(PublishDir)**\*.pdb" />
     </ItemGroup>
     <Delete Files="@(_PublishPdbs)" />
   </Target>
   ```
   ILC's native PDB lands AFTER the `Publish` target chain, so a
   second PowerShell sweep post-process is required.
2. **Don't pre-clean `dist/`** — `dotnet publish` handles overwrites,
   and pre-cleaning races with file handles held by a prior
   `dotnet test` (mmap on the freshly-published exe). ILC's first
   invocation flakes with exit -1.
3. **Verify `DirectPInvoke` actually folded the static lib**:
   ```powershell
   dumpbin /imports MyApp.exe | Select-String "yourlib.dll"
   ```
   If your Rust cdylib name appears as a dynamic import, the
   `<DirectPInvoke>` name didn't match `[LibraryImport]` and it
   silently fell back to dynamic loading.

---

## §4  Avalonia patterns deep dive

### §4.1  Compiled bindings: `x:DataType` everywhere

`AvaloniaUseCompiledBindingsByDefault=true` requires `x:DataType` on:

- Every `Window` / `UserControl` / `Page` root
- Every `DataTemplate` (even nested ones inside `ItemsControl.ItemTemplate`)
- Every `ControlTheme` / `Style` with `TargetType` that does data binding

```xml
<UserControl x:Class="MyApp.Views.FooPanel"
             xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:MyApp.ViewModels"
             xmlns:m="using:MyApp.Models"
             x:DataType="vm:FooViewModel">

  <DataGrid ItemsSource="{Binding Items}">
    <DataGrid.Columns>
      <DataGridTemplateColumn>
        <DataGridTemplateColumn.CellTemplate>
          <DataTemplate x:DataType="m:ItemRow">     <!-- required per-template -->
            <TextBlock Text="{Binding Name}" />
          </DataTemplate>
        </DataGridTemplateColumn.CellTemplate>
      </DataGridTemplateColumn>
    </DataGrid.Columns>
  </DataGrid>
</UserControl>
```

### §4.2  ViewLocator: switch expression, not reflection

```csharp
/// <summary>Explicit ViewModel → View mapping. No reflection — AOT safe.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        MainWindowViewModel  => new MainWindow(),
        TypeListViewModel    => new TypeListPanel(),
        LiveWalkerViewModel  => new LiveWalkerPanel(),
        _                    => new TextBlock { Text = $"No view for {data?.GetType().Name}" }
    };

    public bool Match(object? data) => data is ViewModelBase;
}
```

Default Avalonia template uses `Activator.CreateInstance(Type.GetType(...))` —
both trim AND AOT unsafe.

### §4.3  DataContext + parent binding on one element

Avalonia's compiled-binding type checker fails when a single element
both switches `DataContext` and binds against the parent context. Split
across two nested panels:

```xml
<!-- Wrong: outer binding can't see parent's BoolProp once DataContext changes -->
<StackPanel DataContext="{Binding SelectedRow}"
            x:DataType="vm:Row"
            IsVisible="{Binding $parent[UserControl].((vm:Outer)DataContext).BoolProp}">
  ...
</StackPanel>

<!-- Right: outer keeps the VM scope; inner switches into row scope -->
<StackPanel IsVisible="{Binding BoolProp}">   <!-- resolved against OuterViewModel -->
  <StackPanel DataContext="{Binding SelectedRow}"
              x:DataType="vm:Row"
              Spacing="10">
    ...
  </StackPanel>
</StackPanel>
```

### §4.4  ResourceDictionary and runtime language switching

Four lessons stacked together (the most expensive bug investigation
across these repos — CrimsonAtomtic spent 4 sessions on it):

**Lesson 1**: don't construct `ResourceInclude` from C# — its
`(Uri)` ctor is `[RequiresUnreferencedCode]` so calling it from code
trips `IL2026`. Declare every dictionary in `App.axaml` at XAML
compile time:

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceInclude Source="avares://MyApp/I18n/Strings.en.axaml" />
      <ResourceInclude Source="avares://MyApp/I18n/Strings.zh-TW.axaml" />
      <ResourceInclude Source="avares://MyApp/I18n/Strings.ja.axaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

**Lesson 2**: the AOT XAML compiler inlines `<ResourceInclude>`
targets as concrete `Avalonia.Controls.ResourceDictionary` instances
that don't expose `Source`. You cannot identify a merged dictionary
by URI after AOT. Mark it with a sentinel key:

```xml
<!-- Strings.en.axaml -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=mscorlib">
  <sys:String x:Key="__UiLangCode__">en</sys:String>
  ...
</ResourceDictionary>
```

**Lesson 3**: reorder via `Clear()` + sequential `Add()`. NEVER
`RemoveAt + Add`. Avalonia's `AvaloniaList` ForEachItem callbacks
manage `AddOwner` / `RemoveOwner` on per-item add/remove only; an
in-place `RemoveAt + Add` of the same instance does NOT propagate a
`ResourcesChanged` event:

```csharp
// Right: Clear+Add fires the right cascade
var merged = Application.Current!.Resources.MergedDictionaries;
merged.Clear();
foreach (var dict in OrderedDictsForLanguage(target))
    merged.Add(dict);
```

**Lesson 4**: every XAML resource reference that needs to live-update
must use `DynamicResource`, not `StaticResource`. `StaticResource`
references are baked in at XAML load time and ignore later changes.

### §4.5  DataGrid sort: the three-layer trap

Three independent gates close under AOT for `DataGridTemplateColumn`
in a Window/UserControl with `x:DataType`. All three must be opened
in tandem:

```
Layer 1 — Window-level x:DataType compiles column Bindings to
          CompiledBinding (typed delegate, no readable Path string)
Layer 2 — Empty Path → DataGrid auto-sets CanUserSort=False on the
          column (grid-level CanUserSortColumns=True is overridden!).
          Header click is a silent no-op — Sorting event never fires.
Layer 3 — Default sort uses reflection on SortMemberPath to read row
          property values. AOT trim drops that reflection — comparer
          reads nothing, "sorted" data appears unchanged while the
          column header glyph (▲/▼) animates correctly.
```

| Symptom | Layer | Cause |
|---|---|---|
| Header click does nothing, no glyph | 1 + 2 | Default config — column auto-disabled |
| Glyph flips ▲↔▼ but rows don't reorder | 1 + 3 | Default reflection-sort silently fails |
| Rows actually reorder | all 3 open | Working config |

**The fix (all three layers)** — XAML:

```xml
<DataGridTemplateColumn Header="Offset"
                        SortMemberPath="Offset"   <!-- defeats layer 2 -->
                        CanUserSort="True"        <!-- belt-and-braces -->
                        Width="70">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate x:DataType="vm:FieldRow">
      <TextBlock Text="{Binding OffsetDisplay}" />
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

Code-behind (wire `CustomSortComparer` per column at `Loaded`):

```csharp
private static readonly IReadOnlyDictionary<string, IComparer> FieldsSortComparers =
    new Dictionary<string, IComparer>
    {
        ["Offset"]     = DataGridSortComparers.Number<FieldRow>(r => r.Offset),
        ["Name"]       = DataGridSortComparers.Ordinal<FieldRow>(r => r.Name),
        ["Flags"]      = DataGridSortComparers.Number<FieldRow>(r => r.Flags),
        ["IsStatic"]   = DataGridSortComparers.Bool<FieldRow>(r => r.IsStatic),
    };

private void OnLoaded(object? _, RoutedEventArgs __)
{
    FieldsGrid.WireSortComparers(FieldsSortComparers);
}
```

Plus the comparer factory:

```csharp
public static class DataGridSortComparers
{
    public static IComparer Number<T>(System.Func<T, long> getter) =>
        Comparer<object?>.Create((a, b) =>
        {
            if (a is not T ta || b is not T tb) return 0;
            return getter(ta).CompareTo(getter(tb));
        });

    public static IComparer Ordinal<T>(System.Func<T, string?> getter) =>
        Comparer<object?>.Create((a, b) =>
        {
            if (a is not T ta || b is not T tb) return 0;
            return string.Compare(getter(ta), getter(tb), System.StringComparison.Ordinal);
        });

    public static IComparer Hex<T>(System.Func<T, ulong> getter) => Number<T>(x => unchecked((long)getter(x)));
    public static IComparer Bool<T>(System.Func<T, bool> getter)  => Number<T>(x => getter(x) ? 1 : 0);
}

public static class DataGridSortExtensions
{
    public static void WireSortComparers(this DataGrid grid,
        IReadOnlyDictionary<string, IComparer> comparers)
    {
        foreach (var col in grid.Columns)
        {
            string? path = col.SortMemberPath;
            if (string.IsNullOrEmpty(path)) continue;
            if (comparers.TryGetValue(path, out var cmp))
                col.CustomSortComparer = cmp;
        }
    }
}
```

**Counter-intuition**: `DataGridTextColumn` with simple `Binding="{Binding
Foo}"` (`UE5CEDumper`, `CrimsonAtomtic`) sorts out-of-box because the
compiled `Binding` still carries an inspectable Path. **Only
`DataGridTemplateColumn`** (`ZoltDump`, `discrete`, `AOBMaker`,
`RE-UNION`) needs the three-layer fix. If you can express the column as
a `DataGridTextColumn` with `StringFormat`, prefer that.

### §4.6  DataGrid `x:CompileBindings="False"` island

When a `DataGridTemplateColumn` needs ancestor-binding
(`$parent[DataGrid].((vm:OuterVM)DataContext).XCommand`), compiled
bindings get awkward. Two options:

**Option A** (`AOBMaker` pattern): turn off compiled bindings on the
`DataTemplate` itself and lean on `[DynamicallyAccessedMembers]` to
keep the row class's properties alive through trim:

```xml
<DataGridTemplateColumn.CellTemplate>
  <DataTemplate x:CompileBindings="True" x:DataType="m:Row">
    <Button Command="{Binding $parent[DataGrid].((vm:Outer)DataContext).RowCommand}"
            CommandParameter="{Binding}" />
  </DataTemplate>
</DataGridTemplateColumn.CellTemplate>
```

```csharp
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public class Row { public string Name { get; set; } = ""; ... }
```

**Option B** (`ZoltDump` pattern): set `x:CompileBindings="False"` on
the entire DataGrid:

```xml
<DataGrid x:Name="FieldsGrid"
          x:CompileBindings="False"
          ItemsSource="{Binding Fields}">
```

Then suppress the resulting `IL2026` with a targeted assembly
attribute. RE-UNION uses this with
`[assembly: UnconditionalSuppressMessage("TrimAnalysis", "IL2026", Scope="member", Target="...")]`.

Both work. Option A is more granular and recommended unless the
ancestor-binding density is high.

### §4.7  DataGrid context menus must be code-behind

XAML-bound `ContextMenu` on a `DataGrid` row fails under AOT because
the popup is disconnected from the visual tree; `DataContext` becomes
`object?` and compiled bindings produce nothing. Listbox + commands
bound to the OUTER VM is fine (no row context); only DataGrid
explicitly fails.

```csharp
private void BuildFieldsContextMenu()
{
    var menu = new ContextMenu();
    var copy = new MenuItem { Header = "Copy CE XML" };
    copy.Click += async (_, _) =>
    {
        if (GetVm() is { } vm && _contextField is { } field)
            await vm.CopyCeFieldAsync(field);
    };
    menu.Items.Add(copy);
    FieldsGrid.ContextMenu = menu;

    // Right-click row capture — Avalonia DataGrid doesn't auto-select on right-click.
    FieldsGrid.AddHandler(Control.ContextRequestedEvent, (_, e) =>
    {
        if (e.Source is Visual v)
        {
            var row = v.FindAncestorOfType<DataGridRow>();
            if (row?.DataContext is FieldRow f) { _contextField = f; FieldsGrid.SelectedItem = f; return; }
        }
        _contextField = FieldsGrid.SelectedItem as FieldRow;
    }, RoutingStrategies.Tunnel);
}
```

### §4.8  Modal dialogs: code-behind only

`MemberFilterDialog`, `PathMatchDialog` (ZoltDump),
`ObjectInstancePickerDialog`, `FreezeValueDialog` (UE5CEDumper) — all
windows with custom layout + interaction skip MVVM entirely. Reasons
documented:

```csharp
/// AOT-safe: no reflection, no data binding — direct control access.
```

The MVVM corner cases in dialog `Window`s under AOT aren't worth
fighting. If you need a dialog, build it imperatively.

### §4.9  ControlTheme pseudo-class selector priority

If you `<Setter Property="Foreground" Value="..." />` on a control
whose `FluentTheme`-provided `ControlTheme` uses `:pointerover`/`:focus`
pseudo-class selectors, your direct setter loses to the theme's
selector at StyleTrigger priority. Solution: match selectors at the
same priority AND override the `ThemeDictionaries` resources the
ControlTheme reads:

```xml
<Style Selector="NumericUpDown"><Setter Property="Foreground" Value="#D4D4D4"/></Style>
<Style Selector="NumericUpDown /template/ TextBox#PART_TextBox">
  <Setter Property="Foreground" Value="#D4D4D4"/>
</Style>
<Style Selector="NumericUpDown /template/ TextBox#PART_TextBox:pointerover">
  <Setter Property="Foreground" Value="#E0E0E0"/>
</Style>

<!-- Plus override the resources the ControlTheme reads via DynamicResource -->
<ResourceDictionary>
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Dark">
      <SolidColorBrush x:Key="TextControlForeground" Color="#D4D4D4"/>
      ...
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
```

### §4.10  Other Avalonia 12 quirks

- **`<Run Text="{Binding}"/>` inside `<TextBlock>` → StackOverflow in
  AOT.** Use `<TextBlock Text="{Binding}"/>` directly.
- **`TextBlock.Text` and `TextBlock.Inlines` are mutually exclusive.**
  Set one, null the other.
- **`DataGridCheckBoxColumn` requires double-click** to toggle.
  Single-click via `DataGridTemplateColumn` + `<CheckBox Focusable="False">`.
- **Avalonia 12 clipboard API churn**: `IDataObject` / `DataFormats` /
  `SetDataObjectAsync` gone. Use
  `IClipboard.SetDataAsync(IAsyncDataTransfer)` with `DataTransferItem`
  and `DataFormat.Text`.
- **`avares://` keys off `<AssemblyName>`, not `<RootNamespace>`.** If
  you `<AssemblyName>Foo</AssemblyName>` while project is `Bar`,
  all `avares://` URIs are `avares://Foo/...`. Same for
  `InternalsVisibleTo` — it matches `AssemblyName`. Renaming silently
  no-ops with CS0122 at call sites otherwise.
- **`Avalonia.Diagnostics` is 11.x only as of 2026-05.** Add behind
  `<IncludeAssets Condition="'$(Configuration)' != 'Debug'">None</IncludeAssets>`
  to keep out of AOT publish.
- **A compiled `x:Name`/`Name` field is NULL under AOT for a control nested
  in a non-selected `TabItem` (or other lazily-realized content) — a direct
  deref in the ctor NRE-crashes startup, JIT-only.** The trimmer drops the
  generated field assignment for content the ContentPresenter hasn't realized
  yet, so `MyControl.AddHandler(...)` / `MyControl.Foo` in a View/Window ctor
  throws `NullReferenceException` at `..ctor()` **before the window shows** —
  and it passes Debug/Release (JIT) + all unit tests, only dying in the trimmed
  publish. Never touch a named-control field at construction time. Use the
  codebase idiom `this.FindControl<T>("name")?.…` deferred to a `Loaded`
  handler, or — best — wire the control's OWN `Loaded="OnX"` in XAML and take
  the instance from `sender` (no name resolution at all). Paid for by
  discrete's Teleport-tab `TeleportContent.AddHandler(...)` (build #1316, fixed
  #1320): shipped a hard 0xC0000409 crash on `build.cmd publish` that no JIT
  build reproduced. **Corollary: add the `Program.Main` crash-log guard
  ([§8.3](#83-stack-trace-fidelity)) — without it a WinExe startup crash is a
  silent process-vanish with no console and no log.**

---

## §5  Source generators (JSON, MVVM, Regex)

### §5.1  System.Text.Json

Every `JsonSerializer.Serialize/Deserialize` call MUST pass
`JsonSerializerContext.Default.MyType` — never the `Type`-argument
overload that triggers reflection.

```csharp
[JsonSerializable(typeof(MyDto))]
[JsonSerializable(typeof(List<MyDto>))]          // generic shapes need explicit entries
[JsonSerializable(typeof(Dictionary<string, MyDto>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class MyJsonContext : JsonSerializerContext;
```

Usage:

```csharp
var dto = JsonSerializer.Deserialize(stream, MyJsonContext.Default.MyDto);
var json = JsonSerializer.Serialize(dto, MyJsonContext.Default.MyDto);
```

**One context per feature area** (`ZoltDump` and `UE5CEDumper`'s
pattern). Beats one mega-context — keeps the generated code surface
per-context smaller and the dependency graph cleaner.

**When you need a custom encoder** (CE Lua can't parse `\uXXXX`
escapes; CrimsonAtomtic and UE5CEDumper hit this):

```csharp
private static MyJsonContext? _relaxed;
public static MyJsonContext Relaxed => _relaxed ??= new MyJsonContext(new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});
```

`JsonValue.Create<string>(string)` still trips `IL2026` + `IL3050` even
under a context. Use `JsonObject` initializers (`new JsonObject { ["k"] =
"v" }`) or `Utf8JsonWriter` directly for hot paths.

When the schema is dynamic, use the `JsonNode` / `JsonObject` DOM —
`JsonNode.Parse` is AOT-safe (no reflection).

### §5.2  CommunityToolkit.Mvvm

```csharp
public partial class FooViewModel : ObservableObject   // must be partial
{
    [ObservableProperty]
    private string _name = "";                          // field form

    [ObservableProperty]
    private int _count;

    partial void OnNameChanged(string value)            // source-gen hook
    {
        // react to changes
    }

    [RelayCommand]
    private async Task DoStuffAsync() { ... }

    [RelayCommand(CanExecute = nameof(CanDoMore))]
    private Task DoMoreAsync() => ...;
    private bool CanDoMore() => !string.IsNullOrEmpty(_name);
}
```

**Known gotchas**:

- **Partial-property syntax broken** in CTMvvm 8.4.0 on .NET 10. Use
  field form `[ObservableProperty] private T _name;`. Revisit when the
  toolkit catches up.
- **Field name `Backend` collides with source-gen field `_backend2`**.
  If you hit `CS0102` (member already defined), rename your property
  (e.g. `Backend2`).
- **`[RelayCommand(CanExecute=...)]` doesn't auto re-evaluate** on
  `ObservableCollection<T>` row changes. Subscribe to each row's
  `PropertyChanged` and call the generated `NotifyCanExecuteChanged`
  manually.

### §5.3  Regex

```csharp
internal partial class FooParser
{
    [GeneratedRegex(@"^\s*(\w+)\s*=\s*(.*)$", RegexOptions.Compiled)]
    private static partial Regex KeyValueLine();

    public void Parse(string line)
    {
        var m = KeyValueLine().Match(line);
        ...
    }
}
```

Runtime `Regex.Compile` / `new Regex(...)` works under AOT but allocates
an emit-time IL stream — `[GeneratedRegex]` source-generates the
matcher and removes that overhead.

---

## §6  Native interop

### §6.1  `[LibraryImport]`, not `[DllImport]`

```csharp
internal static partial class NativeMethods
{
    private const string Lib = "kernel32.dll";

    // LibraryImport doesn't auto-suffix A/W like DllImport does — name the export literally.
    [LibraryImport(Lib, EntryPoint = "GetModuleHandleW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string name);

    [LibraryImport(Lib, EntryPoint = "GetProcAddress", SetLastError = true,
                   StringMarshalling = StringMarshalling.Custom,
                   StringMarshallingCustomType = typeof(AnsiStringMarshaller))]
    public static partial IntPtr GetProcAddress(IntPtr hMod, string name);
}
```

For an exported function the AOT marshaller can't trivially handle
(e.g. parameterless `ushort`-returning kernel function), classic
`[DllImport]` with `[DefaultDllImportSearchPaths(System32)]` is
acceptable — see `CrimsonAtomtic`'s `UiLanguageService` for the
`GetUserDefaultUILanguage` example.

### §6.2  Rust cdylib integration (CrimsonAtomtic pattern)

When linking a Rust staticlib into the AOT exe (no DLL on disk):

```xml
<ItemGroup Condition="'$(PublishAot)' == 'true'">
  <!-- DirectPInvoke name MUST match every [LibraryImport("crimson_rs")] -->
  <DirectPInvoke Include="crimson_rs" />
  <NativeLibrary Include="..\vendor\crimson-rs\target\release\crimson_rs.lib"
                 Condition="Exists('..\vendor\crimson-rs\target\release\crimson_rs.lib')" />
  <!-- Rust std::env::home_dir pulls GetUserProfileDirectoryW (userenv.dll) -->
  <NativeLibrary Include="Userenv.lib" />
</ItemGroup>
```

**Critical:** the `<DirectPInvoke Include="X" />` name MUST match
`[LibraryImport("X")]`. Mismatch → silent fallback to dynamic loading
(your `.lib` is ignored, you'll see the `.dll` getting copied as a
runtime dependency). Verify with:

```powershell
dumpbin /imports MyApp.exe | Select-String "crimson_rs.dll"   # should be empty
```

For Rust →C# inverse calls, use `[UnmanagedCallersOnly]`:

```csharp
[UnmanagedCallersOnly(EntryPoint = "Reflect")]
public static unsafe int Reflect(byte* requestBuf, int requestLen) { ... }
```

### §6.3  Blittable interop structs

Use `[InlineArray(N)]` instead of `unsafe fixed byte[N]` for
fixed-size buffers — keeps struct blittable without requiring
`<AllowUnsafeBlocks>` in the consuming project:

```csharp
public readonly struct PazFileEntry
{
    private readonly PazFileNameBuffer _nameBuffer;
    public uint Offset { get; }
    public uint Size { get; }
}

[InlineArray(256)]
internal struct PazFileNameBuffer
{
    private byte _e0;
}
```

For Rust `#[repr(C)]` structs, pair with `[StructLayout(LayoutKind.Sequential)]`
on the C# side and pin sizes with unit tests:

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PathStep(uint FieldIndex, uint ElementIndex);

[Fact]
public void PathStep_Size_MatchesRustAbi() =>
    Assert.Equal(8, Marshal.SizeOf<PathStep>());
```

### §6.4  `SafeHandle` + CA1419

CA1419 demands a parameterless ctor at type-level visibility for
SafeHandles, even when the marshaller never invokes it. Keep the ctor
to satisfy the analyzer:

```csharp
internal sealed class CrimsonSaveHandle : SafeHandle
{
    public CrimsonSaveHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public static CrimsonSaveHandle FromOwnedPointer(IntPtr ptr)
    {
        var h = new CrimsonSaveHandle();
        h.SetHandle(ptr);
        return h;
    }

    public override bool IsInvalid => handle == IntPtr.Zero;
    protected override bool ReleaseHandle() { NativeMethods.Free(handle); return true; }
}
```

### §6.5  IPC: prefer pipe + JSON over direct P/Invoke

If your UI talks to a native DLL, **strongly prefer an IPC boundary
(named pipe + line-delimited JSON)** over direct P/Invoke into the
DLL. Benefits under AOT:

- UI side has zero `[LibraryImport]` / `[DllImport]` — AOT story is
  trivially clean.
- DLL side can be C++ (or anything) and not subject to .NET trimming.
- Cross-language debugging via wire-protocol logs.
- Multiple consumers (UI + diagnostic tools) can attach simultaneously.

This is the pattern used by `UE5CEDumper`, `RE-UNION`, `ZoltDump`, and
this project (`ShinkuHitomi`). Only `CrimsonAtomtic` does direct
P/Invoke — and it pays for it with the cdylib/staticlib dual-build
complexity above.

---

## §7  Globalization, fonts, resources

### §7.1  `InvariantGlobalization=true` consequences

- `CultureInfo.CurrentUICulture.Name` returns `""` regardless of OS UI
  language. **Anything locale-sensitive must P/Invoke `kernel32!GetUserDefaultUILanguage`**:

```csharp
[DllImport("kernel32.dll", EntryPoint = "GetUserDefaultUILanguage", ExactSpelling = true)]
[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
internal static extern ushort GetUserDefaultUILanguage();

public static string DetectLanguage()
{
    ushort lcid = GetUserDefaultUILanguage();
    return lcid switch {
        0x0404 => "zh-TW",
        0x0411 => "ja",
        0x0804 => "zh-CN",
        _      => "en",
    };
}
```

- All string comparisons fall back to ordinal. Use
  `StringComparison.Ordinal` explicitly everywhere.
- Date/number formatting defaults to invariant culture. Pass
  `CultureInfo.InvariantCulture` explicitly where you mean it.

### §7.2  Embedded resources

Use `[GetManifestResourceStream]` with a stable, hardcoded
`LogicalName` — no reflection-based resource name search:

```xml
<EmbeddedResource Include="..\..\scripts\helper.lua">
    <Link>Resources\CE\helper.lua</Link>
    <LogicalName>MyApp.Resources.CE.helper.lua</LogicalName>
</EmbeddedResource>
```

```csharp
public static string ReadHelperLua()
{
    var asm = typeof(HelperLuaResource).Assembly;
    using var stream = asm.GetManifestResourceStream("MyApp.Resources.CE.helper.lua")
        ?? throw new InvalidOperationException("missing embedded resource");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
```

### §7.3  `avares://` URI quirks under AOT

`avares://` URI parser's `AbsolutePath` can be empty under AOT
trimming. Always compare with a multi-surface helper:

```csharp
static bool MatchesUri(Uri uri, string expected) =>
    string.Equals(uri.OriginalString, expected, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(uri.ToString(),      expected, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(uri.AbsolutePath,    expected, StringComparison.OrdinalIgnoreCase);
```

---

## §8  Versioning, diagnostics, AV

### §8.1  `Assembly.GetEntryAssembly()` not `GetExecutingAssembly()`

Single-file AOT trims `Assembly.GetExecutingAssembly()` — it returns
no version. Use `GetEntryAssembly()`:

```csharp
var version = Assembly.GetEntryAssembly()?.GetName().Version;
```

### §8.2  `Version.Build` vs `Version.Revision` confusion

`AssemblyVersion="M.m.p.b"` lays out as `Version.Major`,
`Version.Minor`, **`Version.Build` (= "patch")**, **`Version.Revision`
(= "build")**. The .NET schema is `M.m.B.R` — if you want the
4th-position number you wrote, read `.Revision`, not `.Build`.

### §8.3  Stack trace fidelity

AOT stack traces are limited (function names obfuscated, inlining
opaque, no PDB lookup at runtime). The top-level try/catch in
`Program.Main` writing to `%LOCALAPPDATA%\<app>\crash.log` is your
only diagnostic surface.

If you need more detail, consider `<StackTraceSupport>true</StackTraceSupport>` —
but expect 1-2 MB binary size growth.

### §8.4  Antivirus false positives

Freshly-built AOT exes get flagged by some AV vendors due to:

- Many `LibraryImport` native calls
- Hand-rolled COM/IPC patterns that look like RAT signatures
- No code signing on dev builds
- Static-linked CRT looks unfamiliar to ML-based scanners

Dev workflow: **exclude your `dist\` folder from real-time scanning**.
Ship binaries should be code-signed.

---

## §9  Source-repo cross-reference

Where each lesson was originally paid for:

| Lesson | UE5CEDumper | RE-UNION | CrimsonAtomtic | discrete | ZoltDump | AOBMaker |
|---|---|---|---|---|---|---|
| §0/1 csproj defaults | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `Win32CompositionMode.RedirectionSurface` | ✓ | ✓ |  | ✓ | ✓ | ✓ (orig diagnosis) |
| `BuiltInComInteropSupport=false` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| ViewLocator switch expression | ✓ (orig) | ✓ | ✓ | ✓ | ✓ | ✓ |
| `TrimmerRootAssembly` 13-list | ✓ | ✓ |  | ✓ | ✓ | partial |
| Static CRT (no VC++ Redist) |  |  |  | ✓ | ✓ |  |
| §3.6 post-publish hygiene |  |  | ✓ (orig) | ✓ |  |  |
| Don't reference `Avalonia.Desktop` (win-x64) |  |  |  | ✓ (orig diagnosis) |  |  |
| Explicit `.UseHarfBuzz()` |  |  |  | ✓ (orig — 0xC0000409 diagnosis) |  |  |
| §4.4 ResourceDictionary swap |  |  | ✓ (4-session bug, orig) |  |  | ✓ (Lang.cs is the working reference) |
| §4.5 DataGrid 3-layer sort |  |  |  | ✓ (orig, lessons-learned:722-826) | ✓ (CustomSortComparer pattern) | ✓ (RelocationSortComparer) |
| §4.6 `x:CompileBindings=False` island |  |  |  |  | ✓ (DataGrid ancestor binding) | ✓ (orig) |
| §4.7 DataGrid context menu code-behind |  |  |  |  | ✓ (orig diagnosis) |  |
| §4.8 Modal dialogs code-behind | ✓ | ✓ |  |  | ✓ |  |
| §4.9 ControlTheme pseudo-class | ✓ |  |  |  | ✓ |  |
| §4.10 `<Run>` stack overflow |  |  |  |  |  | ✓ (orig) |
| §5.1 JSON source-gen | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` for CE Lua | ✓ | ✓ |  |  | ✓ |  |
| §5.2 CTMvvm `partial` broken on .NET 10 |  |  | ✓ (orig) |  |  |  |
| §5.2 `Backend` field collision |  |  |  | ✓ |  |  |
| §6.2 Rust cdylib `<DirectPInvoke>` |  |  | ✓ (orig) |  |  |  |
| §6.3 `[InlineArray]` blittable |  |  | ✓ (orig) |  |  |  |
| §6.4 `SafeHandle` + CA1419 |  |  | ✓ (orig) |  |  |  |
| §7.1 `InvariantGlobalization` + LCID |  |  | ✓ (orig) |  |  |  |
| §7.3 `avares://` AOT URI parsing |  |  | ✓ (orig — 4-session bug) |  |  |  |
| §8.1 `GetEntryAssembly` | ✓ (orig — build 657 lesson) |  |  |  |  |  |
| §8.2 `Version.Build` vs `Revision` | ✓ (orig) |  |  |  |  |  |
| `[GeneratedRegex]` |  |  |  |  |  | ✓ |
| Avalonia 12 clipboard `IDataObject` gone |  |  |  | ✓ (orig) |  |  |
| `<AssemblyName>` vs `<RootNamespace>` |  |  |  | ✓ (orig) |  |  |
| `InternalsVisibleTo` matches `AssemblyName` |  |  |  | ✓ (orig) |  |  |
| `LibraryImport` no A/W suffix |  |  |  | ✓ (orig) |  |  |
| `PublishSingleFile` deflate-compress vs AOT |  |  |  | ✓ (orig — integrity stamp) |  |  |
| Antivirus heads-up |  |  |  | ✓ |  |  |

---

## §10  Export-to-CE feature spec

The user-facing feature requirement: **`Copy CE XML`** and **`Copy CE
Field`** clipboard operations, plus optional **`.CT` file save** and
**`.CSX` structure dissect** export. Source patterns:

| Implementation | Lines | File |
|---|---|---|
| **`UE5CEDumper`** — most complete | ~2,000 | `ui/UE5DumpUI/Services/CeXmlExportService.cs` |
| **`RE-UNION`** — working port | ~1,700 | `ui/REUnionUI/Services/CeExportService.cs` |
| **`ZoltDump`** — + Structure Dissect | mid | `ui/ZoltDumpUI/Services/CeXmlExportService.cs` + `CsxExportService.cs` |
| **`discrete`** — Structure + Live modes | mid | `ui/UnityDumpUI.Core/Services/Export/CeXmlExporter.cs` |
| `AOBMaker` | — | **No offline XML emission** — pipe-to-CE only. Has reusable data model in `AddressCaptureRecordInfo.cs` (27 lines) |

### §10.1  CE-XML format rules (consensus across UE5/RE-UNION/ZoltDump/discrete)

**Address model**:

| Field kind | `<Address>` | `<Offsets>` | Notes |
|---|---|---|---|
| Inline scalar | `+{offset}` | absent | CE reads `parentResolved + offset` |
| Pointer field (deref) | `+{offset}` | `<Offset>0</Offset>` | CE reads `*(parentResolved + offset)`, child entries offset from THAT |
| Group header (root) | absolute `"module.exe"+RVA` | absent | first node in the tree |
| Group header (child) | `+{offset}` | absent if inline, `<Offset>0</Offset>` if pointer | |
| TArray (UE) — group | `+{fieldOffset}` | `<Offset>0</Offset>` | dereferences `TArray.Data` |
| TArray elements | `+{N*elemSize}` | absent | offset from already-dereferenced Data pointer |
| Vec3 / Color (no native CE type) | parent group at field offset | `<Offset>0</Offset>` if pointer | children: `x` at `+0`, `y` at `+4`, `z` at `+8`, etc. |

**CRITICAL: never `Address=+0, Offsets=[fieldOffset]`** — that's a
double-dereference bug. The correct form is `Address=+{fieldOffset},
Offsets=[0]`.

**TVariableType strings** (CE XML uses string forms, not the integer
enum):

| Type | CE string | Notes |
|---|---|---|
| `byte` / `uint8` / `bool` | `"Byte"` | |
| `int16` / `uint16` / `short` | `"2 Bytes"` | |
| `int32` / `uint32` / `int` | `"4 Bytes"` | |
| `int64` / `uint64` / `long` | `"8 Bytes"` | |
| `float` | `"Float"` | |
| `double` | `"Double"` | |
| `string` | `"String"` | with `<Length>` and `<Unicode>` |
| pointer | `"8 Bytes"` + `ShowAsHex=1` | |
| AA script | `"Auto Assembler Script"` | with `<AssemblerScript>` body |
| `enum` | parent type's `"4 Bytes"` + `<DropDownList DisplayValueAsItem="1">val:name\nval:name</DropDownList>` | first occurrence; subsequent use `<DropDownListLink>...</DropDownListLink>` |

### §10.2  Format conventions

- **UTF-8 no BOM, CRLF line endings**.
- `<Description>` content **wrapped in literal double quotes** (CE
  convention, not XML quoting): `<Description>"Player HP"</Description>`.
- `<Address>`: **uppercase hex, no `0x` prefix**. Module form:
  `"module.exe"+1C912F` (the `+` is unquoted, the module name is
  literal-quoted inside the XML value).
- `<Color>`: **`RRGGBB` hex, no `0x`, no `#`** (6 chars), e.g.
  `FF8080`.
- `<Offsets>` order: CE serializes last-to-first when walking pointer
  chains. **Verify against a CE-saved exemplar** before shipping —
  there's an off-by-one risk here.
- `<ID>` is per-entry monotonic uint32; use a top-level counter.

### §10.3  Output sample (canonical shape)

```xml
<?xml version="1.0" encoding="utf-8"?>
<CheatTable>
  <CheatEntries>
    <CheatEntry>
      <ID>100</ID>
      <Description>"pa::CharacterStatusSaveData @ 0x145fb9800"</Description>
      <ShowAsHex>1</ShowAsHex>
      <GroupHeader>1</GroupHeader>
      <Address>"CrimsonDesert.exe"+5FB9800</Address>
      <CheatEntries>
        <CheatEntry>
          <ID>101</ID>
          <Description>"_level"</Description>
          <ShowAsSigned>0</ShowAsSigned>
          <VariableType>4 Bytes</VariableType>
          <Address>+10</Address>
        </CheatEntry>
        <CheatEntry>
          <ID>102</ID>
          <Description>"_currentHp"</Description>
          <ShowAsSigned>1</ShowAsSigned>
          <VariableType>8 Bytes</VariableType>
          <Address>+30</Address>
        </CheatEntry>
        <CheatEntry>
          <ID>103</ID>
          <Description>"_factionKey (FactionKey)"</Description>
          <ShowAsHex>1</ShowAsHex>
          <VariableType>4 Bytes</VariableType>
          <Address>+8</Address>
        </CheatEntry>
      </CheatEntries>
    </CheatEntry>
  </CheatEntries>
  <UserdefinedSymbols/>
</CheatTable>
```

### §10.4  Implementation choice for ShinkuHitomi

Use **`StringBuilder.AppendLine`** like UE5CEDumper / RE-UNION /
discrete, not `XmlWriter` / `XmlSerializer`. Reasons:

- Output formatting is exactly controllable (CE is picky about
  whitespace in some edge cases).
- No reflection / no analyzer warnings.
- Hand-templating is ~50% the line count of `XDocument` for this kind
  of tree.

**File**: `src/ui/Services/CeXmlExporter.cs` (mirror discrete's name —
shorter than the others).

**Entry points** (commands on the property browser VM):

```csharp
[RelayCommand] private Task CopyCeXmlAsync() { ... }              // entire class
[RelayCommand] private Task CopyCeFieldAsync(LiveField? f) { ... }// single field
[RelayCommand] private Task SaveCtFileAsync() { ... }             // wraps in <CheatTable> + dialog
```

**Recursive walk** for `ReflectObject`-typed fields (drill down into
nested SaveData types using the same metabind dictionary) — bounded
by `<DepthLimit>` and `<ArrayLimit>` options exposed in the UI.

**Lift `AddressCaptureRecordInfo` from AOBMaker** (27 lines) as the
intermediate model — it already maps cleanly to CE's TVariableType
enum and `<Offsets>` list.

---

## §11  When AOT bites: triage flowchart

```
Symptom: app exits immediately, no window, no log
  ↓
Did 0xC0000409 fast-fail show in Event Viewer?
  Yes → likely missing .UseHarfBuzz() — see §0.13, §4.10
  No  → continue
  ↓
NRE at compositor thread / "QueryInterface returned null" in crash.log?
  Yes → MicroCom CCW vtable bug — set Win32CompositionMode.RedirectionSurface
         + BuiltInComInteropSupport=false (§0.9, §0.3)
  No  → continue
  ↓
Window opens but bindings show empty / "Binding error" in Output?
  Yes → missing x:DataType on Window/DataTemplate (§0.11, §4.1)
  No  → continue
  ↓
DataGrid header click does nothing OR glyph flips but rows don't sort?
  Yes → three-layer sort trap, DataGridTemplateColumn — apply §4.5 fix
  No  → continue
  ↓
"DataContext is null" inside a context menu?
  Yes → ContextMenu on DataGrid; must build in code-behind — §4.7
  No  → continue
  ↓
"will always throw" warnings during ILC link?
  Yes → likely Avalonia.Desktop dragging X11/DBus; reference Win32/Skia/
         HarfBuzz explicitly — §0.13
  No  → continue
  ↓
Crash log shows JsonSerializer reflection path?
  Yes → IL2026/IL3050 fired; add JsonSerializerContext for that type — §5.1
  No  → check the per-repo lessons-learned docs cited in §9
```

---

## §12  Further reading (per-repo)

- **UE5CEDumper**: `docs/ui-spec.md` (AOT compat table),
  `docs/dev-log.md` (build 657 trim lesson),
  `docs/private/docs/UE5CEDumper-SD.md`.
- **RE-UNION**: code comments in `ui/REUnionUI/Program.cs`,
  `ui/REUnionUI/Services/DataService.cs`, `Services/AobMakerService.cs`.
- **CrimsonAtomtic**: `docs/status.md:2764-2794` (live AOT pain list),
  `scripts/package_aot.ps1` (publish hardening),
  `App.axaml:25-37` (language-swap comment block),
  `Services/UiLanguageService.cs:340-370` (Clear+Add rationale).
- **discrete**: `docs/lessons-learned.md:722-1042` (THE definitive
  DataGrid sort writeup + the AOT-vs-PublishSingleFile byte-literal
  rule).
- **ZoltDump**: `docs/dev-log.md:52`, code comments in
  `ui/ZoltDumpUI/Views/LiveWalkerPanel.axaml.cs`,
  `ui/ZoltDumpUI/Views/MemberFilterDialog.axaml.cs`.
- **AOBMaker**: `docs/Native-AOT-Notes.md` (繁體中文,
  comprehensive — the `<Run>` stack-overflow finding lives here).
