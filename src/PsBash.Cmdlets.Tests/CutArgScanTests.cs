using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for cut (shared ordered parser + <c>CutPlan</c>). Checked against
/// GNU cut 9.4 (`wsl bash`): usage/validation errors exit 1, -z valid-but-unsupported (exit 2),
/// exactly one of -b/-c/-f, -d a single character and only with -f, -s only with -f, -n ignored.
/// </summary>
public class CutArgScanTests
{
    private static string Scan(string[] argv)
    {
        var c = InvokeBashCutCommand.Plan(argv);
        if (c.Parsed.Error is { } e) return "ERR " + e.Message("cut");
        if (c.Error is { } m) return "ERR " + m.Replace("\n\t", " / ");
        var s = c.Selection!;
        return $"{s.Mode} delim={(s.Delimiter == "\t" ? "TAB" : s.Delimiter == "\0" ? "NUL" : s.Delimiter)} s={s.OnlyDelimited} comp={s.Complement} od={s.OutputDelimiter ?? "-"} ops=[{string.Join(",", c.Operands)}]";
    }

    [Theory]
    [InlineData("Fields delim=TAB s=False comp=False od=- ops=[]", "-f1")]
    [InlineData("Fields delim=TAB s=False comp=False od=- ops=[]", "-f", "1")]
    [InlineData("Fields delim=TAB s=False comp=False od=- ops=[]", "--fields=1")]
    [InlineData("Fields delim=TAB s=False comp=False od=- ops=[]", "--fie", "1")]  // FIX (abbreviation)
    [InlineData("Fields delim=: s=False comp=False od=- ops=[]", "-d:", "-f1")]
    [InlineData("Fields delim=: s=False comp=False od=- ops=[]", "-d", ":", "-f1")]  // FIX (was: an unknown option)
    [InlineData("Fields delim=: s=False comp=False od=- ops=[]", "--delimiter", ":", "-f1")]
    [InlineData("Fields delim=y s=False comp=False od=- ops=[]", "-dx", "-dy", "-f1")]  // last -d wins
    [InlineData("Fields delim=NUL s=False comp=False od=- ops=[]", "-d", "", "-f1")]  // GNU: empty DELIM is NUL
    [InlineData("Fields delim=: s=True comp=False od=- ops=[]", "-d:", "-s", "-f1")]
    [InlineData("Fields delim=: s=True comp=False od=- ops=[]", "--only", "-d:", "-f1")]
    [InlineData("Fields delim=TAB s=False comp=True od=- ops=[]", "--complement", "-f1")]
    [InlineData("Fields delim=TAB s=False comp=True od=- ops=[]", "--compl", "-f1")]
    [InlineData("Fields delim=TAB s=False comp=False od=+ ops=[]", "-f1", "--output-delimiter=+")]
    [InlineData("Fields delim=TAB s=False comp=False od=+ ops=[]", "-f1", "--output-delimiter", "+")]
    [InlineData("Fields delim=TAB s=False comp=False od= ops=[]", "-f1", "--output-delimiter=")]
    [InlineData("Chars delim=TAB s=False comp=False od=- ops=[]", "-c1-3")]
    [InlineData("Chars delim=TAB s=False comp=False od=- ops=[]", "-c", "1-3")]
    [InlineData("Chars delim=TAB s=False comp=False od=- ops=[]", "--ch=1")]
    [InlineData("Bytes delim=TAB s=False comp=False od=- ops=[]", "-b1")]  // FIX (was: unsupported)
    [InlineData("Bytes delim=TAB s=False comp=False od=- ops=[]", "--by", "1")]
    [InlineData("Bytes delim=TAB s=False comp=False od=- ops=[]", "-n", "-b1")]  // -n ignored
    [InlineData("Fields delim=TAB s=False comp=False od=- ops=[f,g]", "f", "-f1", "g")]  // options after operands
    [InlineData("Fields delim=TAB s=False comp=False od=- ops=[-,a]", "-f1", "-", "a")]
    [InlineData("Fields delim=TAB s=False comp=False od=- ops=[-f2]", "-f1", "--", "-f2")]
    [InlineData("ERR cut: you must specify a list of bytes, characters, or fields")]
    [InlineData("ERR cut: you must specify a list of bytes, characters, or fields", "--complement")]  // FIX
    [InlineData("ERR cut: you must specify a list of bytes, characters, or fields", "-d,")]
    [InlineData("ERR cut: you must specify a list of bytes, characters, or fields", "--", "-c1")]
    [InlineData("ERR cut: only one list may be specified", "-c1", "-f1")]  // FIX (was: -c silently won)
    [InlineData("ERR cut: only one list may be specified", "-f1", "-f2")]
    [InlineData("ERR cut: only one list may be specified", "-b1", "-b1")]
    [InlineData("ERR cut: an input delimiter may be specified only when operating on fields", "-d:", "-c1")]  // FIX
    [InlineData("ERR cut: suppressing non-delimited lines makes sense / only when operating on fields", "-s", "-c1")]  // FIX
    [InlineData("ERR cut: the delimiter must be a single character", "-dab", "-f1")]  // FIX (was: multi-char accepted)
    [InlineData("ERR cut: the delimiter must be a single character", "-d", "ab", "-f1")]
    [InlineData("ERR cut: option requires an argument -- 'f'", "-f")]
    [InlineData("ERR cut: option requires an argument -- 'd'", "-f1", "-d")]
    [InlineData("ERR cut: option '--output-delimiter' requires an argument", "-f1", "--output-delimiter")]
    [InlineData("ERR cut: option '--o=x' is ambiguous; possibilities: '--only-delimited' '--output-delimiter'", "--o=x", "-f1")]
    [InlineData("ERR cut: option '--c' is ambiguous; possibilities: '--characters' '--complement'", "--c", "-f1")]
    [InlineData("ERR cut: invalid option -- 'x'", "-x", "-f1")]
    [InlineData("ERR cut: invalid option -- 'a'", "-ac")]
    [InlineData("ERR cut: unrecognized option '--nope'", "--nope")]
    [InlineData("ERR cut: option '-z' is recognized but not supported by ps-bash", "-z", "-f1")]
    [InlineData("ERR cut: option '--zero-terminated' is recognized but not supported by ps-bash", "--zero", "-f1")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Theory]
    [InlineData("ERR cut: fields are numbered from 1", "-f0")]
    [InlineData("ERR cut: fields are numbered from 1", "-f", "")]
    [InlineData("ERR cut: fields are numbered from 1", "-f", ",")]
    [InlineData("ERR cut: fields are numbered from 1", "-f", "1,,2")]
    [InlineData("ERR cut: byte/character positions are numbered from 1", "-c0")]
    [InlineData("ERR cut: byte/character positions are numbered from 1", "-b", " 1")]
    [InlineData("ERR cut: invalid decreasing range", "-f3-2")]
    [InlineData("ERR cut: invalid decreasing range", "-f", "2,3-1")]
    [InlineData("ERR cut: invalid range with no endpoint: -", "-f", "-")]
    [InlineData("ERR cut: invalid byte or character range", "-b", "1-2-3")]
    [InlineData("ERR cut: invalid field value 'a'", "-f", "1,a")]
    [InlineData("ERR cut: invalid field value 'x'", "-f", "1-x")]
    [InlineData("ERR cut: invalid field value 'x-2'", "-f", "x-2")]
    [InlineData("ERR cut: invalid byte/character position 'x'", "-c1x")]
    [InlineData("ERR cut: invalid byte/character position 'x-2'", "-c", "x-2")]
    [InlineData("ERR cut: field number '99999999999999999999' is too large", "-f", "99999999999999999999")]
    [InlineData("ERR cut: byte/character offset '99999999999999999999' is too large", "-c", "1-99999999999999999999")]
    public void ListErrors(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Theory]
    [InlineData("1-2", "1-2")]
    [InlineData("1,3", "1,3")]
    [InlineData("3,1", "1,3")]            // sorted
    [InlineData("1,1", "1")]              // duplicates merge
    [InlineData("1-2,2-4", "1-4")]        // overlap merges
    [InlineData("1-2,3-4", "1-2,3-4")]    // adjacent ranges stay separate (matters for --output-delimiter)
    [InlineData("3-,1", "1,3-")]
    [InlineData("-3", "1-3")]
    [InlineData("1 3", "1,3")]            // blanks separate like commas
    [InlineData("1-1000000000000", "1-")]  // beyond int clamps to open
    public void List_SortsAndMerges(string list, string expected)
    {
        var r = InvokeBashCutCommand.ParseList(list, isField: true, out var err);
        Assert.Null(err);
        var merged = CutPlan.MergeRanges(r!);
        Assert.Equal(expected, string.Join(",", merged.Select(x =>
            x.Hi == int.MaxValue ? $"{x.Lo}-" : x.Lo == x.Hi ? $"{x.Lo}" : $"{x.Lo}-{x.Hi}")));
    }

    [Fact]
    public void ExitStatus_UsageIs1_UnsupportedIs2()
    {
        Assert.Equal(1, InvokeBashCutCommand.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashCutCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
        Assert.True(InvokeBashCutCommand.ScanArgs(new[] { "--ver" }).Has("version"));
    }
}
