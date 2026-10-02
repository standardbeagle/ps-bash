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
        string extra = (r.MaxCount != int.MaxValue ? $" max={r.MaxCount}" : "") + (r.MaxDepth >= 0 ? $" depth={r.MaxDepth}" : "")
            + (r.Quiet ? " quiet" : "") + (r.Json ? " json" : "") + (r.Pcre ? " pcre" : "")
            + (r.Replace is not null ? $" repl=[{r.Replace}]" : "")
            + (r.PatternFiles.Count > 0 ? $" files=[{string.Join(",", r.PatternFiles)}]" : "")
            + (r.Null ? " null" : "") + (r.WithFilename is { } wf ? $" H={wf}" : "")
            + (r.SortKey is not null ? $" sort={r.SortKey}{(r.SortReverse ? "R" : "")}" : "")
            + (r.ColorWhen is not null ? $" color={r.ColorWhen}" : "") + (r.Stats ? " stats" : "")
            + (r.Follow ? " follow" : "") + (r.Text ? " text" : "") + (r.SearchZip ? " zip" : "")
            + (r.Multiline ? " multiline" : "") + (r.Vimgrep ? " vimgrep" : "") + (r.Column ? " column" : "");
        return $"flags={flags} pat=[{string.Join(",", r.Patterns)}] ops=[{string.Join(",", r.Operands)}] ctx={r.After}/{r.Before} g=[{string.Join(",", r.Globs)}] u={r.Unrestricted}{extra}";
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
    [InlineData("flags= pat=[] ops=[c3] ctx=0/0 g=[] u=0 color=never", "--color", "never", "c3")]
    [InlineData("flags= pat=[] ops=[c3] ctx=0/0 g=[] u=0 color=auto", "--color=auto", "c3")]
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
    // valid ripgrep options the internal engine still refuses (exit 2)
    [InlineData("ERR 2 rg: option '--pre' is recognized but not supported by ps-bash", "--pre", "cat", "3")]
    [InlineData("ERR 2 rg: option '--max-filesize' is recognized but not supported by ps-bash", "--max-filesize=1M", "3")]
    [InlineData("ERR 2 rg: option '--no-ignore-dot' is recognized but not supported by ps-bash", "--no-ignore-dot", "3")]
    [InlineData("ERR 2 rg: option '--null-data' is recognized but not supported by ps-bash", "--null-data", "3")]
    // the batch-8 options are implemented: they resolve like any other flag
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 max=1", "-m1", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 max=7", "--max-count", "7", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 depth=2", "--max-depth=2", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 depth=1", "-d1", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 quiet", "-q", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 json", "--json", "3")]
    [InlineData("flags=i pat=[] ops=[3] ctx=0/0 g=[] u=0 pcre", "-iP", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 repl=[X$1]", "-r", "X$1", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 files=[p1,p2]", "-f", "p1", "--file=p2", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 null", "-0", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 H=True", "-H", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 H=False", "-H", "-I", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 sort=path", "--sort", "path", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 sort=pathR", "--sortr=path", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 color=always", "--color=always", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 stats follow text zip multiline", "--stats", "-L", "-a", "-z", "-U", "3")]
    // --column / --vimgrep / -p switch line numbers ON at their position; a later -N wins
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 column", "--column", "3")]
    [InlineData("flags=N pat=[] ops=[3] ctx=0/0 g=[] u=0 vimgrep column", "--vimgrep", "-N", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0 color=always", "-p", "3")]
    // new usage errors (texts from ripgrep 14.1)
    [InlineData("ERR 2 rg: error parsing flag -m: value is not a valid number: invalid digit found in string", "-m", "x", "3")]
    [InlineData("ERR 2 rg: error parsing flag --max-depth: value is not a valid number: invalid digit found in string", "--max-depth", "x", "3")]
    [InlineData("ERR 2 rg: error parsing flag --sort: choice 'bogus' is unrecognized", "--sort", "bogus", "3")]
    [InlineData("ERR 2 rg: error parsing flag -E: grep config error: unknown encoding: bogus", "-E", "bogus", "3")]
    [InlineData("ERR 2 rg: unrecognized file type: nosuch", "-t", "nosuch", "3")]
    [InlineData("ERR 2 rg: unrecognized file type: rust", "--type-clear", "rust", "-t", "rust", "3")]
    [InlineData("flags= pat=[] ops=[3] ctx=0/0 g=[] u=0", "--type-add", "foo:*.x", "-tfoo", "3")]
    [InlineData("ERR 2 rg: error parsing flag --colors: unrecognized output type 'bogus'. Choose from: path, line, column, match.", "--colors", "bogus:fg:red", "3")]
    [InlineData("ERR 2 rg: error parsing flag --colors: unrecognized color name 'nocolor'. Choose from: black, blue, green, red, cyan, magenta, yellow, white", "--colors", "match:fg:nocolor", "3")]
    // info
    [InlineData("HELP", "--help")]
    [InlineData("HELP", "-h")]
    [InlineData("VERSION", "-V")]
    [InlineData("VERSION", "--version")]
    public void Plan_ResolvesArgvLikeRipgrep(string expected, params string[] argv) =>
        Assert.Equal(expected, Scan(argv));
}
