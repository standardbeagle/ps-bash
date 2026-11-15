using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Expansion errors that discard the rest of the shell's input line (bash jump_to_top_level(DISCARD)) and nothing
/// more: a bad substitution, an invalid indirect name. The commands after the error on its line are dropped, even
/// from inside a function or loop it was called from, and the shell reads the next line, a <c>bash -c</c> string as
/// well as a script file. In an interactive shell an unbound variable under set -u does the same. Also: a backtick
/// body that does not parse is an error when it runs, not a syntax error of the whole script, and a quoted
/// <c>local "NAME=v"</c> is a local.
/// </summary>
public class LineDiscardTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    /// <summary><paramref name="inner"/> written to a file and run by <paramref name="runner"/> (which names it as
    /// s.sh), its error stream discarded: the error texts name the script and line differently.</summary>
    private static Task EqRun(string inner, string runner) =>
        Eq("d=$(mktemp -d); cd \"$d\"; cat > s.sh <<'PSBASH_EOF'\n" + inner + "\nPSBASH_EOF\n"
            + runner + " 2>/dev/null; echo \"rc=$?\"; cd /; rm -rf \"$d\"");

    private const string BadSubstitutionLines =
        "echo a ${x!y} b; echo same\necho next=$?\nf() { echo f1; echo ${x!y}; echo f2; }\ng() { f; echo g2; }\ng; echo same2\n"
        + "for i in 1 2; do echo $i; echo ${x!y}; done; echo same3\necho end=$?";

    [SkippableFact]
    public Task BadSubstitution_DashC_DiscardsTheLine_AndRunsTheNext()
        => EqRun(BadSubstitutionLines, "bash -c \"$(cat s.sh)\"");

    [SkippableFact]
    public Task BadSubstitution_ScriptFile_DiscardsTheLineFromInsideAFunctionOrLoop()
        => EqRun(BadSubstitutionLines, "bash s.sh");

    [SkippableFact]
    public Task InvalidIndirectName_DiscardsTheLineThatCalledTheFunction()
        => EqRun("check() { r=\"${!1}\"; echo \"in $1\"; }\nx=y\ncheck x; echo same=$?\ncheck 'a[0'; echo same2\n"
            + "echo next=$?\ncheck '!x'; echo same3\ncheck 'x:-foo'\necho done", "bash s.sh");

    [SkippableFact]
    public Task InvalidIndirectName_DashC_GoesOnWithTheNextLine()
        => EqRun("unset r; echo ${!r}; echo same\necho next=$?\nb='bad name'; echo ${!b}\necho st=$?\n"
            + "echo \"a$(echo ${!r})b\"; echo same4\nx=$(echo ${x!y}); echo \"x=[$x] $?\"",
            "bash -c \"$(cat s.sh)\"");

    [SkippableFact]
    public Task Discard_InEvalOrSourcedText_EndsOnlyItsLine()
        => EqRun("eval 'echo ${x!y}; echo in\necho eline2'; echo after-eval=$?\n"
            + "printf 'echo ${x!y}; echo in\\necho sline2\\n' > src.sh; . ./src.sh; echo after-src=$?", "bash s.sh");

    [SkippableFact]
    public Task Interactive_UnboundVariable_DiscardsOnlyTheLine()
        => EqRun("set -u; echo before; echo $x; echo after\necho line2=$?\nf() { echo ${q:?boom}; echo inf; }; f; echo s2\necho end=$?",
            "bash -i -c \"$(cat s.sh)\"");

    [SkippableFact]
    public Task NonInteractive_UnboundVariable_StillEndsTheShell()
        => EqRun("set -u; echo before; echo ${!r}; echo $x; echo after\necho line2", "bash -c \"$(cat s.sh)\"");

    [SkippableFact]
    public Task BacktickBodySyntaxError_IsARunTimeError()
        => EqRun("echo `echo \\\\\\\\\"`\necho st=$?\nx=`echo \\\\\\\\\"`; echo \"st=$? x=[$x]\"\necho \"a`echo \\\\\\\\\"`b\"",
            "bash s.sh");

    [SkippableFact]
    public Task QuotedLocalAssignment_IsALocal()
        => Eq("f() { local \"lv=inner\" 'lq=$x'; echo \"in=$lv $lq\"; }\nlv=outer; lq=keep; f; echo \"out=$lv $lq\"\n"
            + "g() { local \"lw=a b\" \"n\"=1; echo \"$lw $n\"; }\nunset lw n; g; echo \"lw=${lw-unset} n=${n-unset}\"\n"
            + "h() { export \"ex=v\"; }; h; echo \"ex=$ex\"");
}
