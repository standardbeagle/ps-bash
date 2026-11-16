using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Which background jobs stream their output live (<c>Invoke-BashBackground -Live</c>, docs/specs/background-jobs.md
/// section 12). Hand-written: the -Live flag is ps-bash surface with no bash equivalent. A job is live only when its
/// output is the shell's own; anything under a redirect, pipe, capture, or function keeps the collected-at-wait path,
/// because a live write to the session would bypass that redirect.
/// </summary>
public class LiveJobEmitterTests
{
    private static string Emit(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("sleep 1 &")]
    [InlineData("{ echo a; sleep 1; echo b; } & wait")]
    [InlineData("if true; then echo a & fi")]
    [InlineData("for i in 1 2; do echo $i & done; wait")]
    [InlineData("while false; do echo a & done")]
    [InlineData("case x in x) echo a & ;; esac")]
    [InlineData("true && echo a &")]
    [InlineData("( echo a & wait )")]
    [InlineData("{ echo a & } < /dev/null")]
    public void Transpile_TerminalBoundJob_IsLive(string bash)
        => Assert.Contains("Invoke-BashBackground -Live", Emit(bash));

    [Theory]
    [InlineData("{ echo a & wait; } > out.txt")]
    [InlineData("{ echo a & wait; } 2>&1 | cat")]
    [InlineData("x=$(echo a & wait)")]
    [InlineData("cat <(echo a &)")]
    [InlineData("f() { echo a & }; f")]
    [InlineData("for i in 1; do echo a & done > out.txt")]
    [InlineData("if true; then echo a & fi 2> err.txt")]
    [InlineData("( echo a & ) >> out.txt")]
    [InlineData("exec > out.txt; echo a &")]
    public void Transpile_RedirectedOrCapturedJob_IsNotLive(string bash)
    {
        var ps = Emit(bash);
        Assert.Contains("Invoke-BashBackground", ps);
        Assert.DoesNotContain("-Live", ps);
    }

    /// <summary>The job's OWN redirect (<c>cmd &gt; f &amp;</c>) is applied inside the job body, so the job may be live: its
    /// stdout goes to the file, and only what it still writes to the shell's streams (here nothing) reaches the session.</summary>
    [Fact]
    public void Transpile_JobWithItsOwnRedirect_IsLiveAndRedirectsInside()
    {
        var ps = Emit("echo a > out.txt &");
        Assert.Contains("Invoke-BashBackground -Live", ps);
        Assert.Contains("out.txt", ps);
    }

    [Fact]
    public void Transpile_IsolatedJob_CarriesBothFlags()
        => Assert.Contains("Invoke-BashBackground -Isolated -Live", Emit("cd /tmp &"));

    /// <summary>eval / source / trap text runs inside a caller whose output may be redirected: never live.</summary>
    [Fact]
    public void Transpile_EvalContext_IsNotLive()
    {
        var ps = PsEmitter.Transpile("echo a &", TranspileContext.Eval)!;
        Assert.Contains("Invoke-BashBackground", ps);
        Assert.DoesNotContain("-Live", ps);
    }

    /// <summary>Two identical jobs in different contexts: identity, not structural equality, decides.</summary>
    [Fact]
    public void Transpile_SameJobTextInTwoContexts_OnlyTheTerminalOneIsLive()
    {
        var ps = Emit("sleep 1 &\n{ sleep 1 & } > out.txt");
        int first = ps.IndexOf("Invoke-BashBackground", StringComparison.Ordinal);
        int second = ps.IndexOf("Invoke-BashBackground", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, ps);
        Assert.StartsWith("Invoke-BashBackground -Live", ps[first..]);
        Assert.DoesNotContain("-Live", ps[second..]);
    }
}
