using System.Collections;
using System.Management.Automation;
using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="BashIndexedArray.Concat"/> is <c>a+=(…)</c>'s copy: it must flatten exactly as PowerShell's
/// <c>@(@($current) + @($items))</c> did, which it replaced for speed.
/// Oracle note (Directive 1): a .NET seam with no bash surface of its own; the bash behaviour (`a+=(x)`) is covered
/// by the spec suite's array cases.
/// </summary>
public class BashIndexedArrayTests
{
    [Fact]
    public void Concat_TwoArrays_JoinsInOrder()
        => Assert.Equal(new object?[] { "a", "b", "c" }, BashIndexedArray.Concat(new object?[] { "a", "b" }, new object?[] { "c" }));

    [Fact]
    public void Concat_ResultIsANewArray_TheOperandsAreUntouched()
    {
        var current = new object?[] { "a" };
        var joined = BashIndexedArray.Concat(current, new object?[] { "b" });
        Assert.NotSame(current, joined);
        Assert.Single(current);
    }

    [Fact]
    public void Concat_ScalarCurrent_IsOneElement()
        // `s=x; s+=(y)`: the scalar in $env:s becomes element 0.
        => Assert.Equal(new object?[] { "x", "y" }, BashIndexedArray.Concat("x", new object?[] { "y" }));

    [Fact]
    public void Concat_NullItems_AddNothing()
        => Assert.Equal(new object?[] { "a" }, BashIndexedArray.Concat(new object?[] { "a" }, null));

    [Fact]
    public void Concat_DictionaryAndString_AreSingleValues_AsPowerShellEnumerates()
    {
        var table = new Hashtable { ["k"] = "v" };
        var joined = BashIndexedArray.Concat(table, new object?[] { "z" });
        Assert.Equal(2, joined.Length);
        Assert.Same(table, joined[0]);
    }

    [Fact]
    public void Concat_WrappedOrListOperands_FlattenLikeTheScriptForm()
    {
        var wrapped = PSObject.AsPSObject(new object?[] { "a", "b" });
        var list = new List<object?> { "c", "d" };
        Assert.Equal(new object?[] { "a", "b", "c", "d" }, BashIndexedArray.Concat(wrapped, list));
    }

    [Fact]
    public void Concat_MatchesPowerShellArrayAddition()
    {
        using var ps = PowerShell.Create();
        object?[] current = ["1", 2, null, "x y"];
        object?[] items = ["", "z"];
        var expected = ps.AddScript("param($c, $i) ,@(@($c) + @($i))").AddArgument(current).AddArgument(items)
            .Invoke().Single().BaseObject;
        Assert.Equal((object?[])expected, BashIndexedArray.Concat(current, items));
    }
}
