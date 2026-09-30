using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for join (shared ordered parser). Checked against GNU join 9.4
/// (`wsl bash`): options -1 -2 -j -t -a -v -i/--ignore-case plus refused -o -e --check-order
/// --nocheck-order --header -z (exit 2, FIX: `-o FORMAT` / `-e STR` used to be silently swallowed
/// so `join -o 0,1.2 a b` printed the default format). Field numbers must be positive integers,
/// -a/-v take 1 or 2, -t is a single character (`\0` = NUL): each violation is exit 1 (the old scan
/// kept field 1 for `-1 x`, read `-a3` as an operand and accepted a multi-character -t). Unique
/// long prefixes (`--ignore`, `--i`) resolve; `--check` is the refused --check-order.
/// </summary>
public class JoinArgScanTests
{
    private static string Set(HashSet<int> s) => string.Join(",", s.OrderBy(x => x));

    private static string Scan(string[] argv)
    {
        var j = InvokeBashJoinCommand.Plan(argv);
        if (j.Parsed.Error is { } e) return "ERR " + e.Message("join");
        if (j.Error is { } m) return "ERR " + m;
        string t = j.Delimiter.Replace("\0", "\\0");
        return $"t=[{t}] 1={j.Field1} 2={j.Field2} a=[{Set(j.AFiles)}] v=[{Set(j.VFiles)}] i={j.IgnoreCase} ops=[{string.Join(",", j.Operands)}]";
    }

    private const string D = "t=[ ] 1=1 2=1 a=[] v=[] i=False ops=[]";

    [Theory]
    [InlineData(D)]
    [InlineData("t=[:] 1=1 2=1 a=[] v=[] i=False ops=[]", "-t:")]
    [InlineData("t=[:] 1=1 2=1 a=[] v=[] i=False ops=[]", "-t", ":")]
    [InlineData("t=[] 1=1 2=1 a=[] v=[] i=False ops=[]", "-t", "")]
    [InlineData("t=[\\0] 1=1 2=1 a=[] v=[] i=False ops=[]", "-t", "\\0")]
    [InlineData("t=[:] 1=2 2=1 a=[] v=[] i=False ops=[]", "-t:", "-1", "2")]
    [InlineData("t=[ ] 1=2 2=3 a=[] v=[] i=False ops=[]", "-1", "2", "-2", "3")]
    [InlineData("t=[ ] 1=2 2=3 a=[] v=[] i=False ops=[]", "-12", "-23")]  // value takes the rest: -1 2, -2 3
    [InlineData("t=[ ] 1=3 2=3 a=[] v=[] i=False ops=[]", "-j", "3")]
    [InlineData("t=[ ] 1=3 2=3 a=[] v=[] i=False ops=[]", "-j3")]
    [InlineData("t=[ ] 1=4 2=2 a=[] v=[] i=False ops=[]", "-j", "2", "-1", "4")]  // argv order
    [InlineData("t=[ ] 1=2 2=2 a=[] v=[] i=False ops=[]", "-1", "4", "-j", "2")]
    [InlineData("t=[ ] 1=1 2=1 a=[1,2] v=[] i=False ops=[]", "-a", "1", "-a", "2")]
    [InlineData("t=[ ] 1=1 2=1 a=[1] v=[] i=False ops=[]", "-a1")]
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[1] i=False ops=[]", "-v", "1")]
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[1,2] i=False ops=[]", "-v1", "-v2")]
    [InlineData("t=[ ] 1=1 2=1 a=[1] v=[] i=True ops=[]", "-ia1")]  // bundle: i then a takes "1"
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[] i=True ops=[]", "-i")]
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[] i=True ops=[]", "--ignore-case")]
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[] i=True ops=[]", "--ignore")]  // FIX (abbreviation)
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[] i=True ops=[]", "--i")]
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[] i=False ops=[f1,f2]", "f1", "f2")]
    [InlineData("t=[ ] 1=2 2=1 a=[] v=[] i=False ops=[f1,f2]", "f1", "-1", "2", "f2")]  // FIX (options may follow operands)
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[] i=False ops=[-1,f2]", "--", "-1", "f2")]
    [InlineData("t=[ ] 1=1 2=1 a=[] v=[] i=False ops=[-,f2]", "-", "f2")]
    [InlineData("ERR join: invalid field number: 'x'", "-1", "x")]  // FIX (was: silently field 1)
    [InlineData("ERR join: invalid field number: '0'", "-1", "0")]
    [InlineData("ERR join: invalid field number: '0'", "-j", "0")]
    [InlineData("ERR join: invalid field number: '-1'", "-1", "-1")]
    [InlineData("ERR join: invalid field number: '3'", "-a3")]  // FIX (was: an operand)
    [InlineData("ERR join: invalid field number: 'f'", "-a", "f")]
    [InlineData("ERR join: multi-character tab 'ab'", "-t", "ab")]  // FIX (was: accepted)
    [InlineData("ERR join: option requires an argument -- 'a'", "-a")]
    [InlineData("ERR join: option requires an argument -- 't'", "-t")]
    [InlineData("ERR join: invalid option -- 'x'", "-x")]
    [InlineData("ERR join: unrecognized option '--nope'", "--nope")]
    [InlineData("ERR join: option '-o' is recognized but not supported by ps-bash", "-o", "0,1.2")]  // FIX (was: format ignored)
    [InlineData("ERR join: option '-e' is recognized but not supported by ps-bash", "-e", "X")]
    [InlineData("ERR join: option '--check-order' is recognized but not supported by ps-bash", "--check-order")]
    [InlineData("ERR join: option '--check-order' is recognized but not supported by ps-bash", "--check")]
    [InlineData("ERR join: option '--nocheck-order' is recognized but not supported by ps-bash", "--nocheck-order")]
    [InlineData("ERR join: option '--header' is recognized but not supported by ps-bash", "--header")]
    [InlineData("ERR join: option '-z' is recognized but not supported by ps-bash", "-z")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void UsageErrors_Exit1_UnsupportedExit2()
    {
        Assert.Equal(1, InvokeBashJoinCommand.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashJoinCommand.ScanArgs(new[] { "-o", "0" }).ErrorExitCode);
    }
}
