using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>od</c> against GNU od: address radix, type lists, column alignment, duplicates, skip/limit, files.</summary>
public class OdTests
{
    [Theory]
    [InlineData("printf 'a\\nb\\0c' | od -A n -t x1")]
    [InlineData("printf 'hi\\n\\t\\x7f\\xff' | od -A n -t c -t x1")]
    [InlineData("printf 'abc' | od -c")]
    [InlineData("printf 'hello world, this is a longer line of text\\n' | od")]
    [InlineData("printf 'x' | od -An -c")]
    [InlineData("printf 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab' | od -A x -t x1")]
    [InlineData("printf 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab' | od -v -t x1")]
    [InlineData("printf 'abcdefgh' | od -A d -t x2 -t c")]
    [InlineData("printf '' | od")]
    [InlineData("printf 'abcdefgh' | od -j 2 -N 3 -t a")]
    [InlineData("printf 'abcde' | od -t d1 -t u2 -t o4")]
    [InlineData("printf 'ab\\n' | od -b -w2")]
    [InlineData("echo hi | od -tx1")]
    public Task Od_Matches_Gnu(string script) => AssertOracle.EqualAsync(script);

    [Fact]
    public Task Od_MissingFile_FailsWithoutAnAddressLine() => AssertOracle.EqualAsync(
        "{ od -t x1 nosuchfile_zz; } 2>/dev/null; echo st=$?");

    [Fact]
    public Task Od_BadRadix_Fails() => AssertOracle.EqualAsync(
        "{ printf x | od -A q; } 2>/dev/null; echo st=$?");
}
