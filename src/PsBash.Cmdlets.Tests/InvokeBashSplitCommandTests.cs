using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashSplit</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="PsBash.Cmdlets.InvokeBashSplitCommand"/>.
///
/// Oracle: the original psm1 function. Each test stands up an isolated temp
/// working directory, invokes the cmdlet with <c>Set-Location</c> targeting
/// that directory, then asserts on the output files produced under it.
///
/// Failure-surface axes covered (per Directive 3): default-size single-piece
/// case, <c>-l</c> multi-piece split, <c>-d</c> numeric suffix, <c>-a</c>
/// suffix length, custom prefix, missing-input file (error continuation),
/// <c>--help</c>, alias resolution, and a Directive-12 injection probe on the
/// prefix operand.
/// </summary>
public class InvokeBashSplitCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashSplitCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-split-{Guid.NewGuid():N}".Substring(0, 23));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunInDir(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var wrapped = $"Set-Location -LiteralPath '{Esc(_tmpDir)}'; {script}";
        var result = pwsh.AddScript(wrapped).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            if (o == null) return "";
            var bashText = o.Properties["BashText"]?.Value as string;
            if (bashText != null) return bashText;
            return o.ToString() ?? "";
        }).ToArray();
    }

    private (string[] outLines, string[] errors) RunInDirWithErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var wrapped = $"Set-Location -LiteralPath '{Esc(_tmpDir)}'; {script}";
        var result = pwsh.AddScript(wrapped).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var outLines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        return (outLines, errs);
    }

    private static string Esc(string path) => path.Replace("'", "''");

    private static string MakeLines(int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= count; i++)
        {
            sb.Append("line").Append(i).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Split_Default1000_OneFileForSmallInput()
    {
        var input = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(input, MakeLines(100));

        RunInDir($"Invoke-BashSplit '{Esc(input)}'");

        Assert.True(File.Exists(Path.Combine(_tmpDir, "xaa")));
        Assert.False(File.Exists(Path.Combine(_tmpDir, "xab")));
        var lines = File.ReadAllLines(Path.Combine(_tmpDir, "xaa"));
        Assert.Equal(100, lines.Length);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line100", lines[99]);
    }

    [Fact]
    public void Split_LFlag10_ProducesFivePieces()
    {
        var input = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(input, MakeLines(50));

        RunInDir($"Invoke-BashSplit -l 10 '{Esc(input)}'");

        var expected = new[] { "xaa", "xab", "xac", "xad", "xae" };
        foreach (var name in expected)
        {
            var p = Path.Combine(_tmpDir, name);
            Assert.True(File.Exists(p), $"expected {name}");
            Assert.Equal(10, File.ReadAllLines(p).Length);
        }
        Assert.False(File.Exists(Path.Combine(_tmpDir, "xaf")));
    }

    [Fact]
    public void Split_NumericSuffixD_ProducesZeroPaddedNames()
    {
        var input = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(input, MakeLines(30));

        RunInDir($"Invoke-BashSplit -l 10 -d '{Esc(input)}'");

        Assert.True(File.Exists(Path.Combine(_tmpDir, "x00")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "x01")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "x02")));
        Assert.False(File.Exists(Path.Combine(_tmpDir, "x03")));
    }

    [Fact]
    public void Split_AFlag3_ProducesThreeCharSuffix()
    {
        var input = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(input, MakeLines(20));

        RunInDir($"Invoke-BashSplit -l 10 -a 3 '{Esc(input)}'");

        Assert.True(File.Exists(Path.Combine(_tmpDir, "xaaa")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "xaab")));
        Assert.False(File.Exists(Path.Combine(_tmpDir, "xaac")));
    }

    [Fact]
    public void Split_CustomPrefix_UsesItForOutputNames()
    {
        var input = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(input, MakeLines(20));

        RunInDir($"Invoke-BashSplit -l 10 '{Esc(input)}' 'chunk-'");

        Assert.True(File.Exists(Path.Combine(_tmpDir, "chunk-aa")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "chunk-ab")));
    }

    [Fact]
    public void Split_MissingFile_NoOutputFilesCreated()
    {
        var ghost = Path.Combine(_tmpDir, "ghost.txt");

        // Suppress stderr so a bash-style error doesn't fail the test runner;
        // we only care that no output files appear.
        RunInDir($"Invoke-BashSplit '{Esc(ghost)}' 2>$null");

        Assert.False(File.Exists(Path.Combine(_tmpDir, "xaa")));
    }

    [Fact]
    public void Split_HelpFlag_EmitsUsage()
    {
        var lines = RunInDir("Invoke-BashSplit --help");
        Assert.NotEmpty(lines);
        Assert.Contains(
            lines,
            l => l.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Split_ViaAlias_Works()
    {
        var input = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(input, MakeLines(5));

        RunInDir($"split -l 2 '{Esc(input)}'");

        Assert.True(File.Exists(Path.Combine(_tmpDir, "xaa")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "xab")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "xac")));
    }

    [Fact]
    public void Split_PrefixWithScriptblockChars_TreatedAsLiteralName()
    {
        // Directive 12: a user-controlled prefix containing $(...) and ;
        // must not re-parse as a PowerShell scriptblock or command separator.
        // The cmdlet receives it as a string operand and emits it literally
        // as the output-file basename prefix.
        var input = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(input, MakeLines(5));

        var weirdPrefix = "$(throw'pwn');pfx-";
        RunInDir($"Invoke-BashSplit -l 5 '{Esc(input)}' '{Esc(weirdPrefix)}'");

        // The literal prefix string must appear in the output filename, with
        // no thrown exception (the suppress-on-stderr would not save us — a
        // thrown PS exception aborts the script before the file is written).
        var expected = Path.Combine(_tmpDir, weirdPrefix + "aa");
        Assert.True(File.Exists(expected), $"expected literal-prefix file {expected}");
    }

    [Fact]
    public void Split_PipelineInput_UsesDefaultXPrefix()
    {
        // Three pipeline items become three lines. -l 1 produces three pieces.
        RunInDir("'a','b','c' | Invoke-BashSplit -l 1");

        Assert.True(File.Exists(Path.Combine(_tmpDir, "xaa")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "xab")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "xac")));
    }

    [Fact]
    public void Split_ValidButUnsupportedFlag_NotSupportedMessage()
    {
        // --number is a real GNU split flag (split into N chunks) but
        // ps-bash does not implement it. Must report "not supported",
        // not "No such file or directory".
        var (_, errs) = RunInDirWithErrors("'a','b' | Invoke-BashSplit --number 2");
        Assert.Contains(errs, m => m.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Split_UnrecognizedLongOption_BashParityMessage()
    {
        // --bogus is not a real split option → bash-style "unrecognized option".
        var (_, errs) = RunInDirWithErrors("'a','b' | Invoke-BashSplit --bogus");
        Assert.Contains(errs, m => m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("--bogus", StringComparison.Ordinal));
    }
}
