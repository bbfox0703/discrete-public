using UnityDumpUI.Core;
using Xunit;

namespace UnityDumpUI.Tests;

public class SmokeTests
{
    [Fact]
    public void Constants_PipeNameMatchesDllMagicString()
    {
        Assert.Equal("DiscreteBfx", Constants.PipeName);
    }

    [Fact]
    public void Constants_LogRetentionIsAgeBased21Days()
    {
        // Age-based retention (2026-11, modelled on UE5CEDumper) replaced the
        // 5-file UI rotation and the 15-newest PID-folder cap. DLL mirror:
        // MagicStrings::LogRetentionDays.
        Assert.Equal(21, Constants.LogRetentionDays);
    }

    [Fact]
    public void Constants_LogRootSubpathPointsToDiscrete()
    {
        // Log root is %LOCALAPPDATA%\discrete\Logs — matches the repo name.
        // The C++ DLL mirror const is in MagicStrings.h and both sides must
        // agree on the path.
        Assert.Equal(@"discrete\Logs", Constants.LogRootSubpath);
    }
}
