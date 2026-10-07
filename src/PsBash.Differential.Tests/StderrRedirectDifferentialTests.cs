using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for bash stderr: <c>&gt;&amp;2</c> output and the redirects that must apply
/// to it. stdout, stderr and the exit status are diffed separately.
///
/// Covered regressions:
///   - <c>&gt;&amp;2</c> wrote through <c>$Host.UI.WriteErrorLine</c>, which no redirection sees:
///     <c>f 2&gt;/dev/null</c>, <c>{ echo e &gt;&amp;2; } 2&gt;/dev/null</c> still printed, <c>2&gt;&amp;1</c>
///     did not merge it, <c>2&gt; f</c> did not capture it
///   - <c>&gt;&amp;2</c> ignored its position: <c>2&gt;/dev/null &gt;&amp;2</c> must discard,
///     <c>&gt;&amp;2 2&gt;/dev/null</c> must still show, <c>&gt;f &gt;&amp;2</c> must truncate f
///   - a <c>2&gt;&amp;1</c> merge left ErrorRecords that an ENCLOSING <c>2&gt; f</c> wrote to f
///     (<c>ls missing 2&gt;&amp;1</c> under an outer capture went to stderr)
///   - writing stderr is not a failure: <c>$?</c> 0, <c>||</c> not taken
/// </summary>
public class StderrRedirectDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    private const string F = "f() { echo f-out; echo f-err >&2; return 3; }; ";

    [SkippableFact]
    public Task Stderr_DupToStderr_GoesToStderr()
        => Eq("echo x >&2; echo y 1>&2; echo out");

    [SkippableFact]
    public Task Stderr_SilencedByEnclosingRedirect()
        => Eq(F + "{ echo e >&2; } 2>/dev/null; f 2>/dev/null; ( echo sub >&2 ) 2>/dev/null; echo \"rc=$?\"");

    [SkippableFact]
    public Task Stderr_MergedByEnclosing2To1()
        => Eq(F + "{ echo e >&2; } 2>&1; f 2>&1 | tr a-z A-Z; { echo a; echo b >&2; echo c; } 2>&1");

    [SkippableFact]
    public Task Stderr_DupOrder_LeftToRight()
        => Eq("echo 1 >&2 2>/dev/null; echo 2 2>/dev/null >&2; echo 3 2>&1 >&2; echo 4 >&2 2>&1");

    [SkippableFact]
    public Task Stderr_NotCapturedBySubstitution()
        => Eq(F + "v=$(echo cap >&2); echo \"v=[$v]\"; w=$(f 2>&1); echo \"w=[$w]\"");

    [SkippableFact]
    public Task Stderr_WriteIsNotAFailure()
        => Eq("echo x >&2 || echo FALLBACK; echo y >&2 && echo AND-RAN; echo z >&2; echo \"rc=$?\"; ( set -e; echo q >&2; echo after )");

    [SkippableFact]
    public Task Stderr_CmdletError_MergeUnderOuterCapture()
        => Eq("d=$(mktemp -d); eval 'ls /nonexistent-zz 2>&1' >\"$d/o\" 2>\"$d/e\"; echo \"rc=$? o=[$(cat \"$d/o\")] e=[$(cat \"$d/e\")]\"; rm -rf \"$d\"");

    [SkippableFact]
    public Task Stderr_FileTargetsAroundDup()
        => Eq("d=$(mktemp -d); echo x >\"$d/t\" >&2; echo y 2>\"$d/u\" >&2; echo z >&2 2>\"$d/w\"; "
            + "{ echo e >&2; } 2>\"$d/g\"; echo \"t=[$(cat \"$d/t\")] u=[$(cat \"$d/u\")] w=[$(cat \"$d/w\")] g=[$(cat \"$d/g\")]\"; rm -rf \"$d\"");
}
