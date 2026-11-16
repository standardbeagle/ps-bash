using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Keeps the stage-stdin model honest (docs/specs/emitter-strategy.md "Stage stdin"): a pipe stage's records are
/// <c>$input</c> of the stage's own script block only, and every wrapper nested inside sees an empty one. Five bugs of one
/// family came from emitted text that named <c>$input</c> somewhere a wrapper could already have hidden it. The emitted
/// text may name it only through the <c>PsBuild</c> stage-stdin primitives (<c>StdinScope</c>, <c>ForwardStageInput</c>,
/// <c>DrainStageInput</c>, <c>FunctionStdinScope</c>), so the transpiler's other sources must not contain the token.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class PsBuildStageStdinGuardTests
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
    public void Transpiler_NamesThePipeStageInputOnlyInPsBuild()
    {
        var offenders = new List<string>();
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var name = Path.GetFileName(file);
            if (name == "PsBuild.cs") continue;

            // PsEmitter.cs is Latin-1; the token is ASCII either way.
            var lines = File.ReadAllLines(file, Encoding.Latin1);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                // A trailing comment may describe the model; only the code before it counts.
                int comment = t.IndexOf(" // ", StringComparison.Ordinal);
                var code = comment < 0 ? t : t.Substring(0, comment);
                if (code.Contains("$input", StringComparison.Ordinal))
                    offenders.Add($"{name}:{i + 1}: {t}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A pipe stage's `$input` must be handed on through PsBuild (StdinScope / ForwardStageInput / DrainStageInput): " +
            "anywhere else a wrapper may already hide it. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PsBuild_NamesThePipeStageInputThroughOneConstant()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsBuild.cs"));
        var offenders = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;
            if (t.Contains("$input", StringComparison.Ordinal) && !t.StartsWith("public const string StageInput", StringComparison.Ordinal))
                offenders.Add($"PsBuild.cs:{i + 1}: {t}");
        }
        Assert.True(offenders.Count == 0, "PsBuild must spell `$input` only as StageInput. Offenders:\n  " + string.Join("\n  ", offenders));
    }
}
