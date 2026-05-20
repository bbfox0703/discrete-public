using UnityDumpUI.Core;
using Xunit;

namespace UnityDumpUI.Tests;

public class SmokeTests
{
    [Fact]
    public void Constants_PipeNameMatchesDllMagicString()
    {
        Assert.Equal("UnityDumpBfx", Constants.PipeName);
    }

    [Fact]
    public void Constants_LogRotationCountIsFive()
    {
        // Bumped from 4 → 5 on 2026-05-20 alongside the "rotate on every
        // UI startup" policy change. DLL side stays at 4 (separate
        // constant in MagicStrings.h).
        Assert.Equal(5, Constants.LogRotationFileCount);
    }

    [Fact]
    public void Constants_LogRootSubpathPointsToDiscreteUEDumper()
    {
        // Renamed 2026-05-20 from UnityCEDumper to Discrete_UEDumper to
        // match the repo name. The C++ DLL mirror const is in
        // MagicStrings.h and both sides must agree on the path.
        Assert.Equal(@"Discrete_UEDumper\Logs", Constants.LogRootSubpath);
    }
}
