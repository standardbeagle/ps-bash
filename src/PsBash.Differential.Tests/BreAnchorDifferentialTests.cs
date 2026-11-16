using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// POSIX BRE anchors are positional: <c>^</c> anchors only at the start of the pattern (or after <c>\(</c> /
/// <c>\|</c>), <c>$</c> only at the end (or before <c>\)</c> / <c>\|</c>), and a leading <c>*</c> is literal.
/// Anywhere else they are ordinary characters, so <c>sed 's/a$(b/X/'</c> rewrites <c>a$(b</c>. ps-bash passed them
/// to .NET as anchors and matched nothing (found when editing a test file with sed through the Bash tool).
/// </summary>
public class BreAnchorDifferentialTests
{
    [SkippableFact]
    public async Task Differential_SedDollarInsideTheBre_IsLiteral()
        => await AssertOracle.EqualAsync("echo 'x a$(b y' | sed 's/a$(b/X/'; echo 'p$q' | sed 's/p$q/Y/'");

    [SkippableFact]
    public async Task Differential_GrepCaretInsideTheBre_IsLiteral()
        => await AssertOracle.EqualAsync("printf 'a^b\\nab\\n' | grep 'a^b'; echo \"rc $?\"");

    [SkippableFact]
    public async Task Differential_BreAnchorsAtTheEndsAndAroundGroups_StillAnchor()
        => await AssertOracle.EqualAsync("printf 'ab\\nb\\nxab\\n' | grep '^ab$'; printf 'b\\nab\\n' | sed -n '/\\(^b$\\)/p'; printf 'ab\\nb\\n' | grep -c 'x\\|^b'");

    [SkippableFact]
    public async Task Differential_LeadingStarInABre_IsLiteral()
        => await AssertOracle.EqualAsync("printf '*a\\nba\\n' | grep '*a'; echo '*z' | sed 's/*z/S/'");
}
