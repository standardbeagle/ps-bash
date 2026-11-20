using Xunit;
using PsBash.Core.Parser;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The emitted shape of bash's shell diagnostics for redirects that cannot be opened
/// (docs/specs/error-handling.md "Diagnostic prefix", "Redirect open errors"). bash 5.2 (WSL) is the oracle for every
/// expectation; the end-to-end cases are Differential <c>RedirectOpenErrorDifferentialTests</c>, the stage's routing is
/// Cmdlets.Tests <c>RedirectOpenPlanTests</c>. These pin what the emitter hands the runtime.
/// </summary>
public class RedirectOpenErrorEmitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    // ── output redirects: open order and where each open error goes (-OpenPlan) ──────────────────

    // bash opens left to right; an open error is written where fd 2 points at that moment.
    [Theory]
    [InlineData("cmd 2>/dev/null >o", "'p:null'")]       // silent
    [InlineData("cmd 2>e >o", "'e p:e'")]                // into e, which is opened first
    [InlineData("cmd 2>&1 >o", "'p:out'")]               // down the pipe
    [InlineData("cmd 2>f1 2>f2", "'t0 e:t0'")]           // into f1
    [InlineData("cmd >>a 2>e >o", "'a0 e p:e'")]
    public void Transpile_RedirectsBeforeAnOpen_PlanItsOrderAndReport(string bash, string plan)
        => Assert.EndsWith(" -OpenPlan " + plan, T(bash));

    // The stage's own order (-Truncate, -Touch, -Path, -ErrorPath; reports on stderr) needs no plan: tails unchanged.
    [Theory]
    [InlineData("cmd >o")]
    [InlineData("cmd >a >b")]
    [InlineData("cmd >o 2>e")]
    [InlineData("cmd >o 2>/dev/null")]
    [InlineData("cmd >/dev/null 2>e")]
    [InlineData("cmd >o 2>&1")]
    [InlineData("cmd &>o")]
    public void Transpile_RedirectsInTheStagesOrder_WriteNoPlan(string bash)
        => Assert.DoesNotContain("-OpenPlan", T(bash));

    // ── input redirects: the report goes through the redirects written before the < ─────────────

    [Fact]
    public void Transpile_StderrDiscardedBeforeTheInput_ReportsThroughIt()
        // cat 2>/dev/null < nofile is silent; cat < nofile 2>/dev/null is not.
        => Assert.Contains(PsBuild.InputRedirectFailure(" 2>$null"), T("cat 2>/dev/null < nofile"));

    [Fact]
    public void Transpile_StderrDiscardedAfterTheInput_ReportsOnStderr()
        => Assert.Contains(PsBuild.InputRedirectFailure(null), T("cat < nofile 2>/dev/null"));

    [Fact]
    public void Transpile_CompoundInputAfterStderrRedirect_ReportsThroughIt()
        => Assert.Contains("& { " + PsBuild.ShellDiagnosticLine("$__psbash_ir + ': No such file or directory'") + " } 2>$null",
            T("while read l; do echo $l; done 2>/dev/null < nofile"));

    [Fact]
    public void Transpile_LoneFileRead_IsGuarded()
        // x=$(< nofile): bash reports it (status 1). It was a PowerShell Get-Content error with status 0.
        => Assert.Contains(PsBuild.InputRedirectFailure(null), T("x=$(< nofile)"));

    [Fact]
    public void Transpile_LoneFileReadWithItsOwnRedirect_KeepsReadingTheFile()
        // x=$(< nofile 2>/dev/null): the redirect applies; it was emitted as a command after a pipe (a parse error).
        => Assert.Contains("Get-Content", T("x=$(< nofile 2>/dev/null)"));

    // ── the line of the diagnostic ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("echo a\necho b > nodir/f", 2)]
    [InlineData("echo a\ncat < nofile", 2)]
    [InlineData("echo a\nx=$(< nofile)", 2)]
    [InlineData("echo a\n{ echo b; } > nodir/f", 2)]
    [InlineData("echo a\nwhile read l; do :; done < nofile", 2)]
    [InlineData("echo a\n( echo b ) > nodir/f", 2)]
    public void Transpile_StatementThatMayRaiseADiagnostic_RecordsItsLine(string bash, int line)
        => Assert.Contains(PsBuild.SetCallLine(line), T(bash));

    // A redirect that cannot fail to open costs nothing.
    [Theory]
    [InlineData("echo a\necho b 2>&1")]
    [InlineData("echo a\necho b >/dev/null")]
    [InlineData("echo a\necho b >&2")]
    [InlineData("echo a\ncat <<< x")]
    public void Transpile_RedirectThatCannotFail_RecordsNoLine(string bash)
        => Assert.DoesNotContain("SetLine(", T(bash));
}
