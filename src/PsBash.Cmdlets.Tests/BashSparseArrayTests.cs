using Xunit;
namespace PsBash.Cmdlets.Tests;

public class BashSparseArrayTests
{
    [Fact]
    public void FromList_ThenRemoveMiddle_KeepsOtherSubscripts()
    {
        var a = BashSparseArray.FromList(new object[] { "a", "b", "c" });
        a.RemoveAt(1);
        Assert.Equal(2, a.Count);
        Assert.Equal("c", a[2L]);
        Assert.Equal(3, a.NextIndex);
    }

    [Fact]
    public void NegativeSubscript_CountsBackFromNextIndex()
    {
        var a = new BashSparseArray();
        a[10L] = "x";
        a[20L] = "y";
        Assert.Equal("y", a[-1L]);
    }

    [Fact]
    public void NonNumericKey_IsNotAnElement()
    {
        var a = new BashSparseArray();
        a[5L] = "x";
        Assert.False(a.Contains("Count"));
        Assert.Equal(1, a.Count);
    }
}
