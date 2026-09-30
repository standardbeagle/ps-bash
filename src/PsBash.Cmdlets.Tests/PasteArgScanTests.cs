using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for paste (shared ordered parser). Checked against GNU paste 9.4
/// (`wsl bash`): options -d/--delimiters=LIST, -s/--serial (refused: -z/--zero-terminated, exit 2),
/// unique long prefixes (`--ser`, `--de=,`), bundle `-sd,` (d takes the rest). The delimiter is a
/// LIST of single characters cycled per column — FIX: the old cmdlet used the whole string as one
/// multi-character delimiter (`paste -d ',;' a b c` printed `a,;b,;c`, GNU `a,b;c`); escapes
/// `\n \t \r \b \f \v \\ \0` (`\0` = empty), any other `\x` is `x`, an empty list is `\0`, a trailing
/// lone backslash is "delimiter list ends with an unescaped backslash" (exit 1).
/// Delimiters render joined by `|`, with tab as \t, newline as \n and the empty delimiter as ~.
/// </summary>
public class PasteArgScanTests
{
    private static string Show(string[] d) => string.Join("|", d.Select(x =>
        x.Length == 0 ? "~" : x.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r")));

    private static string Scan(string[] argv)
    {
        var p = InvokeBashPasteCommand.Plan(argv);
        if (p.Parsed.Error is { } e) return "ERR " + e.Message("paste");
        if (p.Error is { } m) return "ERR " + m;
        return $"d=[{Show(p.Delimiters)}] s={p.Serial} ops=[{string.Join(",", p.Operands)}]";
    }

    [Theory]
    [InlineData("d=[\\t] s=False ops=[]")]
    [InlineData("d=[,] s=False ops=[a,b]", "-d,", "a", "b")]
    [InlineData("d=[,] s=False ops=[a,b]", "-d", ",", "a", "b")]
    [InlineData("d=[,] s=False ops=[]", "--delimiters=,")]
    [InlineData("d=[,] s=False ops=[]", "--delimiters", ",")]
    [InlineData("d=[,] s=False ops=[]", "--d=,")]  // FIX (abbreviation)
    [InlineData("d=[,] s=False ops=[]", "--de=,")]
    [InlineData("d=[,|;] s=False ops=[]", "-d", ",;")]  // FIX: a LIST, cycled per column (was one 2-char delimiter)
    [InlineData("d=[,|;|:] s=False ops=[]", "-d,;:")]
    [InlineData("d=[,] s=True ops=[f]", "-sd,", "f")]  // FIX-guard: bundle, d takes the rest
    [InlineData("d=[,] s=True ops=[f]", "-s", "-d,", "f")]
    [InlineData("d=[s|,] s=False ops=[f]", "-ds,", "f")]  // d takes the REST of the bundle: list "s,"
    [InlineData("d=[\\t] s=True ops=[]", "-s")]
    [InlineData("d=[\\t] s=True ops=[]", "--serial")]
    [InlineData("d=[\\t] s=True ops=[]", "--ser")]  // FIX
    [InlineData("d=[;] s=False ops=[]", "-d,", "-d;")]  // last wins
    [InlineData("d=[\\n] s=False ops=[]", "-d", "\\n")]
    [InlineData("d=[\\t|\\n] s=False ops=[]", "-d", "\\t\\n")]
    [InlineData("d=[~|\\n] s=False ops=[]", "-d", "\\0\\n")]  // \0 = empty delimiter
    [InlineData("d=[\\r] s=False ops=[]", "-d", "\\r")]
    [InlineData("d=[\\] s=False ops=[]", "-d", "\\\\")]
    [InlineData("d=[q] s=False ops=[]", "-d", "\\q")]  // unknown escape = the char
    [InlineData("d=[~] s=False ops=[]", "-d", "")]  // empty list = \0
    [InlineData("d=[\\t] s=False ops=[a,b]", "a", "b")]
    [InlineData("d=[,] s=False ops=[a,b]", "a", "-d,", "b")]  // FIX (options may follow operands)
    [InlineData("d=[\\t] s=False ops=[-d,]", "--", "-d,")]
    [InlineData("d=[\\t] s=False ops=[-,-]", "-", "-")]  // stdin operands
    [InlineData("ERR paste: delimiter list ends with an unescaped backslash: \\", "-d", "\\")]
    [InlineData("ERR paste: delimiter list ends with an unescaped backslash: a\\", "-d", "a\\")]
    [InlineData("ERR paste: option requires an argument -- 'd'", "-d")]
    [InlineData("ERR paste: option '--delimiters' requires an argument", "--delimiters")]
    [InlineData("ERR paste: invalid option -- 'x'", "-x")]
    [InlineData("ERR paste: invalid option -- 'x'", "-sx")]
    [InlineData("ERR paste: option '-z' is recognized but not supported by ps-bash", "-z")]
    [InlineData("ERR paste: option '--zero-terminated' is recognized but not supported by ps-bash", "--zero-terminated")]
    [InlineData("ERR paste: option '--zero-terminated' is recognized but not supported by ps-bash", "--zero")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void UsageErrors_Exit1_UnsupportedExit2()
    {
        Assert.Equal(1, InvokeBashPasteCommand.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashPasteCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
    }
}
