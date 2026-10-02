using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for comm (shared ordered parser). Checked against GNU comm 9.4
/// (`wsl bash`): -1 -2 -3 (any bundle: `-12`, `-123`; the old scan took only a pure 1/2/3 bundle),
/// --total, --output-delimiter=STR (FIX: it was refused; now implemented, and applies to the
/// `--total` line), unique long prefixes (`--tot`, `--output`), `--` ; refused with exit 2:
/// --check-order, --nocheck-order, -z/--zero-terminated. `-4` / `-x` are usage errors (exit 1).
/// </summary>
public class CommArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashCommCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("comm");
        string bits = (p.Has("s1") ? "1" : "") + (p.Has("s2") ? "2" : "") + (p.Has("s3") ? "3" : "");
        string delim = p.Last("delim") is { } d ? d.Value! : "-";
        return $"sup={bits} total={p.Has("total")} delim={delim} ops=[{string.Join(",", p.Operands())}]";
    }

    [Theory]
    [InlineData("sup= total=False delim=- ops=[a,b]", "a", "b")]
    [InlineData("sup=1 total=False delim=- ops=[a,b]", "-1", "a", "b")]
    [InlineData("sup=12 total=False delim=- ops=[a,b]", "-12", "a", "b")]
    [InlineData("sup=123 total=False delim=- ops=[a,b]", "-123", "a", "b")]
    [InlineData("sup=13 total=False delim=- ops=[a,b]", "-3", "-1", "a", "b")]
    [InlineData("sup=12 total=False delim=- ops=[a,b]", "a", "-1", "b", "-2")]  // FIX (options may follow operands)
    [InlineData("sup= total=True delim=- ops=[a,b]", "--total", "a", "b")]
    [InlineData("sup= total=True delim=- ops=[a,b]", "--tot", "a", "b")]  // FIX (abbreviation)
    [InlineData("sup=1 total=True delim=- ops=[a,b]", "-1", "--total", "a", "b")]
    [InlineData("sup= total=False delim=: ops=[a,b]", "--output-delimiter=:", "a", "b")]  // FIX (was: refused)
    [InlineData("sup= total=False delim=: ops=[a,b]", "--output-delimiter", ":", "a", "b")]
    [InlineData("sup= total=False delim=: ops=[a,b]", "--output=:", "a", "b")]
    [InlineData("sup= total=False delim= ops=[a,b]", "--output-delimiter=", "a", "b")]
    [InlineData("sup= total=False delim=; ops=[a,b]", "--output-delimiter=:", "--output-delimiter=;", "a", "b")]
    [InlineData("sup= total=False delim=- ops=[-1,b]", "--", "-1", "b")]
    [InlineData("sup= total=False delim=- ops=[-,b]", "-", "b")]
    [InlineData("ERR comm: invalid option -- '4'", "-4", "a", "b")]
    [InlineData("ERR comm: invalid option -- 'x'", "-x")]
    [InlineData("ERR comm: invalid option -- '4'", "-14")]
    [InlineData("ERR comm: unrecognized option '--nope'", "--nope")]
    [InlineData("ERR comm: option '--output-delimiter' requires an argument", "--output-delimiter")]
    [InlineData("ERR comm: option '--total' doesn't allow an argument", "--total=1")]
    [InlineData("sup= total=False delim=- ops=[a,b]", "--check-order", "a", "b")]
    [InlineData("sup= total=False delim=- ops=[a,b]", "--chec", "a", "b")]
    [InlineData("sup= total=False delim=- ops=[a,b]", "--nocheck-order", "a", "b")]
    [InlineData("sup= total=False delim=- ops=[a,b]", "--no", "a", "b")]
    [InlineData("sup= total=False delim=- ops=[a,b]", "-z", "a", "b")]
    [InlineData("sup=1 total=False delim=- ops=[a,b]", "--zero-terminated", "-1", "a", "b")]
    [InlineData("sup= total=False delim=- ops=[a,b]", "--zero", "a", "b")]
    [InlineData("ERR comm: option '--check-order' doesn't allow an argument", "--check-order=1")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Theory]   // the LAST of --check-order / --nocheck-order wins (oracle)
    [InlineData(InvokeBashCommCommand.OrderCheck.Default)]
    [InlineData(InvokeBashCommCommand.OrderCheck.Enabled, "--check-order")]
    [InlineData(InvokeBashCommCommand.OrderCheck.Disabled, "--nocheck-order")]
    [InlineData(InvokeBashCommCommand.OrderCheck.Enabled, "--nocheck-order", "--check-order")]
    [InlineData(InvokeBashCommCommand.OrderCheck.Disabled, "--check-order", "--nocheck-order")]
    [InlineData(InvokeBashCommCommand.OrderCheck.Enabled, "--chec")]
    [InlineData(InvokeBashCommCommand.OrderCheck.Disabled, "--n")]
    public void OrderCheck_LastWins(InvokeBashCommCommand.OrderCheck expected, params string[] argv)
        => Assert.Equal(expected, InvokeBashCommCommand.ResolveOrderCheck(InvokeBashCommCommand.ScanArgs(argv)));

    [Fact]
    public void UsageErrors_Exit1()
    {
        Assert.Equal(1, InvokeBashCommCommand.ScanArgs(new[] { "-4" }).ErrorExitCode);
        Assert.Null(InvokeBashCommCommand.ScanArgs(new[] { "-z" }).Error);
    }
}
