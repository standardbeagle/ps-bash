using PsBash.Core.Runtime.Compaction;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Compaction;

public sealed class CompactChainRouteTests
{
    private const string Route = "git.stage-commit-push.v1";
    private const string FullCommand = "git add . && git commit -m save && git push";

    [Fact]
    public void Apply_RoutedSuccessfulChain_UsesFullHeaderAndOneCombinedConfirmation()
    {
        var result = FilterEngine.Apply(
            FullCommand, 0, false,
            [Out("staged\n"), Out("[main abc] save\n"), Out("pushed\n")],
            filters: [], routeKey: Route);

        Assert.Contains(FullCommand, result);
        Assert.Equal(1, Count(result, "changes staged, committed, and pushed"));
        Assert.DoesNotContain("staged\n", result);
        Assert.DoesNotContain("[main abc]", result);
        Assert.DoesNotContain(result.Split('\n'), line => line == "pushed");
    }

    [Fact]
    public void Apply_RoutedFailedChain_PreservesBodyAndNeverEmitsSuccess()
    {
        var result = FilterEngine.Apply(
            FullCommand, 1, false,
            [Out("staged\n"), Err("push rejected\n")],
            filters: [], routeKey: Route);

        Assert.Contains(FullCommand, result);
        Assert.Contains("[out] staged", result);
        Assert.Contains("[err] push rejected", result);
        Assert.DoesNotContain("changes staged, committed, and pushed", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown.route")]
    public void Apply_WithoutEligibleRoute_IsUnchanged(string? routeKey)
    {
        var frames = new[] { Out("staged\n"), Out("pushed\n") };

        var result = FilterEngine.Apply(FullCommand, 0, false, frames, filters: [], routeKey: routeKey);
        var expected = OutputCompactor.CompactCommandOutput(FullCommand, 0, false, frames);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Apply_CompactOffEquivalent_DoesNotUseRoute()
    {
        var frames = new[] { Out("normal output\n") };
        Assert.Equal(
            OutputCompactor.CompactCommandOutput(FullCommand, 0, false, frames),
            FilterEngine.Apply(FullCommand, 0, false, frames));
    }

    private static OutputFrame Out(string text) => new(StreamTag.Stdout, text);
    private static OutputFrame Err(string text) => new(StreamTag.Stderr, text);

    private static int Count(string value, string needle)
    {
        var count = 0;
        for (var start = 0; (start = value.IndexOf(needle, start, StringComparison.Ordinal)) >= 0; start += needle.Length)
            count++;
        return count;
    }
}
