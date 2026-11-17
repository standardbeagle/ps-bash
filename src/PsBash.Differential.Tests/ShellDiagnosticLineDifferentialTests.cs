using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A shell diagnostic carries bash's source prefix (error.c <c>error_prolog</c>): <c>NAME: line N: </c>, NAME the
/// script or sourced file (<c>${BASH_SOURCE[0]}</c>) or else <c>$0</c>, N the line of the failing command. ps-bash wrote
/// <c>bash: x: command not found</c> with no line at all (oils sh-options-bash "xtrace" case). Lines: a simple command's
/// start line plus the newlines inside its quoted words (a backslash-newline is not counted), a function body's script
/// lines, an eval's text counted from the eval's line, a sourced file's own lines. Recorded from bash 5.2 (cassette).
/// </summary>
public class ShellDiagnosticLineDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";

    /// <summary>
    /// A function defined in the script reports its body's script line. (Not under <c>bash -c</c>: there bash names a
    /// function's diagnostics <c>environment:</c>, its <c>${BASH_SOURCE[0]}</c>, which ps-bash does not model.)
    /// </summary>
    private const string Function =
        "f() {\n" +
        "  nosuch_b\n" +
        "}\n" +
        "f\n";

    /// <summary>One missing command per line shape: multi-line words, continuations, static and run-time eval, `""`.</summary>
    private const string Body =
        "echo 'x\n" +
        "y'\n" +
        "nosuch_c\n" +
        "eval $'true\\nnosuch_d'\n" +
        "c=$'true\\ntrue\\nnosuch_e'; eval \"$c\"\n" +
        "nosuch_f \\\n" +
        "  arg\n" +
        "nosuch_g a \\\n" +
        "  b\n" +
        "nosuch_h 'p\n" +
        "q' 'r\n" +
        "s'\n" +
        "\"\" quoted-empty\n" +
        "echo \"rc=$?\"\n";

    [SkippableFact]
    public Task CommandNotFound_InAScriptFile_NamesTheScriptAndLine()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\necho one\nnosuch_a\n" + Function + Body + "EOF\n"
            + "bash s.sh 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    [SkippableFact]
    public Task CommandNotFound_UnderDashC_IsBashAndTheLine()
        => AssertOracle.EqualAsync(Work + "{\necho one\nnosuch_a\n" + Body + "} 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// An <c>if</c>/<c>elif</c>/<c>while</c>/<c>until</c> condition reports its own line, a loop's on every pass (after
    /// its body ran later lines), and a multi-line static <c>eval</c> counts its text from the line it is numbered.
    /// </summary>
    [SkippableFact]
    public Task CommandNotFound_InAConditionOrMultiLineEval_ReportsItsLine()
        => AssertOracle.EqualAsync(Work + "{\n"
            + "echo a\n"
            + "eval 'true\n"
            + "nosuch_x'\n"
            + "if nosuch_if; then :; fi\n"
            + "i=0\n"
            + "while [ $i -lt 2 ] && nosuch_wh; do :; done\n"
            + "until nosuch_un || [ $i -ge 2 ]; do\n"
            + "  i=$((i+1))\n"
            + "  echo body\n"
            + "done\n"
            + "if false; then :\n"
            + "elif nosuch_elif; then :; fi\n"
            + "} 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// After a run-time eval or a sourced file that reported later lines, the caller's next command on the same line
    /// reports the caller's line again, and a file sourced inside an eval keeps its own lines.
    /// </summary>
    [SkippableFact]
    public Task CommandNotFound_AfterAnEvalOrSourceOnTheSameLine_IsTheCallersLine()
        => AssertOracle.EqualAsync(Work
            + "printf '%s\\n' 'true' 'true' 'nosuch_t3' > t3.sh\n"
            + "{ c=$'true\\ntrue\\nnosuch_e'; eval \"$c\"; nosuch_1; . ./t3.sh; nosuch_2; eval '. ./t3.sh'; nosuch_3; } 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>A function's call line (BASH_LINENO) follows the same rule as a diagnostic's.</summary>
    [SkippableFact]
    public Task FunctionCallLine_ContinuedCall_IsTheLineOfItsSecondWord()
        => AssertOracle.EqualAsync(Work
            + "printf '%s\\n' 'f() { echo \"called from ${BASH_LINENO[0]}\"; }' 'f a \\' 'b' 'f \\' 'b' \"f 'x\" \"y'\" > s.sh\n"
            + "bash s.sh 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    [SkippableFact]
    public Task CommandNotFound_InASourcedFile_NamesThatFile()
        => AssertOracle.EqualAsync(Work
            + "printf '%s\\n' 'true' 'nosuch_t' > t.sh\n"
            + "printf '%s\\n' 'echo top' '. ./t.sh' 'nosuch_s' > s.sh\n"
            + "bash s.sh 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    [SkippableFact]
    public Task CommandNotFound_UnderDashCWithArg0_NamesArg0()
        => AssertOracle.EqualAsync(Work
            + "bash -c 'echo a\nnosuch_m' myname 2>&1; echo \"rc=$?\"" + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// A name looked up by <c>command</c>, known only at run time, or inside an and-or list, a pipeline or a loop
    /// reports its own line too (the statement is not a plain missing name, so the line is recorded on purpose).
    /// </summary>
    [SkippableFact]
    public Task CommandNotFound_ThroughABuiltinOrARunTimeName_ReportsTheLine()
        => AssertOracle.EqualAsync(Work + "{\n"
            + "echo start\n"
            + "command nosuch_cmd\n"
            + "n=nosuch_var; $n\n"
            + "true && nosuch_and\n"
            + "echo p | nosuch_pipe\n"
            + "for i in 1 2; do\n"
            + "  nosuch_loop\n"
            + "done\n"
            + "} 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));
}
