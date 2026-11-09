using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Under <c>set -e</c> a compound command whose <c>&lt; file</c> cannot be opened ends the script (bash 5.2); in a condition or <c>||</c> list it does not.</summary>
public class ErrexitInputRedirectTests
{
    [Fact]
    public Task BraceGroup_MissingInput_EndsScript() => AssertOracle.EqualAsync(
        "set -e\n{ { cat; } < /nonexistent/zz.txt; } 2>/dev/null\necho should-not-print");

    [Fact]
    public Task WhileRead_MissingInput_EndsScript() => AssertOracle.EqualAsync(
        "set -e\n{ while read l; do echo $l; done < /nonexistent/zz.txt; } 2>/dev/null\necho should-not-print");

    [Fact]
    public Task MissingInput_InOrList_Continues() => AssertOracle.EqualAsync(
        "set -e\n{ { cat; } < /nonexistent/zz.txt || echo handled; } 2>/dev/null\necho continues");

    [Fact]
    public Task WhileRead_WithoutErrexit_StatusIsOne() => AssertOracle.EqualAsync(
        "{ while read l; do echo $l; done < /nonexistent/zz.txt; echo st=$?; } 2>/dev/null");
}
