using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Whether the positional parameters are the shell's list or the script's <c>$args</c> is decided by
/// <see cref="PsBuild.PositionalIsShells"/> (a <c>$null</c> test), never by the truthiness of
/// <c>$global:BashPositional</c>.
/// <para>
/// The bug class: nine hand-copied <c>if ($global:BashPositional) { … } else { $args… }</c> sites read an EMPTY list
/// (a <c>shift</c> past the last argument, <c>set --</c>) as "none" and fell back to <c>$args</c>: in a function
/// <c>while [ "$1" ]; do shift; done</c> never ended (the loop limit stopped it), and <c>set --; echo "$1"</c> printed
/// the script's first argument. Behavior against bash: <c>ShellLanguageGapDifferentialTests</c>
/// (<c>ShiftToEmpty_InAFunction_EndsTheLoop</c>).
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): which PowerShell test the emitter writes has no bash equivalent, so a source check.</para>
/// </summary>
public class PositionalFallbackGuardTests
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

    private static readonly Regex TruthinessTest = new(@"if\s*\(\s*\$global:BashPositional\s*\)", RegexOptions.CultureInvariant);

    [Fact]
    public void NoSourceTestsThePositionalListForTruthiness()
    {
        string src = Path.Combine(RepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (!(file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".psm1", StringComparison.Ordinal)
                  || file.EndsWith(".ps1", StringComparison.Ordinal))) continue;
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}") || file.Contains(".Tests" + sep)) continue;
            if (TruthinessTest.IsMatch(File.ReadAllText(file, System.Text.Encoding.Latin1)))
                offenders.Add(Path.GetRelativePath(src, file));
        }
        Assert.True(offenders.Count == 0,
            "Test the positional list with PsBuild.PositionalIsShells (an empty list is still the shell's):\n"
            + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("#")]
    [InlineData("@")]
    public void PositionalExpansion_TestsForNull(string sigil)
        => Assert.Contains(PsBuild.PositionalIsShells, PsBuild.BuildPositionalExpansion(sigil));
}
