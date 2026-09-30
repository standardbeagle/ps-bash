using PsBash.Cmdlets.Args;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>Expected-value table for bash's printf BUILTIN option scan (oracle: wsl bash 5.2).</summary>
public class PrintfArgScanTests
{
    [Theory]
    // args (| separated)        var      first  invalid  missing
    [InlineData("%s|-n", null, 0, '\0', false)]
    [InlineData("--|%s|-n", null, 1, '\0', false)]
    [InlineData("--|-x", null, 1, '\0', false)]             // after -- the format is literal
    [InlineData("-v|x|%s", "x", 2, '\0', false)]
    [InlineData("-vx|%s", "x", 1, '\0', false)]
    [InlineData("-v|x|-v|y|%s", "y", 4, '\0', false)]       // last -v wins
    [InlineData("-v|x|--|-fmt", "x", 3, '\0', false)]
    [InlineData("-vx|-vy|%s", "y", 2, '\0', false)]
    [InlineData("-vv|%s", "v", 1, '\0', false)]             // -vv: var named v
    [InlineData("-v", null, 0, '\0', true)]                 // option requires an argument
    [InlineData("-x", null, 0, 'x', false)]
    [InlineData("-xv", null, 0, 'x', false)]                // first bad letter wins
    [InlineData("-n|\\n", null, 0, 'n', false)]
    [InlineData("--help", null, 0, '-', false)]             // reported as "--"
    [InlineData("---", null, 0, '-', false)]
    [InlineData("-|x", null, 0, '\0', false)]               // lone dash = the format
    [InlineData("-v|x", "x", 2, '\0', false)]               // no format left: caller prints usage
    [InlineData("", null, 0, '\0', false)]
    public void Scan(string joined, string? var, int first, char invalid, bool missing)
    {
        var args = joined.Length == 0 ? Array.Empty<string>() : joined.Split('|');
        var r = PrintfArgScan.Scan(args);
        Assert.Equal((var, first, invalid, missing), (r.VarName, r.FirstOperand, r.InvalidOption, r.MissingValue));
    }

    [Theory]
    [InlineData("x", true)]
    [InlineData("_a1", true)]
    [InlineData("arr[2]", true)]
    [InlineData("1bad", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData("a[]", false)]
    public void AssignableName(string name, bool ok) => Assert.Equal(ok, InvokeBashPrintfCommand.IsAssignableName(name));
}
