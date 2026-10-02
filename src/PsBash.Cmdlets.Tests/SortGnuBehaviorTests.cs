using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end sort behavior in the shape the transpiler emits (every dash word single-quoted) plus
/// DIRECT calls (the Pester shape). Oracle: GNU sort 9.4, LC_ALL=C (`wsl bash`); tests marked FIX
/// assert output that used to be silently wrong.
/// </summary>
public class SortGnuBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public SortGnuBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-sortg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private static string Q(params string[] lines) => string.Join(",", lines.Select(l => "'" + l.Replace("'", "''") + "'"));

    private string F(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return "'" + p.Replace("'", "''") + "'";
    }

    [Fact]
    public void Key_WithOwnOrdering_DoesNotInheritGlobalReverse()   // FIX (was: reversed)
    {
        Assert.Equal(new[] { "1 b", "2 a", "3 c" }, Run($"{Q("1 b", "2 a", "3 c")} | Invoke-BashSort '-r' '-k1,1n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "3 c", "2 a", "1 b" }, Run($"{Q("1 b", "2 a", "3 c")} | Invoke-BashSort '-n' '-r' '-k1,1'").AssertSuccess().Lines);
        Assert.Equal(new[] { "1 b", "1 a", "3 c" }, Run($"{Q("1 b", "1 a", "3 c")} | Invoke-BashSort '-r' '-k1,1n' '-k2'").AssertSuccess().Lines);
    }

    [Fact]
    public void KeyModifiers_GFMVAreHonoured()   // FIX (were dropped: the key became field 0)
    {
        Assert.Equal(new[] { "a 9", "a 10" }, Run($"{Q("a 10", "a 9")} | Invoke-BashSort '-k2,2g'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a y", "B x" }, Run($"{Q("B x", "a y")} | Invoke-BashSort '-k1f'").AssertSuccess().Lines);
        Assert.Equal(new[] { "2.9", "2.10" }, Run($"{Q("2.10", "2.9")} | Invoke-BashSort '-k1V'").AssertSuccess().Lines);
        Assert.Equal(new[] { "Jan", "Mar" }, Run($"{Q("Mar", "Jan")} | Invoke-BashSort '-k1M'").AssertSuccess().Lines);
    }

    [Fact]
    public void Key_CharOffsets_CountFromTheLineStartOfTheirField_NotClippedToIt()   // FIX
    {
        // GNU: -k1.2,1.3 is characters 2-3 of the line even past the first blank.
        Assert.Equal(new[] { "abXdef", "abcdef" }, Run($"{Q("abcdef", "abXdef")} | Invoke-BashSort '-k1.2,1.3'").AssertSuccess().Lines);
        Assert.Equal(new[] { "xbc", "axc" }, Run($"{Q("axc", "xbc")} | Invoke-BashSort '-k1.2,1.2'").AssertSuccess().Lines);
        Assert.Equal(new[] { "Z 1", " 1 z" }, Run($"{Q(" 1 z", "Z 1")} | Invoke-BashSort '-k1.2,1.3'").AssertSuccess().Lines);   // keys " 1" < "1 "
    }

    [Fact]
    public void Numeric_ReadsAPrefix_ForWholeLineKeysToo()   // FIX (whole-line -n parsed the WHOLE line: "10 b" = 0)
    {
        Assert.Equal(new[] { "9 a", "10 b" }, Run($"{Q("10 b", "9 a")} | Invoke-BashSort '-n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "0.25", ".5" }, Run($"{Q(".5", "0.25")} | Invoke-BashSort '-n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "+5", "3" }, Run($"{Q("3", "+5")} | Invoke-BashSort '-n'").AssertSuccess().Lines);   // GNU: '+' is not numeric
    }

    [Fact]
    public void GeneralAndHuman_ReadAPrefix()   // FIX
    {
        // GNU -g: the longest float prefix; text with no number sorts below every number.
        Assert.Equal(new[] { "x", "9 a", "1e1 b" }, Run($"{Q("1e1 b", "x", "9 a")} | Invoke-BashSort '-g'").AssertSuccess().Lines);
        Assert.Equal(new[] { "10K", "2M", "1G" }, Run($"{Q("1G", "10K", "2M")} | Invoke-BashSort '-h'").AssertSuccess().Lines);
        Assert.Equal(new[] { "9 a", "10 b" }, Run($"{Q("10 b", "9 a")} | Invoke-BashSort '-h'").AssertSuccess().Lines);
    }

    [Fact]
    public void Unique_KeepsTheFirstLineOfEachRunInInputOrder()   // FIX (was: the smallest line)
    {
        Assert.Equal(new[] { "1 b" }, Run($"{Q("1 b", "1 a")} | Invoke-BashSort '-u' '-k1,1'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a", "b" }, Run($"{Q("b", "a", "b")} | Invoke-BashSort '-u'").AssertSuccess().Lines);
    }

    [Fact]
    public void Check_UsesTheLastResortComparison_AndUniqueMeansStrict()   // FIX
    {
        Run($"{Q("b 1", "a 1")} | Invoke-BashSort '-c' '-k2,2'").AssertFailed(1, "disorder: a 1");
        Run($"{Q("b 1", "a 1")} | Invoke-BashSort '-c' '-s' '-k2,2'").AssertSuccess();
        Run($"{Q("b 1", "a 1")} | Invoke-BashSort '-c' '-u' '-k2,2'").AssertFailed(1, "disorder");
        Run($"{Q("a", "a")} | Invoke-BashSort '-c' '-u'").AssertFailed(1, "disorder: a");
        Run($"{Q("a", "b")} | Invoke-BashSort '-c'").AssertSuccess();
        Run($"{Q("b", "a")} | Invoke-BashSort '-C'").AssertFailed(1);   // quiet: no message
        Assert.Empty(Run($"{Q("b", "a")} | Invoke-BashSort '-C'").Errors);
    }

    [Fact]
    public void Merge_IsAKWayMergeOfTheInputs()   // FIX (-m was unsupported)
    {
        var m1 = F("m1", "3\n1\n");
        var m2 = F("m2", "4\n2\n");
        Assert.Equal(new[] { "3", "1", "4", "2" }, Run($"Invoke-BashSort '-m' {m1} {m2}").AssertSuccess().Lines);   // unsorted inputs merge as-is, like GNU
        var s1 = F("s1", "1\n3\n5\n");
        var s2 = F("s2", "2\n3\n6\n");
        Assert.Equal(new[] { "1", "2", "3", "3", "5", "6" }, Run($"Invoke-BashSort '-m' '-n' {s1} {s2}").AssertSuccess().Lines);
        Assert.Equal(new[] { "1", "2", "3", "5", "6" }, Run($"Invoke-BashSort '-m' '-u' '-n' {s1} {s2}").AssertSuccess().Lines);
    }

    [Fact]
    public void IgnoreNonprinting_DropsControlCharsFromTheKey()   // FIX (-i was unsupported)
    {
        // Without -i "a<SOH>c" < "ab"; with -i the key is "ac" > "ab".
        Assert.Equal(new[] { "ab", "a\u0001c" }, Run("\"a`u{1}c\",\"ab\" | Invoke-BashSort '-i'").AssertSuccess().Lines);
    }

    [Fact]
    public void MissingInput_IsFatal_NoOutput_Exit2()   // FIX (was: sorted the rest, exit 1)
    {
        var ok = F("ok.txt", "b\na\n");
        var r = Run($"Invoke-BashSort {ok} '{Path.Combine(_dir, "nope.txt")}'");
        r.AssertFailed(2, "cannot read");
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void OutputFile_Forms_AndOpenFailure()
    {
        var f = F("inplace.txt", "b\na\n");
        Run($"Invoke-BashSort '-o' {f} {f}").AssertSuccess();
        Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(_dir, "inplace.txt")));
        Run($"Invoke-BashSort '-o' '{Path.Combine(_dir, "no-such-dir", "x")}' {f}").AssertFailed(2, "open failed");
    }

    [Fact]
    public void StdinOperandDash_ReadsThePipeline()   // FIX ("-" was a missing file)
    {
        Assert.Equal(new[] { "a", "b" }, Run($"{Q("b", "a")} | Invoke-BashSort '-'").AssertSuccess().Lines);
    }

    [Fact]
    public void Errors_Exit2()
    {
        Run($"{Q("a")} | Invoke-BashSort '-n' '-g'").AssertFailed(2, "options '-gn' are incompatible");
        Run($"{Q("a")} | Invoke-BashSort '-tab'").AssertFailed(2, "multi-character tab");
        Run($"{Q("a")} | Invoke-BashSort '-k' '0'").AssertFailed(2, "field number is zero");
        Run($"{Q("a")} | Invoke-BashSort '--sort=x'").AssertFailed(1, "invalid argument 'x' for '--sort'");
    }

    [Fact]
    public void Version_FollowsGnuVerrevcmp()   // FIX (known -V parity gap: leading blanks, ~, letters, dot files)
    {
        Assert.Equal(new[] { "1~rc1", "1", "1a", "1.0", "1.0.1", "9", "10", "ABC", "abc", "v1.2", "v1.2.3", "v1.2.10" },
            Run($"{Q("v1.2.10", "v1.2", "1~rc1", "1", "1a", "1.0", "1.0.1", "ABC", "abc", "10", "9", "v1.2.3")} | Invoke-BashSort '-V'").AssertSuccess().Lines);
        Assert.Equal(new[] { ".hidden", "file1.9", "file1.10" }, Run($"{Q("file1.10", "file1.9", ".hidden")} | Invoke-BashSort '-V'").AssertSuccess().Lines);
    }

    [Fact]
    public void DirectCall_DecoysStillBind()
    {
        Assert.Equal(new[] { "a", "b" }, Run($"{Q("b", "a")} | Invoke-BashSort -u").AssertSuccess().Lines);
        Assert.Equal(new[] { "1.2", "1.10" }, Run($"{Q("1.10", "1.2")} | Invoke-BashSort -V").AssertSuccess().Lines);
        Assert.Equal(new[] { "B", "a" }, Run($"{Q("a", "B")} | Invoke-BashSort -d").AssertSuccess().Lines);   // -d = dictionary order (still ordinal: B < a)
        Run($"{Q("b", "a")} | Invoke-BashSort -c").AssertFailed(1, "disorder");
        Assert.Equal(new[] { "ab", "a\u0001c" }, Run("\"a`u{1}c\",\"ab\" | Invoke-BashSort -i").AssertSuccess().Lines);   // -i decoy (InformationAction prefix)
        Run($"{Q("b", "a")} | Invoke-BashSort -o '{Path.Combine(_dir, "direct-o.txt")}'").AssertSuccess();
        Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(_dir, "direct-o.txt")));   // -o decoy (OutVariable prefix)
        Assert.Equal(new[] { "a", "b" }, Run($"{Q("b", "a")} | Invoke-BashSort -k 1").AssertSuccess().Lines);
    }
}
