using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the child-environment invariant (<c>docs/specs/environment-model.md</c>): a child process's environment is
/// the shell's EXPORTED variables only, and one primitive builds it (<see cref="ShellExports.RemoveUnexported"/>,
/// reached through <c>BashVariableStore.ApplyTo</c>). Every shell variable lives in the process environment, so a
/// spawn that hands a child the process environment as it is leaks every unexported one.
/// <para>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle. The behavior itself is pinned against bash by <c>ExportedEnvironmentDifferentialTests</c>.
/// </para>
/// </summary>
public class ChildEnvironmentGuardTests
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

    private static IEnumerable<(string Name, string Text)> Sources(params string[] projects)
    {
        foreach (var project in projects)
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

    /// <summary>The text without comment lines, so a spawn or a primitive named only in prose does not count.</summary>
    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static readonly Regex Spawn = new(
        @"\bProcess\.Start\(|new\s+Process\s*\(|new\s+Process\s*\{|\bJobProcess\.Start\(|\bCreateProcessW?\(",
        RegexOptions.Compiled);

    private static readonly Regex Primitive = new(
        @"BashVariableStore\.ApplyTo\(|ShellExports\.RemoveUnexported\(|ShellExports\.WindowsEnvironmentBlock\(",
        RegexOptions.Compiled);

    /// <summary>Files that start a process which is NOT a child of a running shell script, each with its reason.</summary>
    private static readonly Dictionary<string, string> SpawnWithoutShellEnvironment = new(StringComparer.Ordinal)
    {
        ["IpcWorker.cs"] = "the launcher starts the host: the shell itself, which inherits everything",
        ["WindowsHostSpawn.cs"] = "the launcher starts the host: the shell itself, which inherits everything",
        ["Program.cs"] = "the AOT launcher, before any shell exists",
        ["WindowsPtySpawner.cs"] = "the AOT launcher's pty host, before any shell exists",
        ["TerminalClient.cs"] = "the AOT launcher's session client, before any shell exists",
        ["JobProcess.Windows.cs"] = "the spawn mechanism: it is handed the complete environment its caller built",
    };

    [Fact]
    public void EverySpawnSite_BuildsTheChildEnvironmentThroughThePrimitive()
    {
        var offenders = new List<string>();
        var spawning = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, text) in Sources(ProductProjects))
        {
            string code = Code(text);
            if (!Spawn.IsMatch(code)) continue;
            spawning.Add(name);
            if (SpawnWithoutShellEnvironment.ContainsKey(name)) continue;
            if (!Primitive.IsMatch(code)) offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "These files start a process without building its environment from the exported variables. Call "
            + "BashVariableStore.ApplyTo(startInfo) (or ShellExports.RemoveUnexported / WindowsEnvironmentBlock) before "
            + "the spawn, or start the child through ChildStdio.Start, which does (environment-model.md). Offenders:\n  "
            + string.Join("\n  ", offenders));

        var stale = SpawnWithoutShellEnvironment.Keys.Where(k => !spawning.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Exempted files that no longer spawn anything: " + string.Join(", ", stale));
    }

    /// <summary>The one spawn every cmdlet child goes through must itself apply the primitive.</summary>
    [Fact]
    public void ChildStdio_AppliesTheExportedEnvironment()
    {
        string code = Code(File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", "ChildStdio.cs")));
        Assert.Contains("BashVariableStore.ApplyTo(", code);
    }

    /// <summary>
    /// Files that read a variable as the SHELL sees it (exported or not): expansions, builtins, the shell's own
    /// settings. Everything else in the cmdlets is an emulated external program and reads the exported view.
    /// </summary>
    private static readonly HashSet<string> ShellViewReaders = new(StringComparer.Ordinal)
    {
        "BashVariableStore.cs", "BashVariableLookup.cs",
        "BashGlob.cs", "BashLocale.cs", "BashPromptText.cs", "BashRuntime.cs", "BashShellState.cs",
        "BashStateSnapshot.cs", "BashSubscriptExpansion.cs", "BashTimeKeyword.cs", "ConvertToBashWordsCommand.cs",
        "DeclarePrinter.cs", "InvokeBashArithmeticCommand.cs", "InvokeBashCompgenCommand.cs", "InvokeBashDirsCommand.cs",
        "InvokeBashExecFdCommand.cs", "InvokeBashGetoptsCommand.cs",
        "InvokeBashHistoryCommand.cs", "InvokeBashReadCommand.cs", "InvokeBashTestCommand.cs",
        // declare lists every variable; hash and source search the shell's PATH, exported or not, as bash's builtins do;
        // JobEnvironmentProvider is a background job's $env: drive, the store itself (background-jobs.md section 3).
        "InvokeBashDeclareCommand.cs", "InvokeBashHashCommand.cs", "InvokeBashSourceCommand.cs", "JobEnvironmentProvider.cs",
        // ShellArrays computes $DIRSTACK, whose first element is the shell's $PWD (environment-model.md section 9).
        "ShellArrays.cs",
    };

    [Fact]
    public void AnEmulatedProgram_ReadsOnlyExportedVariables()
    {
        var read = new Regex(@"BashVariableStore\.(Get|Enumerate|EnumerateListed)\s*[(,;)]", RegexOptions.Compiled);
        var offenders = new List<string>();
        var readers = new HashSet<string>(StringComparer.Ordinal) { "BashVariableStore.cs" };
        foreach (var (name, text) in Sources("PsBash.Cmdlets"))
        {
            if (!read.IsMatch(Code(text))) continue;
            readers.Add(name);
            if (!ShellViewReaders.Contains(name)) offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "A command that is an external program in bash (ls, grep, date, tar ...) sees only exported variables: use "
            + "BashVariableStore.GetExported / EnumerateExported. A shell builtin or expansion that must see every "
            + "variable is added to ShellViewReaders with that reason (environment-model.md). Offenders:\n  "
            + string.Join("\n  ", offenders));

        var stale = ShellViewReaders.Where(k => !readers.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Listed shell-view readers that no longer read a variable: " + string.Join(", ", stale));
    }

    /// <summary>The emitter writes the export attribute only through <c>PsBuild</c>, so its text has one spelling.</summary>
    [Fact]
    public void TheEmitter_WritesTheExportAttributeOnlyThroughPsBuild()
    {
        var literal = new Regex(@"BashVariableStore\]::(Export|Unexport|TempExport|EndTempExport|SetAllExport)", RegexOptions.Compiled);
        var offenders = new List<string>();
        string parser = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        foreach (var file in Directory.EnumerateFiles(parser, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name == "PsBuild.cs") continue;
            if (literal.IsMatch(File.ReadAllText(file, System.Text.Encoding.Latin1))) offenders.Add(name);
        }
        Assert.True(offenders.Count == 0,
            "Emit the export attribute through PsBuild.MarkExported / MarkUnexported / TempExportBegin / SetAllExport. "
            + "Offenders: " + string.Join(", ", offenders));
    }
}

/// <summary>
/// The primitive behind <see cref="ChildEnvironmentGuardTests"/>: <see cref="ShellExports"/> against the process
/// environment. Each test uses names of its own and puts back what it changed (<see cref="ProcessEnv"/> for the values;
/// the export table is process-wide too), so the class runs alone. Reset() is the shell start that turns tracking on
/// (a plain PowerShell with the module has none, and exports everything).
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class ChildEnvironmentPrimitiveTests
{
    private static Dictionary<string, string?> ChildOf(params string[] names)
    {
        var env = new Dictionary<string, string?>(ShellExports.NameComparer);
        foreach (var n in names) env[n] = Environment.GetEnvironmentVariable(n);
        ShellExports.RemoveUnexported(env);
        return env;
    }

    private static void Forget(params string[] names)
    {
        foreach (var n in names) ShellExports.Forget(n);
    }

    [Fact]
    public void ReservedSpelling_ExportedByItsBashName_IsTheStoreName()
    {
        // `export path` on Windows: the attribute belongs to the diverted variable (BashNameCase, environment-model.md
        // section 12), never to PATH. Needs the shell start (Reset): without it every call re-reads the environment
        // and counts exactly the variables that are set as exported, so an unset name's attribute is lost at once.
        const string store = "__psbash_cv_0_PATH";
        using var _ = BashNameCase.Override(windows: true);
        ShellExports.Reset();
        bool pathWasExported = ShellExports.IsExported("PATH");
        using var env = ProcessEnv.Preserve(store);
        try
        {
            env.Change(store, "v");                               // what `path=v` does
            ShellExports.Export("path");
            Assert.True(ShellExports.IsExported("path"));
            Assert.True(ShellExports.IsExported(store));
            Assert.Equal("v", ShellExports.Get("path"));
            ShellExports.Forget("path");
            Assert.False(ShellExports.IsExported(store));
            Assert.Equal(pathWasExported, ShellExports.IsExported("PATH"));
        }
        finally { Forget(store); }
    }

    [Fact]
    public void AVariableNobodyExported_IsNotInTheChildEnvironment()
    {
        const string plain = "PSB_GUARD_PLAIN", exported = "PSB_GUARD_EXPORTED";
        ShellExports.Reset();
        using var env = ProcessEnv.Preserve(plain, exported);
        try
        {
            env.Change(plain, "1");                               // what `x=1`, `read x`, `for x` all do
            env.Change(exported, "2");
            ShellExports.Export(exported);

            Assert.False(ShellExports.IsExported(plain));
            Assert.True(ShellExports.IsExported(exported));
            var child = ChildOf(plain, exported);
            Assert.False(child.ContainsKey(plain));
            Assert.Equal("2", child[exported]);
            Assert.Null(ShellExports.Get(plain));
            Assert.Equal("2", ShellExports.Get(exported));
            Assert.Contains(plain, ShellExports.UnexportedNames());
            Assert.DoesNotContain(exported, ShellExports.UnexportedNames());
        }
        finally { Forget(plain, exported); }
    }

    [Fact]
    public void Unexport_KeepsTheValue_AndForget_DropsTheAttribute()
    {
        const string a = "PSB_GUARD_UNEXPORT", b = "PSB_GUARD_FORGET";
        ShellExports.Reset();
        using var env = ProcessEnv.Preserve(a, b);
        try
        {
            env.Change(a, "1");
            env.Change(b, "1");
            ShellExports.Export(a, b);

            ShellExports.Unexport(a);                             // export -n / declare +x
            Assert.False(ShellExports.IsExported(a));
            Assert.Equal("1", Environment.GetEnvironmentVariable(a));
            Assert.False(ChildOf(a).ContainsKey(a));

            env.Change(b, null);                                  // unset b; b=2
            ShellExports.Forget(b);
            env.Change(b, "2");
            Assert.False(ShellExports.IsExported(b));
        }
        finally { Forget(a, b); }
    }

    [Fact]
    public void AChildEnvironmentEntryTheCallerSet_IsKept()
    {
        const string name = "PSB_GUARD_OVERRIDE";
        ShellExports.Reset();
        using var shell = ProcessEnv.Set(name, "shell");
        try
        {
            var env = new Dictionary<string, string?>(ShellExports.NameComparer) { [name] = "for-the-child" };
            ShellExports.RemoveUnexported(env);                   // `env NAME=v prog`: the caller's own entry
            Assert.Equal("for-the-child", env[name]);
        }
        finally { Forget(name); }
    }

    [Fact]
    public void ATemporaryExport_EndsWithItsCommand()
    {
        const string name = "PSB_GUARD_TEMP";
        ShellExports.Reset();
        using var env = ProcessEnv.Set(name, "1");
        try
        {
            string[] names = [name];
            bool[] was = ShellExports.BeginTemp(names);           // NAME=1 cmd
            Assert.True(ShellExports.IsExported(name));
            ShellExports.EndTemp(names, was);
            Assert.False(ShellExports.IsExported(name));
        }
        finally { Forget(name); }
    }

    [Fact]
    public void SaveAndRestore_PutTheExportSetBack()
    {
        const string name = "PSB_GUARD_SUBSHELL";
        ShellExports.Reset();
        using var env = ProcessEnv.Set(name, "1");
        try
        {
            var saved = ShellExports.Save();                      // ( export NAME )
            ShellExports.Export(name);
            Assert.True(ShellExports.IsExported(name));
            ShellExports.Restore(saved);
            Assert.False(ShellExports.IsExported(name));
        }
        finally { Forget(name); }
    }
}
