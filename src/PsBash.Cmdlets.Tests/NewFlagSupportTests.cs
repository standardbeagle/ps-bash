using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Tests for newly-implemented flag support (Tier 1 quick wins): wc -m/-L,
/// stat --format, touch -r, du --max-depth, tree --noreport/-f, sort -o.
/// Each was previously missing or refused.
/// </summary>
public class NewFlagSupportTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmp;

    public NewFlagSupportTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-newflag-{Guid.NewGuid():N}".Substring(0, 24));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
    }

    private static string Q(string s) => s.Replace("'", "''");

    // ── wc -m / -L ──────────────────────────────────────────────────────────

    [Fact]
    public void Wc_M_CountsCharacters()
    {
        // "héllo\n" = 6 chars (5 letters + newline). -c (bytes) would be 7 (é = 2 bytes UTF-8).
        var f = Path.Combine(_tmp, "uni.txt");
        File.WriteAllText(f, "héllo\n", new System.Text.UTF8Encoding(false));
        var chars = RunLines($"Invoke-BashWc -m '{Q(f)}'");
        Assert.Single(chars);
        Assert.StartsWith("6", chars[0].TrimStart());
        var bytes = RunLines($"Invoke-BashWc -c '{Q(f)}'");
        Assert.StartsWith("7", bytes[0].TrimStart());
    }

    [Fact]
    public void Wc_L_ReportsLongestLineLength()
    {
        var f = Path.Combine(_tmp, "lines.txt");
        File.WriteAllText(f, "ab\nabcdef\nabc\n");
        var lines = RunLines($"Invoke-BashWc -L '{Q(f)}'");
        Assert.Single(lines);
        Assert.StartsWith("6", lines[0].TrimStart());
    }

    // ── stat --format ──────────────────────────────────────────────────────

    [Fact]
    public void Stat_LongFormat_AliasesShortC()
    {
        var f = Path.Combine(_tmp, "s.txt");
        File.WriteAllText(f, "data");
        var viaC = RunLines($"Invoke-BashStat -c '%n' '{Q(f)}'");
        var viaLong = RunLines($"Invoke-BashStat --format='%n' '{Q(f)}'");
        Assert.Equal(viaC[0].TrimEnd('\n'), viaLong[0].TrimEnd('\n'));
        Assert.Contains("s.txt", viaLong[0]);
    }

    // ── touch -r ────────────────────────────────────────────────────────────

    [Fact]
    public void Touch_Reference_CopiesTimestamp()
    {
        var refFile = Path.Combine(_tmp, "ref.txt");
        var target = Path.Combine(_tmp, "tgt.txt");
        File.WriteAllText(refFile, "r");
        File.WriteAllText(target, "t");
        var refTime = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Local);
        File.SetLastWriteTime(refFile, refTime);
        RunLines($"Invoke-BashTouch -r '{Q(refFile)}' '{Q(target)}'");
        Assert.Equal(refTime, File.GetLastWriteTime(target));
    }

    // ── du --max-depth ──────────────────────────────────────────────────────

    [Fact]
    public void Du_MaxDepth_AliasesShortD()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "a", "b", "c"));
        File.WriteAllText(Path.Combine(_tmp, "a", "b", "c", "deep.txt"), new string('x', 100));
        var d1 = RunLines($"Invoke-BashDu --max-depth=1 '{Q(Path.Combine(_tmp, "a"))}'");
        // depth-1 must not list the grandchild "a/b/c"
        Assert.DoesNotContain(d1, l => l.Replace('\\', '/').EndsWith("/b/c"));
        Assert.Contains(d1, l => l.Replace('\\', '/').EndsWith("/b"));
    }

    // ── tree --noreport / -f ────────────────────────────────────────────────

    [Fact]
    public void Tree_NoReport_OmitsSummaryLine()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "td"));
        File.WriteAllText(Path.Combine(_tmp, "td", "f.txt"), "x");
        var lines = RunLines($"Invoke-BashTree --noreport '{Q(Path.Combine(_tmp, "td"))}'");
        Assert.DoesNotContain(lines, l => l.Contains("directories", StringComparison.Ordinal)
                                          || l.Contains("files", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("f.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Tree_FullPath_ShowsTargetRelativePaths()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "tf", "sub"));
        File.WriteAllText(Path.Combine(_tmp, "tf", "sub", "leaf.txt"), "x");
        var target = Path.Combine(_tmp, "tf");
        var lines = RunLines($"Invoke-BashTree -f '{Q(target)}'");
        // -f prints the target-relative path on the leaf line.
        Assert.Contains(lines, l => l.Replace('\\', '/').Contains("/sub/leaf.txt", StringComparison.Ordinal));
    }

    // ── sort -o ──────────────────────────────────────────────────────────────

    [Fact]
    public void Sort_OutputFile_WritesSortedLinesToFile()
    {
        var outFile = Path.Combine(_tmp, "sorted.txt");
        RunLines($"'banana','apple','cherry' | Invoke-BashSort -o '{Q(outFile)}'");
        Assert.True(File.Exists(outFile));
        var written = File.ReadAllText(outFile).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal(new[] { "apple", "banana", "cherry" }, written);
    }

    // ── nl formatting params ─────────────────────────────────────────────────

    [Fact]
    public void Nl_WidthSeparatorStartIncrement_AreHonored()
    {
        // -w3 width, -s' ' separator, -v10 start, -i5 increment.
        var lines = RunLines("'a','b' | Invoke-BashNl -w 3 -s ' ' -v 10 -i 5");
        Assert.Equal(new[] { " 10 a", " 15 b" }, lines);
    }

    [Fact]
    public void Nl_NumberFormat_LeftAndZero()
    {
        var rz = RunLines("'x' | Invoke-BashNl -n rz -w 4");
        Assert.Equal(new[] { "0001\tx" }, rz);
    }

    // ── uniq -D / --all-repeated ─────────────────────────────────────────────

    [Fact]
    public void Uniq_AllRepeated_PrintsEveryLineOfDuplicateRuns()
    {
        var lines = RunLines("'a','a','b','c','c','c' | Invoke-BashUniq --all-repeated");
        Assert.Equal(new[] { "a", "a", "c", "c", "c" }, lines);
    }

    [Fact]
    public void Uniq_DashD_PrintsEveryLineOfDuplicateRuns()
    {
        var lines = RunLines("'a','a','b' | Invoke-BashUniq -D");
        Assert.Equal(new[] { "a", "a" }, lines);
    }

    // ── grep -f ──────────────────────────────────────────────────────────────

    [Fact]
    public void Grep_PatternFile_MatchesAnyPattern()
    {
        var pf = Path.Combine(_tmp, "pats.txt");
        File.WriteAllText(pf, "apple\ncherry\n");
        var lines = RunLines($"'apple pie','banana','cherry tart','date' | Invoke-BashGrep -f '{Q(pf)}'");
        Assert.Equal(new[] { "apple pie", "cherry tart" }, lines);
    }

    // ── find -type l ─────────────────────────────────────────────────────────

    [Fact]
    public void Find_TypeF_AndPrint_Work()
    {
        File.WriteAllText(Path.Combine(_tmp, "ff.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_tmp, "dd"));
        // -print is now a recognized no-op action (default emission still happens).
        var lines = RunLines($"Invoke-BashFind '{Q(_tmp)}' -type f -print");
        Assert.Contains(lines, l => l.EndsWith("ff.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Replace('\\', '/').EndsWith("/dd"));
    }

    // ── date format codes + @epoch ───────────────────────────────────────────

    [Theory]
    [InlineData("Invoke-BashDate -u -d '@0' +%F", "1970-01-01")]
    [InlineData("Invoke-BashDate -d '2021-03-05 13:00:00' +%R", "13:00")]
    [InlineData("Invoke-BashDate -d '2021-03-05' +%D", "03/05/21")]
    [InlineData("Invoke-BashDate -d '2021-03-08' +%u", "1")] // Monday
    [InlineData("Invoke-BashDate -d '2021-03-05 13:00:00' +%I", "01")]
    public void Date_FormatCodes(string script, string expected)
    {
        var lines = RunLines(script);
        Assert.Single(lines);
        Assert.Equal(expected, lines[0]);
    }

    // ── printf recycling + numeric specs ─────────────────────────────────────

    [Fact]
    public void Printf_RecyclesFormatUntilArgsExhausted()
    {
        // The format repeats once per argument → "a\nb\nc\n". printf emits a
        // single NoTrailingNewline object that carries its bytes VERBATIM — the
        // trailing \n the format produced is preserved (regression: it was
        // silently stripped, so `printf '%s\n' b; echo c` printed "bc" instead
        // of "b"/"c" on two lines — see parity-followups-2026-06-17).
        var lines = RunLines("Invoke-BashPrintf '%s\\n' a b c");
        Assert.Single(lines);
        Assert.Equal("a\nb\nc\n", lines[0]);
    }

    [Theory]
    [InlineData("Invoke-BashPrintf '%i' 42", "42")]
    [InlineData("Invoke-BashPrintf '%u' 42", "42")]
    public void Printf_IntegerAliases(string script, string expected)
    {
        var lines = RunLines(script);
        Assert.Equal(expected, lines[0]);
    }

    [Fact]
    public void Printf_Scientific()
    {
        var lines = RunLines("Invoke-BashPrintf '%e' 1234.5");
        Assert.StartsWith("1.234500e+0", lines[0]);
    }

    // ── ls --group-directories-first ─────────────────────────────────────────

    [Fact]
    public void Ls_GroupDirectoriesFirst_ListsDirsBeforeFiles()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "lsg", "zdir"));
        File.WriteAllText(Path.Combine(_tmp, "lsg", "afile.txt"), "x");
        var names = RunLines($"Invoke-BashLs --group-directories-first '{Q(Path.Combine(_tmp, "lsg"))}'")
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        int dirIdx = Array.FindIndex(names, n => n.Contains("zdir", StringComparison.Ordinal));
        int fileIdx = Array.FindIndex(names, n => n.Contains("afile", StringComparison.Ordinal));
        Assert.True(dirIdx >= 0 && fileIdx >= 0, "both entries present");
        Assert.True(dirIdx < fileIdx, "directory must sort before the file despite 'z' > 'a'");
    }

    // ── rg -S (smart-case) / -x (line-regexp) ────────────────────────────────

    [Fact]
    public void Rg_SmartCase_InsensitiveForLowercasePattern()
    {
        // -S with an all-lowercase pattern matches case-insensitively.
        var lines = RunLines("'Hello','WORLD','hello there' | Invoke-BashRg -S hello");
        Assert.Equal(2, lines.Count(l => l.Contains("hello", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Rg_SmartCase_SensitiveWhenPatternHasUppercase()
    {
        // -S with an uppercase letter becomes case-sensitive.
        var lines = RunLines("'Hello','hello' | Invoke-BashRg -S Hello");
        Assert.Single(lines);
        Assert.Contains("Hello", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Rg_LineRegexp_MatchesWholeLineOnly()
    {
        var lines = RunLines("'cat','category','a cat' | Invoke-BashRg -x cat");
        Assert.Single(lines);
        Assert.Contains("cat", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("category", StringComparison.Ordinal));
    }

    // ── jq simple builtins ───────────────────────────────────────────────────

    [Fact]
    public void Jq_Add_SumsNumberArray()
    {
        var lines = RunLines("'[1,2,3]' | Invoke-BashJq -r 'add'");
        Assert.Contains("6", string.Join("", lines));
    }

    [Fact]
    public void Jq_Add_ConcatenatesStrings()
    {
        var lines = RunLines("'[\"a\",\"b\",\"c\"]' | Invoke-BashJq -r 'add'");
        Assert.Contains(lines, l => l == "abc");
    }

    [Fact]
    public void Jq_AsciiDowncaseUpcase()
    {
        var down = RunLines("'\"HELLO\"' | Invoke-BashJq -r 'ascii_downcase'");
        Assert.Contains(down, l => l == "hello");
        var up = RunLines("'\"hi\"' | Invoke-BashJq -r 'ascii_upcase'");
        Assert.Contains(up, l => l == "HI");
    }

    [Fact]
    public void Jq_ToNumber_ParsesString()
    {
        var lines = RunLines("'\"42\"' | Invoke-BashJq -r 'tonumber'");
        Assert.Contains("42", string.Join("", lines));
    }

    // ── tar --strip-components ────────────────────────────────────────────────

    [Fact]
    public void Tar_StripComponents_DropsLeadingPathSegments()
    {
        // Build an archive whose entries live under top/sub/.
        var src = Path.Combine(_tmp, "top", "sub");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "leaf.txt"), "content");
        var archive = Path.Combine(_tmp, "a.tar");
        RunLines($"Push-Location '{Q(_tmp)}'; try {{ Invoke-BashTar -c -f '{Q(archive)}' top }} finally {{ Pop-Location }}");
        Assert.True(File.Exists(archive), "archive created");

        var dest = Path.Combine(_tmp, "out");
        Directory.CreateDirectory(dest);
        // --strip-components=2 drops top/sub/, so leaf.txt lands directly in dest.
        RunLines($"Invoke-BashTar -x -f '{Q(archive)}' --strip-components=2 --directory='{Q(dest)}'");
        Assert.True(File.Exists(Path.Combine(dest, "leaf.txt")), "leaf.txt extracted at the stripped path");
    }

    // ── gzip -t ──────────────────────────────────────────────────────────────

    [Fact]
    public void Gzip_Test_ValidArchive_NoErrorExitZero()
    {
        var f = Path.Combine(_tmp, "g.txt");
        File.WriteAllText(f, "compress me please");
        RunLines($"Invoke-BashGzip -k '{Q(f)}'"); // creates g.txt.gz, keeps original
        var gz = f + ".gz";
        Assert.True(File.Exists(gz), "gz created");
        // -t on a valid archive is silent and succeeds.
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript($"Invoke-BashGzip -t '{Q(gz)}'; $global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Equal("0", result[^1]?.ToString());
    }

    [Fact]
    public void Gzip_Test_CorruptArchive_Fails()
    {
        var bad = Path.Combine(_tmp, "bad.gz");
        File.WriteAllText(bad, "this is not gzip data at all");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"Invoke-BashGzip -t '{Q(bad)}' 2>$null; $global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Equal("1", result[^1]?.ToString());
    }

    // ── join -a / -v ─────────────────────────────────────────────────────────

    [Fact]
    public void Join_OuterLeft_IncludesUnpairedFile1Lines()
    {
        var f1 = Path.Combine(_tmp, "j1.txt");
        var f2 = Path.Combine(_tmp, "j2.txt");
        File.WriteAllText(f1, "1 apple\n2 banana\n3 cherry\n");
        File.WriteAllText(f2, "1 red\n3 dark\n");
        // -a1: matched rows + file-1 lines with no match (key 2).
        var lines = RunLines($"Invoke-BashJoin -a 1 '{Q(f1)}' '{Q(f2)}'");
        Assert.Contains(lines, l => l == "1 apple red");
        Assert.Contains(lines, l => l == "3 cherry dark");
        Assert.Contains(lines, l => l == "2 banana"); // unpaired, printed key-first
    }

    [Fact]
    public void Join_AntiJoin_V1_OnlyUnpairedFile1Lines()
    {
        var f1 = Path.Combine(_tmp, "v1.txt");
        var f2 = Path.Combine(_tmp, "v2.txt");
        File.WriteAllText(f1, "1 a\n2 b\n3 c\n");
        File.WriteAllText(f2, "1 x\n3 y\n");
        var lines = RunLines($"Invoke-BashJoin -v 1 '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "2 b" }, lines); // only the unpaired file-1 line, no matches
    }

    // ── split -b ─────────────────────────────────────────────────────────────

    [Fact]
    public void Split_ByBytes_WritesSizedChunks()
    {
        var src = Path.Combine(_tmp, "splitme.txt");
        // 30 bytes of content (after the trailing newline split adds 1).
        File.WriteAllText(src, "abcdefghijklmnopqrstuvwxyz123"); // 29 chars + "\n" = 30 bytes reconstructed
        RunLines($"Push-Location '{Q(_tmp)}'; try {{ Invoke-BashSplit -b 10 '{Q(src)}' part_ }} finally {{ Pop-Location }}");
        var parts = Directory.GetFiles(_tmp, "part_*").OrderBy(p => p).ToArray();
        Assert.True(parts.Length >= 3, $"expected >=3 chunks, got {parts.Length}");
        Assert.Equal(10, new FileInfo(parts[0]).Length); // first chunk is exactly the byte size
    }

    [Fact]
    public void Split_AdditionalSuffix_AppendsToNames()
    {
        var src = Path.Combine(_tmp, "lines.txt");
        File.WriteAllText(src, "a\nb\nc\nd\n");
        RunLines($"Push-Location '{Q(_tmp)}'; try {{ Invoke-BashSplit -l 2 '{Q(src)}' chunk_ --additional-suffix=.txt }} finally {{ Pop-Location }}");
        var parts = Directory.GetFiles(_tmp, "chunk_*.txt");
        Assert.True(parts.Length >= 2, "chunks have the .txt suffix");
    }

    // ── xargs -d ─────────────────────────────────────────────────────────────

    [Fact]
    public void Xargs_CustomDelimiter_SplitsOnIt()
    {
        // -d ',' splits the input on commas; echo joins the items back.
        var lines = RunLines("'a,b,c' | Invoke-BashXargs -d ',' echo");
        Assert.Contains(lines, l => l.Contains("a", StringComparison.Ordinal)
                                    && l.Contains("b", StringComparison.Ordinal)
                                    && l.Contains("c", StringComparison.Ordinal));
    }

    // ── jq operators (sort/unique/group_by/@csv/@base64/to_entries/string) ────

    private string JqRaw(string json, string filter)
        => string.Join("\n", RunLines($"'{json}' | Invoke-BashJq -r '{filter}'"));

    private string JqCompact(string json, string filter)
        => string.Join("\n", RunLines($"'{json}' | Invoke-BashJq -c '{filter}'"));

    [Fact]
    public void Jq_Sort_OrdersArray()
        => Assert.Equal("[1,2,3]", JqCompact("[3,1,2]", "sort"));

    [Fact]
    public void Jq_Unique_SortsAndDedups()
        => Assert.Equal("[1,2,3]", JqCompact("[3,1,2,1,3]", "unique"));

    [Fact]
    public void Jq_Reverse_ReversesArray()
        => Assert.Equal("[3,2,1]", JqCompact("[1,2,3]", "reverse"));

    [Fact]
    public void Jq_SortBy_OrdersByKey()
        => Assert.Equal(@"[{""n"":1},{""n"":2},{""n"":3}]", JqCompact(@"[{""n"":3},{""n"":1},{""n"":2}]", "sort_by(.n)"));

    [Fact]
    public void Jq_GroupBy_GroupsByKey()
    {
        // group_by(.t) on three items with t in {a,b} → two groups.
        var outp = JqCompact(@"[{""t"":""a"",""v"":1},{""t"":""b"",""v"":2},{""t"":""a"",""v"":3}]", "group_by(.t)");
        Assert.Contains("\"v\":1", outp);
        Assert.Contains("\"v\":3", outp);
        Assert.StartsWith("[[", outp); // array of arrays
    }

    [Fact]
    public void Jq_ToEntries_RendersKeyValuePairs()
    {
        var entries = JqCompact(@"{""a"":1,""b"":2}", "to_entries");
        Assert.Contains("\"key\":\"a\"", entries);
        Assert.Contains("\"value\":1", entries);
    }

    [Fact]
    public void Jq_Csv_RendersRow()
        => Assert.Equal("\"a\",\"b\",1", JqRaw(@"[""a"",""b"",1]", "@csv"));

    [Fact]
    public void Jq_Base64_Encodes()
        => Assert.Equal("aGk=", JqRaw(@"""hi""", "@base64"));

    [Fact]
    public void Jq_Has_TestsObjectKey()
    {
        Assert.Equal("true", JqRaw(@"{""x"":1}", "has(\"x\")"));
        Assert.Equal("false", JqRaw(@"{""x"":1}", "has(\"y\")"));
    }

    [Fact]
    public void Jq_Join_And_Startswith()
    {
        Assert.Equal("a-b-c", JqRaw(@"[""a"",""b"",""c""]", "join(\"-\")"));
        Assert.Equal("true", JqRaw(@"""hello""", "startswith(\"he\")"));
    }

    // ── jq arithmetic + and/or ───────────────────────────────────────────────

    [Fact]
    public void Jq_Arithmetic_NumberFields()
        => Assert.Equal("3", JqRaw(@"{""a"":1,""b"":2}", ".a + .b"));

    [Fact]
    public void Jq_Arithmetic_LengthPlusConstant()
        => Assert.Equal("13", JqRaw("[1,2,3]", "length + 10"));

    [Fact]
    public void Jq_Arithmetic_Subtract()
        => Assert.Equal("3", JqRaw("null", "5 - 2"));

    [Fact]
    public void Jq_Arithmetic_StringConcat()
        => Assert.Equal("foobar", JqRaw(@"{""x"":""foo""}", ".x + \"bar\""));

    [Fact]
    public void Jq_Arithmetic_StringSplitViaDivide()
        => Assert.Equal("[\"a\",\"b\",\"c\"]", JqCompact(@"""a,b,c""", ". / \",\""));

    [Fact]
    public void Jq_BooleanAndOr()
    {
        Assert.Equal("false", JqRaw("true", ". and false"));
        Assert.Equal("true", JqRaw("false", ". or true"));
    }
}
