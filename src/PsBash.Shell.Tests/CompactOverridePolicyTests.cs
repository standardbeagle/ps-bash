using PsBash.Core.Runtime.Compaction;
using Xunit;

namespace PsBash.Shell.Tests;

public class CompactOverridePolicyTests
{
    private static readonly FilterSpec GitDiff = new()
    {
        Name = "git/diff",
        Match = new FilterMatch { Command = "git", Args = ["diff"] },
        Override = ["git", "diff", "--stat"]
    };

    [Fact]
    public void Rewrite_ExactCommand_UsesQuotedOverrideBeforeTranspile()
    {
        var rewritten = CompactOverridePolicy.Rewrite("git diff", [GitDiff], out var reason);

        Assert.Equal("'git' 'diff' '--stat'", rewritten);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("git diff -p")]
    [InlineData("git diff --name-only")]
    [InlineData("git diff HEAD~1")]
    public void Rewrite_ExplicitDiffSemantics_PreservesOriginalCommand(string command)
    {
        var rewritten = CompactOverridePolicy.Rewrite(command, [GitDiff], out var reason);

        Assert.Equal(command, rewritten);
        Assert.Equal("command has explicit options or operands", reason);
    }

    [Fact]
    public void Rewrite_CompoundCommand_PreservesOriginalCommand()
    {
        const string command = "git diff; echo done";
        var rewritten = CompactOverridePolicy.Rewrite(command, [GitDiff], out var reason);

        Assert.Equal(command, rewritten);
        Assert.Equal("command contains shell operators", reason);
    }
}
