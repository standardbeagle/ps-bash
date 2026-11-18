using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Review findings of one batch, each compared with bash:
/// <list type="bullet">
/// <item>Byte filters stream (docs/specs/runtime-functions.md "Byte filters stream"). REGRESSION: base64 and od read a
/// file operand whole before writing - <c>base64 /dev/urandom | head -c 32</c> never ended, <c>od -v /dev/zero</c>
/// refused, <c>cat /dev/zero | od -v | head -1</c> never ended. Each endless case runs under <c>timeout</c> so a
/// regression is a wrong status, not a hung suite.</item>
/// <item><c>wc -</c> counts stdin as it arrives; a second <c>-</c> finds it at end of input.</item>
/// <item>A table descriptor's writes are in the file when the command ends (<c>exec 3&gt;f; seq 1 1000 &gt;&amp;3</c>, then
/// a program reads the file). REGRESSION: a burst sat in the writer's buffer and <c>stat</c> saw a short file.</item>
/// <item>A job starts with none of a sibling job's trap / alias / directory stack (a job runspace is reused only when the
/// last job left it as it was; docs/specs/background-jobs.md section 12.5).</item>
/// </list>
/// </summary>
public class ByteFilterStreamingDifferentialTests
{
    [SkippableTheory]
    [InlineData("timeout 20 bash -c 'base64 /dev/urandom | head -c 32 | wc -c'; echo \"st=$?\"")]
    [InlineData("timeout 20 bash -c 'base64 -w0 /dev/zero | head -c 10'; echo \" st=$?\"")]
    [InlineData("timeout 20 bash -c 'base64 /dev/zero | head -2'; echo \"st=$?\"")]
    [InlineData("timeout 20 bash -c 'od -v /dev/zero | head -1'; echo \"st=$?\"")]
    [InlineData("timeout 20 bash -c 'cat /dev/zero | od -v | head -1'; echo \"st=$?\"")]
    [InlineData("timeout 20 bash -c 'od -An -tx1 -N 4 /dev/zero'; echo \"st=$?\"")]
    public Task ByteFilter_OfAnEndlessInput_EndsWhenDownstreamStops(string script) => AssertOracle.EqualAsync(script);

    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\" || exit; printf 'abcdefghijklmnopqrstuvwxyz0123456789' > g; od -c -j 3 -N 20 g; echo \"st=$?\"")]
    [InlineData("cd \"$(mktemp -d)\" || exit; printf 'abcdefghijklmnopqrstuvwxyz0123456789' > g; od -j 100 g 2>&1; echo \"st=$?\"")]
    [InlineData("cd \"$(mktemp -d)\" || exit; printf 'abcdefghijklmnopqrstuvwxyz0123456789' > g; od -tx1 g - g <<< hi; echo \"st=$?\"")]
    [InlineData("cd \"$(mktemp -d)\" || exit; printf 'YWJj!!!!' > bad; base64 -d bad 2>/dev/null; echo \" st=$?\"; base64 -d bad 2>&1 >/dev/null")]
    [InlineData("cd \"$(mktemp -d)\" || exit; { seq 1 20000; head -c 3001 /dev/zero; printf '\\377\\001x'; } > z;base64 z | wc -l; base64 -w0 z | wc -c; base64 z | base64 -d | md5sum; md5sum < z")]
    public Task ByteFilter_FileOperands_MatchBash(string script) => AssertOracle.EqualAsync(script);

    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\" || exit; printf 'abcdefghijklmnopqrstuvwxyz0123456789' > g; printf 'a b\\nc\\n' | wc - g")]
    [InlineData("printf 'a\\n' | wc - -")]
    [InlineData("seq 1 5000 | wc -")]
    public Task Wc_DashOperand_MatchesBash(string script) => AssertOracle.EqualAsync(script);

    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\" || exit; exec 3>f; seq 1 1000 >&3; stat -c %s f; exec 3>&-")]
    [InlineData("cd \"$(mktemp -d)\" || exit; exec 3>f; seq 1 1000 >&3; wc -c < f; exec 3>&-; wc -c < f")]
    [InlineData("cd \"$(mktemp -d)\" || exit; exec 3>>f; for i in 1 2 3; do seq 1 300 >&3; stat -c %s f; done; exec 3>&-")]
    public Task ExecFd_ABurstOfWrites_IsInTheFileWhenTheCommandEnds(string script) => AssertOracle.EqualAsync(script);

    // Nine later jobs: more than MaxIdle (8), so one of them gets the first job's runspace if it went back idle. Each
    // iteration prints (a `trap` / `alias` job takes ~2 s here: the launcher's idle timeout must not trip).
    [SkippableTheory]
    [InlineData("{ trap 'echo T1' USR2; } & wait\nfor i in 1 2 3 4 5 6 7 8 9; do { trap -p USR2; } & wait; echo \"j$i\"; done")]
    [InlineData("cd \"$(mktemp -d)\" || exit\n{ pushd / >/dev/null; } & wait\nfor i in 1 2 3 4 5 6 7 8 9; do { dirs -p | wc -l; } & wait; done")]
    [InlineData("{ alias zz='echo stale'; } & wait\nfor i in 1 2 3 4 5 6 7 8 9; do { alias zz >/dev/null 2>&1; echo \"a=$?\"; } & wait; done")]
    public Task JobState_ASiblingJobsChanges_AreNotInheritedByTheNextJob(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(120));
}
