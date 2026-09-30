using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for gzip (shared ordered parser), checked against GNU gzip 1.12
/// (`wsl bash`): usage errors exit 1; the last of -1..-9/--fast/--best wins (`-10` = `-1` then the
/// invalid `-0`); `--s` is ambiguous ('--stdout' '--silent' '--synchronous' '--suffix'); `-S` needs a
/// value; `-L`/`-V`/`-h`/`-H` are info options; -a -m -M -n -N --rsyncable --synchronous are accepted
/// no-ops; `-Z`/`--lzw`/`-b`/`--bits` (LZW `compress` format) are refused with exit 2.
/// </summary>
public class GzipArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashGzipCommand.Plan(argv);
        if (p.Parsed.Error is { } e) return "ERR " + e.Message("gzip");
        string flags = string.Concat(
            p.Decompress ? "d" : "", p.ToStdout ? "c" : "", p.Keep ? "k" : "", p.Force ? "f" : "",
            p.Verbose ? "v" : "", p.List ? "l" : "", p.Test ? "t" : "", p.Recursive ? "r" : "", p.Quiet ? "q" : "");
        return $"flags={flags} level={p.Level} suffix={p.Suffix} ops=[{string.Join(",", p.Operands)}]";
    }

    [Theory]
    [InlineData("flags= level=6 suffix=.gz ops=[f]", "f")]
    [InlineData("flags=d level=6 suffix=.gz ops=[f.gz]", "-d", "f.gz")]
    [InlineData("flags=d level=6 suffix=.gz ops=[f.gz]", "--decompress", "f.gz")]
    [InlineData("flags=d level=6 suffix=.gz ops=[f.gz]", "--uncompress", "f.gz")]
    [InlineData("flags=d level=6 suffix=.gz ops=[f.gz]", "--dec", "f.gz")]  // FIX (abbreviation)
    [InlineData("flags=c level=6 suffix=.gz ops=[f]", "--stdout", "f")]
    [InlineData("flags=c level=6 suffix=.gz ops=[f]", "--to-stdout", "f")]
    [InlineData("flags=c level=6 suffix=.gz ops=[f]", "--std", "f")]  // FIX
    [InlineData("flags=dck level=6 suffix=.gz ops=[f]", "-dck", "f")]
    [InlineData("flags=dck level=6 suffix=.gz ops=[]", "-cdk")]
    [InlineData("flags=fv level=6 suffix=.gz ops=[f]", "-fv", "f")]
    [InlineData("flags=lq level=6 suffix=.gz ops=[f]", "--list", "--silent", "f")]
    [InlineData("flags=tr level=6 suffix=.gz ops=[d]", "-t", "-r", "d")]
    [InlineData("flags=k level=6 suffix=.gz ops=[f]", "--k", "f")]
    [InlineData("flags= level=9 suffix=.gz ops=[f]", "-9", "f")]
    [InlineData("flags= level=1 suffix=.gz ops=[f]", "--fast", "f")]
    [InlineData("flags= level=9 suffix=.gz ops=[f]", "--best", "f")]
    [InlineData("flags= level=9 suffix=.gz ops=[f]", "-1", "-9", "f")]  // last wins
    [InlineData("flags= level=1 suffix=.gz ops=[f]", "--best", "--fast", "f")]
    [InlineData("flags= level=9 suffix=.gz ops=[f]", "-19", "f")]
    [InlineData("flags=v level=9 suffix=.gz ops=[f]", "-9v", "f")]
    [InlineData("flags= level=6 suffix=.z ops=[f]", "-S", ".z", "f")]
    [InlineData("flags= level=6 suffix=.z ops=[f]", "-S.z", "f")]
    [InlineData("flags= level=6 suffix=.z ops=[f]", "--suffix=.z", "f")]
    [InlineData("flags= level=6 suffix=.z ops=[f]", "--suffix", ".z", "f")]
    [InlineData("flags=v level=6 suffix=z ops=[f]", "-vSz", "f")]
    [InlineData("flags= level=6 suffix=.gz ops=[f]", "-a", "-m", "-M", "-n", "-N", "--rsyncable", "--synchronous", "f")]
    [InlineData("flags= level=6 suffix=.gz ops=[f]", "--no-name", "--name", "--ascii", "f")]
    [InlineData("flags= level=6 suffix=.gz ops=[-f]", "--", "-f")]
    [InlineData("flags=c level=6 suffix=.gz ops=[-]", "-c", "-")]
    [InlineData("flags=c level=6 suffix=.gz ops=[f]", "f", "-c")]  // FIX (options after operands)
    // usage errors (exit 1)
    [InlineData("ERR gzip: invalid option -- 'z'", "-z", "f")]
    [InlineData("ERR gzip: invalid option -- 'x'", "-x")]
    [InlineData("ERR gzip: invalid option -- 'T'", "-T", "f")]
    [InlineData("ERR gzip: invalid option -- 'e'", "-e", "f")]
    [InlineData("ERR gzip: invalid option -- '0'", "-10", "f")]
    [InlineData("ERR gzip: unrecognized option '--zzz'", "--zzz", "f")]  // FIX (exit 1; was 2 / a file name)
    [InlineData("ERR gzip: option requires an argument -- 'S'", "-S")]  // FIX (was: ignored)
    [InlineData("ERR gzip: option '--suffix' requires an argument", "--suffix")]
    [InlineData("ERR gzip: option '--f' is ambiguous; possibilities: '--force' '--fast'", "--f", "f")]
    [InlineData("ERR gzip: option '--s' is ambiguous; possibilities: '--stdout' '--silent' '--synchronous' '--suffix'", "--s", "f")]
    [InlineData("ERR gzip: option '--v' is ambiguous; possibilities: '--verbose' '--version'", "--v", "f")]
    [InlineData("ERR gzip: option '--n' is ambiguous; possibilities: '--no-name' '--name'", "--n", "f")]
    [InlineData("ERR gzip: option '--r' is ambiguous; possibilities: '--recursive' '--rsyncable'", "--r")]
    [InlineData("ERR gzip: option '--l' is ambiguous; possibilities: '--list' '--license' '--lzw'", "--l")]
    [InlineData("ERR gzip: option '--t' is ambiguous; possibilities: '--to-stdout' '--test'", "--t")]
    [InlineData("ERR gzip: option '--b' is ambiguous; possibilities: '--best' '--bits'", "--b")]
    [InlineData("ERR gzip: option '--stdout' doesn't allow an argument", "--stdout=1", "f")]
    // the LZW (compress) format is refused (exit 2)
    [InlineData("ERR gzip: option '-Z' is recognized but not supported by ps-bash", "-Z", "f")]
    [InlineData("ERR gzip: option '--lzw' is recognized but not supported by ps-bash", "--lzw", "f")]
    [InlineData("ERR gzip: option '-b' is recognized but not supported by ps-bash", "-b", "9", "f")]
    [InlineData("ERR gzip: option '--bits' is recognized but not supported by ps-bash", "--bits=9", "f")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void InfoOptions_HelpVersionLicense()
    {
        foreach (var a in new[] { "-h", "-H", "--help", "--he" })
            Assert.True(InvokeBashGzipCommand.ScanArgs(new[] { a }).Has("help"), a);
        foreach (var a in new[] { "-V", "--version", "-L", "--license", "--vers" })
            Assert.True(InvokeBashGzipCommand.ScanArgs(new[] { a }).Has("version"), a);
    }

    [Fact]
    public void ExitStatus_UsageIs1_UnsupportedIs2()
    {
        Assert.Equal(1, InvokeBashGzipCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
        Assert.Equal(1, InvokeBashGzipCommand.ScanArgs(new[] { "--zzz" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashGzipCommand.ScanArgs(new[] { "-Z" }).ErrorExitCode);
    }
}
