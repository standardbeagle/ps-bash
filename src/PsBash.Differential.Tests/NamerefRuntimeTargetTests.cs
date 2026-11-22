using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A nameref with no target takes its target from the FIRST write, whatever writes it: an assignment of any value, each
/// pass of a <c>for</c> loop over it, <c>read</c>, <c>printf -v</c>, <c>getopts</c>, a function's <c>local -n r; r=$1</c>.
/// A value that names no variable is "not a valid identifier" (an assignment drops the rest of its line; read and
/// printf -v fail with status 1; a <c>for</c> loop ends). A <c>for</c> loop re-targets a nameref that has a target too.
/// REGRESSION (8bd50899): only a literal <c>r=name</c> the transpiler saw named the target; every other writer stored
/// the value in r.
/// </summary>
public class NamerefRuntimeTargetTests
{
    [SkippableFact]
    public Task FirstWrite_FromACommandSubstitution_NamesTheTarget() => AssertOracle.EqualAsync(
        "declare -n r; r=$(echo tgt); declare -p r; r=5; echo \"tgt=$tgt r=$r\"; r+=6; echo \"tgt=$tgt\"");

    [SkippableFact]
    public Task FirstWrite_InAFunction_FromAParameter() => AssertOracle.EqualAsync(
        "g() { local -n r; r=$1; r=5; }; g tgt; echo \"tgt=$tgt\"; declare -p r >/dev/null 2>&1; echo \"st=$?\"");

    [SkippableFact]
    public Task ForLoop_RetargetsTheNamerefEachPass() => AssertOracle.EqualAsync(
        "declare -n r; for r in a b; do r=1; done; declare -p r a b\n"
        + "x=X; declare -n q=x; for q in c d; do q=2; echo \"in=$q\"; done; declare -p q c d x");

    [SkippableFact]
    public Task ForLoop_AWordThatIsNoName_EndsTheLoop() => AssertOracle.EqualAsync(
        "declare -n r; for r in a 'b c' d; do echo it; done; echo \"st=$?\"; declare -p r");

    [SkippableFact]
    public Task ReadPrintfGetopts_NameTheTarget() => AssertOracle.EqualAsync(
        "declare -n r; read r <<< tgt; declare -p r; r=5; echo \"tgt=$tgt\"\n"
        + "declare -n p; printf -v p %s hi; declare -p p\n"
        + "declare -n o; getopts ab o -a; echo \"st=$?\"; declare -p o");

    [SkippableFact]
    public Task ReadAndPrintfV_ThroughANamerefWithATarget_WriteTheTarget() => AssertOracle.EqualAsync(
        "declare -n r=x; read r <<< hi; declare -n p=y; printf -v p %s yo; declare -p r x p y");

    [SkippableFact]
    public Task AValueThatIsNoName_IsNotAValidIdentifier() => AssertOracle.EqualAsync(
        "declare -n p; printf -v p %s 'a b'; echo \"st=$?\"; declare -p p\n"
        + "declare -n q; read q <<< 'a b'; echo \"st=$?\"; declare -p q\n"
        + "declare -n s; v=5; s=$v; echo 'not reached'\n"
        + "echo \"st=$?\"; declare -p s");

    [SkippableFact]
    public Task AnElementTarget_IsWrittenAndRead() => AssertOracle.EqualAsync(
        "declare -n r; v='a[2]'; r=$v; r=9; echo \"r=$r\"; declare -p r a");

    [SkippableFact]
    public Task UnsetOfARuntimeNameref_UnsetsItsTarget() => AssertOracle.EqualAsync(
        "declare -n r; r=$(echo tgt); tgt=T; echo \"[$r]\"; unset r; declare -p r; echo \"[${tgt-unset}]\"");
}
