using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>time [-p] pipeline</c> is a keyword: it times the whole pipeline or compound command, keeps the pipeline's
/// status and assignments, and writes bash's report (TIMEFORMAT, or <c>-p</c>'s POSIX layout) to stderr after the
/// pipeline's output. Before, <c>time { …; }</c> was a parse error, <c>time -p</c> ran a command named -p, and the
/// report was <c>real    0.123s</c>. Digits are masked: the times themselves differ run to run.
/// </summary>
public class TimeKeywordDifferentialTests
{
    [SkippableTheory]
    [InlineData("{ time -p sleep 0.01; } 2>&1 | sed 's/[0-9]/N/g'")]
    [InlineData("unset TIMEFORMAT; { time { echo in; sleep 0.01; }; } 2>&1 | sed 's/[0-9]/N/g'")]
    [InlineData("TIMEFORMAT='R=%R l=%lR u=%2U p %% x'; { time sleep 0.01; } 2>&1 | sed 's/[0-9]/N/g'")]
    [InlineData("TIMEFORMAT=; { time true; } 2>&1 | wc -c")]
    [InlineData("{ time echo a | tr a b; } 2>/dev/null")]
    [InlineData("f() { return 3; }; { time f 2>/dev/null; } 2>/dev/null; echo st=$?")]
    [InlineData("{ time { x=1; echo in; }; } 2>/dev/null; echo \"x=$x\"")]
    [InlineData("{ time ( echo sub; exit 4 ); } 2>/dev/null; echo st=$?")]
    [InlineData("if { time false; } 2>/dev/null; then echo yes; else echo no; fi; { time ! false; } 2>/dev/null; echo neg=$?")]
    [InlineData("{ time -- echo dd; time echo a && echo b; } 2>/dev/null; time=5; echo \"time=$time\"")]
    public Task TimeKeyword_MatchesBash(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));
}
