using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashUniq</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashUniqCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashUniq</c> function — collapses adjacent
/// duplicate lines. Flags: <c>-c</c> (count), <c>-d</c> (dupes-only),
/// <c>-u</c> (uniques-only), <c>-i</c> (case-insensitive), <c>-f N</c>
/// (skip fields), <c>-s N</c> (skip chars), <c>-w N</c> (compare-chars).
///
/// Failure-surface axes covered (per Directive 3): empty input, unicode,
/// file mode + pipeline mode + alias resolution, missing-file error
/// continuation, value-flags, case-insensitive, mixed dupes, and an
/// injection probe per Directive 12.
/// </summary>
public class InvokeBashUniqCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashUniqCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-uniq-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    [Fact]
    public void Uniq_EmptyPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashUniq");
        Assert.Empty(lines);
    }

    [Fact]
    public void Uniq_AllUnique_PreservesAll()
    {
        var lines = RunLines("'a','b','c' | Invoke-BashUniq");
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void Uniq_AllDuplicate_CollapsesToOne()
    {
        var lines = RunLines("'x','x','x' | Invoke-BashUniq");
        Assert.Equal(new[] { "x" }, lines);
    }

    [Fact]
    public void Uniq_MixedAdjacentDuplicates_CollapsesRuns()
    {
        // Adjacent runs collapse; non-adjacent duplicates remain (oracle uniq
        // semantics, not sort-uniq).
        var lines = RunLines("'a','a','b','b','b','a' | Invoke-BashUniq");
        Assert.Equal(new[] { "a", "b", "a" }, lines);
    }

    [Fact]
    public void Uniq_CountFlag_PrefixesCount()
    {
        // -c emits "{count,7} {line}" — 7-wide right-aligned count.
        var lines = RunLines("'a','a','b' | Invoke-BashUniq -c");
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" a", lines[0]);
        Assert.Contains("2", lines[0]);
        Assert.EndsWith(" b", lines[1]);
        Assert.Contains("1", lines[1]);
    }

    [Fact]
    public void Uniq_DupesOnly_FlagD_ShowsOnlyDuplicates()
    {
        var lines = RunLines("'a','a','b','c','c','d' | Invoke-BashUniq -d");
        Assert.Equal(new[] { "a", "c" }, lines);
    }

    [Fact]
    public void Uniq_UniquesOnly_FlagU_ShowsOnlyNonDuplicates()
    {
        var lines = RunLines("'a','a','b','c','c','d' | Invoke-BashUniq -u");
        Assert.Equal(new[] { "b", "d" }, lines);
    }

    [Fact]
    public void Uniq_IgnoreCase_FlagI_CollapsesAcrossCase()
    {
        var lines = RunLines("'A','a','b' | Invoke-BashUniq -i");
        Assert.Equal(new[] { "A", "b" }, lines);
    }

    [Fact]
    public void Uniq_SkipFields_F1_SkipsFirstField()
    {
        // After skipping the first whitespace-separated field, the key is
        // "x" for both lines (different prefixes), so they collapse.
        var lines = RunLines("'foo x','bar x','bar y' | Invoke-BashUniq -f 1");
        Assert.Equal(new[] { "foo x", "bar y" }, lines);
    }

    [Fact]
    public void Uniq_SkipChars_S2_SkipsFirstTwoChars()
    {
        // After skipping 2 chars, both "ABxyz" and "CDxyz" have key "xyz",
        // so they collapse.
        var lines = RunLines("'ABxyz','CDxyz','EFwww' | Invoke-BashUniq -s 2");
        Assert.Equal(new[] { "ABxyz", "EFwww" }, lines);
    }

    [Fact]
    public void Uniq_FileMode_ReadsAndCollapses()
    {
        var file = Path.Combine(_tmpDir, "u.txt");
        File.WriteAllText(file, "a\na\nb\nb\nb\nc\n");
        var lines = RunLines($"Invoke-BashUniq '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void Uniq_FileMode_CrlfNormalized()
    {
        var file = Path.Combine(_tmpDir, "crlf.txt");
        File.WriteAllText(file, "a\r\na\r\nb\r\n");
        var lines = RunLines($"Invoke-BashUniq '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void Uniq_FileMode_Unicode_NonAscii()
    {
        var file = Path.Combine(_tmpDir, "uni.txt");
        File.WriteAllText(file, "héllo\nhéllo\nwörld\n", new System.Text.UTF8Encoding(false));
        var lines = RunLines($"Invoke-BashUniq '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "héllo", "wörld" }, lines);
    }

    [Fact]
    public void Uniq_FileMode_MissingFile_DoesNotThrow_NoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashUniq '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Uniq_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashUniq --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("uniq", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Uniq_AliasResolution_UniqWorks()
    {
        // The psm1 module registers `Set-Alias uniq Invoke-BashUniq`. The
        // binary cmdlet loads before psm1 runs, so the alias resolves to
        // the cmdlet.
        var lines = RunLines("'a','a','b' | uniq");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void Uniq_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashUniq '{probe.Replace("'", "''")}' 2>$null");
        Assert.Empty(lines);
    }
}
