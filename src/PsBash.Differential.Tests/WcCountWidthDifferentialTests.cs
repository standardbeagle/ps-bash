using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU wc's column width (coreutils compute_number_width; spec runtime-command-reference.md, wc): one input with one
/// column prints the bare count; otherwise every count is right-aligned to the digit width of the regular inputs' total
/// size, at least 7 when an input is not a regular file (a pipe, a directory, /dev/null), and the columns are joined
/// by single spaces. REGRESSION: <c>printf 'a\n' | wc -lc</c> printed <c>1 2</c> (GNU: <c>      1       2</c>) and
/// <c>wc -lc &lt; f</c> padded a regular file like a pipe.
/// </summary>
public class WcCountWidthDifferentialTests
{
    private const string Setup = "cd \"$(mktemp -d)\" || exit; seq 1 1000 > f; seq 1 5 > g; printf 'f\\0g\\0' > L; ";

    [SkippableTheory]
    [InlineData("printf 'a\\n' | wc -lc; printf 'a\\n' | wc; printf 'a\\n' | wc -l; printf '' | wc; printf 'a b\\n' | wc -w")]
    [InlineData("seq 1 100000 | wc -lc; seq 1 3000000 | wc -c; printf 'a\\n' | wc -lwcmL")]
    [InlineData(Setup + "wc -lc < f; wc < f; wc -l < f; wc -c < /dev/null; wc -lc < /dev/null")]
    [InlineData(Setup + "wc -lc f; wc -lc f g; wc -l f g; wc -l f; wc -lc nope g 2>&1")]
    [InlineData(Setup + "cat f | wc -lc -; wc -lc - < f; wc -lc f - < g")]
    [InlineData(Setup + "wc -lc --files0-from=L; printf 'f\\0g\\0' | wc -lc --files0-from=-")]
    [InlineData(Setup + "mkdir d; wc -lc g d 2>&1; echo \"st=$?\"")]
    public Task Wc_ColumnWidth_MatchesGnu(string script) => AssertOracle.EqualAsync(script);
}
