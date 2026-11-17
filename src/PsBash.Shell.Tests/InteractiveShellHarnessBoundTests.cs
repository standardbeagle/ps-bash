using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// The interactive harness's cold-start bound is a floor: a test may wait longer, never shorter (a 15 s override timed
/// out a correct shell's first prompt on a loaded fleet host). Harness rule, no bash equivalent.
/// </summary>
public class InteractiveShellHarnessBoundTests
{
    [Fact]
    public void StartBound_ShorterRequest_IsTheDefault()
        => Assert.Equal(InteractiveShellHarness.DefaultStartTimeout,
            InteractiveShellHarness.StartBound(InteractiveShellHarness.DefaultStartTimeout - TimeSpan.FromSeconds(1)));

    [Fact]
    public void StartBound_LongerRequest_IsKept()
    {
        var longer = InteractiveShellHarness.DefaultStartTimeout + TimeSpan.FromSeconds(30);
        Assert.Equal(longer, InteractiveShellHarness.StartBound(longer));
    }

    [Fact]
    public void StartBound_NoRequest_IsTheDefault()
        => Assert.Equal(InteractiveShellHarness.DefaultStartTimeout, InteractiveShellHarness.StartBound(null));
}
