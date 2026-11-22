using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// GUARD: an array write is a hot path (every <c>arr+=(x)</c> in a function, every <c>mapfile</c> / <c>read -a</c>, in
/// every pipe stage and <c>$( )</c>), so it looks its variable up once, from C#: no script, no exception.
/// REGRESSION: in a subshell each write ran a script that walked the scopes with <c>Get-Variable -Scope $i
/// -ErrorAction Stop</c> (each miss a thrown exception), and <c>a+=(x)</c> named its slot twice, so
/// <c>seq 10000 | while read n; do add "$n"; done</c> with <c>add() { items+=("$1"); }</c> paid two scripts per append.
/// A subshell's writes end with it through <c>BashVariableLookup.ArrayWriteJournal</c> (undone by
/// <c>BashShellState.Restore</c>), not by finding its scope.
/// Oracle note (qa-rubric Directive 1): a cost invariant with no bash equivalent; the behavior is pinned against bash
/// by <c>Differential.Tests/ShellLanguageGapDifferentialTests.Subshell_ArrayWritesToAnInheritedArrayStayInIt</c>.
/// </summary>
public class ArrayWriteHotPathGuardTests
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

    // The run-time array lookup and write primitives.
    private static readonly string[] HotFiles = ["BashVariableLookup.cs", "VarFrameStack.cs", "BashArrayElement.cs"];

    // A script run from C#, or a PowerShell scope walk.
    private static readonly Regex ScriptOrScopeWalk = new(
        @"InvokeScript\(|ScriptBlock\.Create\(|-ErrorAction\s+Stop|Get-Variable\b[^""]*-Scope\s+\$", RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(HotFileData))]
    public void ArrayLookupPrimitive_RunsNoScriptAndWalksNoScope(string file)
    {
        string path = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", file);
        var offenders = File.ReadAllText(path).Split('\n')
            .Select((line, i) => (line, n: i + 1))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && ScriptOrScopeWalk.IsMatch(x.line))
            .Select(x => $"{file}:{x.n}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "An array lookup/write primitive runs a script or walks PowerShell scopes. Find the variable with "
            + "SessionState.PSVariable.Get and let the subshell's ArrayWriteJournal end the write. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    public static TheoryData<string> HotFileData()
    {
        var data = new TheoryData<string>();
        foreach (var file in HotFiles) data.Add(file);
        return data;
    }

    private const string Slot = "[PsBash.Cmdlets.VarFrameStack]::ArraySlot(";

    [Theory]
    [InlineData("f() { a+=(x); }; f")]
    [InlineData("f() { a=(x y); }; f")]
    [InlineData("f() { a[1]=x; }; f")]
    [InlineData("f() { a[1]+=x; }; f")]
    [InlineData("f() { m+=([k]=v); }; f")]
    [InlineData("( f() { a+=(x); }; f )")]
    [InlineData("seq 3 | while read n; do add \"$n\"; done; add() { items+=(\"$1\"); }")]
    public void Transpile_ArrayWriteInAFunction_LooksItsSlotUpOnce(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.Single(Regex.Matches(ps, Regex.Escape(Slot)));
    }
}
