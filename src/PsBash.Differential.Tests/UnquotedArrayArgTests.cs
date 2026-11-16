using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An unquoted <c>${a[@]}</c> beside any other argument failed to bind ("Cannot convert System.Object[] to the
/// type String required by parameter Arguments"): it was one array argument, not N words.
/// </summary>
public class UnquotedArrayArgTests
{
    [SkippableTheory]
    [InlineData("a=(x y z); echo ${a[@]} x")]
    [InlineData("a=(x y z); echo x ${a[*]}")]
    [InlineData("a=(x y z); echo ${a[@]} ${#a[@]} ${!a[@]}")]
    [InlineData("a=(x y z); echo ${a[@]} ${a[@]}")]
    [InlineData("a=('p q' r); echo ${a[@]} x")]
    [InlineData("a=(); echo ${a[@]} x")]
    [InlineData("a=(x y z); echo ${a[@]:1} x")]
    [InlineData("a=(x y z); echo ${!a[@]} x")]
    [InlineData("a=(x y z); echo ${a[@]}")]
    [InlineData("a=(x y z); printf '[%s]' ${a[@]} end; echo")]
    [InlineData("a=(x y); for i in ${a[@]} z; do echo $i; done")]
    [InlineData("a=('p q' r); echo \"${a[@]}\" x")]
    [InlineData("echo {1..5} {a..e} {5..1..2}")]
    [InlineData("echo {1..3} y")]
    [InlineData("echo x{a,b}y z {1..2}")]
    [InlineData("echo {1..3}")]
    [InlineData("printf '[%s]' {a,b} {c,d}; echo")]
    public Task Expands_To_Words(string script) => AssertOracle.EqualAsync(script);
}
