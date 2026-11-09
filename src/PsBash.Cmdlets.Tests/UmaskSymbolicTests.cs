using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>Expected masks are the values bash 5.2 prints (oils builtin-umask).</summary>
public class UmaskSymbolicTests
{
    [Theory]
    [InlineData(0x000, "u-rw", 0x180)]       // 0000 -> 0600
    [InlineData(0x1C0, "u=r", 0x0C0)]        // 0700 -> 0300
    [InlineData(0x0D2, "a=rwx", 0x000)]
    [InlineData(0x054, "a=", 0x1FF)]
    [InlineData(0x054, "=", 0x1FF)]
    [InlineData(0x049, "u=rwx,u=rw,u=r,u=,g=rwx", 0x1C1)]  // 0111 -> 0701
    public void Applies(int mask, string spec, int expected)
    {
        Assert.True(UmaskSymbolic.TryApply(mask, spec, out var got));
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("u+r,,u-r")]
    [InlineData("u")]
    [InlineData("u+q")]
    [InlineData("a=u")]
    [InlineData("u+r+w+x")]
    [InlineData("=+=")]
    [InlineData("")]
    public void RejectsMalformed(string spec) => Assert.False(UmaskSymbolic.TryApply(0x12, spec, out _));
}
