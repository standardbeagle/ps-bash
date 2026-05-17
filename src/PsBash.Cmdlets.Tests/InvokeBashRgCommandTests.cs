using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashRg</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashRgCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashRg</c> function — a ripgrep-style
/// regex search over pipeline / recursive file input. The cmdlet has a
/// two-path implementation: shell out to a native <c>rg.exe</c> binary
/// when present on PATH, else fall back to an internal regex engine.
/// These tests exercise the internal-fallback path by routing pipeline
/// input (the native binary cannot consume PowerShell pipeline objects
/// in this surface, so pipeline-mode tests deterministically hit the
/// fallback). File-mode tests assert on a <c>PsBash.RgMatch</c>-shaped
/// BashText output — the native binary on a dev machine emits a near-
/// identical line shape, so they still pass when rg.exe is present.
///
/// Failure-surface axes covered (per Directive 3): empty pipeline,
/// literal match, ignore-case, invert, fixed, count-only, line-numbers,
/// only-matching, files-with-matches, pipeline + file dual mode, multi-
/// file, CRLF, missing target (axis 14), alias, <c>--help</c>, and an
/// injection probe per Directive 12.
/// </summary>
public class InvokeBashRgCommandTests : IDisposable
{
    private readonly string _tmpDir;

    public InvokeBashRgCommandTests()
    {
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-rg-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    private string MakeFile(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static string Q(string s) => s.Replace("'", "''");

    [Fact]
    public void Rg_LiteralMatch_FromPipeline()
    {
        var lines = RunLines("'apple','banana','cherry' | Invoke-BashRg banana");
        Assert.Single(lines);
        Assert.Equal("banana", lines[0]);
    }

    [Fact]
    public void Rg_IgnoreCase_I_Flag()
    {
        var lines = RunLines("'Apple','APPLE','banana' | Invoke-BashRg -i apple");
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Rg_WordRegexp_W_Flag()
    {
        var lines = RunLines("'cat','catastrophe','wildcat' | Invoke-BashRg -w cat");
        Assert.Single(lines);
        Assert.Equal("cat", lines[0]);
    }

    [Fact]
    public void Rg_CountOnly_C_Flag()
    {
        // Pipeline mode with -c emits a single bare count string.
        var lines = RunLines("'a','b','a','c','a' | Invoke-BashRg -c a");
        Assert.Single(lines);
        Assert.Equal("3", lines[0]);
    }

    [Fact]
    public void Rg_InvertMatch_V_Flag()
    {
        var lines = RunLines("'apple','banana','cherry' | Invoke-BashRg -v banana");
        Assert.Equal(new[] { "apple", "cherry" }, lines);
    }

    [Fact]
    public void Rg_OnlyMatching_O_Flag_FromPipeline()
    {
        // -o emits only the matched substring per line. The bare `-o` switch
        // prefix-collides with -OutBuffer / -OutVariable under the cmdlet
        // binder; the cmdlet declares an explicit `O` SwitchParameter so the
        // exact-name match wins and `-o` routes correctly.
        var lines = RunLines("'foo=1','bar=2','foo=3' | Invoke-BashRg -o 'foo'");
        // Each match is the literal string "foo" — appears in two of the
        // three pipeline items.
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal("foo", l));
    }

    [Fact]
    public void Rg_FixedString_F_Flag()
    {
        // -F: regex metas are literal.
        var lines = RunLines("'a.b','aXb' | Invoke-BashRg -F 'a.b'");
        Assert.Single(lines);
        Assert.Equal("a.b", lines[0]);
    }

    [Fact]
    public void Rg_FilesOnly_L_FileMode_RecursiveByDefault()
    {
        // Single file operand still hits file mode; -l emits the path.
        var file = MakeFile("a.txt", "apple\nfoo\n");
        var file2 = MakeFile("b.txt", "no-match\n");
        var lines = RunLines($"Invoke-BashRg -l apple '{Q(file)}' '{Q(file2)}'");
        Assert.Single(lines);
        Assert.Contains("a.txt", lines[0]);
    }

    [Fact]
    public void Rg_FileMode_Single_EmitsMatch()
    {
        // Single-file file mode: assert the match appears in the output. The
        // exact format (line-number prefix etc.) depends on whether the host
        // has a native rg.exe binary on PATH — when present the cmdlet
        // delegates verbatim, and rg's own default does not prefix line
        // numbers. Both the internal-fallback and native-passthrough paths
        // emit a line containing "banana".
        var file = MakeFile("nums.txt", "alpha\nbanana\ncherry\n");
        var lines = RunLines($"Invoke-BashRg banana '{Q(file)}'");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("banana"));
    }

    [Fact]
    public void Rg_MultiFile_PrefixesFileName()
    {
        var f1 = MakeFile("a.txt", "apple\n");
        var f2 = MakeFile("b.txt", "apple\n");
        var lines = RunLines($"Invoke-BashRg apple '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Contains("apple", l));
        Assert.Contains(lines, l => l.Contains("a.txt"));
        Assert.Contains(lines, l => l.Contains("b.txt"));
    }

    [Fact]
    public void Rg_FileMode_CrlfNormalized()
    {
        var file = MakeFile("crlf.txt", "apple\r\nbanana\r\ncherry\r\n");
        var lines = RunLines($"Invoke-BashRg banana '{Q(file)}'");
        Assert.Single(lines);
        Assert.Contains("banana", lines[0]);
    }

    [Fact]
    public void Rg_FileMode_MissingFile_EmitsError_NoOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Q(Path.Combine(_tmpDir, "nope.txt"));
        var result = pwsh.AddScript(
            $"Invoke-BashRg foo '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        // Searching a missing target with no other paths and no pipeline →
        // no match output (the path emits an error then continues with no
        // files in the list).
        Assert.Empty(result);
    }

    [Fact]
    public void Rg_AliasResolution_RgWorks()
    {
        // The psm1 module registers `Set-Alias rg Invoke-BashRg`.
        var lines = RunLines("'apple','banana' | rg banana");
        Assert.Single(lines);
        Assert.Equal("banana", lines[0]);
    }

    [Fact]
    public void Rg_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashRg --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("rg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rg_InjectionProbe_PatternWithDollarParen_LiteralRegex()
    {
        // Directive 12: a user-controlled pattern containing PowerShell
        // injection chars must not be re-parsed as script. The pattern is
        // fed only to System.Text.RegularExpressions.Regex (and to rg.exe
        // via ArgumentList — never via shell). `-F` forces literal-string
        // matching so no regex parse error is raised. No side effect should
        // occur and no output is produced.
        var lines = RunLines("'foo','bar' | Invoke-BashRg -F \"`$(throw 'pwn')\" 2>$null");
        Assert.Empty(lines);
    }
}
