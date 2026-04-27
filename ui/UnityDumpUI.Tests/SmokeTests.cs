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
    public void Constants_LogRotationCountIsFour()
    {
        Assert.Equal(4, Constants.LogRotationFileCount);
    }
}
