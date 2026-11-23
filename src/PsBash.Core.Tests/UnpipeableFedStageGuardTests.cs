using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the fed-stage invariant: a command fed records (the stage after a pipe, or after the <c>Invoke-BashCat f</c> of
/// its <c>&lt; f</c>) whose emission is no pipe element (a statement list such as exec's <c>cmd; return</c>, an env
/// prefix's save/restore, or a <c>$( )</c> expression) is re-emitted in a stdin scope, whose readers take the records.
/// Wrapped in <c>&amp; { }</c> (<c>PsEmitter.WrapPipelineStageIfStatementList</c>) its statements never see the block's
/// <c>$input</c>, so the wrap is only for a stage that is not fed.
/// <para>
/// REGRESSION (ec198f16): the input-redirect site went through the wrap so <c>true &lt; f</c> would parse, and
/// <c>( exec cat &lt; &lt;(echo q3) )</c> became <c>... | &amp; { Invoke-BashCat; return }</c>: nothing printed, and
/// <c>( exec cat &lt; f; echo after )</c> printed only "after". <c>echo q | { X=1 cat; }</c> printed nothing too (the
/// prefixed command in a stdin scope was never fed).
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): emitted-text shapes and a call-site inventory; behavior vs bash: Differential
/// <c>ShellLanguageGapDifferentialTests.FedStatementListStage_ReadsItsInput</c>.</para>
/// </summary>
public class UnpipeableFedStageGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static readonly Regex MemberHead = new(@"^\s*(?:public|private|internal)\s+(?:static\s+)?[\w<>\[\]?,.]+\s+(\w+)\s*[(<]", RegexOptions.Compiled);

    /// <summary>Members that may call the wrap, each with why the stage it wraps is not fed through it.</summary>
    private static readonly Dictionary<string, string> WrapCallers = new(StringComparer.Ordinal)
    {
        ["PsEmitter.cs:EmitPipelineStages"] ="a stage that is neither simple nor compound, and a pipefail stage's text, which is the first "
            + "stage or already fed by its own wrapper (EmitSimplePipeStage / a stdin scope)",
    };

    [Fact]
    public void WrapPipelineStageIfStatementList_IsCalledOnlyWhereTheStageIsNotFed()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "PsEmitter*.cs"))
        {
            string? member = null;
            foreach (var line in File.ReadLines(file))
            {
                if (MemberHead.Match(line) is { Success: true } m) member = m.Groups[1].Value;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
                if (line.Contains("WrapPipelineStageIfStatementList(", StringComparison.Ordinal) && member != "WrapPipelineStageIfStatementList")
                    found.Add(Path.GetFileName(file) + ":" + member);
            }
        }
        var unknown = found.Where(f => !WrapCallers.ContainsKey(f)).ToList();
        Assert.True(unknown.Count == 0,
            "WrapPipelineStageIfStatementList hands its statements no input. A FED stage that is no pipe element "
            + "(IsUnpipeableStage) runs in a stdin scope instead; list a caller here only with the reason its stage is not fed:\n  "
            + string.Join("\n  ", unknown));
        var stale = WrapCallers.Keys.Where(k => !found.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "WrapCallers members that no longer call the wrap: " + string.Join(", ", stale));
    }

    /// <summary>The reader inside the statement list takes the shared stdin cursor.</summary>
    private static readonly string FedCat = PsBuild.StdinFeed + " | Invoke-BashCat";

    [Theory]
    [InlineData("( exec cat < f )")]
    [InlineData("( exec cat < <(echo q3) )")]
    [InlineData("( exec cat < f; echo after )")]
    [InlineData("exec cat < f")]
    [InlineData("y=$(exec cat < f)")]
    [InlineData("k() { exec cat < f; echo no; }")]
    [InlineData("( exec 2>/dev/null cat < f )")]
    [InlineData("echo q | { X=1 cat; }")]
    [InlineData("cat f | ( X=1 cat )")]
    [InlineData("while read -r l; do X=1 cat; done < f")]
    public void Transpile_FedStatementList_ReadsThroughTheStdinScope(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(FedCat, ps);
        Assert.DoesNotContain("& { Invoke-BashCat;", ps);
        Assert.DoesNotContain("& { Invoke-BashCat 2>$null;", ps);
    }

    /// <summary>
    /// An expression fed a file (<c>true &lt; f</c>, a return) is no pipe element either: it runs in the scope, never after
    /// a bare <c>|</c> (the whole script failed to parse, ec198f16).
    /// </summary>
    [Theory]
    [InlineData("true < f")]
    [InlineData(": < f")]
    [InlineData("false < f")]
    public void Transpile_ExpressionFedAFile_IsNotABarePipeElement(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.DoesNotContain("-RawStdinBytes | $(", ps);
        Assert.DoesNotContain("| ForEach-Object { Get-BashText $_ } | $(", ps);
    }

    /// <summary>A single-command stage after the file stays a plain pipe element (no scope, no wrap).</summary>
    [Theory]
    [InlineData("cat < f", "-RawStdinBytes | Invoke-BashCat")]
    [InlineData("sort -r < f", "-RawStdinBytes | Invoke-BashSort")]
    public void Transpile_SingleCommandFedAFile_StaysAPlainStage(string script, string expected)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(expected, ps);
        Assert.DoesNotContain("__BashStdIn", ps);
    }
}
