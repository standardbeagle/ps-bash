using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A nameref declared with no target (<c>declare -n r</c>, <c>local -n r</c>): <c>declare -p</c> prints the attribute
/// alone, the first literal assignment names the target, and until then it reads as unset. REGRESSION: the emitter kept
/// it to itself, so <c>declare -p r</c> was "not found" and <c>local -n r; r=x</c> set r.
/// </summary>
public class NamerefNoTargetTests
{
    [SkippableFact]
    public Task NoTarget_IsListedAsANameref() => AssertOracle.EqualAsync(
        "typeset -n r; declare -p r; echo \"st=$?\"; declare -p | grep '^declare -n'; declare -n; set | grep -c '^r'");

    [SkippableFact]
    public Task NoTarget_FirstAssignmentNamesTheTarget() => AssertOracle.EqualAsync(
        "declare -n r; r=x; declare -p r; x=X; echo \"r=$r\"");

    [SkippableFact]
    public Task NoTarget_ReadsAsUnset() => AssertOracle.EqualAsync(
        "declare -n r; echo \"[${r-unset}] [${r:-dflt}]\"");

    [SkippableFact]
    public Task NoTarget_LocalInAFunction() => AssertOracle.EqualAsync(
        "f() { local -n r; declare -p r; r=x; declare -p r; x=1; echo \"r=$r\"; }; f; declare -p r >/dev/null 2>&1; echo \"st=$?\"\n"
        + "g() { local -n s; }; g; s=v; echo \"s=$s\"; declare -p s\n"
        + "y=Y; declare -n t=y; h() { local -n t; declare -p t; }; h; declare -p t");

    /// <summary><c>declare +n</c> leaves a declared, unset scalar; a plain <c>unset</c> removes the reference itself.</summary>
    [SkippableFact]
    public Task NoTarget_PlusNAndUnset() => AssertOracle.EqualAsync(
        "declare -n r; declare +n r; declare -p r; declare -n q; unset q; declare -p q >/dev/null 2>&1; echo \"st=$?\"; q=v; echo \"q=$q\"");
}
