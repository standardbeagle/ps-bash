using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An unquoted positional (<c>$1</c>, <c>${11}</c>) is split on IFS and globbed like any unquoted <c>$x</c>: in an
/// argument list, a <c>for</c> list, an array literal, <c>local $1</c> and <c>return $4</c>. ps-bash passed each
/// positional as ONE word (<c>for w in $1</c> looped once over <c>'x=1 y=2'</c>). A field is data: <c>;</c>,
/// <c>$( )</c> and backticks in it are never run.
/// </summary>
public class PositionalSplitDifferentialTests
{
    [Theory]
    [InlineData("f() {\n  for w in $1; do echo \"[$w]\"; done\n  for w in $1 $2; do echo \"<$w>\"; done\n  printf '{%s}' $1; echo\n  a=($1); echo \"n=${#a[@]}\"\n  for w in ${11}; do echo \"($w)\"; done\n  printf '{%s}' $3; echo \"|\"\n  printf '{%s}' \"$1\"; echo\n  local $1; echo \"x=$x y=$y\"\n  echo $1\n  return $4\n}\nf 'x=1 y=2' 'c d' '' 7 5 6 7 8 9 10 'k l'\necho \"rc=$?\"")]
    [InlineData("set -- 'a b' 'g*'\ncd \"$(mktemp -d)\" && touch g1 g2\nprintf '[%s]' $1 $2; echo\nprintf '[%s]' \"$@\"; echo")]
    [InlineData("set -- 'a;echo pwned' '$(echo pwned2)' '`echo pwned3`'\nprintf '[%s]' $1 $2 $3; echo\nfor w in $1; do echo \"<$w>\"; done")]
    [InlineData("IFS=:\nset -- 'p:q r'\nprintf '[%s]' $1; echo")]
    public Task Positional_UnquotedIsWordSplit(string script) => AssertOracle.EqualAsync(script);
}
