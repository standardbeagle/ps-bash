using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// od's argv planner (<see cref="OdFormat.Plan"/>). Wording from GNU od 9.4 (<c>wsl od -t d99999999999</c>), which
/// quotes with U+2018/U+2019 in a UTF-8 locale and with <c>'</c> in C; the status is compared with od in
/// <c>Differential.Tests/OdTests</c>.
/// </summary>
public class OdFormatTests
{
    // A type size past int range is script text: int.Parse threw OverflowException out of the planner.
    [Theory]
    [InlineData("d99999999999")]
    [InlineData("x99999999999999999999")]
    [InlineData("u4294967296")]
    public void Plan_TypeSizePastIntRange_IsAnInvalidTypeString(string spec)
    {
        var plan = OdFormat.Plan(["-t", spec]);
        Assert.Equal($"od: invalid type string '{spec}'", plan.Error);
    }
}
