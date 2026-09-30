using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for file (shared ordered parser). Checked against file-5.45
/// (`wsl bash`): -b/--brief, -i/--mime (= --mime-type + --mime-encoding), --mime-type,
/// --mime-encoding, -L/--dereference and -h/--no-dereference (last wins; GNU default is NOT to
/// follow), -F/--separator STR, -0/--print0, -v/--version, accepted no-ops -n -N -p -S, unique long
/// prefixes (`--br`, `--mime-t`; `--mi` is ambiguous between '--mime' '--mime-type'
/// '--mime-encoding', `--m` also lists '--magic-file' first), usage errors exit 1 (file prints the
/// message then its usage text), no operand is a usage error. ps-bash refuses -z -Z -s -k -f -r -c -C
/// -d -E -l -m -e -P --apple --extension --exclude-quiet (exit 2).
/// </summary>
public class FileArgScanTests
{
    private static string Scan(string[] argv)
    {
        var f = InvokeBashFileCommand.Plan(argv);
        if (f.Parsed.Error is { } e) return "ERR " + e.Message("file");
        if (f.Error is { } m) return "ERR " + m.Split('\n')[0];
        return $"b={f.Brief} type={f.WantMimeType} enc={f.WantMimeEncoding} L={f.Follow} 0={f.Print0} sep={f.Separator} ops=[{string.Join(",", f.Operands)}]";
    }

    [Theory]
    [InlineData("b=False type=False enc=False L=False 0=False sep=: ops=[f]", "f")]
    [InlineData("b=True type=False enc=False L=False 0=False sep=: ops=[f]", "-b", "f")]
    [InlineData("b=True type=False enc=False L=False 0=False sep=: ops=[f]", "--br", "f")]  // FIX (abbreviation)
    [InlineData("b=False type=True enc=True L=False 0=False sep=: ops=[f]", "-i", "f")]
    [InlineData("b=False type=True enc=True L=False 0=False sep=: ops=[f]", "--mime", "f")]
    [InlineData("b=False type=True enc=False L=False 0=False sep=: ops=[f]", "--mime-type", "f")]  // FIX (was: unsupported)
    [InlineData("b=False type=True enc=False L=False 0=False sep=: ops=[f]", "--mime-t", "f")]
    [InlineData("b=False type=False enc=True L=False 0=False sep=: ops=[f]", "--mime-encoding", "f")]  // FIX
    [InlineData("b=False type=True enc=True L=False 0=False sep=: ops=[f]", "--mime-type", "--mime-encoding", "f")]
    [InlineData("b=True type=True enc=True L=False 0=False sep=: ops=[f]", "-bi", "f")]  // FIX (bundle)
    [InlineData("b=False type=False enc=False L=True 0=False sep=: ops=[f]", "-L", "f")]
    [InlineData("b=False type=False enc=False L=True 0=False sep=: ops=[f]", "--deref", "f")]
    [InlineData("b=False type=False enc=False L=False 0=False sep=: ops=[f]", "-L", "-h", "f")]  // last wins
    [InlineData("b=False type=False enc=False L=True 0=False sep=: ops=[f]", "-h", "-L", "f")]
    [InlineData("b=False type=False enc=False L=False 0=True sep=: ops=[f]", "-0", "f")]  // FIX
    [InlineData("b=False type=False enc=False L=False 0=False sep=@ ops=[f]", "-F", "@", "f")]  // FIX
    [InlineData("b=False type=False enc=False L=False 0=False sep=@ ops=[f]", "-F@", "f")]
    [InlineData("b=False type=False enc=False L=False 0=False sep=@ ops=[f]", "--separator=@", "f")]
    [InlineData("b=False type=False enc=False L=False 0=False sep=: ops=[f]", "-nNpS", "f")]  // accepted no-ops
    [InlineData("b=True type=False enc=False L=False 0=False sep=: ops=[f]", "f", "-b")]  // options after operands
    [InlineData("b=False type=False enc=False L=False 0=False sep=: ops=[-x]", "--", "-x")]
    [InlineData("b=False type=False enc=False L=False 0=False sep=: ops=[-]", "-")]
    [InlineData("ERR Usage: file [-bcCdEhikLlNnprsSvzZ0] [--apple] [--extension] [--mime-encoding]")]  // FIX (was: silent success)
    [InlineData("ERR Usage: file [-bcCdEhikLlNnprsSvzZ0] [--apple] [--extension] [--mime-encoding]", "-b")]
    [InlineData("ERR file: invalid option -- 'x'", "-x", "f")]
    [InlineData("ERR file: unrecognized option '--zzz'", "--zzz", "f")]
    [InlineData("ERR file: option '--mi' is ambiguous; possibilities: '--mime' '--mime-type' '--mime-encoding'", "--mi", "f")]
    [InlineData("ERR file: option '--m' is ambiguous; possibilities: '--magic-file' '--mime' '--mime-type' '--mime-encoding'", "--m", "f")]
    [InlineData("ERR file: option '--e' is ambiguous; possibilities: '--exclude' '--exclude-quiet' '--extension'", "--e", "f")]
    [InlineData("ERR file: option '--brief' doesn't allow an argument", "--brief=1", "f")]
    [InlineData("ERR file: option requires an argument -- 'F'", "-F")]
    [InlineData("ERR file: option '-k' is recognized but not supported by ps-bash", "-k", "f")]
    [InlineData("ERR file: option '--keep-going' is recognized but not supported by ps-bash", "--keep-going", "f")]
    [InlineData("ERR file: option '-z' is recognized but not supported by ps-bash", "-z", "f")]
    [InlineData("ERR file: option '-f' is recognized but not supported by ps-bash", "-f", "l")]
    [InlineData("ERR file: option '--apple' is recognized but not supported by ps-bash", "--apple", "f")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void Version_ShortAndAbbreviated_ResolveWithNoOperands()
    {
        var v = InvokeBashFileCommand.Plan(new[] { "-v" });
        Assert.True(v.Parsed.Has("version"));
        Assert.Null(v.Error);
        Assert.True(InvokeBashFileCommand.ScanArgs(new[] { "--vers" }).Has("version"));
        Assert.True(InvokeBashFileCommand.ScanArgs(new[] { "--he" }).Has("help"));
    }

    [Fact]
    public void ExitStatus_UsageIs1_UnsupportedIs2()
    {
        Assert.Equal(1, InvokeBashFileCommand.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashFileCommand.ScanArgs(new[] { "-k" }).ErrorExitCode);
    }
}
