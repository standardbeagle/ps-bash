using Xunit;

namespace PsBash.Differential.Tests;

public class SmokeTests
{
    [Fact]
    public void Builds_AndRuns()
    {
        Assert.Equal(2, 1 + 1);
    }
}
