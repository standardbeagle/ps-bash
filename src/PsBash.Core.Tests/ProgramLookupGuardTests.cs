using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// "Does this name run a program?" is asked through <c>PsBash.Cmdlets.ProgramLookup.Find</c> only, which looks past the
/// lookup hooks' stand-ins (<see cref="NativeRunner"/>). REGRESSION: while an exported scalar held an invalid byte the
/// shell's hook stood in for every program, and <c>GetCommand(name) is ApplicationInfo</c> in
/// <c>EnterBashNativeStdinCommand.ResolvesToApplication</c> saw a function: <c>printf ab | /usr/bin/od -c</c> showed
/// <c>a b \n</c> (text records, each given a newline), and <c>printf 'x\n' | { /usr/bin/cat; }</c> read the host's fd 0.
/// Behavior vs bash: Differential <c>RawBytesDifferentialTests.Pipe_IntoAProgram_WhileAnExportedScalarIsKept</c>.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class ProgramLookupGuardTests
{
    private const string Primitive = "ProgramLookup.cs";

    // A command lookup whose result is tested for a program on the same line.
    private static readonly Regex Bypass = new(@"GetCommand\(.*ApplicationInfo", RegexOptions.Compiled);

    internal static List<string> Offenders(IEnumerable<(string Name, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (name, text) in files)
        {
            if (Path.GetFileName(name) == Primitive) continue;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (Bypass.IsMatch(lines[i])) offenders.Add($"{name}:{i + 1}: {t.TrimEnd()}");
            }
        }
        return offenders;
    }

    [Fact]
    public void ProgramChecks_GoThroughProgramLookup()
    {
        var root = RepoRoot();
        var files = new[] { "PsBash.Cmdlets", "PsBash.Host" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)));
        var offenders = Offenders(files);
        Assert.True(offenders.Count == 0,
            "Ask whether a name runs a program with ProgramLookup.Find (it looks past the lookup hooks' stand-ins). Offenders:\n  "
            + string.Join("\n  ", offenders));
        Assert.True(File.Exists(Path.Combine(root, "src", "PsBash.Cmdlets", Primitive)), Primitive + " moved: update this guard");
    }

    [Fact]
    public void TheGuard_CatchesTheBypassThatCausedTheBug()
    {
        Assert.Single(Offenders([("X.cs", "try { return cmdlet.InvokeCommand.GetCommand(name, CommandTypes.All) is ApplicationInfo; }")]));
        Assert.Empty(Offenders([("ProgramLookup.cs", "x = GetCommand(name, CommandTypes.All)) is ApplicationInfo app")]));
        Assert.Empty(Offenders([("Y.cs", "// GetCommand(name) is ApplicationInfo in prose")]));
    }

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
}
