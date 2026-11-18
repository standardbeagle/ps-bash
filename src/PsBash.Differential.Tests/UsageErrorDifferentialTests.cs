using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for a usage error (an unknown short / long option, an ambiguous abbreviation) on the commands
/// that share the argv parser: the GNU first line, the tool's hint line(s), and the exit status. REGRESSION: the
/// shared sink wrote only the first line, so <c>cat -x</c> said <c>cat: invalid option -- 'x'</c> with no
/// <c>Try 'cat --help' for more information.</c>. Fixed at the sink (<c>FileSystemHelpers.TryWriteParseError</c> +
/// <c>UsageHint</c>, guard <c>ParseErrorSinkGuardTests</c>).
/// </summary>
public class UsageErrorDifferentialTests
{
    [SkippableFact]
    public Task UnknownOption_PrintsTheToolsHint_AndItsStatus()
        => AssertOracle.EqualAsync(
            "for c in base64 cat column comm cp cut diff du expand fold grep gzip head join ln ls mkdir mv nl paste "
            + "readlink realpath rm rmdir sort split stat tac tail tar tee touch unexpand uniq wc; do "
            + "echo \"## $c\"; $c -@ </dev/null 2>&1; echo \"rc=$?\"; $c --zzqq </dev/null 2>&1; echo \"rc=$?\"; done",
            timeout: TimeSpan.FromSeconds(90));
}
