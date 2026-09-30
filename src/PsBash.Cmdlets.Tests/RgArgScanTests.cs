using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for rg (shared ordered parser with ripgrep 14.1 rules). Expectations read
/// from the oracle (`wsl rg`, ripgrep 14.1.0): usage errors exit 2, NO long-option abbreviation
/// (`--ign` is unrecognized), attached short values (<c>-A1 -g*.rs -epat</c>), counted <c>-uu</c>,
/// <c>--color</c> takes a REQUIRED value, the last of <c>-i/-s/-S</c> wins, <c>-A/-B</c> beat <c>-C</c>
/// in any order, <c>-e</c> makes every operand a path. ripgrep options the internal engine does not run
/// (<c>-t -m -j --json -P -r ...</c>) are refused with exit 2 instead of being silently dropped.
/// </summary>
public class RgArgScanTests
{
    private static string Scan(params string[] argv)
    {
        var r = InvokeBashRgCommand.Plan(argv);
        if (r.Parsed.Error is { } e) return $"ERR {r.Parsed.ErrorExitCode} {e.Message("rg")}";
        if (r.Error is { } m) return $"ERR {r.ErrorExit} {m}";
        if (r.Parsed.Has("help")) return "HELP";
        if (r.Parsed.Has("version")) return "VERSION";
        string flags = (r.CaseMode != '\0' ? r.CaseMode.ToString() : "") + (r.WordRegexp ? "w" : "") + (r.LineRegexp ? "x" : "")
            + (r.CountOnly ? "c" : "") + (r.FilesOnly ? "l" : "") + (r.LineNumbers ? "" : "N") + (r.OnlyMatching ? "o" : "")
            + (r.Invert ? "v" : "") + (r.Fixed ? "F" : "") + (r.Hidden ? "H" : "") + (r.NoIgnore ? "I" : "");
        return $"flags={flags} pat=[{string.Join(",", r.Patterns)}] ops=[{string.Join(",", r.Operands)}] ctx={r.After}/{r.Before} g=[{string.Join(",", r.Globs)}] u={r.Unrestricted}";
    }

    [Theory]
    [InlineData("flags= pat=[] ops=[c3,f] ctx=0/0 g=[] u=0", "c3", "f")]
    [InlineData("flags= pat=[c3] ops=[f] ctx=0/0 g=[] u=0", "-e", "c3", "f")]
    [InlineData("flags= pat=[c3] ops=[f] ctx=0/0 g=[] u=0", "-ec3", "f")]
    [InlineData("flags= pat=[a,b] ops=[f] ctx=0/0 g=[] u=0", "-e", "a", "--regexp=b", "f")]   // FIX (-e was not an option)
    [InlineData("flags= pat=[-x] ops=[f] ctx=0/0 g=[] u=0", "-e", "-x", "f")]
    [InlineData("flags= pat=[] ops=[-x,f] ctx=0/0 g=[] u=0", "--", "-x", "f")]
    [InlineData("flags=N pat=[] ops=[c3,f] ctx=0/0 g=[] u=0", "c3", "-N", "f")]   // options after operands
    [InlineData("flags=N pat=[] ops=[c3,f] ctx=0/0 g=[] u=0", "-N", "c3", "f")]
    [InlineData("flags= pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-N", "-n", "c3")]   // last -n/-N wins
    [InlineData("flags=N pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-n", "--no-line-number", "c3")]
    // case: the last of -i -s -S wins
    [InlineData("flags=i pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-i", "c3")]
    [InlineData("flags=i pat=[] ops=[c3] ctx=0/0 g=[] u=0", "--ignore-case", "c3")]
    [InlineData("flags=S pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-S", "c3")]
    [InlineData("flags=s pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-i", "-s", "c3")]
    [InlineData("flags=i pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-s", "-i", "c3")]
    [InlineData("flags=wx pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-wx", "c3")]
    [InlineData("flags=cov pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-c", "-ov", "c3")]
    [InlineData("flags=lF pat=[] ops=[c3] ctx=0/0 g=[] u=0", "-lF", "c3")]
    // context: -A/-B beat -C in any order
    [InlineData("flags= pat=[] ops=[c3] ctx=1/0 g=[] u=0", "-A1", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=1/3 g=[] u=0", "-A1", "-C3", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=1/3 g=[] u=0", "-C3", "-A1", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=1/1 g=[] u=0", "-C", "1", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=2/1 g=[] u=0", "--after-context=2", "--before-context", "1", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=4/4 g=[] u=0", "--context=4", "c3")]
    // -g repeatable (attached value too), -u counted
    [InlineData("flags= pat=[] ops=[3,.] ctx=0/0 g=[*.rs,!*.txt,*.md] u=0", "-g", "*.rs", "-g!*.txt", "--glob=*.md", "3", ".")]
    [InlineData("flags=I pat=[] ops=[3] ctx=0/0 g=[] u=1", "-u", "3")]
    [InlineData("flags=HI pat=[] ops=[3] ctx=0/0 g=[] u=2", "-uu", "3")]
    [InlineData("flags=HI pat=[] ops=[3] ctx=0/0 g=[] u=3", "-uuu", "3")]
    [InlineData("flags=HI pat=[] ops=[3] ctx=0/0 g=[] u=2", "-u", "-u", "3")]
    [InlineData("flags=H pat=[] ops=[3] ctx=0/0 g=[] u=0", "--hidden", "3")]
    [InlineData("flags=I pat=[] ops=[3] ctx=0/0 g=[] u=0", "--no-ignore", "3")]
    [InlineData("flags=I pat=[] ops=[3] ctx=0/0 g=[] u=0", "--no-ignore-vcs", "3")]
    // accepted no-ops and --color WHEN (required value)
    [InlineData("flags= pat=[] ops=[c3] ctx=0/0 g=[] u=0", "--color", "never", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=0/0 g=[] u=0", "--color=auto", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=0/0 g=[] u=0", "--no-heading", "--no-messages", "--no-config", "--mmap", "--no-mmap", "c3")]
    // errors
    [InlineData("ERR 2 rg: unrecognized option '--bogus'", "--bogus", "c3")]
    [InlineData("ERR 2 rg: invalid option -- 'Q'", "-Q", "c3")]
    [InlineData("ERR 2 rg: unrecognized option '--ign'", "--ign", "c3")]   // ripgrep does not abbreviate long options
    [InlineData("ERR 2 rg: option requires an argument -- 'A'", "-A")]
    [InlineData("ERR 2 rg: option requires an argument -- 'e'", "-e")]
    [InlineData("ERR 2 rg: option '--glob' requires an argument", "--glob")]
    [InlineData("ERR 2 rg: error parsing flag -A: value is not a valid number: invalid digit found in string", "-A", "x", "c3")]
    [InlineData("ERR 2 rg: error parsing flag -C: value is not a valid number: invalid digit found in string", "-C", "-1", "c3")]
    [InlineData("ERR 2 rg: error parsing flag --color: choice 'bogus' is unrecognized", "--color", "bogus", "c3")]
    [InlineData("ERR 2 rg: option '--count' doesn't allow an argument", "--count=1", "c3")]
    // valid ripgrep options the internal engine refuses (exit 2)
    [InlineData("ERR 2 rg: option '-t' is recognized but not supported by ps-bash", "-t", "rust", "3")]
    [InlineData("ERR 2 rg: option '-m' is recognized but not supported by ps-bash", "-m1", "3")]
    [InlineData("ERR 2 rg: option '--json' is recognized but not supported by ps-bash", "--json", "3")]
    [InlineData("ERR 2 rg: option '-P' is recognized but not supported by ps-bash", "-iP", "3")]
    [InlineData("ERR 2 rg: option '--max-depth' is recognized but not supported by ps-bash", "--max-depth=1", "3")]
    [InlineData("ERR 2 rg: option '-q' is recognized but not supported by ps-bash", "-q", "3")]
    // info
    [InlineData("HELP", "--help")]
    [InlineData("HELP", "-h")]
    [InlineData("VERSION", "-V")]
    [InlineData("VERSION", "--version")]
    public void Plan_ResolvesArgvLikeRipgrep(string expected, params string[] argv) =>
        Assert.Equal(expected, Scan(argv));
}
