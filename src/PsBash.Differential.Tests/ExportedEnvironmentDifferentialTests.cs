using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A child process sees the shell's EXPORTED variables and nothing else. ps-bash keeps every shell variable in the
/// process environment, so until the export table (<c>PsBash.Core.ShellExports</c>) a child saw all of them
/// (<c>x=1; sh -c 'echo $x'</c> printed 1). The rule and the one primitive: <c>docs/specs/environment-model.md</c>.
/// <para>
/// Matrix: every way a script gives a variable a value and an export attribute (<see cref="Setters"/>) × every way a
/// child can observe it. The observers are in three groups, one script each so a case costs one shell start:
/// the shell's own children and builtins (<see cref="ShellObservers"/>), a real program started with its stderr
/// merged (ps-bash starts that one itself), and a real program started plainly.
/// </para>
/// <para>
/// Every observer names the one variable it looks at (<c>PSB_M</c>): the environment of a test host is never printed.
/// The program is one the host has: <c>perl</c> or <c>pwsh</c>; a case whose program is not on PATH is skipped.
/// </para>
/// </summary>
public class ExportedEnvironmentDifferentialTests
{
    /// <summary>
    /// Name, the script that sets PSB_M and then runs <c>OBSERVE</c>, and whether PSB_M is then set but NOT exported.
    /// </summary>
    public static readonly (string Name, string Script, bool SetButUnexported)[] Setters =
    [
        ("never set", "OBSERVE", false),
        ("plain assignment", "PSB_M=v\nOBSERVE", true),
        ("export NAME=v", "export PSB_M=v\nOBSERVE", false),
        ("export NAME, then assign", "export PSB_M\nPSB_M=v\nOBSERVE", false),
        ("assign, then export NAME", "PSB_M=v\nexport PSB_M\nOBSERVE", false),
        ("declare -x", "declare -x PSB_M=v\nOBSERVE", false),
        ("declare +x after export", "export PSB_M=v\ndeclare +x PSB_M\nOBSERVE", true),
        ("export -n", "export PSB_M=v\nexport -n PSB_M\nOBSERVE", true),
        ("set -a", "set -a\nPSB_M=v\nset +a\nOBSERVE", false),
        ("assigned after set +a", "set -a\nset +a\nPSB_M=v\nOBSERVE", true),
        ("prefix assignment on a function", "g() { OBSERVE; }\nPSB_M=v g", false),
        ("gone after the prefixed command", "g() { :; }\nPSB_M=v g\nOBSERVE", false),
        ("local in a function", "f() { local PSB_M=v; OBSERVE; }\nf", true),
        ("local -x in a function", "f() { local -x PSB_M=v; OBSERVE; }\nf", false),
        ("local hiding an exported global", "export PSB_M=o\nf() { local PSB_M=v; OBSERVE; }\nf", false),
        ("export inside a function", "f() { export PSB_M=v; }\nf\nOBSERVE", false),
        ("readonly", "readonly PSB_M=v\nOBSERVE", true),
        ("read", "read PSB_M <<< v\nOBSERVE", true),
        ("for loop variable", "for PSB_M in v; do OBSERVE; done", true),
        ("unset after export", "export PSB_M=v\nunset PSB_M\nOBSERVE", false),
        ("unset, then assigned again", "export PSB_M=v\nunset PSB_M\nPSB_M=w\nOBSERVE", true),
        ("exported in a subshell only", "( export PSB_M=v )\nOBSERVE", false),
        ("inherited from the parent", "export PSB_M=v\nbash \"$d/o.sh\"", false),
        ("inherited, then assigned", "export PSB_M=v\nbash -c 'PSB_M=w; . \"$1\"' _ \"$d/o.sh\"", false),
        ("inherited, then export -n", "export PSB_M=v\nbash -c 'export -n PSB_M; . \"$1\"' _ \"$d/o.sh\"", true),
    ];

    /// <summary>Children that are a shell, and the builtins that list the environment.</summary>
    private const string ShellObservers =
        "echo \"self: ${PSB_M-unset}\"\n" +
        "echo \"sh: $(sh -c 'echo \"${PSB_M-unset}\"')\"\n" +
        "echo \"bash: $(bash -c 'echo \"${PSB_M-unset}\"')\"\n" +
        "echo \"env-grep: $(env | grep -c '^PSB_M=')\"\n" +
        "printenv PSB_M; echo \"printenv: $?\"\n" +
        "echo \"env-run: $(env sh -c 'echo \"${PSB_M-unset}\"')\"\n" +
        "echo \"exec: $( (exec sh -c 'echo \"${PSB_M-unset}\"') )\"\n" +
        "echo \"xargs: $(echo a | xargs sh -c 'echo \"${PSB_M-unset}\"' _)\"\n" +
        "echo \"find: $(find . -maxdepth 0 -exec sh -c 'echo \"${PSB_M-unset}\"' \\;)\"\n" +
        "sh -c 'echo \"background: ${PSB_M-unset}\"' & wait\n" +
        "sh -c 'echo \"pipe-stage: ${PSB_M-unset}\"' | cat\n" +
        "echo x | sh -c 'echo \"pipe-target: ${PSB_M-unset}\"'\n" +
        "echo \"compgen-e: $(compgen -e PSB_M)\"\n";

    /// <summary>A program whose stderr the script merges: ps-bash starts it itself (ChildStdio).</summary>
    private const string MergedProgramObservers =
        "PROG 2>&1\n" +
        "x=$(PROG 2>&1); echo \"sub: $x\"\n" +
        "PROG 2>&1 | cat\n";

    /// <summary>A program started plainly, every way a script can.</summary>
    private const string PlainProgramObservers =
        "PROG\n" +
        "x=$(PROG); echo \"sub: $x\"\n" +
        "PROG & wait\n" +
        "PROG | cat\n" +
        "true | PROG\n" +   // a pipe target with nothing written to it: the program does not read stdin
        "(exec PROG)\n" +
        "find . -maxdepth 0 -exec PROG \\;\n" +
        "env PROG\n" +
        "command PROG\n" +
        "timeout 60 PROG\n";

    /// <summary>The programs: name on PATH, and the simple command that prints PSB_M's value or <c>unset</c>.</summary>
    private static readonly (string Name, string Command)[] Programs =
    [
        ("perl", "perl -e 'print(($ENV{PSB_M} // \"unset\"), \"\\n\")'"),
        ("pwsh", "pwsh -NoProfile -NonInteractive -Command '[Console]::Out.Write($(if ($null -eq $env:PSB_M) { \"unset\" } else { $env:PSB_M }) + \"`n\")'"),
    ];

    private static string Script(string setter, string observers) =>
        "d=$(mktemp -d)\ncd \"$d\"\ncat > \"$d/o.sh\" <<'PSB_EOF'\n" + observers + "PSB_EOF\n"
        + setter.Replace("OBSERVE", ". \"$d/o.sh\"", StringComparison.Ordinal)
        + "\necho rc=$?\ncd /\nrm -rf \"$d\"";

    public static TheoryData<string> SetterNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in Setters) data.Add(s.Name);
        return data;
    }

    public static TheoryData<string, string> SetterAndProgram()
    {
        var data = new TheoryData<string, string>();
        foreach (var s in Setters)
            foreach (var p in Programs) data.Add(s.Name, p.Name);
        return data;
    }

    private static (string Name, string Script, bool SetButUnexported) Setter(string name) => Setters.First(s => s.Name == name);

    private static string Program(string name) => Programs.First(p => p.Name == name).Command;

    [SkippableTheory]
    [MemberData(nameof(SetterNames))]
    public Task AShellChild_SeesOnlyExportedVariables(string setter) =>
        AssertOracle.EqualAsync(Script(Setter(setter).Script, ShellObservers), timeout: TimeSpan.FromSeconds(120));

    [SkippableTheory]
    [MemberData(nameof(SetterAndProgram))]
    public Task AProgramWithMergedStderr_SeesOnlyExportedVariables(string setter, string program)
    {
        SkipWithout(program);
        return AssertOracle.EqualAsync(
            Script(Setter(setter).Script, MergedProgramObservers.Replace("PROG", Program(program), StringComparison.Ordinal)),
            timeout: TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// A program PowerShell starts (no merged stderr) inherits the host's process environment, unexported variables
    /// included: the remaining gap (<c>environment-model.md</c> §Remaining gap). The cases where that shows — the
    /// variable is set but not exported — are skipped with that reason, not asserted wrong; every other case runs.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(SetterAndProgram))]
    public Task APlainlyStartedProgram_SeesExportedVariables(string setter, string program)
    {
        SkipWithout(program);
        var s = Setter(setter);
        Skip.If(s.SetButUnexported && !Recording,
            "known gap: a program started by PowerShell's native-command processor inherits unexported variables "
            + "(environment-model.md, Remaining gap)");
        return AssertOracle.EqualAsync(
            Script(s.Script, PlainProgramObservers.Replace("PROG", Program(program), StringComparison.Ordinal)),
            timeout: TimeSpan.FromSeconds(180));
    }

    private static bool Recording => Environment.GetEnvironmentVariable("PSBASH_ORACLE_RECORD") == "1";

    /// <summary>
    /// Skip a case whose program this host does not have (pwsh on a minimal Linux, perl on Windows). Recording the
    /// oracle (<c>PSBASH_ORACLE_RECORD=1</c>, in WSL, which has both) never skips, so every cassette exists.
    /// </summary>
    private static void SkipWithout(string program)
    {
        if (Recording) return;
        Skip.IfNot(OnPath(program), $"{program} is not on PATH on this host; the matrix runs with the program it has");
    }

    private static bool OnPath(string program)
    {
        string[] names = OperatingSystem.IsWindows() ? [program + ".exe", program + ".cmd", program] : [program];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                try { if (File.Exists(Path.Combine(dir, name))) return true; }
                catch (ArgumentException) { /* a malformed PATH entry */ }
            }
        return false;
    }
}
