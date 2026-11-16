using PsBash.Spec;

namespace PsBash.Spec.Tests;

/// <summary>
/// A spec case must not be able to observe or affect another case, nor the checkout the run started in
/// (.claude/rules/testing.md "SPEC CASE ISOLATION"). The harness self-test: a polluting case (files, directory,
/// options, variables, aliases, functions, traps, umask) runs next to probes, on both executors, and every probe must
/// print exactly what it printed before any polluter ran. Hosts are reused across cases, so "before" and "after" on
/// the same executor is the comparison that matters; the concurrent rounds add the cases that overlap in time.
/// Oracle note (qa-rubric Directive 1): the harness is ps-bash's own; there is no bash equivalent.
/// </summary>
public class CaseIsolationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>Everything a case can leave behind that a later or concurrent case could see.</summary>
    internal const string Polluter = """
        echo junk > iso-junk.txt
        echo junk > 'a['
        mkdir iso-sub
        cd iso-sub
        echo junk > iso-junk2.txt
        set -o posix
        set -f
        shopt -s nullglob extglob dotglob expand_aliases
        export ISO_LEAK=1
        readonly ISO_RO=1
        ISO_PLAIN=1
        declare -a ISO_ARR=(1 2)
        alias iso_alias=true
        iso_fn() { :; }
        umask 077
        IFS=:
        trap : USR1
        pushd / >/dev/null
        HISTFILE=iso-history
        echo polluted
        """;

    /// <summary>What a pristine case sees. No path is printed: each case has its own directory name.</summary>
    internal const string Probe = """
        echo "files=$(ls -A | wc -l | tr -d ' ')"
        echo "pwd-is-case-dir=$(case $PWD in *spec-run*) echo y;; *) echo n;; esac)"
        echo "posix=$(shopt -o -p posix)"
        echo "shopt=$(shopt -p nullglob extglob dotglob | tr '\n' ' ')"
        echo "flags=$-"
        echo "vars=${ISO_LEAK-unset} ${ISO_RO-unset} ${ISO_PLAIN-unset} ${ISO_ARR-unset}"
        echo "alias=$(alias | grep -c iso_alias) fn=$(type -t iso_fn)"
        echo "umask=$(umask)"
        echo "ifs=$(printf %q "$IFS")"
        echo "trap=$(trap -p | grep -c USR1)"
        echo "readonly-table=$( (ISO_RO=2) 2>/dev/null; echo $?) $(readonly -p | grep -c ISO_RO)"
        echo "attrs=$(declare -p ISO_LEAK ISO_ARR 2>/dev/null | wc -l | tr -d ' ')"
        echo "dirs=$(dirs -p | wc -l | tr -d ' ')"
        echo "glob=$(echo iso-*)"
        echo "nested-shell-in-case-dir=$([ "$(bash -c pwd)" = "$PWD" ] && echo y || echo n)"
        """;

    private static async Task<string> Run(IScriptExecutor executor, string script)
    {
        var r = await executor.RunAsync(script, Limit, default);
        return $"exit={r.ExitCode}\n{SpecRunner.Normalize(r.Stdout)}{(r.Stderr.Length > 0 ? "stderr: " + SpecRunner.Normalize(r.Stderr) : "")}";
    }

    public static IEnumerable<object[]> ExecutorKinds() => [["inhost"], ["launcher"]];

    private static IScriptExecutor Executor(string kind) => kind == "inhost" ? Executors.InHost : new PsBashExecutor();

    [Theory]
    [MemberData(nameof(ExecutorKinds))]
    public async Task Probe_AfterAPollutingCase_SeesWhatItSawBefore(string kind)
    {
        var executor = Executor(kind);
        string before = await Run(executor, Probe);
        Assert.Contains("files=0\n", before);
        Assert.Contains("pwd-is-case-dir=y\n", before);
        Assert.Contains("nested-shell-in-case-dir=y\n", before);
        // As many polluters as the executor has hosts, so every host a later probe can land on has run one.
        for (int i = 0; i < Executors.Slots; i++)
            Assert.Contains("polluted", await Run(executor, Polluter));
        for (int i = 0; i < Executors.Slots; i++)
            Assert.Equal(before, await Run(executor, Probe));
    }

    [Theory]
    [MemberData(nameof(ExecutorKinds))]
    public async Task Probe_RunningNextToPollutingCases_SeesWhatItSawAlone(string kind)
    {
        var executor = Executor(kind);
        string alone = await Run(executor, Probe);
        var tasks = new List<Task<string>>();
        for (int i = 0; i < 6; i++)
        {
            tasks.Add(Run(executor, Polluter));
            tasks.Add(Run(executor, Probe));
        }
        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < results.Length; i++)
        {
            if (i % 2 == 0) Assert.Contains("polluted", results[i]);
            else Assert.Equal(alone, results[i]);
        }
    }

    /// <summary>
    /// A case that never changes directory still writes into its own directory: the run leaves the directory the test
    /// process runs in, and the checkout, exactly as they were. (The junk files <c>a[</c>, <c>F</c>, <c>foo.txt</c> a
    /// checkout root collects come from scripts run WITHOUT a case directory, never from here.)
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutorKinds))]
    public async Task Case_WritingRelativeFiles_LeavesTheCheckoutAndTheRunnerDirectoryUntouched(string kind)
    {
        string root = RepoRoot();
        var watched = new[] { root, Environment.CurrentDirectory, AppContext.BaseDirectory };
        var before = watched.Select(Entries).ToArray();
        string result = await Run(Executor(kind), Polluter);
        Assert.Contains("polluted", result);
        // A script that does not parse runs nothing, in particular not in the runner's directory.
        await Run(Executor(kind), "echo junk > iso-junk3.txt; if then fi (");
        for (int i = 0; i < watched.Length; i++)
        {
            Assert.Equal(before[i], Entries(watched[i]));
            // Not only "unchanged by this test": an earlier case must not have left them there either.
            Assert.DoesNotContain(Entries(watched[i]), n => n.StartsWith("iso-", StringComparison.Ordinal) || n == "a[");
        }
    }

    /// <summary>The launcher process itself starts in the case directory: nothing depends on the script's first command.</summary>
    [Fact]
    public async Task Launcher_StartsInTheCaseDirectory_NotTheRunnerDirectory()
    {
        using var dir = new CaseDir("");
        var psi = PsBashExecutor.StartInfo(dir, "unused.sh");
        Assert.Equal(dir.Path, psi.WorkingDirectory);
        Assert.Equal(dir.Path, psi.Environment["OLDPWD"]);
        await Task.CompletedTask;
    }

    private static string[] Entries(string dir)
        => Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root not found above " + AppContext.BaseDirectory);
    }
}
