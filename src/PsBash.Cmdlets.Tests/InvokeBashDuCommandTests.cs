using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of <c>Invoke-BashDu</c>
/// from PsBash.psm1 to <see cref="PsBash.Cmdlets.InvokeBashDuCommand"/>.
///
/// Oracle: the original psm1 function. Each test stands up an isolated temp
/// working directory, drops fixture files under it, invokes the cmdlet, and
/// asserts on emitted <c>PsBash.DuEntry</c> objects.
///
/// Failure-surface axes covered (per Directive 3): single-file operand,
/// single-directory recursion, nested directory recursion, <c>-s</c> summary
/// suppression, <c>-h</c> human-readable formatting, <c>-a</c> file emission,
/// <c>-c</c> grand-total, <c>-d N</c> depth limit, multi-operand, alias,
/// <c>--help</c>, and a Directive-12 injection probe on the operand.
/// </summary>
public class InvokeBashDuCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashDuCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-du-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private System.Management.Automation.PSObject[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var wrapped = $"Set-Location -LiteralPath '{Esc(_tmpDir)}'; {script}";
        var result = pwsh.AddScript(wrapped).Invoke();
        pwsh.Commands.Clear();
        return result.ToArray();
    }

    private string[] RunText(string script)
        => Run(script).Select(o =>
        {
            var bt = o?.Properties["BashText"]?.Value as string;
            return bt ?? o?.ToString() ?? "";
        }).ToArray();

    private static string Esc(string path) => path.Replace("'", "''");

    private static void WriteBytes(string path, int n)
    {
        var buf = new byte[n];
        for (int i = 0; i < n; i++) buf[i] = (byte)('a' + (i % 26));
        File.WriteAllBytes(path, buf);
    }

    [Fact]
    public void Du_SingleFile_EmitsOneEntryWithFilePath()
    {
        var p = Path.Combine(_tmpDir, "f.txt");
        WriteBytes(p, 100);

        var results = Run($"Invoke-BashDu '{Esc(p)}'");

        Assert.Single(results);
        Assert.Equal("PsBash.DuEntry", results[0].TypeNames[0]);
        // 100 bytes → ceil(100/1024) = 1 KB block
        Assert.Equal(1L, (long)results[0].Properties["Size"].Value);
        Assert.Equal(100L, (long)results[0].Properties["SizeBytes"].Value);
    }

    [Fact]
    public void Du_SingleDir_EmitsOneEntryForRoot()
    {
        var dir = Path.Combine(_tmpDir, "d");
        Directory.CreateDirectory(dir);
        WriteBytes(Path.Combine(dir, "a.txt"), 2048);

        var results = Run($"Invoke-BashDu '{Esc(dir)}'");

        Assert.Single(results);
        // 2048 → 2 KB
        Assert.Equal(2L, (long)results[0].Properties["Size"].Value);
        Assert.Equal(2048L, (long)results[0].Properties["SizeBytes"].Value);
    }

    [Fact]
    public void Du_NestedDirs_EmitsAllSubdirsWithAccumulatedSizes()
    {
        var dir = Path.Combine(_tmpDir, "tree");
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        WriteBytes(Path.Combine(dir, "a.txt"), 1024);
        WriteBytes(Path.Combine(sub, "b.txt"), 1024);

        var results = Run($"Invoke-BashDu '{Esc(dir)}'");

        Assert.Equal(2, results.Length);
        // Sorted by Path ordinal: "tree" then "tree/sub"
        var rootEntry = results.Single(r => (string)r.Properties["Path"].Value == dir.Replace('\\', '/'));
        Assert.Equal(1024L + 1024L, (long)rootEntry.Properties["SizeBytes"].Value);
        var subEntry = results.Single(r => ((string)r.Properties["Path"].Value).EndsWith("/sub"));
        Assert.Equal(1024L, (long)subEntry.Properties["SizeBytes"].Value);
    }

    [Fact]
    public void Du_SummarizeFlagS_OnlyEmitsRoot()
    {
        var dir = Path.Combine(_tmpDir, "tree");
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        WriteBytes(Path.Combine(dir, "a.txt"), 1024);
        WriteBytes(Path.Combine(sub, "b.txt"), 1024);

        var results = Run($"Invoke-BashDu -s '{Esc(dir)}'");

        Assert.Single(results);
        Assert.Equal(2048L, (long)results[0].Properties["SizeBytes"].Value);
    }

    [Fact]
    public void Du_HumanReadable_FormatsWithUnitSuffix()
    {
        var dir = Path.Combine(_tmpDir, "tree");
        Directory.CreateDirectory(dir);
        // 2048 bytes → 2.0K
        WriteBytes(Path.Combine(dir, "a.txt"), 2048);

        var lines = RunText($"Invoke-BashDu -h '{Esc(dir)}'");

        Assert.Single(lines);
        Assert.StartsWith("2.0K\t", lines[0]);
    }

    [Fact]
    public void Du_AllFilesFlagA_EmitsFileEntries()
    {
        var dir = Path.Combine(_tmpDir, "tree");
        Directory.CreateDirectory(dir);
        WriteBytes(Path.Combine(dir, "a.txt"), 1024);
        WriteBytes(Path.Combine(dir, "b.txt"), 1024);

        var results = Run($"Invoke-BashDu -a '{Esc(dir)}'");

        // 2 files + 1 directory = 3 entries
        Assert.Equal(3, results.Length);
        var files = results.Where(r => ((string)r.Properties["Path"].Value).EndsWith(".txt")).ToArray();
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void Du_GrandTotalFlagC_EmitsTotalEntry()
    {
        var dir = Path.Combine(_tmpDir, "tree");
        Directory.CreateDirectory(dir);
        WriteBytes(Path.Combine(dir, "a.txt"), 1024);

        var results = Run($"Invoke-BashDu -c '{Esc(dir)}'");

        var total = results.SingleOrDefault(r => (bool)r.Properties["IsTotal"].Value);
        Assert.NotNull(total);
        Assert.Equal("total", (string)total!.Properties["Path"].Value);
        Assert.Equal(1024L, (long)total.Properties["SizeBytes"].Value);
    }

    [Fact]
    public void Du_DepthD1_LimitsRecursionDepth()
    {
        var dir = Path.Combine(_tmpDir, "tree");
        var sub = Path.Combine(dir, "sub");
        var subSub = Path.Combine(sub, "deeper");
        Directory.CreateDirectory(subSub);
        WriteBytes(Path.Combine(subSub, "x.txt"), 1024);

        var results = Run($"Invoke-BashDu -d 1 '{Esc(dir)}'");

        // depth 0 = root; depth 1 = sub. "deeper" (depth 2) excluded.
        Assert.Equal(2, results.Length);
        var paths = results.Select(r => (string)r.Properties["Path"].Value).ToArray();
        Assert.DoesNotContain(paths, p => p.Contains("/deeper"));
    }

    [Fact]
    public void Du_MultiOperand_EmitsEntriesForEach()
    {
        var d1 = Path.Combine(_tmpDir, "d1");
        var d2 = Path.Combine(_tmpDir, "d2");
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        WriteBytes(Path.Combine(d1, "a.txt"), 100);
        WriteBytes(Path.Combine(d2, "b.txt"), 100);

        var results = Run($"Invoke-BashDu -s '{Esc(d1)}' '{Esc(d2)}'");

        Assert.Equal(2, results.Length);
    }

    [Fact]
    public void Du_ViaAlias_Works()
    {
        var p = Path.Combine(_tmpDir, "f.txt");
        WriteBytes(p, 50);

        var results = Run($"du '{Esc(p)}'");

        Assert.Single(results);
        Assert.Equal("PsBash.DuEntry", results[0].TypeNames[0]);
    }

    [Fact]
    public void Du_HelpFlag_EmitsUsage()
    {
        var lines = RunText("Invoke-BashDu --help");

        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("du", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Du_InjectionInOperand_StaysLiteralPath()
    {
        // Directive 12: an operand containing $(...) and ; must not re-parse
        // as PowerShell. The string reaches Get-BashItem / Path resolver as a
        // literal path; the path does not exist so a bash-style error is
        // written, no thrown exception, no output object.
        // Use a single-quoted PowerShell string so $(...) does NOT expand at
        // the PS parser layer; the literal token reaches the cmdlet, where
        // GetUnresolvedProviderPathFromPSPath + Directory/File.Exists fails
        // → bash-style error, no thrown exception, no output object.
        var results = Run("$ErrorActionPreference='Continue'; Invoke-BashDu '$(throw ''pwn'');missing' 2>$null");

        // No success output objects.
        Assert.Empty(results);
    }

    [Fact]
    public void Du_NoOperand_DefaultsToCurrentDirectory()
    {
        WriteBytes(Path.Combine(_tmpDir, "a.txt"), 512);

        var results = Run("Invoke-BashDu -s");

        Assert.Single(results);
        Assert.Equal(512L, (long)results[0].Properties["SizeBytes"].Value);
    }

    [Fact]
    public void Du_DJoinedForm_SetsMaxDepth()
    {
        // -d3 joined form (oracle: ^-d(\d+)$)
        var dir = Path.Combine(_tmpDir, "tree");
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        WriteBytes(Path.Combine(sub, "x.txt"), 64);

        var results = Run($"Invoke-BashDu -d0 '{Esc(dir)}'");

        // depth 0 only: just root
        Assert.Single(results);
        Assert.Equal(dir.Replace('\\', '/'), (string)results[0].Properties["Path"].Value);
    }

    [Fact]
    public void Du_UnrecognizedOption_WritesError()
    {
        // Unrecognized option-like token: classified as "unrecognized option", exit 2.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "Invoke-BashDu --bogus 2>$null; $LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Single(result);
        Assert.Equal(2, (int)result[0].BaseObject);
    }

    [Fact]
    public void Du_ValidButUnsupportedOption_WritesError()
    {
        // Catalog flag (valid GNU, not implemented): classified as "recognized but not supported", exit 2.
        // Using long form --apparent-size (short -B is swallowed by the per-char bundle decoder).
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "Invoke-BashDu --apparent-size 2>$null; $LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Single(result);
        Assert.Equal(2, (int)result[0].BaseObject);
    }

    [Fact]
    public void Du_Exclude_PrunesMatchingSubtree()
    {
        // --exclude=GLOB prunes a matching directory (and its subtree) from the
        // totals. Build keep/ (1 file) and skip/ (1 large file); excluding skip
        // must drop it from the listing entirely.
        var keep = Path.Combine(_tmpDir, "keep");
        var skip = Path.Combine(_tmpDir, "skip");
        Directory.CreateDirectory(keep);
        Directory.CreateDirectory(skip);
        WriteBytes(Path.Combine(keep, "a.txt"), 100);
        WriteBytes(Path.Combine(skip, "big.txt"), 5000);

        var paths = RunText("Invoke-BashDu --exclude=skip .")
            .Select(t => t.Split('\t').Last())
            .ToArray();

        Assert.Contains(paths, p => p.EndsWith("keep", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("skip", StringComparison.Ordinal));
    }
}
