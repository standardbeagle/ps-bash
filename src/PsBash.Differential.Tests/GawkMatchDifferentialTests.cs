using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>gawk</c> runs ps-bash's awk, and gawk's three-argument <c>match(s, re, arr)</c> fills arr: arr[n] is group n's
/// text (0 = whole match) plus arr[n,"start"] and arr[n,"length"]; a group outside the match gets no entries and a
/// failed match empties arr. Before, <c>gawk</c> was "command not found" and the third argument was ignored.
/// </summary>
public class GawkMatchDifferentialTests
{
    [SkippableTheory]
    [InlineData("echo ab12cd | gawk '{ n = match($0, /([a-z]+)([0-9]+)(x)?/, m); print n, length(m), m[0], m[1], m[2], m[1, \"start\"], m[2, \"start\"], m[2, \"length\"], ((3, \"start\") in m) }'")]
    [InlineData("echo zz | gawk '{ m[\"old\"] = 1; print match($0, /q/, m), length(m), RSTART, RLENGTH }'")]
    [InlineData("echo 'v=1.25' | gawk 'match($0, /\\.([0-9]+)/, m) { print m[1], m[0] }'")]
    [InlineData("printf 'k1 a\\nx\\nk22 b\\n' | gawk 'match($0, /^k([0-9]+)/, m) { print NR \":\" m[1] }'")]
    public Task GawkMatch_FillsTheArray(string script) => AssertOracle.EqualAsync(script);
}
