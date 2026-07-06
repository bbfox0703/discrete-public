// UnityGameSupportTests.cs — UnityGameEntry.IsSupported gate for Proxy Deploy.
//
// Phase 12e: Mono is supported on BOTH x64 and x86 (x86 backend + proxy ship
// in dist/x86/). IL2CPP is x64-only (no 32-bit IL2CPP backend), so an x86
// IL2CPP game stays unsupported. Unknown bitness / no backend → unsupported.

using UnityDumpUI.Core.Platform;
using Xunit;

namespace UnityDumpUI.Tests;

public class UnityGameSupportTests
{
    private static UnityGameEntry Make(UnityBackend backend, ExeBitness bitness) =>
        new("C:\\game\\game.exe", "C:\\game", "game",
            backend, bitness, "game_Data", ProxyKind.None, "test");

    [Theory]
    [InlineData(UnityBackend.Mono,   ExeBitness.X64, true)]   // x64 Mono
    [InlineData(UnityBackend.IL2CPP, ExeBitness.X64, true)]   // x64 IL2CPP
    [InlineData(UnityBackend.Mono,   ExeBitness.X86, true)]   // x86 Mono — Phase 12e
    [InlineData(UnityBackend.IL2CPP, ExeBitness.X86, false)]  // no 32-bit IL2CPP backend
    [InlineData(UnityBackend.None,   ExeBitness.X64, false)]  // unknown backend
    [InlineData(UnityBackend.Mono,   ExeBitness.Unknown, false)] // ARM64 / corrupt PE
    [InlineData(UnityBackend.IL2CPP, ExeBitness.Unknown, false)]
    public void IsSupported_MatchesBackendBitnessMatrix(
        UnityBackend backend, ExeBitness bitness, bool expected)
    {
        Assert.Equal(expected, Make(backend, bitness).IsSupported);
    }

    // Is32BitMono gates the "Show 32-bit Mono" toggle (on by default) in the
    // Proxy Deploy filter. Only x86 Mono qualifies — it must NOT overlap the
    // "Show unsupported" set, so x86 IL2CPP stays false here.
    [Theory]
    [InlineData(UnityBackend.Mono,   ExeBitness.X86, true)]    // the one supported 32-bit case
    [InlineData(UnityBackend.IL2CPP, ExeBitness.X86, false)]   // unsupported → "Show unsupported"
    [InlineData(UnityBackend.Mono,   ExeBitness.X64, false)]   // 64-bit, not gated
    [InlineData(UnityBackend.IL2CPP, ExeBitness.X64, false)]
    [InlineData(UnityBackend.None,   ExeBitness.X86, false)]
    [InlineData(UnityBackend.Mono,   ExeBitness.Unknown, false)]
    public void Is32BitMono_OnlyTrueForX86Mono(
        UnityBackend backend, ExeBitness bitness, bool expected)
    {
        Assert.Equal(expected, Make(backend, bitness).Is32BitMono);
    }
}
