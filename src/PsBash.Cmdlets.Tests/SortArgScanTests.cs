using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for sort (shared ordered parser + key/tab/option validation). Checked
/// against GNU sort 9.4 (`wsl bash`): usage and validation errors exit 2 (except an invalid
/// --sort/--check argument: 1), --debug valid-but-unsupported
/// (ps-bash, exit 2); -R/-z/--files0-from/--random-source are implemented, a key with its own ordering option does not inherit the global ones.
/// </summary>
public class SortArgScanTests
{
    // One compact line per key: field.char,field.char then the EFFECTIVE option letters (after global inheritance).
    private static string Letters(SortKeyOpts o) =>
        (o.BlankStart ? "b" : "") + (o.Dict ? "d" : "") + (o.Fold ? "f" : "") + (o.General ? "g" : "")
        + (o.Human ? "h" : "") + (o.NonPrint ? "i" : "") + (o.Month ? "M" : "") + (o.Numeric ? "n" : "")
        + (o.Version ? "V" : "") + (o.Reverse ? "r" : "");

    private static string Scan(string[] argv)
    {
        var s = InvokeBashSortCommand.Plan(argv);
        if (s.Parsed.Error is { } e) return "ERR " + e.Message("sort");
        if (s.Error is { } m) return $"ERR {s.ErrorExit} " + m.Split('\n')[0];
        var keys = string.Join(" ", s.Plan.Keys.Select(k => k.Whole ? $"[{Letters(k.Opts)}]" :
            $"{k.StartField}.{k.StartChar},{k.EndField}.{k.EndChar}:{Letters(k.Opts)}"));
        return $"{keys} u={s.Plan.Unique} s={s.Plan.Stable} m={s.Plan.Merge} chk={s.CheckMode} t={(s.Plan.Delimiter is { } d ? (d == '\0' ? "NUL" : d.ToString()) : "-")} o={s.Output ?? "-"} ops=[{string.Join(",", s.Operands)}]";
    }

    [Theory]
    [InlineData("[] u=False s=False m=False chk=0 t=- o=- ops=[]")]
    [InlineData("[r] u=False s=False m=False chk=0 t=- o=- ops=[]", "-r")]
    [InlineData("[nr] u=True s=False m=False chk=0 t=- o=- ops=[f]", "-rnu", "f")]
    [InlineData("[n] u=False s=False m=False chk=0 t=- o=- ops=[]", "--numeric-sort")]
    [InlineData("[n] u=False s=False m=False chk=0 t=- o=- ops=[]", "--numer")]  // FIX (abbreviation)
    [InlineData("[n] u=False s=False m=False chk=0 t=- o=- ops=[]", "--sort=n")]
    [InlineData("[n] u=False s=False m=False chk=0 t=- o=- ops=[]", "--sort=nu")]   // FIX (argmatch abbreviation)
    [InlineData("[g] u=False s=False m=False chk=0 t=- o=- ops=[]", "--sort=g")]
    [InlineData("[M] u=False s=False m=False chk=0 t=- o=- ops=[]", "--sort=m")]
    [InlineData("[bi] u=False s=False m=False chk=0 t=- o=- ops=[]", "-bi")]   // FIX (-i was unsupported)
    [InlineData("[i] u=False s=False m=False chk=0 t=- o=- ops=[]", "-i")]
    [InlineData("[] u=False s=False m=True chk=0 t=- o=- ops=[a,b]", "-m", "a", "b")]   // FIX (-m was unsupported)
    [InlineData("[] u=False s=False m=False chk=1 t=- o=- ops=[]", "-c")]
    [InlineData("[] u=False s=False m=False chk=2 t=- o=- ops=[]", "-C")]   // FIX
    [InlineData("[] u=False s=False m=False chk=2 t=- o=- ops=[]", "--check=quiet")]
    [InlineData("[] u=False s=False m=False chk=2 t=- o=- ops=[]", "--check=s")]
    [InlineData("[] u=False s=False m=False chk=1 t=- o=- ops=[]", "--check")]
    [InlineData("[] u=False s=False m=False chk=1 t=- o=- ops=[]", "--check=diagnose-first")]
    [InlineData("[] u=False s=False m=False chk=0 t=- o=o ops=[]", "-o", "o")]
    [InlineData("[] u=False s=False m=False chk=0 t=- o=o ops=[]", "-oo")]
    [InlineData("[] u=False s=False m=False chk=0 t=- o=o ops=[]", "--output=o")]
    [InlineData("[] u=False s=False m=False chk=0 t=- o=o ops=[]", "-o", "o", "-o", "o")]  // the same file twice is fine
    [InlineData("[] u=False s=False m=False chk=0 t=: o=- ops=[]", "-t:")]
    [InlineData("[] u=False s=False m=False chk=0 t=: o=- ops=[]", "-t", ":")]
    [InlineData("[] u=False s=False m=False chk=0 t=: o=- ops=[]", "--field-separator=:")]
    [InlineData("[] u=False s=False m=False chk=0 t=NUL o=- ops=[]", "-t", "\\0")]
    [InlineData("[] u=False s=False m=False chk=0 t=- o=- ops=[]", "-S", "1M", "-T", "/tmp", "--parallel=2", "--batch-size=2", "--compress-program=gzip")]  // accepted, no effect
    [InlineData("[] u=False s=False m=False chk=0 t=- o=- ops=[]", "-S", "10%")]
    // keys
    [InlineData("2.0,0.0: u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2")]
    [InlineData("2.0,0.0: u=False s=False m=False chk=0 t=- o=- ops=[]", "-k", "2")]
    [InlineData("2.0,0.0: u=False s=False m=False chk=0 t=- o=- ops=[]", "--key=2")]
    [InlineData("2.0,2.0: u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2,2")]
    [InlineData("1.2,1.3: u=False s=False m=False chk=0 t=- o=- ops=[]", "-k1.2,1.3")]
    [InlineData("2.0,2.0:n u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2,2n")]
    [InlineData("2.0,2.0:nr u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2,2nr")]
    [InlineData("2.0,0.0:V u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2V")]   // FIX (modifiers other than n r b were silently dropped)
    [InlineData("2.0,0.0:f u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2f")]
    [InlineData("2.0,0.0:g u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2g")]
    [InlineData("2.0,0.0:M u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2M")]
    [InlineData("2.0,0.0:b u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2b")]
    [InlineData("2.0,2.0:n u=False s=False m=False chk=0 t=- o=- ops=[]", "-k2n,2")]   // options may sit on either position
    [InlineData("2.0,0.0:n u=False s=False m=False chk=0 t=- o=- ops=[]", "-n", "-k2")]  // a plain key inherits -n
    [InlineData("2.0,0.0:nr u=False s=False m=False chk=0 t=- o=- ops=[]", "-nr", "-k2")]  // ... and -r
    [InlineData("2.0,2.0:n u=False s=False m=False chk=0 t=- o=- ops=[]", "-r", "-k2,2n")]  // FIX: a key with its own option does NOT inherit -r
    [InlineData("1.0,1.0:n 2.0,0.0:r u=False s=False m=False chk=0 t=- o=- ops=[]", "-r", "-k1,1n", "-k2")]
    [InlineData("1.0,1.0:r u=False s=False m=False chk=0 t=- o=- ops=[]", "-n", "-k1,1r")]  // 'r' alone still stops inheritance
    [InlineData("1.0,1.0:n 2.0,0.0:nr u=False s=False m=False chk=0 t=- o=- ops=[]", "-nr", "-k1,1n", "-k2")]
    [InlineData("1.0,1.0:f u=False s=False m=False chk=0 t=- o=- ops=[]", "-n", "-k1,1f")]
    [InlineData("1.0,0.0: 2.0,0.0: u=False s=False m=False chk=0 t=- o=- ops=[]", "-k1", "-k2")]
    [InlineData("1.0,0.0:r u=False s=False m=False chk=0 t=- o=- ops=[f]", "-rk1", "f")]  // bundle: -r then -k1
    [InlineData("1.0,0.0: u=False s=False m=False chk=0 t=- o=- ops=[f,g]", "f", "-k1", "g")]  // options after operands
    // errors: usage = 2
    [InlineData("ERR sort: invalid option -- 'x'", "-x")]
    [InlineData("ERR sort: unrecognized option '--nope'", "--nope")]
    [InlineData("ERR sort: option requires an argument -- 't'", "-t")]
    [InlineData("ERR sort: option requires an argument -- 'k'", "-k")]
    [InlineData("ERR sort: option requires an argument -- 'S'", "-S")]
    [InlineData("ERR sort: option '--output' requires an argument", "--output")]
    [InlineData("ERR sort: option '--r' is ambiguous; possibilities: '--random-sort' '--random-source' '--reverse'", "--r")]
    [InlineData("ERR sort: option '--s' is ambiguous; possibilities: '--sort' '--stable'", "--s")]
    [InlineData("ERR sort: option '--c' is ambiguous; possibilities: '--check' '--compress-program'", "--c")]
    [InlineData("ERR sort: option '--m' is ambiguous; possibilities: '--merge' '--month-sort'", "--m")]
    [InlineData("ERR sort: option '--ig' is ambiguous; possibilities: '--ignore-leading-blanks' '--ignore-case' '--ignore-nonprinting'", "--ig")]
    [InlineData("ERR sort: option '--fi' is ambiguous; possibilities: '--files0-from' '--field-separator'", "--fi")]
    [InlineData("ERR sort: option '--d' is ambiguous; possibilities: '--debug' '--dictionary-order'", "--d")]
    [InlineData("ERR sort: option '--b' is ambiguous; possibilities: '--batch-size' '--buffer-size'", "--b")]
    [InlineData("ERR sort: option '--ver' is ambiguous; possibilities: '--version-sort' '--version'", "--ver")]
    [InlineData("ERR sort: option '--h' is ambiguous; possibilities: '--human-numeric-sort' '--help'", "--h")]
    [InlineData("ERR 2 sort: empty tab", "-t", "")]
    [InlineData("ERR 2 sort: multi-character tab 'ab'", "-tab")]
    [InlineData("ERR 2 sort: incompatible tabs", "-t:", "-t,")]
    [InlineData("ERR 2 sort: multiple output files specified", "-o", "a", "-o", "b")]
    [InlineData("ERR 2 sort: options '-gn' are incompatible", "-n", "-g")]
    [InlineData("ERR 2 sort: options '-gn' are incompatible", "-k1n,1g")]
    [InlineData("ERR 2 sort: options '-nV' are incompatible", "-nV")]
    [InlineData("ERR 2 sort: options '-dn' are incompatible", "-dn")]
    [InlineData("ERR 2 sort: options '-Mn' are incompatible", "-Mn")]
    [InlineData("ERR 2 sort: options '-Mn' are incompatible", "-k1Mn")]
    [InlineData("ERR 2 sort: options '-cC' are incompatible", "-c", "-C")]
    [InlineData("ERR 2 sort: extra operand 'f2' not allowed with -c", "-c", "f1", "f2")]
    [InlineData("ERR 2 sort: field number is zero: invalid field specification '0'", "-k", "0")]
    [InlineData("ERR 2 sort: field number is zero: invalid field specification '0.1'", "-k", "0.1")]
    [InlineData("ERR 2 sort: field number is zero: invalid field specification '1,0'", "-k", "1,0")]
    [InlineData("ERR 2 sort: character offset is zero: invalid field specification '1.0'", "-k", "1.0")]
    [InlineData("ERR 2 sort: invalid number at field start: invalid count at start of 'a'", "-k", "a")]
    [InlineData("ERR 2 sort: invalid number at field start: invalid count at start of ''", "-k", "")]
    [InlineData("ERR 2 sort: invalid number at field start: invalid count at start of ',2'", "-k", ",2")]
    [InlineData("ERR 2 sort: invalid number after ',': invalid count at start of ''", "-k", "1,")]
    [InlineData("ERR 2 sort: invalid number after ',': invalid count at start of 'x'", "-k", "1,x")]
    [InlineData("ERR 2 sort: invalid number after '.': invalid count at start of 'x'", "-k", "1.x")]
    [InlineData("ERR 2 sort: stray character in field spec: invalid field specification '1,2,3'", "-k", "1,2,3")]
    [InlineData("ERR 2 sort: stray character in field spec: invalid field specification '1x'", "-k", "1x")]
    [InlineData("ERR 2 sort: stray character in field spec: invalid field specification '1.2.3'", "-k", "1.2.3")]
    [InlineData("ERR 2 sort: invalid suffix in -S argument '1x'", "-S", "1x")]
    [InlineData("ERR 2 sort: invalid -S argument 'x'", "-S", "x")]
    [InlineData("ERR 2 sort: number in parallel must be nonzero", "--parallel=0")]
    [InlineData("ERR 2 sort: invalid --parallel argument 'x'", "--parallel=x")]
    [InlineData("ERR 2 sort: minimum --batch-size argument is '2'", "--batch-size=1")]
    // argmatch errors: exit 1 (GNU)
    [InlineData("ERR 1 sort: invalid argument 'x' for '--sort'", "--sort=x")]
    [InlineData("ERR 1 sort: invalid argument 'x' for '--check'", "--check=x")]
    // valid but refused by ps-bash (exit 2)
    [InlineData("ERR sort: option '--debug' is recognized but not supported by ps-bash", "--debug")]
    [InlineData("ERR 2 sort: options '-nR' are incompatible", "-n", "-R")]
    [InlineData("ERR 2 sort: options '-nR' are incompatible", "-R", "-n")]
    [InlineData("ERR 2 sort: options '-nR' are incompatible", "-k1nR")]
    [InlineData("ERR 2 sort: extra operand 'f'", "--files0-from=l", "f")]
    [InlineData("ERR sort: option '--random-source' requires an argument", "--random-source")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Theory]
    [InlineData("-R")]
    [InlineData("--random-sort")]
    [InlineData("--sort=random")]
    [InlineData("--sort=r")]
    [InlineData("-k1R")]
    [InlineData("-zR")]
    [InlineData("-z")]
    [InlineData("--zero-terminated")]
    [InlineData("--files0-from=x")]
    [InlineData("--random-source=x")]
    public void ZeroRandomAndFiles0_AreAccepted(string arg) => Assert.False(Scan(new[] { arg }).StartsWith("ERR"));

    [Fact]
    public void RandomKey_FlagsAndPlanFields()
    {
        Assert.True(InvokeBashSortCommand.Plan(new[] { "-R" }).Plan.UsesRandom);
        Assert.True(InvokeBashSortCommand.Plan(new[] { "-k2,2R" }).Plan.UsesRandom);
        Assert.False(InvokeBashSortCommand.Plan(new[] { "-r" }).Plan.UsesRandom);
        Assert.True(InvokeBashSortCommand.Plan(new[] { "-z" }).Plan.Zero);
        Assert.Equal("l", InvokeBashSortCommand.Plan(new[] { "--files0-from=l" }).Plan.Files0From);
        Assert.Equal("s", InvokeBashSortCommand.Plan(new[] { "--random-source=s", "-R" }).Plan.RandomSource);
        Assert.False(InvokeBashSortCommand.Plan(new[] { "-R", "-V" }).Parsed.HasError);   // R and V share one exclusivity bucket (GNU)
    }

    [Fact]
    public void ExitStatus_UsageIs2_UnsupportedIs2()
    {
        Assert.Equal(2, InvokeBashSortCommand.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashSortCommand.ScanArgs(new[] { "--debug" }).ErrorExitCode);
        Assert.True(InvokeBashSortCommand.ScanArgs(new[] { "--he" }).Has("help"));
        Assert.True(InvokeBashSortCommand.ScanArgs(new[] { "-V" }).Has("version-sort"));
    }
}
