using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// bash applies a command's redirects left to right: a redirect that cannot be opened is reported WHERE FD 2 POINTS AT
/// THAT MOMENT, with the shell's prefix (<c>s.sh: line 3: nodir/y: No such file or directory</c>), the later redirects
/// are never opened, and the command fails with status 1. ps-bash opened them in its own order and wrote
/// <c>bash: nodir/y: …</c> on stderr even after <c>2&gt;/dev/null</c>; <c>cat &lt; nofile</c> had no prefix, and
/// <c>x=$(&lt; nofile)</c> was a PowerShell error with status 0. docs/specs/error-handling.md "Diagnostic prefix", "Redirect open errors". Recorded from
/// bash 5.2 (cassette).
/// </summary>
public class RedirectOpenErrorDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";

    /// <summary>
    /// One redirect order per line. Not a multi-line compound: bash names the line it ENDS on, ps-bash its first line
    /// (known difference, spec).
    /// </summary>
    private const string Redirects =
        "cat < nofile 2>/dev/null; echo \"rc1=$?\"\n" +
        "cat 2>/dev/null < nofile; echo \"rc2=$?\"\n" +
        "echo hi 2>err > nodir/y; echo \"rc3=$?\"; cat err\n" +
        "echo hi > nodir/y 2>err2; echo \"rc4=$?\"; ls\n" +
        "ls 2>&1 > nodir/q | sed 's/^/OUT:/'\n" +
        "echo hi > nodir/y > b; echo \"rc6=$?\"; ls\n" +
        "{ echo a; } 2>/dev/null > nodir/z; echo \"rc7=$?\"\n" +
        "while read l; do echo $l; done < nofile; echo \"rc8=$?\"\n" +
        "while read l; do echo $l; done 2>/dev/null < nofile; echo \"rc9=$?\"\n" +
        "x=$(< nofile); echo \"rc10=$?\"\n" +
        "x=$(< nofile 2>/dev/null); echo \"rc11=$?\"\n" +
        "read v < /nosuchfile; echo \"rc12=$?\"\n" +
        "fn() { echo in; }\n" +
        "fn > nodir/w; echo \"rc13=$?\"\n" +
        "{ echo a; } > nodir/z; echo \"rc14=$?\"\n" +
        "echo hi >> nodir/y; echo \"rc15=$?\"\n" +
        "echo hi &> nodir/y; echo \"rc16=$?\"\n" +
        "echo hi 2>/dev/null > nodir/y; echo \"rc17=$?\"\n";

    [SkippableFact]
    public Task RedirectOpenError_InAScriptFile_IsReportedWhereFd2PointsWithTheScriptAndLine()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n" + Redirects + "EOF\n"
            + "bash s.sh 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// <c>exec 2&gt;&amp;1</c>, not a <c>{ } 2&gt;&amp;1</c> group: inside a multi-line group bash names the GROUP's end
    /// line for the nested <c>while</c>/<c>{ }</c> redirects (known difference, spec).
    /// </summary>
    [SkippableFact]
    public Task RedirectOpenError_UnderDashC_IsBashAndTheLine()
        => AssertOracle.EqualAsync(Work + "exec 2>&1\n" + Redirects + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// Under <c>set -e</c> the failed open ends the script (status 1) and is reported once, where fd 2 pointed: after
    /// <c>2&gt;/dev/null</c> not at all. ps-bash printed <c>ps-bash: bash: nodir/y: …</c> in every case.
    /// </summary>
    [SkippableFact]
    public Task RedirectOpenError_UnderErrexit_EndsTheScriptReportedOnce()
        => AssertOracle.EqualAsync(Work
            + "bash -ec 'echo hi 2>/dev/null > nodir/y; echo after' 2>&1; echo \"rc=$?\"\n"
            + "bash -ec 'echo hi > nodir/y; echo after' 2>&1; echo \"rc=$?\"\n"
            + "bash -ec 'cat < nofile; echo after' 2>&1; echo \"rc=$?\"\n"
            + "bash -ec 'x=$(< nofile); echo after' 2>&1; echo \"rc=$?\"\n"
            + "bash -c 'fn() { echo in; }; fn 2>e > nodir/w; cat e' 2>&1; echo \"rc=$?\""
            + Done,
            timeout: TimeSpan.FromSeconds(60));
}
