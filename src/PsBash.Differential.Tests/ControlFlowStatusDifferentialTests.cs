using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for control-flow conditions and statuses (no <c>set -e</c>).
///
/// Covered regressions:
///   - an if/elif/while/until condition that RAN A COMMAND had its stdout discarded
///     (<c>if echo in; then …</c> printed nothing for <c>in</c>): the condition ran in expression
///     position, where <c>[void]</c> was required. Same for <c>grep pat f || exit 1</c>.
///   - a multi-command condition (<c>while a; b; do</c>, <c>if a; b; then</c>) failed to parse.
///   - <c>||</c>/<c>&amp;&amp;</c> did not see a function's <c>return N</c> or a subshell's
///     <c>(exit N)</c>: PowerShell's chain operators test <c>$?</c>, which those leave true.
///   - <c>local</c>/<c>export</c>/<c>readonly</c>/<c>declare x=$(false)</c> left <c>$?</c> = 1 (bash: 0,
///     the builtin's own status); <c>declare x=$(cmd)</c> was "command not found".
/// </summary>
public class ControlFlowStatusDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Condition_CommandOutput_Streams()
        => Eq("if echo in-if; then echo yes; fi\n"
            + "if ! echo in-not; then echo no; else echo else; fi\n"
            + "if echo a && echo b; then echo both; fi\n"
            + "if false || echo c; then echo or; fi\n"
            + "f(){ echo in-func; return 0; }; if f; then echo func-yes; fi");

    [SkippableFact]
    public Task Condition_ElifRunsOnlyAfterEarlierFailed()
        => Eq("if echo c1; false; then echo b1; elif echo c2; true; then echo b2; elif echo c3; then echo b3; fi");

    [SkippableFact]
    public Task Condition_OutputReachesPipeAndCapture()
        => Eq("if echo piped; then echo body; fi | tr a-z A-Z\n"
            + "x=$(if echo captured; then echo body; fi); printf '[%s]\\n' \"$x\"");

    [SkippableFact]
    public Task Loop_MultiCommandCondition_StreamsAndParses()
        => Eq("n=0; while echo in-while $n; [ $n -lt 2 ]; do n=$((n+1)); done; echo \"after rc=$?\"\n"
            + "m=0; until echo in-until $m; [ $m -ge 1 ]; do m=$((m+1)); done");

    [SkippableFact]
    public Task Status_IfWithoutBranchAndLoopEnd()
        => Eq("if false; then :; fi; echo \"if rc=$?\"\n"
            + "while false; do :; done; echo \"while rc=$?\"\n"
            + "i=0; while [ $i -lt 1 ]; do i=1; false; done; echo \"body rc=$?\"");

    [SkippableFact]
    public Task Chain_KeywordForm_KeepsConditionOutput()
        => Eq("printf 'a\\nb\\n' > f.txt; f(){ grep a f.txt || return 1; echo found; }; f\n"
            + "for i in 1 2; do echo \"it $i\" && continue; echo never; done");

    [SkippableFact]
    public Task Chain_SeesReturnAndExitStatus()
        => Eq("g(){ return 3; }\n"
            + "g || echo \"fb rc=$?\"\n"
            + "g; echo \"g rc=$?\"\n"
            + "g && echo nope || echo \"and-or rc=$?\"\n"
            + "(exit 4) || echo \"sub fb\"\n"
            + "(exit 0) && echo \"sub ok\"\n"
            + "h(){ return 0; }; h && echo \"h ok\"");

    [SkippableFact]
    public Task DeclarationBuiltins_HaveTheirOwnStatus()
        => Eq("f2(){ local x=$(false); echo \"local rc=$?\"; }; f2\n"
            + "export y=$(false); echo \"export rc=$?\"\n"
            + "declare z=$(echo hi); echo \"declare [$z] rc=$?\"\n"
            + "declare w=$(false); echo \"declare rc=$?\"\n"
            + "v=$(false); echo \"plain assign rc=$?\"\n"
            + "readonly r=$(false); echo \"readonly rc=$?\"");

    // `declare`/`readonly` wrote PowerShell globals no bash read ever reached: every value was "".
    [SkippableFact]
    public Task DeclarationBuiltins_AssignReadableValues()
        => Eq("declare x=hello; echo \"[$x]\"; readonly q=5; echo \"[$q]\"; declare -i n=2+3; echo \"[$n] $((n * 2))\"\n"
            + "typeset t=v; echo \"[$t]\"; declare d=\"$x-$q\"; echo \"[$d]\"; readonly e=$(echo ro); echo \"[$e]\"");
}
