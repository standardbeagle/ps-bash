using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A file redirect writes each command's output as it is produced, not when the redirected command ends: a later
/// command in the same redirected group (or loop body, function, <c>exec &gt; f</c> script) reads what the earlier ones
/// wrote. ps-bash held it in a 64 KB buffer until the group finished, so the reader saw an empty file
/// (<c>PsBash.Cmdlets.RedirectFileWriter</c>).
/// </summary>
public class RedirectWriteTimingTests
{
    [SkippableFact]
    public Task Group_ReaderSeesEarlierOutput() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && { echo a; cat out.txt >&2; } > out.txt; echo \"---\"; cat out.txt");

    // Two records in quick succession, then the reader: the second is still buffered unless a read flushes it.
    [SkippableFact]
    public Task Group_BurstThenReader_SeesEveryLine() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && { echo a; echo b; cat out.txt >&2; for i in 1 2 3 4 5 6 7 8 9 10; do echo $i; done; wc -l < out.txt >&2; } > out.txt; cat out.txt");

    [SkippableFact]
    public Task Append_ReaderSeesEarlierOutput() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && echo z > ap.txt; { echo a; cat ap.txt >&2; } >> ap.txt; echo \"---\"; cat ap.txt");

    [SkippableFact]
    public Task StderrTarget_ReaderSeesEarlierOutput() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && { echo e >&2; cat err.txt; } 2> err.txt; echo \"---\"; cat err.txt");

    [SkippableFact]
    public Task BothStreams_ReaderSeesEarlierOutput() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && { echo a; echo e >&2; wc -l < both.txt > n1; } &> both.txt; "
        + "{ echo b; echo f >&2; wc -l < both2.txt > n2; } > both2.txt 2>&1; cat n1 n2 both.txt both2.txt");

    [SkippableFact]
    public Task WhileBody_ReadsItsOwnTarget() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && printf '1\\n2\\n' > in.txt; while read x; do echo \"$x\"; cat w.txt >&2; done < in.txt > w.txt; echo \"---\"; cat w.txt");

    [SkippableFact]
    public Task RedirectedFunctionCall_ReadsItsOwnTarget() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && f1() { echo a; cat fn.txt >&2; }; f1 > fn.txt; echo \"---\"; cat fn.txt");

    [SkippableFact]
    public Task ExecRedirect_LaterCommandSeesEarlierOutput() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && ( exec > ex.txt; echo a; cat ex.txt >&2; echo b; echo c; grep -c . ex.txt >&2 ); echo \"---\"; cat ex.txt");

    // A size check, not a read: the first record is written at once.
    [SkippableFact]
    public Task Group_TestSizeSeesEarlierOutput() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && { echo hello; [ -s ts.txt ] && echo nonempty >&2; } > ts.txt; cat ts.txt");
}
