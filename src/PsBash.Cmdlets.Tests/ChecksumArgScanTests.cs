using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution + line-format tables for md5sum/sha1sum/sha256sum (shared ChecksumEngine), checked
/// against GNU coreutils 9.4 (`wsl bash`): usage errors exit 1; `-c`-only options (--warn --status --quiet
/// --strict --ignore-missing) without -c are "meaningful only when verifying checksums"; -c with
/// --tag/-b/-t/-z is "meaningless"/"not supported"; the last of -b/-t wins; `--t` is ambiguous
/// ('--tag' '--text'), `--s` ('--status' '--strict'); -V and -h are NOT options (only --version/--help).
/// </summary>
public class ChecksumArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = ChecksumEngine.Plan("md5sum", argv);
        if (p.Parsed.Error is { } e) return "ERR " + e.Message("md5sum");
        if (p.Error is { } m) return "ERR " + m;
        string bin = p.Binary switch { true => "b", false => "t", null => "-" };
        string flags = string.Concat(
            p.Check ? "c" : "", p.Warn ? "w" : "", p.Quiet ? "q" : "", p.Status ? "s" : "", p.Strict ? "S" : "",
            p.IgnoreMissing ? "i" : "", p.Tag ? "T" : "", p.Zero ? "z" : "");
        return $"mode={bin} flags={flags} ops=[{string.Join(",", p.Operands)}]";
    }

    [Theory]
    [InlineData("mode=- flags= ops=[a]", "a")]
    [InlineData("mode=- flags= ops=[]")]
    [InlineData("mode=b flags= ops=[a]", "-b", "a")]
    [InlineData("mode=b flags= ops=[a]", "--binary", "a")]
    [InlineData("mode=b flags= ops=[a]", "--bin", "a")]  // FIX (abbreviation)
    [InlineData("mode=t flags= ops=[a]", "-t", "a")]
    [InlineData("mode=t flags= ops=[a]", "-bt", "a")]  // last wins
    [InlineData("mode=b flags= ops=[a]", "--text", "--binary", "a")]
    [InlineData("mode=- flags=T ops=[a]", "--tag", "a")]
    [InlineData("mode=- flags=T ops=[a]", "--ta", "a")]
    [InlineData("mode=- flags=z ops=[a]", "-z", "a")]
    [InlineData("mode=- flags=z ops=[a]", "--zero", "a")]
    [InlineData("mode=b flags=Tz ops=[a]", "--tag", "--zero", "-b", "a")]
    [InlineData("mode=- flags=c ops=[sums]", "-c", "sums")]
    [InlineData("mode=- flags=c ops=[sums]", "--check", "sums")]
    [InlineData("mode=- flags=c ops=[]", "-c")]
    [InlineData("mode=- flags=cw ops=[sums]", "-cw", "sums")]
    [InlineData("mode=- flags=cw ops=[sums]", "-c", "--warn", "sums")]
    [InlineData("mode=- flags=cq ops=[sums]", "-c", "--quiet", "sums")]
    [InlineData("mode=- flags=cs ops=[sums]", "-c", "--status", "sums")]
    [InlineData("mode=- flags=cS ops=[sums]", "-c", "--strict", "sums")]
    [InlineData("mode=- flags=ci ops=[sums]", "-c", "--ignore-missing", "sums")]
    [InlineData("mode=- flags=cwqSi ops=[sums]", "-cw", "--quiet", "--strict", "--ignore-missing", "sums")]
    [InlineData("mode=- flags=c ops=[a,-,b]", "-c", "a", "-", "b")]
    [InlineData("mode=- flags= ops=[-c]", "--", "-c")]
    [InlineData("mode=- flags=c ops=[a]", "a", "-c")]  // FIX (options after operands)
    // option-combination errors (exit 1)
    [InlineData("ERR md5sum: the --warn option is meaningful only when verifying checksums", "--warn", "a")]
    [InlineData("ERR md5sum: the --warn option is meaningful only when verifying checksums", "-w", "a")]
    [InlineData("ERR md5sum: the --quiet option is meaningful only when verifying checksums", "--quiet", "a")]
    [InlineData("ERR md5sum: the --status option is meaningful only when verifying checksums", "--status", "a")]
    [InlineData("ERR md5sum: the --strict option is meaningful only when verifying checksums", "--strict", "a")]
    [InlineData("ERR md5sum: the --ignore-missing option is meaningful only when verifying checksums", "--ignore-missing", "a")]
    [InlineData("ERR md5sum: the --ignore-missing option is meaningful only when verifying checksums", "--i", "a")]
    [InlineData("ERR md5sum: the --tag option is meaningless when verifying checksums", "-c", "--tag", "sums")]
    [InlineData("ERR md5sum: the --tag option is meaningless when verifying checksums", "--tag", "-c", "sums")]
    [InlineData("ERR md5sum: the --zero option is not supported when verifying checksums", "-c", "-z", "sums")]
    [InlineData("ERR md5sum: the --binary and --text options are meaningless when verifying checksums", "-c", "-b", "sums")]
    [InlineData("ERR md5sum: the --binary and --text options are meaningless when verifying checksums", "-c", "-t", "sums")]
    // parse errors (exit 1)
    [InlineData("ERR md5sum: invalid option -- 'x'", "-x", "a")]
    [InlineData("ERR md5sum: unrecognized option '--zzz'", "--zzz", "a")]
    [InlineData("ERR md5sum: invalid option -- 'V'", "-V")]
    [InlineData("ERR md5sum: invalid option -- 'h'", "-h")]
    [InlineData("ERR md5sum: invalid option -- 'a'", "-a")]
    [InlineData("ERR md5sum: option '--check' doesn't allow an argument", "--check=sums")]
    [InlineData("ERR md5sum: option '--binary' doesn't allow an argument", "--binary=1", "a")]
    [InlineData("ERR md5sum: option '--t' is ambiguous; possibilities: '--tag' '--text'", "--t", "a")]
    [InlineData("ERR md5sum: option '--s' is ambiguous; possibilities: '--status' '--strict'", "--s", "a")]
    [InlineData("ERR md5sum: option '--st' is ambiguous; possibilities: '--status' '--strict'", "--st", "a")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void ExitStatus_UsageIs1_InfoOptionsResolve()
    {
        Assert.Equal(1, ChecksumEngine.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.True(ChecksumEngine.ScanArgs(new[] { "--version" }).Has("version"));
        Assert.True(ChecksumEngine.ScanArgs(new[] { "--vers" }).Has("version"));
        Assert.True(ChecksumEngine.ScanArgs(new[] { "--help" }).Has("help"));
    }

    // ----- output lines: byte-for-byte coreutils 9.4 -----

    [Theory]
    [InlineData("HASH  a", "a", false, false, false)]
    [InlineData("HASH *a", "a", true, false, false)]
    [InlineData("MD5 (a) = HASH", "a", false, true, false)]
    [InlineData("MD5 (a) = HASH", "a", true, true, false)]  // --tag beats -b
    [InlineData("\\HASH  e\\\\f", "e\\f", false, false, false)]  // a backslash in the name: `\` prefix, `\\`
    [InlineData("\\HASH  a\\nb", "a\nb", false, false, false)]  // a newline: `\n`
    [InlineData("\\MD5 (e\\\\f) = HASH", "e\\f", false, true, false)]
    [InlineData("HASH  e\\f", "e\\f", false, false, true)]  // -z never escapes
    [InlineData("HASH  c d", "c d", false, false, false)]
    public void FormatLine_MatchesCoreutils(string expected, string name, bool binary, bool tag, bool zero)
        => Assert.Equal(expected, ChecksumEngine.FormatLine("HASH", name, "MD5", binary, tag, zero));

    // ----- checksum-list line parsing -----

    private const string H32 = "b1946ac92492d2347c6235b4d2611184";

    [Theory]
    [InlineData(H32 + "  a", true, "a")]
    [InlineData(H32 + " *a", true, "a")]
    [InlineData(H32 + " a", true, "a")]                      // GNU accepts a single separator space
    [InlineData(H32 + "  c d", true, "c d")]
    [InlineData("B1946AC92492D2347C6235B4D2611184  a", true, "a")]
    [InlineData("MD5 (a) = " + H32, true, "a")]              // BSD --tag format
    [InlineData("MD5 (c d) = " + H32, true, "c d")]
    [InlineData("\\" + H32 + "  a\\\\b", true, "a\\b")]       // escaped name
    [InlineData("\\" + H32 + "  a\\nb", true, "a\nb")]
    [InlineData("SHA1 (a) = " + H32, false, "")]             // another algorithm's tag
    [InlineData("abcd  a", false, "")]                       // wrong hash length
    [InlineData(H32, false, "")]
    [InlineData("junk", false, "")]
    [InlineData(H32 + "  ", false, "")]
    public void TryParseCheckLine_MatchesCoreutils(string line, bool ok, string name)
    {
        bool got = ChecksumEngine.TryParseCheckLine(line, "MD5", 32, out var parsed);
        Assert.Equal(ok, got);
        if (ok) Assert.Equal(name, parsed.Name);
    }
}
