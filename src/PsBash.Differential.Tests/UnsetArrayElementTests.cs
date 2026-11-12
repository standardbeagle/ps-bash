using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An element of an unset array (or of a scalar, past index 0) is empty. <c>${u[1]}</c> / <c>${u[i]}</c> /
/// <c>${u[$k]}</c> indexed <c>$null</c>: "Cannot index into a null array", which aborted the whole command line.
/// </summary>
public class UnsetArrayElementTests
{
    [Fact]
    public Task UnsetArray_ElementReads_AreEmpty() => AssertOracle.EqualAsync(
        "echo \"[${u[1]}]\" \"[${u[i]}]\" \"[${u[$k]}]\" \"[${u[1]:-d}]\" \"[${u[i]-e}]\"\n"
        + "x=${zz[3]}; echo \"[$x]\"\n"
        + "s=pq; echo \"[${s[1]}]\" \"[${s[0]}]\" \"[${s[1]-w}]\"\n"
        + "declare -A m; echo \"[${m[key]:+yes}]\" \"[${q[key]:+yes}]\"\n"
        + "a=(x y z); echo \"${a[1]}\" \"${a[$((1+1))]}\" \"${a[-1]}\"\necho done");
}
