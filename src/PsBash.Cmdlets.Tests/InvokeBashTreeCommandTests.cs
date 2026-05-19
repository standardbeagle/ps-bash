using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashTree</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="PsBash.Cmdlets.InvokeBashTreeCommand"/>.
///
/// Oracle: the original psm1 <c>Invoke-BashTree</c> function. Each test
/// stands up an isolated temp working directory, populates it, invokes the
/// cmdlet against it, then asserts on the typed <c>PsBash.TreeEntry</c>
/// objects (BashText is the rendered line including the tree-prefix).
///
/// Failure-surface axes covered (per Directive 3): empty input, one-level
/// tree, nested tree, <c>-L 1</c> depth limit, <c>-d</c> dirs-only,
/// <c>-a</c> show-hidden, <c>-I PATTERN</c> exclude, <c>--dirsfirst</c>
/// sort, summary count, alias resolution, <c>--help</c>, and a
/// Directive-12 injection probe on the <c>-I</c> pattern operand.
/// </summary>
public class InvokeBashTreeCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashTreeCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-tree-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string Esc(string path) => path.Replace("'", "''");

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            if (o == null) return "";
            var bashText = o.Properties["BashText"]?.Value as string;
            if (bashText != null) return bashText;
            return o.ToString() ?? "";
        }).ToArray();
    }

    [Fact]
    public void Tree_EmptyDir_RootAndZeroSummary()
    {
        var lines = Run($"Invoke-BashTree '{Esc(_tmpDir)}'");
        // Root + summary; nothing in between.
        Assert.Equal(2, lines.Length);
        Assert.Contains("0 directories, 0 files", lines[1]);
    }

    [Fact]
    public void Tree_OneLevel_ListsChildrenAlphabetically()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "a.txt"), "");
        File.WriteAllText(Path.Combine(_tmpDir, "b.txt"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "sub"));

        var lines = Run($"Invoke-BashTree '{Esc(_tmpDir)}'");
        // Root + 3 entries (a.txt, b.txt, sub) + summary = at least 5 lines.
        Assert.True(lines.Length >= 5);
        // a.txt sorts before b.txt sorts before sub (alpha).
        var aIdx = Array.FindIndex(lines, l => l.EndsWith("a.txt"));
        var bIdx = Array.FindIndex(lines, l => l.EndsWith("b.txt"));
        var sIdx = Array.FindIndex(lines, l => l.EndsWith("sub"));
        Assert.True(aIdx > 0 && bIdx > aIdx && sIdx > bIdx,
            $"alpha order broken: a={aIdx} b={bIdx} sub={sIdx} | {string.Join("|", lines)}");
        // Summary reflects 1 dir + 2 files.
        Assert.Contains("1 directory, 2 files", lines[^1]);
    }

    [Fact]
    public void Tree_Nested_ChildrenIndented()
    {
        Directory.CreateDirectory(Path.Combine(_tmpDir, "outer"));
        File.WriteAllText(Path.Combine(_tmpDir, "outer", "inner.txt"), "");

        var lines = Run($"Invoke-BashTree '{Esc(_tmpDir)}'");
        // Find the inner.txt line; it must have a deeper tree prefix
        // (containing a non-empty indent before the connector).
        var innerLine = lines.First(l => l.EndsWith("inner.txt"));
        // Inner line should start with at least 4 columns of prefix
        // (the outer entry's child-prefix) before the box connector.
        // The outer dir is the last entry under the root, so its child
        // prefix is 4 spaces.
        Assert.StartsWith("    ", innerLine);
    }

    [Fact]
    public void Tree_LFlag1_StopsAtFirstLevel()
    {
        Directory.CreateDirectory(Path.Combine(_tmpDir, "outer"));
        File.WriteAllText(Path.Combine(_tmpDir, "outer", "inner.txt"), "");

        var lines = Run($"Invoke-BashTree -L 1 '{Esc(_tmpDir)}'");
        // outer must appear; inner.txt must NOT.
        Assert.Contains(lines, l => l.EndsWith("outer"));
        Assert.DoesNotContain(lines, l => l.EndsWith("inner.txt"));
    }

    [Fact]
    public void Tree_DFlag_OnlyDirectories()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "file.txt"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "sub"));

        var lines = Run($"Invoke-BashTree -d '{Esc(_tmpDir)}'");
        Assert.Contains(lines, l => l.EndsWith("sub"));
        Assert.DoesNotContain(lines, l => l.EndsWith("file.txt"));
        // Summary is dirs-only form: "N directories".
        Assert.Contains("1 directory", lines[^1]);
        Assert.DoesNotContain("file", lines[^1]);
    }

    [Fact]
    public void Tree_AFlag_ShowsDotfiles()
    {
        File.WriteAllText(Path.Combine(_tmpDir, ".hidden"), "");
        File.WriteAllText(Path.Combine(_tmpDir, "visible.txt"), "");

        // Without -a: .hidden is filtered out.
        var withoutA = Run($"Invoke-BashTree '{Esc(_tmpDir)}'");
        Assert.DoesNotContain(withoutA, l => l.EndsWith(".hidden"));
        Assert.Contains(withoutA, l => l.EndsWith("visible.txt"));

        // With -a: .hidden appears.
        var withA = Run($"Invoke-BashTree -a '{Esc(_tmpDir)}'");
        Assert.Contains(withA, l => l.EndsWith(".hidden"));
    }

    [Fact]
    public void Tree_IFlag_ExcludesMatchingPattern()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "a.tmp"), "");
        File.WriteAllText(Path.Combine(_tmpDir, "b.txt"), "");

        var lines = Run($"Invoke-BashTree -I '*.tmp' '{Esc(_tmpDir)}'");
        Assert.DoesNotContain(lines, l => l.EndsWith("a.tmp"));
        Assert.Contains(lines, l => l.EndsWith("b.txt"));
    }

    [Fact]
    public void Tree_DirsFirst_SortsDirectoriesBeforeFiles()
    {
        // Names chosen so default-alpha sort would interleave but dirsfirst
        // pulls the directory ahead.
        File.WriteAllText(Path.Combine(_tmpDir, "afile.txt"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "zdir"));

        var lines = Run($"Invoke-BashTree --dirsfirst '{Esc(_tmpDir)}'");
        var zIdx = Array.FindIndex(lines, l => l.EndsWith("zdir"));
        var aIdx = Array.FindIndex(lines, l => l.EndsWith("afile.txt"));
        Assert.True(zIdx > 0 && aIdx > zIdx,
            $"--dirsfirst broken: zdir at {zIdx}, afile at {aIdx}");
    }

    [Fact]
    public void Tree_Summary_PluralizesCorrectly()
    {
        // Exactly 1 dir, 1 file => singular forms.
        Directory.CreateDirectory(Path.Combine(_tmpDir, "onedir"));
        File.WriteAllText(Path.Combine(_tmpDir, "onefile.txt"), "");

        var lines = Run($"Invoke-BashTree '{Esc(_tmpDir)}'");
        Assert.Contains("1 directory, 1 file", lines[^1]);
        // Must NOT pluralize.
        Assert.DoesNotContain("directories", lines[^1]);
        Assert.DoesNotContain("files", lines[^1]);
    }

    [Fact]
    public void Tree_ViaAlias_Works()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "x.txt"), "");

        var lines = Run($"tree '{Esc(_tmpDir)}'");
        Assert.Contains(lines, l => l.EndsWith("x.txt"));
    }

    [Fact]
    public void Tree_HelpFlag_EmitsUsage()
    {
        var lines = Run("Invoke-BashTree --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("tree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tree_IPatternWithScriptblockChars_TreatedAsLiteralGlob()
    {
        // Directive 12: a user-controlled exclude pattern containing
        // $(throw'pwn') must NOT be evaluated as PowerShell. It must arrive
        // at the wildcard matcher as a literal string. With this pattern,
        // no real filename matches the glob, so all files survive — and
        // critically no exception is thrown.
        File.WriteAllText(Path.Combine(_tmpDir, "real.txt"), "");

        var weirdPattern = "$(throw 'pwn')";
        var lines = Run(
            $"Invoke-BashTree -I '{Esc(weirdPattern)}' '{Esc(_tmpDir)}'");

        // The file survives (pattern didn't match) and the cmdlet didn't
        // throw — both halves of the injection guard.
        Assert.Contains(lines, l => l.EndsWith("real.txt"));
    }

    [Fact]
    public void Tree_Root_EmitsTypedTreeEntryWithBashText()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            $"Invoke-BashTree '{Esc(_tmpDir)}'").Invoke();
        pwsh.Commands.Clear();
        Assert.NotEmpty(result);
        var first = result[0];
        Assert.NotNull(first);
        Assert.Contains("PsBash.TreeEntry", first.TypeNames);
        var bashText = first.Properties["BashText"]?.Value as string;
        Assert.NotNull(bashText);
    }
}
