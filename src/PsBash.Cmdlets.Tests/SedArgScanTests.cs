using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for sed (shared ordered parser, GNU sed 4.9 option table). Every
/// expectation was read from the oracle (`wsl bash`, GNU sed 4.9): usage errors exit 1, the first
/// operand is the script only when no -e/-f was given, <c>-i</c>'s suffix is ATTACHED only
/// (<c>-i.bak</c>; <c>-i -e x</c> means no suffix; <c>-in</c> is suffix "n"), long options abbreviate
/// in GNU table order, --debug is refused by ps-bash (exit 2). Lines marked FIX used to be silently
/// wrong before the migration.
/// </summary>
public class SedArgScanTests
{
    private static string Scan(params string[] argv)
    {
        var s = InvokeBashSedCommand.Plan(argv);
        if (s.Parsed.Error is { } e) return $"ERR {s.Parsed.ErrorExitCode} {e.Message("sed")}";
        if (s.Parsed.Has("help")) return "HELP";
        if (s.Parsed.Has("version")) return "VERSION";
        string flags = (s.Quiet ? "n" : "") + (s.Extended ? "E" : "") + (s.Separate ? "s" : "") + (s.NullData ? "z" : "");
        string inPlace = s.InPlace ? (s.Suffix ?? "-") : "no";
        string src = string.Join(",", s.Sources.Select(x => (x.IsFile ? "f:" : "e:") + x.Value));
        return $"flags={flags} i={inPlace} src=[{src}] ops=[{string.Join(",", s.Operands)}]";
    }

    [Theory]
    // script / operands
    [InlineData("flags= i=no src=[] ops=[p]", "p")]
    [InlineData("flags= i=no src=[] ops=[p,a,b]", "p", "a", "b")]
    [InlineData("flags=n i=no src=[] ops=[2p,a]", "-n", "2p", "a")]
    [InlineData("flags=n i=no src=[e:p] ops=[a]", "-ne", "p", "a")]   // FIX (-ne was a bundle the binder could swallow)
    [InlineData("flags=n i=no src=[e:p] ops=[]", "-nep")]
    [InlineData("flags=n i=no src=[e:2p,e:3p] ops=[a]", "-n", "-e", "2p", "-e", "3p", "a")]
    [InlineData("flags= i=no src=[e:2p,f:s.sed,e:3p] ops=[]", "-e", "2p", "-f", "s.sed", "-e", "3p")]   // sources keep command-line order
    [InlineData("flags= i=no src=[e:p] ops=[]", "--expression=p")]
    [InlineData("flags= i=no src=[e:p] ops=[]", "--expression", "p")]
    [InlineData("flags= i=no src=[e:p] ops=[]", "--exp=p")]
    [InlineData("flags= i=no src=[e:p] ops=[]", "--expr", "p")]
    [InlineData("flags= i=no src=[f:x] ops=[]", "-f", "x")]
    [InlineData("flags= i=no src=[f:x] ops=[]", "-fx")]
    [InlineData("flags= i=no src=[f:x] ops=[]", "--file=x")]
    [InlineData("flags=n i=no src=[f:s] ops=[a]", "-nf", "s", "a")]
    [InlineData("flags=n i=no src=[] ops=[p,a]", "p", "-n", "a")]   // options after operands (permutation)
    [InlineData("flags= i=no src=[e:-n] ops=[]", "-e", "-n")]   // the value of -e may itself be `-n`
    [InlineData("flags=n i=no src=[] ops=[2p,a]", "-n", "--", "2p", "a")]
    [InlineData("flags= i=no src=[] ops=[-n,a]", "--", "-n", "a")]   // after -- the script may start with a dash
    [InlineData("flags= i=no src=[] ops=[s/a/b/,-]", "s/a/b/", "-")]
    // -n / -E / -r
    [InlineData("flags=n i=no src=[] ops=[p]", "--quiet", "p")]
    [InlineData("flags=n i=no src=[] ops=[p]", "--quie", "p")]
    [InlineData("flags=n i=no src=[] ops=[p]", "--silent", "p")]
    [InlineData("flags=n i=no src=[] ops=[p]", "--si", "p")]
    [InlineData("flags=n i=no src=[] ops=[p]", "--q", "p")]
    [InlineData("flags=E i=no src=[] ops=[p]", "-E", "p")]
    [InlineData("flags=E i=no src=[] ops=[p]", "-r", "p")]
    [InlineData("flags=E i=no src=[] ops=[p]", "--regexp-extended", "p")]
    [InlineData("flags=E i=no src=[] ops=[p]", "--regexp-e", "p")]
    [InlineData("flags=nE i=no src=[] ops=[p]", "-nE", "p")]
    [InlineData("flags=nE i=no src=[e:p] ops=[]", "-rne", "p")]
    // -i[SUFFIX] / --in-place[=SUFFIX]: attached suffix only
    [InlineData("flags= i=- src=[] ops=[p,f]", "-i", "p", "f")]
    [InlineData("flags= i=.bak src=[] ops=[p,f]", "-i.bak", "p", "f")]   // FIX (no backup was made, the suffix was read as flags)
    [InlineData("flags= i=- src=[e:x] ops=[f]", "-i", "-e", "x", "f")]   // a separate word is NOT the suffix
    [InlineData("flags=n i=- src=[] ops=[p]", "-ni", "p")]
    [InlineData("flags= i=n src=[] ops=[p]", "-in", "p")]   // `-in` is -i with suffix "n"
    [InlineData("flags= i=- src=[] ops=[p]", "--in-place", "p")]
    [InlineData("flags= i=.b2 src=[] ops=[p]", "--in-place=.b2", "p")]
    [InlineData("flags= i=- src=[] ops=[p]", "--in", "p")]
    [InlineData("flags= i=bak/* src=[] ops=[s,f]", "-ibak/*", "s", "f")]
    // -s / -z / accepted no-ops
    [InlineData("flags=s i=no src=[] ops=[p]", "-s", "p")]
    [InlineData("flags=s i=no src=[] ops=[p]", "--se", "p")]
    [InlineData("flags=s i=no src=[] ops=[p]", "--separate", "p")]
    [InlineData("flags=z i=no src=[] ops=[p]", "-z", "p")]   // FIX (-z was silently ignored)
    [InlineData("flags=z i=no src=[] ops=[p]", "--null-data", "p")]
    [InlineData("flags=z i=no src=[] ops=[p]", "--zero-terminated", "p")]
    [InlineData("flags=z i=no src=[] ops=[p]", "--null", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "-u", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "--unbuffered", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "-b", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "--binary", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "--posix", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "--sandbox", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "--follow-symlinks", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "--fo", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "-l", "5", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "-l5", "p")]
    [InlineData("flags= i=no src=[] ops=[p]", "--line-length=5", "p")]
    // errors: usage = exit 1
    [InlineData("ERR 1 sed: invalid option -- 'Q'", "-Q", "p")]
    [InlineData("ERR 1 sed: unrecognized option '--bogus'", "--bogus", "p")]
    [InlineData("ERR 1 sed: invalid option -- '2'", "-n2p", "a")]   // FIX (the digit was swallowed into the bundle)
    [InlineData("ERR 1 sed: option requires an argument -- 'e'", "-e")]
    [InlineData("ERR 1 sed: option requires an argument -- 'f'", "-f")]
    [InlineData("ERR 1 sed: option requires an argument -- 'l'", "-l")]
    [InlineData("ERR 1 sed: option '--expression' requires an argument", "--expression")]
    [InlineData("ERR 1 sed: option '--quiet' doesn't allow an argument", "--quiet=x", "p")]
    [InlineData("ERR 1 sed: option '--s' is ambiguous; possibilities: '--silent' '--sandbox' '--separate'", "--s", "p")]
    [InlineData("ERR 1 sed: option '--f' is ambiguous; possibilities: '--file' '--follow-symlinks'", "--f", "p")]
    [InlineData("ERR 2 sed: option '--debug' is recognized but not supported by ps-bash", "--debug", "p")]
    [InlineData("ERR 2 sed: option '--debug' is recognized but not supported by ps-bash", "--d", "p")]
    // info options
    [InlineData("HELP", "--help")]
    [InlineData("HELP", "--he")]
    [InlineData("VERSION", "--version")]
    [InlineData("VERSION", "--ve")]
    public void Plan_ResolvesArgvLikeGnuSed(string expected, params string[] argv) =>
        Assert.Equal(expected, Scan(argv));
}
