using System;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests.Platform;

/// <summary>
/// <see cref="LaunchBytes"/>: an argument or environment value .NET decoded to U+FFFD at start is taken from the bytes
/// the process was started with. REGRESSION: ps-bash started as <c>bash</c> / <c>sh</c> by a script got
/// <c>ef bf bd</c> for <c>$'\xff'</c>. Behavior vs bash: Differential <c>RawBytesDifferentialTests.Export_ToANestedShell_KeepsTheInvalidByte</c>;
/// these pin the matching rule, which has no bash equivalent (qa-rubric Directive 1).
/// </summary>
public class LaunchBytesTests
{
    [Fact]
    public void SplitNul_KeepsEmptyArguments_AndIgnoresTheTrailingNul()
    {
        var parts = LaunchBytes.SplitNul(new byte[] { (byte)'a', 0, 0, (byte)'b', 0xFF, 0 });
        Assert.Equal(3, parts.Count);
        Assert.Equal(new byte[] { (byte)'a' }, parts[0]);
        Assert.Empty(parts[1]);
        Assert.Equal(new byte[] { (byte)'b', 0xFF }, parts[2]);
    }

    [Fact]
    public void Exact_AnArgumentThatLostAByte_IsItsBytes()
    {
        var exact = LaunchBytes.Exact(["-c", "b�"], [[(byte)'-', (byte)'c'], [(byte)'b', 0xFF]]);
        Assert.Equal("-c", exact[0]);
        Assert.Equal("b" + RawBytes.ToMarker(0xFF), exact[1]);
    }

    // Not the bytes the value came from (another argv, a value changed since the start): .NET's values stand.
    [Fact]
    public void Exact_BytesThatDoNotDecodeToTheValue_KeepTheValue()
    {
        string[] lossy = ["x�"];
        Assert.Same(lossy, LaunchBytes.Exact(lossy, [[(byte)'y', 0xFF]]));
    }

    [Fact]
    public void Arguments_WithoutAReplacementCharacter_AreReturnedAsTheyAre()
    {
        string[] args = ["-c", "echo é"];
        Assert.Same(args, LaunchBytes.Arguments(args));
    }

    [Fact]
    public void EnvironmentValue_WithoutAReplacementCharacter_IsTheValue()
        => Assert.Equal("plain", LaunchBytes.EnvironmentValue("PSBASH_T_NONE", "plain"));
}
