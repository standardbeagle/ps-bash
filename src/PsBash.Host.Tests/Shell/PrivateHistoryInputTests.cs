using PsBash.Host.Shell;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Shell;

// PSBASH_HISTORY_PRIVATE is process-global: runs alone.
[Collection(ProcessEnvironmentCollection.Name)]
public class PrivateHistoryInputTests
{
    [Fact]
    public void LeadingSpace_IsPrivate()
    {
        Assert.True(InteractiveShell.IsPrivateInput(" export TOKEN=abc"));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("echo ' not leading'")]
    [InlineData("")]
    public void OrdinaryInput_IsRecorded(string command)
    {
        Assert.False(InteractiveShell.IsPrivateInput(command));
    }

    [Fact]
    public void PrivateEnvFlag_MakesEveryLinePrivate()
    {
        using (ProcessEnv.Set("PSBASH_HISTORY_PRIVATE", "1"))
            Assert.True(InteractiveShell.IsPrivateInput("ls"));
        using (ProcessEnv.Set("PSBASH_HISTORY_PRIVATE", null))
            Assert.False(InteractiveShell.IsPrivateInput("ls"));
    }
}
