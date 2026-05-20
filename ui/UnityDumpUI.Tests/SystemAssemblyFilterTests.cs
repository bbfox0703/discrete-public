// SystemAssemblyFilterTests.cs — Phase 25 multi-assembly helper unit tests.
//
// The Symbol Finder's "Search all user assemblies" toggle relies on
// SystemAssemblyFilter to draw the line between BCL / Unity engine
// assemblies and user gameplay assemblies. A miss here would either
// drag the user's "MaxHP" search through every UnityEngine module
// (slow + noisy) or silently drop a real user assembly that happens
// to share a prefix with a system name.
//
// Coverage: the canonical system prefixes from the user-confirmed
// Conservative list, the "boundary match" guard against false
// positives like "SystemicGame", and the user-assembly survivors.

using System.Linq;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SystemAssemblyFilterTests
{
    [Theory]
    // BCL — exact name + name.* prefix forms.
    [InlineData("System")]
    [InlineData("System.Core")]
    [InlineData("System.Xml")]
    [InlineData("System.Runtime")]
    [InlineData("mscorlib")]
    [InlineData("netstandard")]
    [InlineData("Microsoft.CSharp")]
    [InlineData("Microsoft.Win32.Primitives")]
    [InlineData("Mono.Security")]
    [InlineData("Mono.Posix")]
    // Unity engine.
    [InlineData("UnityEngine")]
    [InlineData("UnityEngine.CoreModule")]
    [InlineData("UnityEngine.UI")]
    [InlineData("UnityEngine.PhysicsModule")]
    [InlineData("UnityEngine.TextRenderingModule")]
    [InlineData("Unity.Burst")]
    [InlineData("Unity.Mathematics")]
    [InlineData("Unity.TextMeshPro")]
    [InlineData("Unity.InputSystem")]
    [InlineData("Unity.Addressables")]
    public void IsSystemAssembly_KnownSystemNames_ReturnsTrue(string name)
    {
        Assert.True(SystemAssemblyFilter.IsSystemAssembly(name), $"expected {name} to be classed as system");
    }

    [Theory]
    // Canonical user assemblies — the targets the multi-assembly toggle exists for.
    [InlineData("Assembly-CSharp")]
    [InlineData("Assembly-CSharp-firstpass")]
    // Third-party libs the user explicitly chose to keep in the user set
    // during the Phase 25 multi-assembly design Q&A (Conservative option).
    [InlineData("DOTween")]
    [InlineData("Newtonsoft.Json")]
    [InlineData("Cinemachine")]
    [InlineData("TextMeshPro")]    // note: no "Unity." prefix, so user-set
    [InlineData("spine-csharp")]
    // Boundary-match guard: names that *start* with a system prefix but
    // aren't system assemblies. The filter must not swallow these.
    [InlineData("SystemicGame")]        // "System" prefix but no '.'
    [InlineData("UnityEngineHelper")]   // "UnityEngine" prefix but no '.'
    [InlineData("MonochromeShader")]    // "Mono" prefix but no '.'
    [InlineData("MicrosoftPaintClone")] // "Microsoft" prefix but no '.'
    public void IsSystemAssembly_UserAssemblies_ReturnsFalse(string name)
    {
        Assert.False(SystemAssemblyFilter.IsSystemAssembly(name), $"expected {name} to be classed as user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSystemAssembly_NullOrEmpty_ReturnsFalse(string? name)
    {
        // Empty / null gets classed as user (false). Defensive — the caller
        // shouldn't pass these, but a runtime drop into the multi-assembly
        // list mustn't crash.
        Assert.False(SystemAssemblyFilter.IsSystemAssembly(name));
    }

    [Fact]
    public void SelectUserAssemblies_FiltersAndPreservesOrder()
    {
        // Realistic mixed input — the kind of list AssemblyBrowserViewModel
        // populates from getAssemblies on a typical Unity 2022 IL2CPP game.
        var input = new[]
        {
            "Assembly-CSharp",
            "Assembly-CSharp-firstpass",
            "DOTween",
            "Microsoft.CSharp",
            "Mono.Security",
            "mscorlib",
            "Newtonsoft.Json",
            "netstandard",
            "System",
            "System.Core",
            "System.Xml",
            "TextMeshPro",
            "Unity.Burst",
            "Unity.Mathematics",
            "UnityEngine",
            "UnityEngine.CoreModule",
            "UnityEngine.UI",
        };

        var users = SystemAssemblyFilter.SelectUserAssemblies(input);

        Assert.Equal(
            new[] { "Assembly-CSharp", "Assembly-CSharp-firstpass",
                    "DOTween", "Newtonsoft.Json", "TextMeshPro" },
            users.ToArray());
    }
}
