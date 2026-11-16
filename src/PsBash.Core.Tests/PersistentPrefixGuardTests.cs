using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A prefix assignment is a temp binding frame while its command runs, also when it persists afterwards
/// (<c>set -o posix; v=t special-builtin</c>): <c>v=t unset v</c> must take the frame and reveal the value under it.
/// The persisting form was once emitted as a plain assignment followed by the builtin, which lost that value
/// (docs/specs/emitter-strategy.md "Prefix assignments"). Every POSIX special builtin the emitter knows must therefore
/// emit its POSIX form through <c>PsBuild.BindSameScope</c> / <c>PsBuild.PersistSameScope</c>; the names are read from
/// the emitter's own table, so a builtin added there is covered without touching this test.
/// Oracle note (qa-rubric Directive 1): the emitted text is ps-bash's own; the behaviour is compared with bash in
/// Differential.Tests/TempBindingDifferentialTests.
/// </summary>
public class PersistentPrefixGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static string EmitterSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"), Encoding.Latin1);

    public static IEnumerable<object[]> SpecialBuiltins()
    {
        var m = Regex.Match(EmitterSource(), @"s_posixSpecialBuiltins = new\(StringComparer\.Ordinal\)\s*\{([^}]*)\}");
        if (!m.Success) throw new InvalidOperationException("s_posixSpecialBuiltins not found in PsEmitter.cs");
        var names = Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
        if (names.Count < 10) throw new InvalidOperationException("s_posixSpecialBuiltins parsed to " + names.Count + " names");
        return names.Select(n => new object[] { n });
    }

    [Theory]
    [MemberData(nameof(SpecialBuiltins))]
    public void Transpile_PosixPrefixBeforeSpecialBuiltin_IsABindingFrameThatPersists(string builtin)
    {
        string ps = BashTranspiler.Transpile("set -o posix\nvv=t " + builtin + " arg\n");
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::BindSameScope('vv'", ps);
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::PersistSameScope('vv')", ps);
    }

    [Fact]
    public void Transpile_PosixPrefixBeforeExportDeclaration_IsABindingFrameThatPersists()
    {
        string ps = BashTranspiler.Transpile("set -o posix\nvv=t export w=1\n");
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::PersistSameScope('vv')", ps);
    }

    [Fact]
    public void Transpile_WithoutPosixMode_EmitsNoPersistingBinding()
    {
        string ps = BashTranspiler.Transpile("vv=t unset vv\nvv=t eval 'unset vv'\n");
        Assert.DoesNotContain("PersistSameScope", ps);
    }

    /// <summary>The persisting form has one emitter: nothing else in the transpiler may write the primitive's call.</summary>
    [Fact]
    public void Emitter_PersistsAPrefixOnlyThroughEmitPersistentPrefix()
    {
        var lines = EmitterSource().Split('\n');
        var uses = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("PsBuild.PersistSameScope(", StringComparison.Ordinal)) uses.Add(i);
        Assert.Single(uses);
        int start = uses[0];
        while (start > 0 && !lines[start].Contains("private static string ", StringComparison.Ordinal)) start--;
        Assert.Contains("EmitPersistentPrefix(", lines[start]);
        // The POSIX form of a special builtin must not go back to a plain assignment in front of the builtin.
        int posix = Array.FindIndex(lines, l => l.Contains("private static string EmitPosixSpecialBuiltin(", StringComparison.Ordinal));
        Assert.True(posix >= 0, "EmitPosixSpecialBuiltin not found");
        int end = Array.FindIndex(lines, posix + 1, l => l.StartsWith("    }", StringComparison.Ordinal));
        Assert.DoesNotContain(lines[posix..end], l => l.Contains("EmitShAssignment(", StringComparison.Ordinal));
        Assert.Contains(lines[posix..end], l => l.Contains("EmitPersistentPrefix(", StringComparison.Ordinal));
    }
}
