using Xunit;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// Tests for <see cref="BashTranspiler.TranspileWithMap"/> which returns the
/// transpiled PowerShell together with a line map from pwsh lines back to
/// the original bash source lines.
/// </summary>
public class TranspileWithMapTests
{
    [Fact]
    public void SingleCommand_ProducesOneLineMapping()
    {
        var result = BashTranspiler.TranspileWithMap("echo hello");
        Assert.NotNull(result.PowerShell);
        Assert.Equal("Invoke-BashEcho hello", result.PowerShell);
        Assert.Single(result.LineMap);
        Assert.Equal(1, result.LineMap[0].PwshLine);
        Assert.Equal(1, result.LineMap[0].BashLine);
        Assert.Equal(1, result.LineMap[0].BashCol);
    }

    [Fact]
    public void FourStatements_WithCommentAndBlanks_ProduceMonotonicPwshLines()
    {
        // Acceptance: printf 'a\nb\n  # comment\nc\n' transpiles, map's pwsh
        // lines cover [1..N] monotonically, each mapping points back to a
        // real bash line.
        var source = "a\nb\n  # comment\nc\n";
        // Use placeholder commands that actually parse as simple commands.
        // "a", "b", "c" aren't mapped commands, so they emit as-is.
        var result = BashTranspiler.TranspileWithMap(source);

        Assert.NotEmpty(result.LineMap);

        // Pwsh lines are 1-based, monotonically non-decreasing, and cover [1..N].
        for (int i = 0; i < result.LineMap.Count; i++)
        {
            Assert.Equal(i + 1, result.LineMap[i].PwshLine);
        }

        // Each mapping must point to a real (non-blank, non-comment-only) bash line.
        // Our source has three real statements: line 1 (a), line 2 (b), line 4 (c).
        var bashLines = new System.Collections.Generic.HashSet<int>();
        foreach (var m in result.LineMap)
        {
            bashLines.Add(m.BashLine);
            Assert.Contains(m.BashLine, new[] { 1, 2, 4 });
            Assert.True(m.BashCol >= 1);
        }

        // Bash lines in the map must be strictly increasing for distinct
        // statements (monotonic back to source).
        int prev = 0;
        foreach (var m in result.LineMap)
        {
            Assert.True(m.BashLine >= prev,
                $"Bash lines must be non-decreasing; got {m.BashLine} after {prev}.");
            prev = m.BashLine;
        }
    }

    [Fact]
    public void TranspileWrapper_AndWithMap_EmitSameStatementsModuloSeparator()
    {
        // The plain wrapper joins top-level statements with "; " (single line);
        // TranspileWithMap joins with "\n" so each statement gets its own line
        // for the bash↔pwsh line map. The per-statement bodies must match.
        var source = "echo one\necho two\necho three";
        var plain = BashTranspiler.Transpile(source);
        var mapped = BashTranspiler.TranspileWithMap(source);
        Assert.Equal(plain.Replace("; ", "\n"), mapped.PowerShell);
    }

    [Fact]
    public void MultipleStatements_PwshLineCountMatchesStatementCount()
    {
        var result = BashTranspiler.TranspileWithMap("echo a\necho b\necho c");
        Assert.Equal(3, result.LineMap.Count);
        Assert.Equal(1, result.LineMap[0].BashLine);
        Assert.Equal(2, result.LineMap[1].BashLine);
        Assert.Equal(3, result.LineMap[2].BashLine);
    }

    [Fact]
    public void SemicolonSeparatedStatements_EmitOneMappingEach()
    {
        // "a; b; c" is three statements on one bash line. Spec: take the
        // first-token span as representative for each statement.
        var result = BashTranspiler.TranspileWithMap("a; b; c");
        Assert.Equal(3, result.LineMap.Count);
        Assert.All(result.LineMap, m => Assert.Equal(1, m.BashLine));
        // Columns should reflect the offset of each statement's first token.
        Assert.Equal(1, result.LineMap[0].BashCol);
        Assert.Equal(4, result.LineMap[1].BashCol);
        Assert.Equal(7, result.LineMap[2].BashCol);
    }

    [Fact]
    public void EmptyInput_ProducesEmptyResult()
    {
        var result = BashTranspiler.TranspileWithMap("");
        Assert.Equal(string.Empty, result.PowerShell);
        Assert.Empty(result.LineMap);
    }

    [Fact]
    public void WhitespaceAndCommentsOnly_ProducesEmptyResult()
    {
        var result = BashTranspiler.TranspileWithMap("  \n# just a comment\n\n");
        Assert.Equal(string.Empty, result.PowerShell);
        Assert.Empty(result.LineMap);
    }

    [Fact]
    public void LineMappingStruct_HasExpectedShape()
    {
        var m = new LineMapping(PwshLine: 3, BashLine: 2, BashCol: 5);
        Assert.Equal(3, m.PwshLine);
        Assert.Equal(2, m.BashLine);
        Assert.Equal(5, m.BashCol);
    }

    [Fact]
    public void TranspileResult_HasExpectedShape()
    {
        var r = new TranspileResult("cmd", new[] { new LineMapping(1, 1, 1) });
        Assert.Equal("cmd", r.PowerShell);
        Assert.Single(r.LineMap);
    }
}
