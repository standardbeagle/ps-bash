using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guard for the bug class "a variable bash defines is unset in ps-bash" (PPID, SHLVL, GROUPS, DIRSTACK,
/// BASH_ALIASES were unset and <c>$IFS</c> read empty). Every name bash 5.2 defines in <c>env -i bash --norc</c> must be
/// in exactly one place: seeded at shell start (<see cref="ShellVariables.Seeded"/>), computed on read
/// (<see cref="ShellVariables.Computed"/>, each with an emitter mapping), inherited from the environment, or on the
/// list below of names ps-bash does not provide, each with its reason. A new bash variable, or a name dropped from the
/// table, fails here. Behavior per name: Differential.Tests/ShellLanguageGapDifferentialTests.
/// <para>Oracle note (qa-rubric Directive 1): the list itself is bash 5.2's <c>compgen -v</c> (checked with WSL); the
/// values are checked against bash in the differential tests.</para>
/// </summary>
public class ShellVariablesGuardTests
{
    /// <summary>bash 5.2's own variables: <c>env -i bash --norc -c 'compgen -v'</c> plus the dynamic ones it lists only when set.</summary>
    private static readonly string[] BashDefines =
    [
        "BASH", "BASHOPTS", "BASHPID", "BASH_ALIASES", "BASH_ARGC", "BASH_ARGV", "BASH_ARGV0", "BASH_CMDS",
        "BASH_COMMAND", "BASH_EXECUTION_STRING", "BASH_LINENO", "BASH_LOADABLES_PATH", "BASH_SOURCE", "BASH_SUBSHELL",
        "BASH_VERSINFO", "BASH_VERSION", "COMP_WORDBREAKS", "DIRSTACK", "EPOCHREALTIME", "EPOCHSECONDS", "EUID",
        "GROUPS", "HISTCMD", "HOSTNAME", "HOSTTYPE", "IFS", "LINENO", "MACHTYPE", "OPTERR", "OPTIND", "OSTYPE",
        "PATH", "PIPESTATUS", "PPID", "PS4", "PWD", "RANDOM", "SECONDS", "SHELL", "SHELLOPTS", "SHLVL", "SRANDOM",
        "TERM", "UID", "_", "FUNCNAME",
    ];

    /// <summary>Taken from the environment the shell started with (bash adds a default only when it is missing).</summary>
    private static readonly HashSet<string> Inherited = new(StringComparer.Ordinal) { "PATH", "TERM" };

    /// <summary>Names ps-bash does not provide, and why.</summary>
    private static readonly Dictionary<string, string> NotProvided = new(StringComparer.Ordinal)
    {
        ["BASH_ARGC"] = "extdebug call-stack argument counts: no extdebug",
        ["BASH_ARGV"] = "extdebug call-stack arguments: no extdebug",
        ["BASH_ARGV0"] = "assigning $0 through it is not supported; $0 is read directly",
        ["BASH_CMDS"] = "the hash table is read with `hash` (Invoke-BashHash), not as an array",
        ["BASH_COMMAND"] = "the command text being run is not tracked per simple command",
        ["BASH_EXECUTION_STRING"] = "the -c text is not kept after transpiling",
        ["BASH_LOADABLES_PATH"] = "no loadable builtins (enable -f)",
        ["HISTCMD"] = "the history number of the current command exists only in the interactive shell",
        ["_"] = "the last argument of the previous command is tracked by the emitter ($_ reads), not stored",
    };

    [Fact]
    public void EveryBashVariable_IsSeededComputedInheritedOrListedAsNotProvided()
    {
        var seeded = ShellVariables.Seeded.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var computed = ShellVariables.Computed.ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var name in BashDefines)
        {
            int homes = (seeded.Contains(name) ? 1 : 0) + (computed.Contains(name) ? 1 : 0)
                + (Inherited.Contains(name) ? 1 : 0) + (NotProvided.ContainsKey(name) ? 1 : 0);
            if (homes != 1) problems.Add($"{name}: in {homes} places (must be exactly one)");
        }
        foreach (var name in seeded.Concat(computed).Concat(NotProvided.Keys))
            if (Array.IndexOf(BashDefines, name) < 0) problems.Add($"{name}: not a bash variable (stale entry)");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void EveryComputedVariable_HasAnEmitterMapping()
    {
        int savedLine = PsBuild.TrapLine;
        PsBuild.TrapLine = 3;
        try { AssertEveryComputedVariableMapped(); } finally { PsBuild.TrapLine = savedLine; }
    }

    private static void AssertEveryComputedVariableMapped()
    {
        var unmapped = ShellVariables.Computed
            // PIPESTATUS is the $global:PIPESTATUS the pipeline writes; LINENO maps to the line of the statement being
            // emitted (PsBuild.TrapLine), so it is checked with a line set.
            .Where(n => PsBuild.ShellArrayExpr(n) is null && n != "PIPESTATUS"
                && PsBuild.TryMapSpecialVar(n, braced: true, inDoubleQuote: false) is null
                && PsBuild.TryMapSpecialVar(n, braced: false, inDoubleQuote: false) is null)
            .ToList();
        Assert.True(unmapped.Count == 0, "computed but never mapped: " + string.Join(", ", unmapped));
    }

    [Fact]
    public void SeededValues_HaveBashsShape()
    {
        string Value(string name) => ShellVariables.Seeded.Single(v => v.Name == name).Value(null);
        Assert.Equal(" \t\n", Value("IFS"));
        Assert.Equal("1", Value("OPTIND"));
        Assert.Equal("+ ", Value("PS4"));
        Assert.Equal("1", ShellVariables.NextShellLevel(null));
        Assert.Equal("3", ShellVariables.NextShellLevel("2"));
        Assert.Equal("1", ShellVariables.NextShellLevel("junk"));
        Assert.Matches("^[0-9]+$", Value("PPID"));
        Assert.True(ShellVariables.Seeded.Single(v => v.Name == "SHLVL").Exported);
        Assert.Contains("PPID", ShellVariables.AlwaysUnexported);
    }
}
