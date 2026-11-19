using System;
using System.IO;
using System.Linq;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A merge of stderr into a pipe (<c>a |&amp; b</c>) hands the next stage STDOUT records, made by
/// <c>PsBuild.MergeStderrIntoStdout</c> (<c>2&gt;&amp;1 | Invoke-BashRedirect -MergeErrors</c>), never PowerShell's raw
/// ErrorRecords.
/// <para>
/// The bug class: <c>|&amp;</c> emitted a bare <c> 2&gt;&amp;1 | </c>, so <c>cat</c> read each <c>echo X &gt;&amp;2</c> as an
/// ErrorRecord whose message already held its line's <c>\n</c>; under the one record -> bytes rule (text + boundary,
/// always: 0161d612) that was one more empty line (oils pipeline spec "|&amp;"). The guard keeps the emitter from building
/// the raw form again. Behavior against bash: <c>PipeDifferentialTests.Differential_PipeAmp_StderrWritesKeepTheirBytes</c>.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class StderrMergeGuardTests
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

    [Fact]
    public void Emitter_NeverPipesARawStderrMerge()
    {
        var parser = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var offenders = Directory.EnumerateFiles(parser, "PsEmitter*.cs")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && x.line.Contains("2>&1 | \"", StringComparison.Ordinal))
            .Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1) + ": " + x.line.Trim())
            .ToList();
        Assert.True(offenders.Count == 0,
            "a stderr merge into a pipe goes through PsBuild.MergeStderrIntoStdout:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void PipeAmp_MergesThroughTheStdoutRecordStage()
    {
        var ps = PsEmitter.Transpile("f() { echo e >&2; }\nf |& cat")!;
        Assert.Contains(PsBuild.MergeStderrIntoStdout + " | Invoke-BashCat", ps);
    }
}
