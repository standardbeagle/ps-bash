using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for stat (shared ordered parser). Checked against GNU stat 9.4
/// (`wsl bash`): -c/--format FMT, --printf FMT, -t/--terse, --cached=MODE (default|never|always),
/// unique long prefixes (`--form`, `--term`; `--f` is ambiguous between '--file-system' and
/// '--format'), bundles (`-tc %n`), the LAST of -c/--format/--printf wins, a format beats -t,
/// unknown options are usage errors (exit 1), -L/-f are valid-but-unsupported (ps-bash, exit 2).
/// </summary>
public class StatArgScanTests
{
    private static string Scan(string[] argv)
    {
        var s = InvokeBashStatCommand.Plan(argv);
        if (s.Parsed.Error is { } e) return "ERR " + e.Message("stat");
        if (s.Error is { } m) return "ERR " + m.Split('\n')[0];
        return $"fmt={s.Format ?? "-"} printf={s.FormatIsPrintf} t={s.Terse} ops=[{string.Join(",", s.Operands)}]";
    }

    [Theory]
    [InlineData("fmt=- printf=False t=False ops=[f]", "f")]
    [InlineData("fmt=%n printf=False t=False ops=[f]", "-c", "%n", "f")]
    [InlineData("fmt=%n printf=False t=False ops=[f]", "-c%n", "f")]
    [InlineData("fmt=%n printf=False t=False ops=[f]", "--format=%n", "f")]
    [InlineData("fmt=%n printf=False t=False ops=[f]", "--format", "%n", "f")]
    [InlineData("fmt=%n printf=False t=False ops=[f]", "--form=%n", "f")]  // FIX (abbreviation)
    [InlineData("fmt=%n printf=True t=False ops=[f]", "--printf=%n", "f")]
    [InlineData("fmt=%n printf=True t=False ops=[f]", "--printf", "%n", "f")]
    [InlineData("fmt=- printf=False t=True ops=[f]", "-t", "f")]
    [InlineData("fmt=- printf=False t=True ops=[f]", "--terse", "f")]
    [InlineData("fmt=- printf=False t=True ops=[f]", "--ter", "f")]  // FIX
    [InlineData("fmt=%n printf=False t=True ops=[f]", "-tc", "%n", "f")]  // FIX (bundle; -c wins over -t at run time)
    [InlineData("fmt=%s printf=False t=False ops=[f,g]", "-c", "%n", "-c", "%s", "f", "g")]  // last wins
    [InlineData("fmt=%n printf=True t=False ops=[f]", "-c", "%s", "--printf=%n", "f")]  // FIX (last wins)
    [InlineData("fmt=%s printf=False t=False ops=[f]", "--printf=%n", "-c", "%s", "f")]  // FIX (was: printf always won)
    [InlineData("fmt=%n printf=False t=False ops=[f]", "f", "-c", "%n")]  // FIX (options after operands)
    [InlineData("fmt=- printf=False t=False ops=[-c]", "--", "-c")]
    [InlineData("fmt=- printf=False t=False ops=[-]", "-")]
    [InlineData("fmt=- printf=False t=False ops=[f]", "--cached=never", "f")]
    [InlineData("fmt=- printf=False t=False ops=[f]", "--cached=always", "f")]
    [InlineData("ERR stat: invalid argument 'foo' for '--cached'", "--cached=foo", "f")]
    [InlineData("ERR stat: missing operand", "-t")]
    [InlineData("ERR stat: missing operand")]
    [InlineData("ERR stat: option requires an argument -- 'c'", "-c")]  // FIX (was: a file name)
    [InlineData("ERR stat: option '--printf' requires an argument", "--printf")]
    [InlineData("ERR stat: option '--terse' doesn't allow an argument", "--terse=1", "f")]
    [InlineData("ERR stat: invalid option -- 'z'", "-z", "f")]
    [InlineData("ERR stat: unrecognized option '--nope'", "--nope", "f")]  // FIX (was: a file name)
    [InlineData("ERR stat: invalid option -- 'Z'", "-Z", "f")]
    [InlineData("ERR stat: unrecognized option '--context'", "--context", "f")]
    [InlineData("ERR stat: option '--f' is ambiguous; possibilities: '--file-system' '--format'", "--f", "x")]
    [InlineData("ERR stat: option '-L' is recognized but not supported by ps-bash", "-L", "f")]
    [InlineData("ERR stat: option '--dereference' is recognized but not supported by ps-bash", "--dereference", "f")]
    [InlineData("ERR stat: option '--dereference' is recognized but not supported by ps-bash", "--de", "f")]
    [InlineData("ERR stat: option '-f' is recognized but not supported by ps-bash", "-f", "f")]
    [InlineData("ERR stat: option '--file-system' is recognized but not supported by ps-bash", "--file", "f")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void ExitStatus_UsageIs1_UnsupportedIs2()
    {
        Assert.Equal(1, InvokeBashStatCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashStatCommand.ScanArgs(new[] { "-L" }).ErrorExitCode);
        Assert.True(InvokeBashStatCommand.ScanArgs(new[] { "--ver" }).Has("version"));
    }
}
