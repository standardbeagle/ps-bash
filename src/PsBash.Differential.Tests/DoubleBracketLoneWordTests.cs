using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A lone operator word in [[ ]] is a one-word string test; a failed failglob array literal leaves status 1.</summary>
public class DoubleBracketLoneWordTests
{
    [Fact]
    public Task LoneOperatorWord_IsTrue() => AssertOracle.EqualAsync("[[ = ]]\necho status=$?\n[[ == ]]\necho status=$?\n[[ ! = ]]\necho status=$?");

    // A literal operand that starts with `$` is text. It was emitted bare: `$` was run as a command ("$: command not
    // found") or, before `&&`, made the whole line a PowerShell parse error.
    [Fact]
    public Task DollarLiteralOperand_IsText() => AssertOracle.EqualAsync(
        "[[ $ =~ \"$\" ]] && echo 'yes $'\n[[ $ == \"$\" ]] && echo eq1\n[[ \"$\" == $ ]] && echo eq2\n[[ a$ == \"a$\" ]] && echo eq3\n"
        + "[ $ = $ ] && echo eq4\n[[ $ != x ]] && echo ne\nx='$'; [[ $x == $ ]] && echo eq5\ny=5; [[ $y -eq 5 ]] && echo num");

    [Fact]
    public Task Failglob_ArrayLiteral_StatusIsOne() => AssertOracle.EqualAsync("shopt -s failglob\n{ myarr=(*.ZZ); } 2>/dev/null\necho status=$?\na=(x y)\necho $?");
}
