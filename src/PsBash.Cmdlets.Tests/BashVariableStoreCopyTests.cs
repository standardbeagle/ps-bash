using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="BashVariableStore.Copy"/>, the park of <c>IFS=: cmd $x</c> (the words read the IFS from before the binding).
/// An unset source leaves the target unset: copied through script (<c>Set('t', Get('IFS'))</c>) the null became '' and
/// <c>unset IFS; IFS=: printf '&lt;%s&gt;' $x</c> with x='a:b c' gave &lt;a:b c&gt;, bash &lt;a:b&gt;&lt;c&gt;. Names are unique
/// to this class (the store is the process environment).
/// </summary>
public class BashVariableStoreCopyTests
{
    [Fact]
    public void Copy_OfAnUnsetVariable_UnsetsTheTarget()
    {
        BashVariableStore.Set("__psbash_copytest_to1", "stale");
        BashVariableStore.Set("__psbash_copytest_from1", null);
        BashVariableStore.Copy("__psbash_copytest_from1", "__psbash_copytest_to1");
        Assert.Null(BashVariableStore.Get("__psbash_copytest_to1"));
    }

    [Fact]
    public void Copy_OfAValue_CopiesIt()
    {
        try
        {
            BashVariableStore.Set("__psbash_copytest_from2", " :");
            BashVariableStore.Copy("__psbash_copytest_from2", "__psbash_copytest_to2");
            Assert.Equal(" :", BashVariableStore.Get("__psbash_copytest_to2"));
        }
        finally
        {
            BashVariableStore.Set("__psbash_copytest_from2", null);
            BashVariableStore.Set("__psbash_copytest_to2", null);
        }
    }
}
