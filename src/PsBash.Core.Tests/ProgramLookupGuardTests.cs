using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// "Does this name run a program?" is asked through <c>PsBash.Cmdlets.ProgramLookup</c> only, which looks past the
/// lookup hooks' stand-ins (<see cref="NativeRunner"/>). REGRESSION: while an exported scalar held an invalid byte the
/// shell's hook stood in for every program, and <c>GetCommand(name) is ApplicationInfo</c> in
/// <c>EnterBashNativeStdinCommand.ResolvesToApplication</c> saw a function: <c>printf ab | /usr/bin/od -c</c> showed
/// <c>a b \n</c> (text records, each given a newline), and <c>printf 'x\n' | { /usr/bin/cat; }</c> read the host's fd 0.
/// Behavior vs bash: Differential <c>RawBytesDifferentialTests.Pipe_IntoAProgram_WhileAnExportedScalarIsKept</c>.
/// <para>
/// The rule is on the LOOKUP, not on the test of its result (a result kept in a variable and tested later is the same
/// bypass): <c>InvokeCommand.GetCommand</c> runs the runspace's lookup hook, so a call that may return a program (any
/// command type but Function / Alias / Cmdlet / script kinds, or one not written out on the line) is made inside
/// <c>NativeRunner.Raw</c> / <c>JobProgramLookup.Raw</c>, in ProgramLookup, or is inventoried with its reason. The
/// <c>Get-Command</c> cmdlet runs no lookup hook (pinned by Cmdlets.Tests <c>ProgramLookupTests</c>), so a script's
/// <c>Get-Command</c> sees the program; a script's <c>$ExecutionContext.InvokeCommand.GetCommand(</c> is caught here
/// as text.
/// </para>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class ProgramLookupGuardTests
{
    private const string Primitive = "ProgramLookup.cs";

    // GetCommand( / GetCommand (, in C# or in script text.
    private static readonly Regex Lookup = new(@"\bGetCommand\s*\(", RegexOptions.Compiled);

    // The command types written out on the line, every one of which can never be a program.
    private static readonly Regex NeverAProgram = new(
        @"GetCommand\s*\([^,]+,\s*(CommandTypes\.|')?(Function|Alias|Cmdlet|Filter|Configuration)'?(\s*[|,]\s*(CommandTypes\.|')?(Function|Alias|Cmdlet|Filter|Configuration)'?)*\s*\)",
        RegexOptions.Compiled);

    // Hook-visible on purpose, with why.
    private static readonly (string File, string Line, string Reason)[] Inventory =
    [
        ("ConvertToBashNativeStdinCommand.cs", "is CommandInfo ci and (FunctionInfo or ScriptInfo)",
            "asks what the hook hands back (ProgramFile's stand-in for a #! script) after ProgramLookup said: not a program"),
    ];

    internal static List<string> Offenders(IEnumerable<(string Name, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (name, text) in files)
        {
            var file = Path.GetFileName(name);
            if (file == Primitive) continue;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var t = line.TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)
                    || t.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!Lookup.IsMatch(line) || NeverAProgram.IsMatch(line)) continue;
                if (line.Contains("NativeRunner.Raw(", StringComparison.Ordinal)
                    || line.Contains("JobProgramLookup.Raw(", StringComparison.Ordinal)) continue;
                if (Inventory.Any(e => e.File == file && line.Contains(e.Line, StringComparison.Ordinal))) continue;
                offenders.Add($"{name}:{i + 1}: {t.TrimEnd()}");
            }
        }
        return offenders;
    }

    [Fact]
    public void ProgramChecks_GoThroughProgramLookup()
    {
        var root = RepoRoot();
        var files = new[] { "PsBash.Cmdlets", "PsBash.Host", "PsBash.Transpiler", "PsBash.Core", "PsBash.Module" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".psm1", StringComparison.Ordinal)
                        || f.EndsWith(".ps1", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)))
            .ToList();
        var offenders = Offenders(files);
        Assert.True(offenders.Count == 0,
            "A command lookup that may return a program runs the lookup hook, which may hand back a stand-in: ask "
            + "ProgramLookup.Find (or wrap it in NativeRunner.Raw). Offenders:\n  " + string.Join("\n  ", offenders));
        Assert.True(File.Exists(Path.Combine(root, "src", "PsBash.Cmdlets", Primitive)), Primitive + " moved: update this guard");
        foreach (var entry in Inventory)
            Assert.True(files.Any(f => Path.GetFileName(f.Item1) == entry.File && f.Item2.Contains(entry.Line, StringComparison.Ordinal)),
                $"{entry.File}: the inventoried lookup ({entry.Reason}) is gone; remove the entry");
    }

    [Fact]
    public void TheGuard_CatchesTheBypassThatCausedTheBug()
    {
        // The bug, and the same check through a variable.
        Assert.Single(Offenders([("X.cs", "try { return cmdlet.InvokeCommand.GetCommand(name, CommandTypes.All) is ApplicationInfo; }")]));
        Assert.Single(Offenders([("X.cs", "var c = InvokeCommand.GetCommand(name, CommandTypes.All);\nif (c is ApplicationInfo app) Run(app);")]));
        Assert.Single(Offenders([("X.cs", "var c = InvokeCommand.GetCommand(name, CommandTypes.Application | CommandTypes.Function);")]));
        Assert.Single(Offenders([("X.cs", "var c = InvokeCommand.GetCommand(name, types);")]));
        // In script text.
        Assert.Single(Offenders([("X.cs", "\"$c = $ExecutionContext.InvokeCommand.GetCommand($n, 'All'); $c -is [System.Management.Automation.ApplicationInfo]\"")]));
        Assert.Single(Offenders([("x.psm1", "$c = $ExecutionContext.InvokeCommand.GetCommand($n, 'Application')")]));

        // Allowed: the primitive, a Raw lookup, a lookup that can never return a program, prose.
        Assert.Empty(Offenders([("ProgramLookup.cs", "x = GetCommand(name, CommandTypes.All)) is ApplicationInfo app")]));
        Assert.Empty(Offenders([("X.cs", "info = NativeRunner.Raw(() => InvokeCommand.GetCommand(Name, CommandTypes.All));")]));
        Assert.Empty(Offenders([("X.cs", "fn = InvokeCommand.GetCommand(name, CommandTypes.Function) as FunctionInfo;")]));
        Assert.Empty(Offenders([("X.cs", "a = InvokeCommand.GetCommand(name, CommandTypes.Alias | CommandTypes.Function);")]));
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
