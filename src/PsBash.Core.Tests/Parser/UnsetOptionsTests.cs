using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// <see cref="UnsetOptions"/>, the one reading of unset's options the emitter and Invoke-BashUnset share. Oracle (bash 5.2,
/// WSL): <c>declare -n r=y; y=1; unset -vn r</c> (and <c>-nv</c>, <c>-n -n</c>) drops r and keeps y; <c>unset -fn r</c> /
/// <c>-nf</c> keeps both; <c>unset -fv f</c>, <c>-vf</c>, <c>-f -v</c>, <c>-v -f</c>, <c>-fvn</c> all report "cannot
/// simultaneously unset a function and a variable" (status 1, nothing unset: no last-wins); <c>unset x -f g</c> unsets
/// the variables x and g; <c>unset -- -v v</c> unsets v; <c>unset -n -- v</c> keeps v=1; <c>unset -vx v</c> is the usage
/// error. REGRESSION: both sides matched only the exact words -f/-v/-n, so a cluster was skipped.
/// </summary>
public class UnsetOptionsTests
{
    private static UnsetOptions P(params string?[] args) => UnsetOptions.Parse(args);

    // Every arrangement of the three letters: one word or split, any order, repeated.
    [Theory]
    [InlineData("-v", false, true, false)]
    [InlineData("-f", true, false, false)]
    [InlineData("-n", false, false, true)]
    [InlineData("-vn", false, true, true)]
    [InlineData("-nv", false, true, true)]
    [InlineData("-fn", true, false, true)]
    [InlineData("-nf", true, false, true)]
    [InlineData("-fv", true, true, false)]
    [InlineData("-vf", true, true, false)]
    [InlineData("-fvn", true, true, true)]
    [InlineData("-fnv", true, true, true)]
    [InlineData("-vfn", true, true, true)]
    [InlineData("-vnf", true, true, true)]
    [InlineData("-nfv", true, true, true)]
    [InlineData("-nvf", true, true, true)]
    [InlineData("-nn", false, false, true)]
    [InlineData("-vv", false, true, false)]
    [InlineData("-v -n", false, true, true)]
    [InlineData("-n -v", false, true, true)]
    [InlineData("-f -n", true, false, true)]
    [InlineData("-n -f", true, false, true)]
    [InlineData("-f -v", true, true, false)]
    [InlineData("-v -f", true, true, false)]
    [InlineData("-n -n", false, false, true)]
    [InlineData("-v -nf", true, true, true)]
    public void Parse_EveryClusterPermutation_SetsTheLettersItHolds(string options, bool functions, bool variables, bool reference)
    {
        var words = options.Split(' ');
        var parsed = UnsetOptions.Parse([.. words, "r"]);
        Assert.True(parsed.Known);
        Assert.Equal((functions, variables, reference), (parsed.Functions, parsed.Variables, parsed.Reference));
        Assert.Equal(words.Length, parsed.FirstOperand);
        Assert.Equal(functions && variables, parsed.Conflict);
        Assert.Equal(reference && !functions, parsed.ByReference);
    }

    [Fact]
    public void Parse_TheFirstNameEndsTheOptions()
    {
        var parsed = P("x", "-f", "g");
        Assert.Equal(0, parsed.FirstOperand);
        Assert.False(parsed.Functions);
    }

    [Fact]
    public void Parse_DoubleDashEndsTheOptions_AndIsNoOperand()
    {
        var parsed = P("-n", "--", "-v", "v");
        Assert.Equal(2, parsed.FirstOperand);
        Assert.True(parsed.ByReference);
        Assert.False(parsed.Variables);
    }

    [Fact]
    public void Parse_LoneDashIsAnOperand() => Assert.Equal(0, P("-", "v").FirstOperand);

    [Theory]
    [InlineData("-x")]
    [InlineData("-vx")]
    [InlineData("--bogus")]
    public void Parse_AnOptionUnsetDoesNotHave_IsTheRuntimesUsageError(string word)
        => Assert.False(P(word, "v").Known);

    [Fact]
    public void Parse_AWordKnownOnlyAtRunTime_LeavesTheOptionsToTheRuntime()
    {
        var parsed = P("-n", null, "r");
        Assert.False(parsed.Known);
        Assert.Equal(1, parsed.FirstOperand);
    }

    [Fact]
    public void Parse_FromAnIndex_SkipsTheCommandName()
    {
        var parsed = UnsetOptions.Parse(["unset", "-vn", "r"], 1);
        Assert.Equal(2, parsed.FirstOperand);
        Assert.True(parsed.ByReference);
    }
}
