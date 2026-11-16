using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Compound-command stdin: a compound used as a pipe stage (or given <c>&lt; file</c> / <c>&lt;&lt;&lt;</c>)
/// puts its input in one shared queue, and every stdin-reading command inside is fed from it.
/// </summary>
public class CompoundStdinEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("printf 'b\\na\\n' | { sort; }")]
    [InlineData("printf 'x\\n' | ( cat )")]
    [InlineData("echo a | if true; then sort; fi")]
    [InlineData("echo a | for i in 1; do cat; done")]
    [InlineData("echo a | case x in x) wc -l;; esac")]
    public void Transpile_CompoundPipeStage_OpensStdinScopeAndFeedsReader(string bash)
    {
        var ps = T(bash);

        Assert.Contains("Queue[object]", ps);                       // scope opened
        Assert.Contains("in $input", ps);                           // pipe drained into it
        Assert.Contains(PsBuild.StdinFeed + " | ", ps);             // reader fed from it
        Assert.Contains("finally { $global:__BashStdIn = $__psbash_stdin_prev }", ps); // parent restored
    }

    [Theory]
    [InlineData("{ sort; } < f")]
    [InlineData("( cat ) < f")]
    [InlineData("{ cat; } <<< hi")]
    [InlineData("if true; then sort; fi < f")]
    [InlineData("for i in 1; do cat; done < f")]
    public void Transpile_CompoundWithInputRedirect_OpensStdinScopeAndFeedsReader(string bash)
    {
        var ps = T(bash);

        Assert.Contains("Queue[object]", ps);
        Assert.Contains(PsBuild.StdinFeed + " | ", ps);
    }

    [Theory]
    [InlineData("{ sort; } <<EOF\nb\na\nEOF\n")]
    [InlineData("( cat ) <<EOF\nhi\nEOF\n")]
    [InlineData("( cat ) <<< hi")]
    [InlineData("while true; do cat; break; done <<EOF\nhi\nEOF\n")]
    public void Transpile_HeredocOrHereStringIntoCompound_FeedsItsStdin(string bash)
    {
        // REGRESSION. `( cat ) <<< hi` and `{ sort; } <<EOF` were parse errors
        // ("Unexpected token '<<<'" / "'<<'"): only brace groups took a here-string and no
        // compound took a here-document.
        var ps = T(bash);

        Assert.Contains("Queue[object]", ps);
        Assert.Contains(PsBuild.StdinFeed + " | ", ps);
    }

    [Fact]
    public void Transpile_EchoInsideCompoundStage_IsNotFed()
    {
        // echo ignores stdin: feeding it would consume what a later reader needs.
        var ps = T("echo a | { echo one; cat; }");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ps, System.Text.RegularExpressions.Regex.Escape(PsBuild.StdinFeed + " | ")));
        Assert.DoesNotContain(PsBuild.StdinFeed + " | Invoke-BashEcho", ps);
    }

    [Fact]
    public void Transpile_CommandWithFileOperandInsideCompound_IsNotFed()
    {
        var ps = T("echo a | { cat file.txt; }");

        Assert.DoesNotContain(PsBuild.StdinFeed, ps);
    }

    [Fact]
    public void Transpile_CompoundWithoutStdin_EmitsNoScopeAndNoFeed()
    {
        // No pipe and no redirect: the commands inside keep emitting exactly as before.
        var ps = T("{ sort; cat; }");

        Assert.DoesNotContain("__BashStdIn", ps);
    }

    [Fact]
    public void Transpile_PipeTargetsInsideCompound_ReadThePipeNotTheScope()
    {
        // `{ grep a | sort; }`: only the FIRST stage reads the compound's stdin.
        var ps = T("echo a | { grep a | sort; }");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ps, System.Text.RegularExpressions.Regex.Escape(PsBuild.StdinFeed)));
        Assert.Contains(PsBuild.StdinFeed + " | Invoke-BashGrep", ps);
    }

    [Fact]
    public void Transpile_NestedCompoundStage_SavesAndRestoresParentQueue()
    {
        var ps = T("echo a | { cat | { sort; }; }");

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ps, "Queue\\[object\\]").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ps, "finally \\{ \\$global:__BashStdIn = \\$__psbash_stdin_prev \\}").Count);
    }

    [Fact]
    public void Transpile_WhileReadInsideScope_ReadsTheSharedQueueNotInput()
    {
        var ps = T("{ while read l; do echo $l; done; } < f");

        // Each LINE is taken from the shared stdin cursor (the one `read` in a body would advance too), not from $input.
        Assert.Contains("while ($null -ne ($__psbash_rec0 = " + PsBuild.SharedStdinNextLine + "))", ps);
        Assert.DoesNotContain("$__psbash_rec0 in $input", ps);
    }

    [Fact]
    public void Transpile_WhileReadAsPipeStage_RunsPerRecordAsTheRecordsArrive()
    {
        var ps = T("printf 'a\\n' | while read l; do echo $l; done");

        // REGRESSION: the loop ran in the stage's end block over `$input` (the producer's COLLECTED output), so it
        // started only after the producer ended and `yes | while read …; break; done` never ended. Now each record
        // runs in a process block, behind the gate that stops the producer once the loop is done.
        Assert.Contains("| " + PsBuild.PipeGate + " | & { begin { $__psbash_wg0 = [PsBash.Cmdlets.BashPipeGate]::Claim()", ps);
        Assert.Contains("process { if ($__psbash_wg0.Done) { return }", ps);
        Assert.Contains(":__psbash_l0 foreach ($__psbash_rec0 in @($_))", ps);
        Assert.DoesNotContain("in $input", ps);
        Assert.DoesNotContain("__BashStdIn", ps);
    }

    [Fact]
    public void Transpile_WhileReadAsPipeStage_ARecordThatFinishesReopensTheGate()
    {
        var ps = T("printf 'a\\n' | while read l; do echo $l; done");

        // The gate closes per record and reopens only when the record's lines all ran: a `break`/`exit` skips the
        // reopen, so the producer is stopped.
        Assert.Contains("; $__psbash_wg0.Done = $false }", ps);
    }

    [Theory]
    [InlineData("while read l; do echo $l; done < f")]                 // not a pipe stage
    [InlineData("printf 'a\\n' | while read l; do echo $l; done < f")] // its own redirect replaces the pipe
    [InlineData("while read l; do echo $l; done | cat")]               // the first stage reads the shell's stdin
    [InlineData("printf 'a\\n' | until read l; do echo $l; done")]     // not a read loop
    [InlineData("printf 'a\\n' | while true; do cat; break; done")]
    [InlineData("printf 'a\\n' | { while read l; do echo $l; done; }")]
    public void Transpile_NotAPlainReadLoopStage_GetsNoGate(string bash)
    {
        Assert.DoesNotContain(PsBuild.PipeGate, T(bash));
    }

    [Fact]
    public void Transpile_WhileReadAsPipeStage_UnderPipefail_GateStaysOutsideTheStageWrapper()
    {
        var ps = T("set -o pipefail\nprintf 'a\\n' | while read l; do echo $l; done");

        // The gate stops ITS pipeline's upstream: inside the per-stage steppable pipeline it would stop nothing.
        Assert.Contains(PsBuild.PipeGate + " | Invoke-BashPipeStage", ps);
    }

    [Theory]
    [InlineData("for i in 1 2; do echo x | { cat; break; }; done")]
    [InlineData("for i in 1 2; do printf 'a\\n' | while read l; do break 2; done; done")]
    [InlineData("while true; do printf 'a\\n' | while read l; do continue 2; done; done")]
    public void Transpile_LoopControlInAPipeStage_NeverReachesALoopOutsideTheStage(string bash)
    {
        // REGRESSION: a pipe stage is a subshell, which starts with no loop. The stage's `break` named the enclosing
        // loop's label, unwound the stage's pipeline and ended that loop (bash: a warning, or only the stage's own loop).
        var ps = T(bash);
        Assert.DoesNotContain("break __psbash_l0", ps);
        Assert.DoesNotContain("continue __psbash_l0", ps);
        Assert.DoesNotContain("continue __psbash_lc0", ps);
    }

    [Fact]
    public void Transpile_WhileReadAsPipeStage_UnderLastpipe_KeepsTheCollectedLoop()
    {
        var ps = T("shopt -s lastpipe\nprintf 'a\\n' | while read l; do x=$l; done");

        Assert.DoesNotContain(PsBuild.PipeGate, ps);
    }

    [Fact]
    public void Transpile_NativeInsideCompound_GetsTheScopeAsItsProcessStdin()
    {
        var ps = T("echo a | { somenativetool -x; }");

        // Enter-BashNativeStdin resolves the name at run time (only an application gets the pipe).
        Assert.Contains(PsBuild.NativeStdinScope("somenativetool", "somenativetool -x"), ps);
    }

    [Fact]
    public void Transpile_NativeInsideCompound_NeverProbesTheStdinBeforeTheProgramStarts()
    {
        // REGRESSION (v0.11.0): `if ($global:__BashStdIn.Count -gt 0 …) { feed | prog }` blocked until a record
        // arrived or stdin ended, so under a launcher whose stdin is open and silent (an agent shell tool) every
        // native command hung — `git --version` included.
        var ps = T("echo a | { git --version; }");

        Assert.DoesNotContain("Count -gt 0 -and", ps);
        Assert.DoesNotContain("| git --version", ps);
        Assert.Contains("Enter-BashNativeStdin 'git'", ps);
    }

    // ---- `< /dev/null` --------------------------------------------------------------------------------

    [Theory]
    [InlineData("read x < /dev/null")]
    [InlineData("cat < /dev/null")]
    [InlineData("somenativetool < /dev/null")]
    [InlineData("eval 'cat' < /dev/null")]
    public void Transpile_DevNullInput_RunsTheCommandInAnEmptyStdinScope(string bash)
    {
        // REGRESSION: `< /dev/null` was DROPPED, so the command read whatever stdin surrounded it — under the
        // launcher's forwarded stdin, the live pipe (`read x </dev/null` read a line).
        var ps = T(bash);

        Assert.StartsWith(". { $__psbash_nullin_prev1 = ", ps);
        Assert.Contains("$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new()", ps);
        Assert.EndsWith("finally { $global:__BashStdIn = $__psbash_nullin_prev1 } }", ps);
    }

    [Fact]
    public void Transpile_DevNullInputOnANative_HandsItAnEmptyPipe()
    {
        var ps = T("somenativetool -x < /dev/null");

        Assert.Contains(PsBuild.NativeStdinScope("somenativetool", "somenativetool -x"), ps);
    }

    [Fact]
    public void Transpile_EvalWithDevNullInput_InnerNativeSeesTheEmptyScope()
    {
        // The Claude Code Bash-tool prologue shape: `eval '<cmd>' < /dev/null`.
        var ps = PsEmitter.TranspileWithLauncherStdin("eval 'git status' < /dev/null", TranspileContext.Default)!;

        var scope = ps.IndexOf("Queue[object]]::new()", StringComparison.Ordinal);
        var native = ps.IndexOf("Enter-BashNativeStdin 'git'", StringComparison.Ordinal);
        Assert.True(scope >= 0 && native > scope, ps);
    }

    // ---- redirects on statement-list builtins -------------------------------------------------------------

    [Theory]
    [InlineData("eval 'echo hi' > out.txt", " | Invoke-BashRedirect -Path out.txt")]
    [InlineData("eval 'echo hi' 2>/dev/null", " 2>$null")]
    [InlineData("eval \"echo $x\" > out.txt", " | Invoke-BashRedirect -Path out.txt")]
    [InlineData("source ./rc 2>/dev/null", " 2>$null")]
    [InlineData(". ./rc >/dev/null 2>&1", " >$null 2>&1")]
    [InlineData("cd /tmp >/dev/null", " >$null")]
    [InlineData("cd nosuch 2>/dev/null", " 2>$null")]
    [InlineData("set -e 2>/dev/null", " 2>$null")]
    public void Transpile_StatementListBuiltin_KeepsItsRedirects(string bash, string tail)
    {
        // REGRESSION: cd / eval / source / read / readonly / set emit their own statement text and the redirect
        // tail was silently dropped (`eval 'echo hi' > f` printed hi and created no file).
        var ps = T(bash);

        Assert.StartsWith(". { ", ps);
        Assert.Contains(" }" + tail, ps);
    }

    [Fact]
    public void Transpile_StatementListBuiltin_WithoutRedirects_IsUnchanged()
    {
        Assert.DoesNotContain(". {", T("eval 'echo hi'"));
        Assert.DoesNotContain(". {", T("cd /tmp"));
    }

    // ---- declare / typeset print forms ----------------------------------------------------------------

    [Theory]
    [InlineData("declare -f f >/dev/null 2>&1", "Invoke-BashDeclare '-f' f >$null 2>&1")]
    [InlineData("typeset -f rg >/dev/null 2>&1", "Invoke-BashDeclare '-f' rg >$null 2>&1")]
    [InlineData("declare -F", "Invoke-BashDeclare '-F'")]
    [InlineData("declare -p x", "Invoke-BashDeclare '-p' x")]
    [InlineData("declare -pf f", "Invoke-BashDeclare '-pf' f")]
    public void Transpile_DeclarePrintForms_GoToInvokeBashDeclareWithTheirRedirects(string bash, string expected)
    {
        // REGRESSION: the print forms were rewritten to `Invoke-BashType <args>` with the redirects dropped;
        // `type` rejects -f ("bash: type: -f: not found"), and the stock probe
        // `typeset -f rg >/dev/null 2>&1` printed that on every agent shell call.
        Assert.Equal(expected, T(bash));
    }

    [Theory]
    [InlineData("cat", new string[0], true)]
    [InlineData("cat", new[] { "-n" }, true)]
    [InlineData("cat", new[] { "f.txt" }, false)]
    [InlineData("cat", new[] { "-" }, true)]
    [InlineData("cat", new[] { "f.txt", "-" }, true)]
    [InlineData("head", new[] { "-n", "2" }, true)]
    [InlineData("head", new[] { "-n2" }, true)]
    [InlineData("head", new[] { "-n", "2", "f" }, false)]
    [InlineData("sort", new[] { "-k", "2", "-r" }, true)]
    [InlineData("sort", new[] { "-t", ",", "-k2" }, true)]
    [InlineData("sort", new[] { "-o", "out" }, true)]
    [InlineData("sort", new[] { "a", "b" }, false)]
    [InlineData("wc", new[] { "-l" }, true)]
    [InlineData("grep", new[] { "pat" }, true)]
    [InlineData("grep", new[] { "-v", "pat" }, true)]
    [InlineData("grep", new[] { "pat", "f" }, false)]
    [InlineData("grep", new[] { "-e", "a", "-e", "b" }, true)]
    [InlineData("grep", new[] { "-e", "a", "f" }, false)]
    [InlineData("sed", new[] { "-n", "1p" }, true)]
    [InlineData("sed", new[] { "s/a/b/", "f" }, false)]
    [InlineData("sed", new[] { "-e", "s/a/b/" }, true)]
    [InlineData("awk", new[] { "-F:", "{print $1}" }, true)]
    [InlineData("awk", new[] { "-F", ":", "{print $1}", "f" }, false)]
    [InlineData("cut", new[] { "-d:", "-f2" }, true)]
    [InlineData("cut", new[] { "-d", ",", "-f", "1" }, true)]
    [InlineData("tr", new[] { "a", "b" }, true)]
    [InlineData("tee", new[] { "out.txt" }, true)]
    [InlineData("xargs", new[] { "echo" }, true)]
    [InlineData("echo", new string[0], false)]
    [InlineData("ls", new string[0], false)]
    [InlineData("read", new[] { "x" }, false)]
    [InlineData("mytool", new[] { "--flag" }, true)]
    // bash: no script operand = the program is stdin (`f() { $SH --rcfile /dev/null "$@"; }; echo cmds | f -i`).
    [InlineData("bash", new[] { "--rcfile", "/dev/null", "-i" }, true)]
    [InlineData("bash", new[] { "-o", "pipefail" }, true)]
    [InlineData("bash", new[] { "-c", "echo hi" }, false)]
    [InlineData("bash", new[] { "s.sh" }, false)]
    public void ShouldFeed_EstimatesWhetherTheCommandReadsStdin(string name, string[] args, bool expected)
    {
        Assert.Equal(expected, StdinReaders.ShouldFeed(name, args));
    }

    /// <summary>
    /// `egrep` / `fgrep` are grep (`grep -E` / `grep -F`). Unknown to the table they were native programs, fed only
    /// when one resolves on PATH, so `f() { egrep -o x; }; … | f` read nothing on Windows.
    /// </summary>
    [Theory]
    [InlineData("egrep", new[] { "-o", "pat" }, true)]
    [InlineData("fgrep", new[] { "pat", "f" }, false)]
    public void ShouldFeed_EgrepAndFgrepAreGrep(string name, string[] args, bool expected)
    {
        Assert.Equal(expected, StdinReaders.ShouldFeed(name, args));
        Assert.Contains("__BashStdIn", PsEmitter.Transpile("f() { " + name + " -o pat; }")!);
    }

    /// <summary>
    /// `i-shell() { $SH --rcfile /dev/null "$@"; }; echo cmds | i-shell -i` (oils history-expand): a run-time command
    /// name with a splatted operand is resolved inside `& { ... }`. It was never fed, and its function took no stdin
    /// scope, so the nested shell read nothing.
    /// </summary>
    [Fact]
    public void DynamicCommandWithSplat_InAFunction_IsFed()
    {
        var ps = PsEmitter.Transpile("f() { $SH --rcfile /dev/null \"$@\"; }")!;
        Assert.Contains("Resolve-BashCommand -Fed -Words", ps);
        Assert.Contains("$MyInvocation.ExpectingInput", ps);
        Assert.DoesNotContain("-Fed", PsEmitter.Transpile("$SH --rcfile /dev/null \"$@\"")!);
    }

    [Fact]
    public void ShouldFeed_NonLiteralOperandCountsAsAFile()
    {
        Assert.False(StdinReaders.ShouldFeed("cat", new string?[] { null }));
        Assert.True(StdinReaders.ShouldFeed("grep", new string?[] { null }));   // the pattern
    }
}
