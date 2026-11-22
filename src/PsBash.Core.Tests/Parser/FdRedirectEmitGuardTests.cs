using System.Collections.Generic;
using Xunit;
using PsBash.Core.Parser;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The shell's descriptor table (docs/specs/shell-fds-and-bash-source.md, "The descriptor table"): every redirection of a
/// numbered descriptor or a close reaches the table or the route stage, on every kind of command. A form the emitter
/// does not model fell back to an inline comment (<c>&lt;# ps-bash: fd merge 2&gt;&amp;3 has no PowerShell equivalent #&gt;</c>)
/// and the stream silently kept its default destination: <c>cmd 2&gt;&amp;3</c>, <c>3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c> and
/// <c>echo hi &gt;&amp;-</c> wrote to the terminal with status 0. A new command kind or redirect form that drops one fails
/// here for every pair at once.
/// <para>ps-bash-specific (the emitted shape); the behavior against bash is <c>FdTableDifferentialTests</c>.</para>
/// </summary>
public class FdRedirectEmitGuardTests
{
    /// <summary>Each command kind, <c>{0}</c> being its redirections.</summary>
    public static readonly string[] Kinds =
    {
        "echo hi {0}",
        "{{ echo hi; echo e >&2; }} {0}",
        "( echo hi ) {0}",
        "for i in 1; do echo $i; done {0}",
        "while false; do :; done {0}",
        "if true; then echo y; fi {0}",
        "case a in a) echo c;; esac {0}",
    };

    /// <summary>A redirect list and a text its emission must contain (the descriptor reaching the table or route).</summary>
    public static readonly (string Redirects, string[] AnyOf)[] Forms =
    {
        (">&3", ["-Out '3'"]),
        ("2>&3", ["-Err '3'"]),
        ("1>&3 2>&1", ["-Out '3' -Err '3'"]),
        ("2>&1 >&3", ["-Out '3'"]),
        ("3>&1 1>&2 2>&3", ["-Out 'err' -Err 'out'"]),
        (">&-", ["-Out 'close'"]),
        ("2>&-", ["2>$null"]),
        ("3>&-", ["Invoke-BashExecFd 3 '>&' -"]),
        ("3>f", ["Invoke-BashExecFd 3 '>' f"]),
        ("3>&1", ["Invoke-BashExecFd 3 '>&' 1"]),
        ("3>f 2>&3", ["Invoke-BashExecFd 3 '>' f", "-Err '3'"]),
    };

    public static IEnumerable<object[]> Pairs()
    {
        foreach (var kind in Kinds)
            foreach (var (redirects, _) in Forms)
                yield return [kind, redirects];
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Transpile_DescriptorRedirectOnAnyCommand_ReachesTheTable(string kind, string redirects)
    {
        string bash = string.Format(kind, redirects);
        string ps = PsEmitter.Transpile(bash) ?? "";
        Assert.DoesNotContain("<# ps-bash:", ps);
        foreach (var (form, anyOf) in Forms)
        {
            if (form != redirects) continue;
            foreach (var needle in anyOf)
                Assert.True(ps.Contains(needle), $"`{bash}` emitted without `{needle}`:\n{ps}");
        }
    }

    // An exec inside a function, loop, if, group, eval or sourced file outlives the list it is in: it must reach the
    // table at run time, not become `{ rest; } >f` (which ended at the list's end: `fx() { exec >log; }; fx; echo x`
    // printed x on the terminal).
    [Theory]
    [InlineData("fx() { exec >log1; echo in; }; fx; echo after")]
    [InlineData("if true; then exec >log1; echo in; fi; echo after")]
    [InlineData("for i in 1; do exec >log1; echo in; done; echo after")]
    [InlineData("{ exec >log1; echo in; }; echo after")]
    [InlineData("while true; do exec 2>log1; echo in; break; done; echo after")]
    public void Transpile_ExecOfAStandardDescriptorInsideACompound_GoesToTheTable(string bash)
    {
        string ps = PsEmitter.Transpile(bash) ?? "";
        Assert.Contains("Invoke-BashExecFd ", ps);
        Assert.DoesNotContain("Invoke-BashRedirect -Path log1", ps);
    }
}
