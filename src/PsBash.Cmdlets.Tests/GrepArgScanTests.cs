using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for grep (shared ordered parser + GNU grep 3.11 option rules). Every
/// expectation was read from the oracle (`wsl bash`, GNU grep 3.11): usage errors exit 2,
/// two different matchers among -E -F -G -P are "conflicting matchers specified", -A/-B beat -C/-NUM in
/// any order, -l/-L and -h/-H are last-wins, a negative -m means unlimited, -y is -i. Lines marked FIX
/// used to be silently wrong (or refused) before the migration.
/// </summary>
public class GrepArgScanTests
{
    private static string Scan(params string[] argv)
    {
        var g = InvokeBashGrepCommand.Plan(argv);
        if (g.Parsed.Error is { } e) return $"ERR {g.Parsed.ErrorExitCode} {e.Message("grep")}";
        if (g.Error is { } m) return $"ERR {g.ErrorExit} {m}";
        if (g.Parsed.Has("help")) return "HELP";
        if (g.Parsed.Has("version")) return "VERSION";
        string flags = (g.IgnoreCase ? "i" : "") + (g.Invert ? "v" : "") + (g.LineNumbers ? "n" : "")
            + (g.Count ? "c" : "") + (g.Quiet ? "q" : "") + (g.Recursive ? "r" : "") + (g.FilesWith ? "l" : "")
            + (g.FilesWithout ? "L" : "") + (g.Word ? "w" : "") + (g.LineRegexp ? "x" : "")
            + (g.OnlyMatching ? "o" : "") + (g.ForceFileName ? "H" : "") + (g.SuppressFileName ? "h" : "")
            + (g.NoMessages ? "s" : "") + (g.Matcher != '\0' ? g.Matcher.ToString() : "");
        string src = string.Join(",", g.PatternSources.Select(s => (s.IsFile ? "f:" : "e:") + s.Value));
        string max = g.MaxMatches == int.MaxValue ? "inf" : g.MaxMatches.ToString();
        string ext = (g.NullData ? "z" : "") + (g.NullAfterName ? "Z" : "") + (g.ByteOffset ? "b" : "")
            + (g.InitialTab ? "T" : "") + (g.UnixOffsetsWarning ? "u" : "")
            + (g.Binary == GrepBinaryMode.Text ? "a" : g.Binary == GrepBinaryMode.WithoutMatch ? "I" : "")
            + (g.Directories == GrepDirectories.Skip ? "dskip" : "")
            + (g.Devices == GrepDevices.Skip ? "Dskip" : "");
        string tail = (g.Label != null ? $" label={g.Label}" : "")
            + (g.GroupSeparator != "--" ? $" gsep={(g.GroupSeparator ?? "<none>")}" : "")
            + (g.Color != GrepColorMode.Never ? $" color={g.Color}" : "");
        return $"flags={flags}{(ext.Length > 0 ? "+" + ext : "")} src=[{src}] ops=[{string.Join(",", g.Operands)}] ctx={g.After}/{g.Before} max={max}{tail}"
            + (g.Include.Count + g.Exclude.Count + g.ExcludeDir.Count + g.ExcludeFromFiles.Count == 0 ? ""
                : $" inc=[{string.Join(",", g.Include)}] exc=[{string.Join(",", g.Exclude)}] xdir=[{string.Join(",", g.ExcludeDir)}] xfrom=[{string.Join(",", g.ExcludeFromFiles)}]");
    }

    [Theory]
    // patterns and operands
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf", "a")]
    [InlineData("flags= src=[] ops=[a,f] ctx=0/0 max=inf", "a", "f")]
    [InlineData("flags= src=[e:a] ops=[] ctx=0/0 max=inf", "-e", "a")]
    [InlineData("flags= src=[e:a,e:b] ops=[f] ctx=0/0 max=inf", "-e", "a", "-e", "b", "f")]
    [InlineData("flags= src=[e:PAT] ops=[] ctx=0/0 max=inf", "-ePAT")]   // FIX (joined -e value was not in the oracle scan)
    [InlineData("flags=i src=[e:a] ops=[] ctx=0/0 max=inf", "-ie", "a")]
    [InlineData("flags=v src=[e:a] ops=[] ctx=0/0 max=inf", "-ve", "a")]
    [InlineData("flags=w src=[e:a] ops=[] ctx=0/0 max=inf", "-wea")]
    [InlineData("flags= src=[e:e] ops=[] ctx=0/0 max=inf", "-ee")]   // the rest of the bundle is the value
    [InlineData("flags= src=[e:-e] ops=[] ctx=0/0 max=inf", "-e", "-e")]   // the value of -e may itself be `-e`
    [InlineData("flags= src=[e:a] ops=[] ctx=0/0 max=inf", "--regexp=a")]
    [InlineData("flags= src=[e:a] ops=[] ctx=0/0 max=inf", "--regexp", "a")]
    [InlineData("flags= src=[e:c3] ops=[] ctx=0/0 max=inf", "--regex=c3")]   // FIX (abbreviation)
    [InlineData("flags= src=[f:p] ops=[] ctx=0/0 max=inf", "-fp")]
    [InlineData("flags= src=[f:pe.txt] ops=[x] ctx=0/0 max=inf", "-fpe.txt", "x")]   // FIX (a proxy split the bundle at the `e` of the file name)
    [InlineData("flags= src=[f:p,e:a] ops=[] ctx=0/0 max=inf", "--file=p", "-e", "a")]
    [InlineData("flags=n src=[f:/dev/null] ops=[g] ctx=0/0 max=inf", "-nf", "/dev/null", "g")]
    [InlineData("flags= src=[] ops=[-e,f] ctx=0/0 max=inf", "--", "-e", "f")]   // FIX (the proxy read the operand after -- as an option)
    [InlineData("flags= src=[] ops=[-x,g] ctx=0/0 max=inf", "--", "-x", "g")]
    [InlineData("flags= src=[] ops=[a,-v] ctx=0/0 max=inf", "a", "--", "-v")]
    [InlineData("flags=v src=[] ops=[a,f] ctx=0/0 max=inf", "a", "-v", "f")]   // options after operands (permutation)
    // boolean flags, last-wins pairs
    [InlineData("flags=ivnc src=[] ops=[a] ctx=0/0 max=inf", "-ivnc", "a")]
    [InlineData("flags=r src=[] ops=[a,d] ctx=0/0 max=inf", "-R", "a", "d")]
    [InlineData("flags=r src=[] ops=[a] ctx=0/0 max=inf", "--dereference-recursive", "a")]
    [InlineData("flags=L src=[] ops=[a] ctx=0/0 max=inf", "-l", "-L", "a")]
    [InlineData("flags=l src=[] ops=[a] ctx=0/0 max=inf", "-L", "-l", "a")]
    [InlineData("flags=H src=[] ops=[a] ctx=0/0 max=inf", "-h", "-H", "a")]
    [InlineData("flags=h src=[] ops=[a] ctx=0/0 max=inf", "-H", "-h", "a")]
    [InlineData("flags=i src=[] ops=[a] ctx=0/0 max=inf", "-y", "a")]   // FIX (-y was refused)
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf", "-i", "--no-ignore-case", "a")]   // FIX
    [InlineData("flags=i src=[] ops=[a] ctx=0/0 max=inf", "--no-ignore-case", "-i", "a")]
    [InlineData("flags=qs src=[] ops=[a] ctx=0/0 max=inf", "--silent", "-s", "a")]
    [InlineData("flags=xo src=[] ops=[a] ctx=0/0 max=inf", "-ox", "a")]
    // matchers: the same one twice is fine, two different ones conflict (GNU 3.11)
    [InlineData("flags=E src=[] ops=[a] ctx=0/0 max=inf", "-E", "-E", "a")]
    [InlineData("flags=F src=[] ops=[a] ctx=0/0 max=inf", "--fixed", "a")]   // FIX (fixed-regexp / fixed-strings name one option)
    [InlineData("flags=P src=[] ops=[a] ctx=0/0 max=inf", "-P", "-P", "a")]
    [InlineData("ERR 2 grep: conflicting matchers specified", "-E", "-F", "a")]   // FIX (last one used to win silently)
    [InlineData("ERR 2 grep: conflicting matchers specified", "-F", "-E", "a")]
    [InlineData("ERR 2 grep: conflicting matchers specified", "-G", "-E", "a")]
    [InlineData("ERR 2 grep: conflicting matchers specified", "-P", "-E", "a")]
    [InlineData("ERR 2 grep: conflicting matchers specified", "-EF", "a")]
    // context: -A/-B beat -C/-NUM in any order; each -NUM argv element is one number
    [InlineData("flags= src=[] ops=[c] ctx=1/0 max=inf", "-A1", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=2/3 max=inf", "-A2", "-B3", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=1/1 max=inf", "-C", "1", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=1/1 max=inf", "--context=1", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=1/1 max=inf", "-1", "c")]   // FIX (-NUM was an unknown option)
    [InlineData("flags=n src=[] ops=[c] ctx=1/1 max=inf", "-1n", "c")]
    [InlineData("flags=n src=[] ops=[c] ctx=1/1 max=inf", "-n1", "c")]
    [InlineData("flags=n src=[] ops=[c] ctx=12/12 max=inf", "-n12", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=3/3 max=inf", "-12", "-3", "c")]   // a new argv element starts a new number
    [InlineData("flags= src=[] ops=[c] ctx=1/3 max=inf", "-A1", "-C3", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=1/3 max=inf", "-C3", "-A1", "c")]   // explicit -A wins whatever the order
    [InlineData("flags= src=[] ops=[c] ctx=3/2 max=inf", "--after-context=3", "--before=2", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=0/0 max=inf", "-C0", "c")]
    [InlineData("ERR 2 grep: x: invalid context length argument", "-A", "x", "c")]
    [InlineData("ERR 2 grep: -1: invalid context length argument", "-A", "-1", "c")]
    [InlineData("ERR 2 grep: c3: invalid context length argument", "--context", "c3")]
    [InlineData("ERR 2 grep: option requires an argument -- 'A'", "-A")]
    [InlineData("ERR 2 grep: option '--context' requires an argument", "--context")]
    // -m
    [InlineData("flags= src=[] ops=[c] ctx=0/0 max=1", "-m1", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=0/0 max=0", "-m", "0", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=0/0 max=5", "--max-count=5", "c")]
    [InlineData("flags= src=[] ops=[c] ctx=0/0 max=inf", "-m", "-1", "c")]   // FIX (-m -1 printed nothing)
    [InlineData("ERR 2 grep: invalid max count", "-m", "x", "c")]   // FIX (x was silently ignored)
    [InlineData("ERR 2 grep: invalid max count", "-m1x", "c")]
    // --include / --exclude / --exclude-dir / --exclude-from, repeatable
    [InlineData("flags=r src=[] ops=[a,.] ctx=0/0 max=inf inc=[*.c,*.h] exc=[] xdir=[] xfrom=[]", "-r", "--include=*.c", "--include", "*.h", "a", ".")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf inc=[] exc=[x] xdir=[d,e] xfrom=[ex]", "--exclude=x", "--exclude-dir=d", "--exclude-dir", "e", "--exclude-from=ex", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf inc=[x] exc=[] xdir=[] xfrom=[]", "--inc=x", "a")]
    // --color[=WHEN]; --colo = color/colour are the same option
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf color=Auto", "--color", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf color=Auto", "--colour=auto", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf color=Always", "--colo=always", "a")]   // FIX (was refused as an unknown long option)
    [InlineData("flags= src=[] ops=[always,a] ctx=0/0 max=inf color=Auto", "--color", "always", "a")]   // WHEN is attached only
    [InlineData("ERR 2 grep: invalid argument 'bogus' for '--color'", "--color=bogus", "a")]
    // accepted no-ops
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf", "--line-buffered", "a")]   // FIX (was refused: common in tail -f | grep)
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf", "-U", "a")]
    // errors
    [InlineData("ERR 2 grep: invalid option -- 'Q'", "-Q", "x")]
    [InlineData("ERR 2 grep: unrecognized option '--bogus'", "--bogus", "x")]
    [InlineData("ERR 2 grep: option requires an argument -- 'e'", "-e")]
    [InlineData("ERR 2 grep: option '--regexp' requires an argument", "--regexp")]
    [InlineData("ERR 2 grep: option '--in' is ambiguous; possibilities: '--include' '--initial-tab' '--invert-match'", "--in", "c")]
    [InlineData("ERR 2 grep: option '--ex' is ambiguous; possibilities: '--extended-regexp' '--exclude' '--exclude-from' '--exclude-dir'", "--ex", "c")]
    [InlineData("ERR 2 grep: option '--no' is ambiguous; possibilities: '--no-ignore-case' '--no-filename' '--no-group-separator' '--no-messages'", "--no", "c")]
    [InlineData("ERR 2 grep: option '--count' doesn't allow an argument", "--count=1", "c")]
    // formerly valid-but-unsupported (exit 2), now implemented (oracle: GNU grep 3.11)
    [InlineData("flags=+T src=[] ops=[a] ctx=0/0 max=inf", "-T", "a")]
    [InlineData("flags=+dskip src=[] ops=[a] ctx=0/0 max=inf", "-d", "skip", "a")]
    [InlineData("flags=r src=[] ops=[a] ctx=0/0 max=inf", "-d", "recurse", "a")]
    [InlineData("flags=r src=[] ops=[a] ctx=0/0 max=inf", "--directories=recurse", "a")]
    [InlineData("flags=+dskip src=[] ops=[a] ctx=0/0 max=inf", "-r", "-d", "skip", "a")]   // -r and -d are last-wins
    [InlineData("flags=r src=[] ops=[a] ctx=0/0 max=inf", "-d", "skip", "-r", "a")]
    [InlineData("flags=+Dskip src=[] ops=[a] ctx=0/0 max=inf", "-D", "skip", "a")]
    [InlineData("flags=+Dskip src=[] ops=[a] ctx=0/0 max=inf", "--devices=skip", "a")]
    [InlineData("flags=i+z src=[] ops=[a] ctx=0/0 max=inf", "-iz", "a")]
    [InlineData("flags=+z src=[] ops=[a] ctx=0/0 max=inf", "--null-data", "a")]
    [InlineData("flags=+Z src=[] ops=[a] ctx=0/0 max=inf", "-Z", "a")]
    [InlineData("flags=+Z src=[] ops=[a] ctx=0/0 max=inf", "--null", "a")]
    [InlineData("flags=+b src=[] ops=[a] ctx=0/0 max=inf", "-b", "a")]
    [InlineData("flags=+u src=[] ops=[a] ctx=0/0 max=inf", "-u", "a")]
    [InlineData("flags=+a src=[] ops=[a] ctx=0/0 max=inf", "-a", "a")]
    [InlineData("flags=+a src=[] ops=[a] ctx=0/0 max=inf", "--text", "a")]
    [InlineData("flags=+I src=[] ops=[a] ctx=0/0 max=inf", "-I", "a")]
    [InlineData("flags=+I src=[] ops=[a] ctx=0/0 max=inf", "--binary-files=without-match", "a")]
    [InlineData("flags=+a src=[] ops=[a] ctx=0/0 max=inf", "--binary-files=text", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf", "-I", "--binary-files=binary", "a")]   // last wins
    [InlineData("flags=+a src=[] ops=[a] ctx=0/0 max=inf", "-I", "-a", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf label=x", "--label=x", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf gsep=XX", "--group-separator=XX", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf gsep=<none>", "--no-group-separator", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf gsep=YY", "--no-group-separator", "--group-separator=YY", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf gsep=<none>", "--group-separator=YY", "--no-group-separator", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf color=Always", "--color=always", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf color=Always", "--colour=force", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf color=Auto", "--color", "a")]
    [InlineData("flags= src=[] ops=[a] ctx=0/0 max=inf", "--color=never", "a")]
    [InlineData("ERR 2 grep: unknown binary-files type", "--binary-files=bogus", "a")]
    [InlineData("ERR 2 grep: option '--binary-files' requires an argument", "--binary-files")]
    [InlineData("ERR 2 grep: option '--binary' doesn't allow an argument", "--binary=text", "a")]
    [InlineData("ERR 2 grep: unknown devices method", "-D", "bogus", "a")]
    // info options
    [InlineData("HELP", "--help")]
    [InlineData("HELP", "--he")]
    [InlineData("VERSION", "--version")]
    [InlineData("VERSION", "-V")]
    [InlineData("VERSION", "--vers")]
    public void Plan_ResolvesArgvLikeGnuGrep(string expected, params string[] argv) =>
        Assert.Equal(expected, Scan(argv));
}
