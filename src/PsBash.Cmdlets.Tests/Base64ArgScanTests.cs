using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for base64 (shared ordered parser). Checked against GNU base64 9.4
/// (`wsl bash`): -d/--decode, -i/--ignore-garbage, -w/--wrap=COLS, unique long prefixes (`--dec`,
/// `--ig`, `--wr 20`, `--w=20`), bundles (`-dw0`, `-di`), a decimal COLS only (`-w x`, `-w -1`,
/// `-w 1K` = "invalid wrap size", exit 1), ONE FILE ("extra operand"; the old code silently
/// ignored the rest). GNU base64 has no other options, so unknown flags are usage errors (exit 1).
/// </summary>
public class Base64ArgScanTests
{
    private static string Scan(string[] argv)
    {
        var b = InvokeBashBase64Command.Plan(argv);
        if (b.Parsed.Error is { } e) return "ERR " + e.Message("base64");
        if (b.Error is { } m) return "ERR " + m;
        return $"d={b.Decode} i={b.IgnoreGarbage} w={b.Wrap} ops=[{string.Join(",", b.Operands)}]";
    }

    [Theory]
    [InlineData("d=False i=False w=76 ops=[]")]
    [InlineData("d=True i=False w=76 ops=[]", "-d")]
    [InlineData("d=True i=False w=76 ops=[]", "--decode")]
    [InlineData("d=True i=False w=76 ops=[]", "--dec")]  // FIX (abbreviation; --d is ambiguous-free too)
    [InlineData("d=True i=False w=76 ops=[]", "--d")]
    [InlineData("d=False i=True w=76 ops=[]", "-i")]
    [InlineData("d=False i=True w=76 ops=[]", "--ignore-garbage")]
    [InlineData("d=False i=True w=76 ops=[]", "--ig")]  // FIX
    [InlineData("d=True i=True w=76 ops=[]", "-di")]
    [InlineData("d=True i=True w=76 ops=[]", "--decode", "--ignore")]
    [InlineData("d=False i=False w=20 ops=[]", "-w", "20")]
    [InlineData("d=False i=False w=20 ops=[]", "-w20")]
    [InlineData("d=False i=False w=0 ops=[]", "-w0")]
    [InlineData("d=False i=False w=20 ops=[]", "--wrap=20")]
    [InlineData("d=False i=False w=20 ops=[]", "--wrap", "20")]
    [InlineData("d=False i=False w=20 ops=[]", "--wr", "20")]  // FIX
    [InlineData("d=False i=False w=20 ops=[]", "--w=20")]
    [InlineData("d=False i=False w=0 ops=[]", "-w", "10", "-w", "0")]  // last wins
    [InlineData("d=True i=False w=0 ops=[]", "-dw0")]  // FIX (bundle: w takes the rest)
    [InlineData("d=True i=True w=5 ops=[]", "-diw", "5")]
    [InlineData("d=False i=False w=76 ops=[f]", "f")]
    [InlineData("d=True i=False w=76 ops=[f]", "f", "-d")]  // FIX (options may follow operands)
    [InlineData("d=False i=False w=76 ops=[-d]", "--", "-d")]
    [InlineData("d=False i=False w=76 ops=[-]", "-")]
    [InlineData("d=False i=False w=2147483647 ops=[]", "-w", "99999999999999")]
    [InlineData("ERR base64: invalid wrap size: 'x'", "-w", "x")]  // FIX (was: silently 76)
    [InlineData("ERR base64: invalid wrap size: '-1'", "-w", "-1")]  // FIX
    [InlineData("ERR base64: invalid wrap size: '1K'", "-w", "1K")]
    [InlineData("ERR base64: invalid wrap size: ''", "-w", "")]
    [InlineData("ERR base64: extra operand 'g'", "f", "g")]  // FIX (was: ignored)
    [InlineData("ERR base64: option requires an argument -- 'w'", "-w")]
    [InlineData("ERR base64: option '--wrap' requires an argument", "--wrap")]
    [InlineData("ERR base64: invalid option -- 'x'", "-x")]
    [InlineData("ERR base64: invalid option -- 'z'", "-z")]
    [InlineData("ERR base64: unrecognized option '--nope'", "--nope")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void Abbreviations_ResolveHelpAndVersion()
    {
        Assert.True(InvokeBashBase64Command.ScanArgs(new[] { "--ver" }).Has("version"));
        Assert.Equal(1, InvokeBashBase64Command.ScanArgs(new[] { "-x" }).ErrorExitCode);
    }
}
