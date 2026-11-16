using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// exit / break / continue / errexit scope, as a matrix through the emitter: every bash subshell boundary
/// (<c>( )</c>, <c>$( )</c>, <c>&lt;( )</c>, a pipe stage, eval/source text run in one) must stop them.
/// PowerShell's <c>exit</c> ends the whole script and its <c>break</c>/<c>continue</c> are dynamically scoped - they
/// cross script blocks to the caller's loop - so the emitter has to (a) put an <c>exit</c> inside a subshell under
/// the scope guard (<see cref="PsBuild.ShellStateGuard"/>, whose catch of BashSubshellExitException ends only that
/// subshell) and (b) decide a loop-control keyword with no loop of its own inside a subshell statically: bash's
/// "only meaningful in a loop" warning in <c>( )</c> / a compound stage, the end of the subshell in <c>$( )</c> /
/// <c>&lt;( )</c> / a simple first stage. A bare keyword there left the OUTER loop (94 of 357 oracle cells failed).
/// The Differential twins (ScopeControlDifferentialTests) check the same cells against bash.
/// </summary>
public class ScopeControlMatrixTests
{
    private const string GuardOpen = "[PsBash.Cmdlets.BashShellState]::Save(";
    private const string GuardCatch = "catch [PsBash.Cmdlets.BashSubshellExitException]";
    private const string Warning = "only meaningful in a";

    // A bare PowerShell loop-control statement (not the word inside the warning text, not a label).
    private static readonly Regex BareLoopControl = new(@"(?<![\w'`:$-])(?:break|continue)(?:\s+:?\w+)?\s*(?=[;}\r\n]|$)", RegexOptions.Compiled);

    private static string T(string bash) => PsEmitter.Transpile(bash) ?? throw new InvalidOperationException("no output");

    /// <summary>How many scope guards are open at <paramref name="index"/>: opened before it and not yet closed by their catch.</summary>
    private static int OpenGuardsAt(string ps, int index)
    {
        string before = ps[..index];
        return Count(before, GuardOpen) - Count(before, GuardCatch);
    }

    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ---- exit: inside a subshell it is under a guard; at the top it is not ----

    public static TheoryData<string> ExitInSubshellCells => new()
    {
        "( exit 3 ); echo $?",
        "v=$( exit 3 ); echo $?",
        "cat <( exit 3 ); echo done",
        "{ exit 3; } | cat; echo $?",
        "echo a | { cat; exit 3; }; echo $?",
        "eval 'exit 3' | cat; echo $?",
        "( eval 'exit 3' ); echo $?",
        "v=$( eval 'exit 3' ); echo $?",
        "( ( exit 3 ); echo in ); echo $?",
        "for i in 1 2; do ( exit 3 ); done; echo $?",
        "while true; do v=$( exit 3 ); break; done; echo $?",
    };

    [Fact]
    public void Transpile_ExitTrapOfASubshell_RunsUnderACatchThatKeepsItsStatus()
    {
        // The trap text is transpiled when it fires; an `exit N` there throws, and the runner keeps N as the
        // subshell's status (bare `exit` keeps the pre-trap status) instead of ending the script.
        string ps = T("( trap 'exit 3' EXIT; echo sub ); echo $?");
        int at = ps.IndexOf("& $global:__BashTrapEXIT", StringComparison.Ordinal);
        Assert.True(at >= 0, ps);
        Assert.True(OpenGuardsAt(ps, at) > 0, ps);
        Assert.Contains("& $global:__BashTrapEXIT } } " + GuardCatch + " { $__psbash_xs = $_.Exception.Status }", ps);
        Assert.Contains("$global:__BashPreTrapStatus = $__psbash_xs; " + PsBuild.LoopModeSet(1), ps);
    }

    [Theory]
    [MemberData(nameof(ExitInSubshellCells))]
    public void Transpile_ExitInsideASubshell_IsUnderTheScopeGuard(string bash)
    {
        string ps = T(bash);
        // The three shapes of `exit 3`: lexically in the subshell's own script block (`return`), decided at run time
        // (PsBuild.DynamicExit / SubshellExitThrow) and the script-ending `exit`.
        var exits = Regex.Matches(ps, @"throw \[PsBash\.Cmdlets\.BashSubshellExitException\]|LASTEXITCODE = 3; (?:return|exit)\b|exit \$__psbash_xc");
        Assert.True(exits.Count > 0, $"`{bash}`: no exit found in:\n{ps}");
        foreach (Match m in exits)
            Assert.True(OpenGuardsAt(ps, m.Index) > 0, $"`{bash}`: the exit at {m.Index} would end the whole script:\n{ps}");
    }

    [Theory]
    [InlineData("exit 3")]
    [InlineData("echo a; exit 3")]
    [InlineData("for i in 1; do exit 3; done")]
    public void Transpile_ExitAtTheTop_IsNotUnderAGuard(string bash)
    {
        string ps = T(bash);
        int at = ps.IndexOf("LASTEXITCODE = 3; exit $global:LASTEXITCODE", StringComparison.Ordinal);
        Assert.True(at >= 0, ps);
        Assert.True(OpenGuardsAt(ps, at) == 0, ps);
    }

    [Fact]
    public void Transpile_SourceAsAFirstStage_RunsUnderTheScopeGuard()
    {
        // `source ./s.sh | cat`: the sourced text may `exit`; that ends only the stage.
        string ps = T("source ./s.sh | cat; echo after");
        int at = ps.IndexOf("Invoke-BashSource", StringComparison.Ordinal);
        Assert.True(at >= 0, ps);
        Assert.True(OpenGuardsAt(ps, at) > 0, ps);
    }

    // ---- break / continue with no loop of their own inside a subshell in a loop: decided statically ----

    public static TheoryData<string, string> LoopControlCells()
    {
        var data = new TheoryData<string, string>();
        foreach (var kw in new[] { "break", "continue" })
        {
            data.Add($"for i in 1 2; do ( {kw}; echo in ); echo out $i; done", "warn");
            data.Add($"for i in 1 2; do {{ {kw}; echo in; }} | cat; echo out $i; done", "warn");
            data.Add($"for i in 1 2; do echo x | {{ cat; {kw}; }}; echo out $i; done", "warn");
            data.Add($"for i in 1 2; do v=$( {kw}; echo in ); echo \"out $i $v\"; done", "end");
            data.Add($"for i in 1 2; do cat <( {kw}; echo in ); echo out $i; done", "end");
            data.Add($"while true; do ( ( {kw} ); echo in ); break; done", "warn");
            data.Add($"for i in 1 2; do eval '{kw}' | cat; echo out $i; done", "end");
            data.Add($"for i in 1 2; do ( eval '{kw}'; echo in ); echo out $i; done", "warn");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(LoopControlCells))]
    public void Transpile_LoopControlInASubshellInALoop_NeverReachesTheOuterLoop(string bash, string expected)
    {
        string ps = T(bash);
        // The emitter's own `break` that ends a `while true` (the cell's last statement) is outside every subshell.
        string inner = bash.StartsWith("while", StringComparison.Ordinal) ? ps[..ps.LastIndexOf("break", StringComparison.Ordinal)] : ps;
        Assert.False(BareLoopControl.IsMatch(inner), $"`{bash}`: a bare loop-control keyword crosses the subshell:\n{ps}");
        Assert.DoesNotContain(PsBuild.LoopModeVar + "v", inner); // decided here, not at run time
        if (expected == "warn")
            Assert.Contains(Warning, ps);
        else
            Assert.Contains(PsBuild.SubshellExitThrow("0"), ps);
    }

    [Theory]
    [InlineData("for i in 1 2; do break; done")]
    [InlineData("for i in 1 2; do continue; done")]
    [InlineData("for i in 1 2; do ( for j in 1; do break; done ); done")]
    [InlineData("for i in 1 2; do v=$( for j in 1; do continue; done ); done")]
    public void Transpile_LoopControlInItsOwnLoop_StaysAKeyword(string bash)
    {
        string ps = T(bash);
        Assert.True(BareLoopControl.IsMatch(ps), ps);
        Assert.DoesNotContain(Warning, ps);
    }

    [Fact]
    public void Transpile_LoopControlInAFunction_WarnsEvenWhenCalledFromALoop()
    {
        // bash resets the loop level in a function body: `f() { break; }; for ...; do f; done` warns.
        string ps = T("f() { break; }; for i in 1 2; do f; echo $i; done");
        Assert.Contains(Warning, ps);
    }
}
