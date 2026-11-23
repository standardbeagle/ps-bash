using System.Collections.Generic;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// A redirect list that uses a descriptor it did not open itself, and what its emission must match: the descriptor
    /// checked where the list reaches it, its report going where fd 2 points at that point (<c>dN</c> step of the
    /// <c>-OpenPlan</c>, <c>Test-BashFd N</c> before a <c>&lt;&amp;N</c> stdin, or the table open gating the command).
    /// </summary>
    public static readonly (string Redirects, string[] Matches, string[] NoneOf)[] DescriptorUses =
    {
        (">&5", [@"-OpenPlan '[^']*\bd5(?![:\w])"], []),
        ("2>&5", [@"-OpenPlan '[^']*\bd5(?![:\w])"], []),
        ("2>/dev/null >&5", [@"-OpenPlan '[^']*\bd5:null\b"], []),
        (">&5 2>/dev/null", [@"-OpenPlan '[^']*\bd5(?![:\w])"], [@"\bd5:"]),
        ("2>&1 >&5", [@"-OpenPlan '[^']*\bd5:out\b"], []),
        ("3>&- >&3", [@"-OpenPlan '[^']*\bd3(?![:\w])"], []),
        ("<&5", [@"Test-BashFd 5;"], [@"0<&5"]),
        ("2>/dev/null <&5", [@"Test-BashFd 5;", @"2>\$null"], [@"0<&5"]),
        ("3>&5", [@"Invoke-BashExecFd 3 '>&' 5", @"\$__psbash_fdOk"], []),
        // The open's report is held and written through the redirects before it (`2>/dev/null` discards it).
        ("2>/dev/null 3>&5", [@"\$__psbash_fdErr = @\(Invoke-BashExecFd 3 '>&' 5 2>&1\)", @"\}\s*2>\$null"], []),
        ("3>/nonexist/f 4>&5", [@"Invoke-BashExecFd 3 '>' /nonexist/f; \$__psbash_fdOk", @"if \(\$__psbash_fdOk\) \{ Invoke-BashExecFd 4 '>&' 5"], []),
        // `3>$v` with v='a b' is one word or bash's ambiguous redirect, as for stdout.
        ("3>$v", [@"Invoke-BashExecFd 3 '>' \$\(& \{ \$__rt = @\(.*RedirectTarget\]::Ambiguous\('\$v'\)"], []),
        // Opened by this list: nothing to check.
        ("3>f >&3", [@"Invoke-BashExecFd 3 '>' f"], [@"\bd3\b"]),
    };

    public static IEnumerable<object[]> DescriptorUsePairs()
    {
        foreach (var kind in Kinds)
            foreach (var (redirects, _, _) in DescriptorUses)
                yield return [kind, redirects];
    }

    // `echo y 2>/dev/null >&5` with fd 5 closed: bash applies the list left to right, so "5: Bad file descriptor" goes
    // where fd 2 points at that moment (nowhere here; to the terminal for `>&5 2>/dev/null`), status 1, the command not
    // run. The route stage reported it on stderr whatever came before it, and a closed `<&5` stdin ran the command with
    // an empty stdin (a compound's `0<&5` was PowerShell's background `&`). Behavior: FdTableDifferentialTests.
    [Theory]
    [MemberData(nameof(DescriptorUsePairs))]
    public void Transpile_DescriptorTheListDidNotOpen_IsCheckedWhereTheListReachesIt(string kind, string redirects)
    {
        string bash = string.Format(kind, redirects);
        string ps = PsEmitter.Transpile(bash) ?? "";
        foreach (var (form, matches, noneOf) in DescriptorUses)
        {
            if (form != redirects) continue;
            foreach (var pattern in matches)
                Assert.True(Regex.IsMatch(ps, pattern), $"`{bash}` emitted without /{pattern}/:\n{ps}");
            foreach (var pattern in noneOf)
                Assert.False(Regex.IsMatch(ps, pattern), $"`{bash}` emitted /{pattern}/:\n{ps}");
        }
    }

    // A command that writes nothing (`:`, `true`, `break`) makes its redirections too: its descriptor copies were skipped,
    // so `if { true >&7; } 2>/dev/null; then` took 7 for open (oils redirect.spec "Redirect to file descriptor that's not
    // open"). A redirection after a failed one is not made.
    [Theory]
    [InlineData(": >&5", new[] { @"Test-BashFd 5; if \(\$global:LASTEXITCODE -ne 0\)" }, new string[0])]
    [InlineData("true 2>&5", new[] { @"Test-BashFd 5;" }, new string[0])]
    [InlineData(": <&5", new[] { @"Test-BashFd 5;" }, new[] { @"\|\s*\$\(" })]   // an expression after `|` does not parse
    [InlineData("true 2>/dev/null >&5", new[] { @"Test-BashFd 5;", @"\}\s*2>\$null" }, new string[0])]
    [InlineData(": >&5 >f", new[] { @"Test-BashFd 5;.*if \(\$global:LASTEXITCODE -eq 0\) \{ @\(\) \| Invoke-BashRedirect -Path f" }, new string[0])]
    [InlineData(": 3>f >&3", new[] { @"Invoke-BashExecFd 3 '>' f;.*Test-BashFd 3;" }, new string[0])]   // opened first: passes
    [InlineData(": 3>&- >&3", new[] { @"Test-BashFd 3;" }, new string[0])]
    public void Transpile_DescriptorCopyOnACommandThatWritesNothing_IsChecked(string bash, string[] matches, string[] noneOf)
    {
        string ps = PsEmitter.Transpile(bash) ?? "";
        foreach (var pattern in matches)
            Assert.True(Regex.IsMatch(ps, pattern), $"`{bash}` emitted without /{pattern}/:\n{ps}");
        foreach (var pattern in noneOf)
            Assert.False(Regex.IsMatch(ps, pattern), $"`{bash}` emitted /{pattern}/:\n{ps}");
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
