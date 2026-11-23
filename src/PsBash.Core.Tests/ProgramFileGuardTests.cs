using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the program-file invariant (<c>docs/specs/program-files.md</c>): a site that holds a program FILE it resolved
/// (an <c>ApplicationInfo</c>, a PATH search, a path it checked) asks <see cref="ProgramFile.Plan"/> before starting it.
/// The bug class: <c>command ./x</c> and <c>nohup ./x</c> handed a <c>#!</c> script straight to the OS (or to PowerShell
/// as a resolved command), skipping the shebang dispatch that only PowerShell's NAME lookup applied, so on Windows it
/// failed or opened an "open with" dialog instead of running.
/// <para>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle. The behavior is pinned against bash by <c>ScriptFileExecutionDifferentialTests</c>.
/// </para>
/// </summary>
public class ProgramFileGuardTests
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

    private static readonly string[] ProductProjects =
        ["PsBash.Cmdlets", "PsBash.Host", "PsBash.Core", "PsBash.Transpiler", "PsBash.Shell"];

    private static IEnumerable<(string Name, string Text)> Sources()
    {
        foreach (var project in ProductProjects)
        {
            string root = Path.Combine(RepoRoot(), "src", project);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                yield return (Path.GetFileName(file), File.ReadAllText(file));
            }
        }
    }

    /// <summary>The text without comment lines, so a site or the primitive named only in prose does not count.</summary>
    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>Holding a resolved program file: PowerShell's lookup result, a PATH search, an executable-file check, a detached spawn.</summary>
    private static readonly Regex ResolvedProgram = new(
        @"\bApplicationInfo\b|\bExecutableLookup\.(Find|FindAll|IsExecutableFile)\(|\bDetachedProcess\.Spawn\(",
        RegexOptions.Compiled);

    private static readonly Regex Primitive = new(@"\bProgramFile\.(Plan|FromShebang)\(", RegexOptions.Compiled);

    /// <summary>Files that resolve a program but never start the file they found, each with its reason.</summary>
    private static readonly Dictionary<string, string> NeverStartsTheFile = new(StringComparer.Ordinal)
    {
        ["ProgramFile.cs"] = "the primitive: finds a #! line's interpreter on PATH",
        ["InvokeBashHashCommand.cs"] = "`hash NAME` records where NAME is; it runs nothing",
        ["InvokeBashTypeCommand.cs"] = "`type`/`which` report where a name is; they run nothing",
        ["InvokeBashTestCommand.cs"] = "`test -x` reports a mode; it runs nothing",
        ["TabCompleter.cs"] = "lists command names to complete; it runs nothing",
        ["InvokeBashTracerouteCommand.cs"] = "starts the system's own traceroute/tracert program, never a user file",
        ["SystemTools.cs"] = "finds the OS's own stat/id/ps in /usr/bin, /bin (native binaries), never a user file",
    };

    [Fact]
    public void EverySiteThatHoldsAResolvedProgramFile_AsksProgramFileHowToStartIt()
    {
        var offenders = new List<string>();
        var sites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, text) in Sources())
        {
            string code = Code(text);
            if (!ResolvedProgram.IsMatch(code)) continue;
            sites.Add(name);
            if (NeverStartsTheFile.ContainsKey(name)) continue;
            if (!Primitive.IsMatch(code)) offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "These files hold a resolved program file without asking ProgramFile.Plan(path) how to start it. A #! script "
            + "on Windows (or a file with no execute bit on Unix) cannot be handed to the OS or invoked as a resolved "
            + "CommandInfo: run launch.StandInScript(...) in its place, or launch.CommandLine(...) when the site starts a "
            + "process itself (program-files.md). A site that never starts the file goes in NeverStartsTheFile with its "
            + "reason. Offenders:\n  " + string.Join("\n  ", offenders));

        var stale = NeverStartsTheFile.Keys.Where(k => !sites.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Exempted files that no longer resolve a program: " + string.Join(", ", stale));
    }

    /// <summary>The host's lookup hook, the one PowerShell-name path, must itself use the primitive.</summary>
    [Fact]
    public void TheCommandLookupHook_UsesProgramFile()
    {
        string code = Code(File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Host", "Runtime", "GetVerbFallbackGuard.cs")));
        Assert.Matches(@"ProgramFile\.Plan\(app\.Path\)", code);
        Assert.Contains("StandInScript(", code);
    }
}
