using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the two invariants that make a per-invocation bash-variable store
/// implementable later as a ONE-METHOD change rather than a ~100-site audit.
/// See <c>docs/specs/host-lifecycle-contract.md</c> §"the process environment is
/// NOT per-invocation".
///
/// <para>Invariant 1 — every place that spawns a CHILD PROCESS applies the
/// materialization seam (<c>BashVariableStore.ApplyTo</c>), because a store that
/// is not the process environment would otherwise stop `export` reaching the
/// child.</para>
///
/// <para>Invariant 2 — no cmdlet reads or writes a BASH VARIABLE through
/// <c>Environment.*EnvironmentVariable</c> directly. ps-bash's own configuration
/// knobs (<c>PSBASH_*</c>, <c>NO_COLOR</c>, <c>USERNAME</c>, <c>PATH</c>, …) are
/// host process state, not script state, and are explicitly allowed.</para>
///
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash
/// equivalent, so a hand-driven source check is the justified oracle.
/// </summary>
public class BashVariableStoreGuardTests
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

    private static IEnumerable<string> CmdletSources() =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets"), "*.cs", SearchOption.AllDirectories);

    /// <summary>
    /// Names that are ps-bash HOST configuration or process identity, not bash script variables, each with the reason
    /// it reads the real process environment. Scoping them to a job or an invocation would break the launcher's
    /// ability to configure the host at all. <c>PATH</c> is NOT here: <c>PATH=dir:$PATH</c> is the script's own
    /// variable (a background job has its own copy, background-jobs.md section 3), read through the store.
    /// </summary>
    private static readonly (string Literal, string Reason)[] HostConfigNames =
    [
        ("\"PSBASH_", "the launcher's knobs for the host (timeouts, caps, modes, spares)"),
        ("\"NO_COLOR\"", "the terminal's color preference for the host's own rendering"),
        ("\"USERNAME\"", "the OS account a file owner falls back to, not a shell variable"),
    ];

    private static bool IsHostConfigName(string line) =>
        HostConfigNames.Any(n => line.Contains(n.Literal, StringComparison.Ordinal));

    /// <summary>
    /// A helper whose name declares it reads HOST CONFIG (e.g.
    /// <c>BashRuntime.IsHostConfigTruthy</c>) takes the variable name as a
    /// PARAMETER, so the literal-name check above cannot see it. The NAME is the
    /// contract — which is exactly why that helper is not called "IsEnvTruthy".
    /// </summary>
    private static bool IsInsideHostConfigHelper(string[] lines, int index)
    {
        // Walk back to the enclosing member declaration.
        for (int i = index; i >= 0 && index - i < 25; i--)
        {
            if (lines[i].Contains("HostConfig", StringComparison.Ordinal)
                && lines[i].Contains("static", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void NoCmdlet_ReadsOrWritesABashVariable_ThroughEnvironmentDirectly()
    {
        var offenders = new List<string>();

        foreach (var file in CmdletSources())
        {
            // BashVariableStore itself IS the seam — it is the one place allowed
            // to touch Environment for bash variables.
            if (Path.GetFileName(file) == "BashVariableStore.cs") continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!Regex.IsMatch(line, @"Environment\.(Get|Set)EnvironmentVariables?\b")) continue;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("///", StringComparison.Ordinal)) continue;
                if (IsHostConfigName(line)) continue;
                if (IsInsideHostConfigHelper(lines, i)) continue;

                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A bash VARIABLE must go through BashVariableStore, not Environment directly "
            + "(in a background job the store is the job's own, ScalarOverlay; only the host config names in "
            + "HostConfigNames are exempt, each with its reason). Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The files that WRITE a bash variable through <c>BashVariableStore.Set</c>, each with the shell assignment it
    /// performs. A child's environment is never built by setting the shell's variables around the command and
    /// restoring them: in the foreground the store is the PROCESS environment, shared by every runspace of the host
    /// process, so <c>env -i cmd</c> left TEMP and PATH unset for a concurrent command (a parallel test resolved its
    /// temp dir to C:\WINDOWS). A wrapper gives its command its own store instead
    /// (<c>ScalarOverlay.ForCommand</c>, environment-model.md section 11).
    /// </summary>
    private static readonly (string File, string Assignment)[] StoreWriters =
    [
        ("BashArrayElement.cs", "a[0]=v on a scalar (element 0 is the scalar)"),
        ("BashRuntime.cs", "PATH=... rewritten to the Windows list form the OS searches"),
        ("BashShellState.cs", "a subshell's end puts the shell's own variables back"),
        ("BashSubscriptExpansion.cs", "${x:=v} / ${x[0]:=v}"),
        ("BashVariableLookup.cs", "the assignment primitive for a name known at run time"),
        ("InvokeBashArithmeticCommand.cs", "(( x = 1 )) / $(( x++ ))"),
        ("InvokeBashExecFdCommand.cs", "exec {fd}>f binds fd"),
        ("InvokeBashGetoptsCommand.cs", "getopts NAME / OPTARG / OPTIND"),
        ("InvokeBashPushdCommand.cs", "pushd / popd set PWD and OLDPWD"),
        ("InvokeBashReadCommand.cs", "read NAME"),
        ("InvokeBashUnsetCommand.cs", "unset NAME"),
        ("JobEnvironmentProvider.cs", "$env:NAME = v in a job runspace"),
    ];

    [Fact]
    public void OnlyShellAssignments_WriteTheVariableStore()
    {
        var allowed = StoreWriters.Select(w => w.File).ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(".Tests", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            var name = Path.GetFileName(file);
            // The store itself, and the emitter: the code it emits for a script's own `x=v`.
            if (name is "BashVariableStore.cs" or "PsEmitter.cs" or "PsBuild.cs" || name.StartsWith("PsEmitter.", StringComparison.Ordinal)) continue;
            if (allowed.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (lines[i].Contains("BashVariableStore.Set(", StringComparison.Ordinal))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Only a shell assignment writes a bash variable (StoreWriters, each with its reason). A command's own "
            + "environment is a ScalarOverlay.ForCommand attached for the command, never the shell's variables set and "
            + "restored around it (the process environment every runspace shares). Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The process environment is written only by the store (a bash variable), in the cmdlets: no cmdlet sets an
    /// environment variable itself, not even a host knob, which would be the whole host process's for the duration.
    /// </summary>
    [Fact]
    public void NoCmdlet_SetsTheProcessEnvironment_ExceptTheStore()
    {
        var offenders = new List<string>();
        foreach (var file in CmdletSources())
        {
            if (Path.GetFileName(file) == "BashVariableStore.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.Contains("///", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(line, @"Environment\.SetEnvironmentVariable\b|\[(System\.)?Environment\]::SetEnvironmentVariable"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "A cmdlet must not write the process environment (BashVariableStore.Set for a bash variable; a child's own "
            + "environment on its ProcessStartInfo or a ScalarOverlay.ForCommand). Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), "src", .. parts]));

    /// <summary>
    /// Emitted PowerShell and the module read and write a scalar as <c>$env:NAME</c> (a job runspace's Env: drive is
    /// the job's store) or through <c>BashVariableStore</c>, never <c>System.Environment</c>, which is the process
    /// environment the foreground and every other job share (background-jobs.md section 3).
    /// </summary>
    [Fact]
    public void EmittedCodeAndModule_NeverReachAScalarThroughSystemEnvironment()
    {
        var direct = new Regex(@"\[(System\.)?Environment\]::(Get|Set)EnvironmentVariables?\b");
        var offenders = new List<string>();
        foreach (var (name, text) in new[]
                 {
                     ("PsEmitter.cs", Src("PsBash.Transpiler", "Parser", "PsEmitter.cs")),
                     ("PsBuild.cs", Src("PsBash.Transpiler", "Parser", "PsBuild.cs")),
                     ("PsBash.psm1", Src("PsBash.Module", "PsBash.psm1")),
                 })
        {
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#')) continue;
                if (direct.IsMatch(lines[i])) offenders.Add($"{name}:{i + 1}: {line}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Emitted code and the module must reach a bash scalar through $env:NAME or "
            + "[PsBash.Cmdlets.BashVariableStore]: System.Environment bypasses a background job's own store. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A job runspace replaces PowerShell's Environment provider, and resolves a program a command word names through
    /// <c>Invoke-BashNative</c> (PowerShell's own launcher would hand it the process environment).
    /// </summary>
    [Fact]
    public void AJobRunspace_ServesEnvFromTheJobStore_AndStartsProgramsThroughTheStore()
    {
        var runspaces = Src("PsBash.Cmdlets", "BashJobRunspaces.cs");
        // The Env: drive swap is shared with an embedding's secret-holding session (ShellSecretsSession).
        Assert.Contains("ShellSecretsSession.UseShellEnvironmentDrive(", runspaces, StringComparison.Ordinal);
        Assert.Contains("typeof(JobEnvironmentProvider)", Src("PsBash.Cmdlets", "ShellSecretsSession.cs"), StringComparison.Ordinal);
        Assert.Contains("JobProgramLookup.Install(", runspaces, StringComparison.Ordinal);
        Assert.Contains("ScalarOverlay.Capture()", runspaces, StringComparison.Ordinal);
        // Only there: the shell's own runspace keeps PowerShell's launcher and its own lookup hook.
        var installers = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".Tests", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("JobProgramLookup.Install(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["BashJobRunspaces.cs"], installers);
    }

    [Fact]
    public void EverySpawnSite_AppliesTheMaterializationSeam()
    {
        var offenders = new List<string>();

        foreach (var file in CmdletSources())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("Process.Start(", StringComparison.Ordinal)) continue;

            // RunChildProcess is the shared seam and applies it centrally; every
            // OTHER Process.Start is a documented streaming/interactive exemption
            // and must apply it itself.
            int spawnCount = Regex.Matches(text, @"Process\.Start\(").Count;
            int applyCount = Regex.Matches(text, @"BashVariableStore\.ApplyTo\(").Count;

            if (applyCount < spawnCount)
            {
                offenders.Add(
                    $"{Path.GetFileName(file)}: {spawnCount} Process.Start call(s) but only "
                    + $"{applyCount} BashVariableStore.ApplyTo call(s)");
            }
        }

        Assert.True(offenders.Count == 0,
            "A child-process spawn must apply BashVariableStore.ApplyTo so `export` still reaches "
            + "the child once the store moves off the process environment. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }
}
