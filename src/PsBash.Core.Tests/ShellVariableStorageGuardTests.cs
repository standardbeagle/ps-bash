using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards "one storage per variable" (<c>docs/specs/environment-model.md</c> section 8): a bash scalar lives only in the
/// shell variable store, an array only as a PowerShell variable, and a name resolves through ONE chain.
/// <para>
/// The bug this pins: a loop bound its variable as a PowerShell variable beside the shell variable, and the arithmetic
/// cmdlet read a PowerShell variable first, so a function called from the loop body read the CALLER's loop variable
/// through PowerShell's dynamic scope instead of its own <c>local</c>; <c>read</c> / <c>printf -v</c> / <c>getopts</c>
/// kept PowerShell copies that a later assignment left stale. Two storages, readers disagreeing on the order.
/// </para>
/// <para>
/// Oracle note (qa-rubric Directive 1): both checks are repo-structure invariants with no bash equivalent, so a source
/// and an emitted-text check are the justified oracle. The behavior is pinned against bash by
/// <c>Differential.Tests/VariableScopeChainTests</c>.
/// </para>
/// </summary>
public class ShellVariableStorageGuardTests
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

    /// <summary>The text without comment lines, so a call named only in prose does not count.</summary>
    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                                     && !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));

    // A PowerShell variable reached by a name held in a variable or built at run time: the first argument is not a
    // string literal (`PSVariable.GetValue("global:BashPositional")` names one internal variable; `GetValue(name)`
    // could be any bash name).
    private static readonly Regex ComputedCSharpAccess = new(
        @"\b(?:PSVariable\.(?:Get|GetValue|Set|Remove)|GetVariableValue)\(\s*(?!""|new\s+PSVariable\(\s*"")(?<arg>[^,)]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Computed-name accesses that are NOT bash variables, as (file, first argument): each names one internal
    /// PowerShell variable through a constant or a fixed table.
    /// </summary>
    private static readonly Dictionary<string, string> InternalVariableAccess = new(StringComparer.Ordinal)
    {
        ["InvokeBashTestCommand.cs|flag"] = "`test -o`: the __Bash* option flag a fixed switch picked",
        ["InvokeBashShoptCommand.cs|global"] = "`shopt -o`: the __Bash* option flag a fixed table picked",
        ["RuntimeOptionHints.cs|name"] = "the __Bash* option flags its callers name",
        ["SharedStdin.cs|VariableName"] = "the shared stdin cursor's own constant",
        ["SharedStdin.cs|SharedStdin.VariableName"] = "the shared stdin cursor's own constant",
        ["InvokeBashRedirectCommand.cs|RedirectFailedVariable"] = "the redirect-failure mark's own constant",
        ["ShellSignalTraps.cs|SubshellScopeVariable"] = "the open subshell scope's own constant ($__psbash_ss, PsBuild.ShellStateGuard): is a subshell open here",
        ["BashShellState.cs|BashRuntime.SubshellPidVariable"] = "the subshell pid mark's own constant",
        ["BashRuntime.cs|SubshellPidVariable"] = "the subshell pid mark's own constant",
        ["JobClock.cs|VariableName"] = "a background job's launch clock's own constant (__psbashJobClock)",
        ["BashRuntime.cs|SubshellDepthVariable"] = "the subshell depth ($BASH_SUBSHELL) mark's own constant",
        ["BashShellState.cs|BashRuntime.SubshellDepthVariable"] = "the subshell depth ($BASH_SUBSHELL) mark's own constant",
        ["ErrTrapInheritance.cs|TrapVariable"] = "the ERR trap's own constant (__BashTrapERR), put aside on scope entry",
        ["ErrTrapInheritance.cs|ErrtraceVariable"] = "the set -E flag's own constant (__BashErrtrace)",
    };

    private const string Primitive = "BashVariableLookup.cs";

    [Fact]
    public void NoCmdlet_ReachesAPowerShellVariableByComputedName_OutsideThePrimitive()
    {
        string root = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            string name = Path.GetFileName(file);
            if (name == Primitive) continue;
            foreach (Match m in ComputedCSharpAccess.Matches(Code(File.ReadAllText(file))))
            {
                string key = name + "|" + m.Groups["arg"].Value.Trim();
                seen.Add(key);
                if (!InternalVariableAccess.ContainsKey(key)) offenders.Add(key + "   <- " + m.Value.Trim());
            }
        }

        Assert.True(offenders.Count == 0,
            "These cmdlet sites reach a PowerShell variable by a computed name. A bash variable resolves through ONE "
            + "chain: read it with BashVariableLookup.Scalar / ArrayHolder / IsSet, assign it with SetScalar / SetArray "
            + "(a scalar PowerShell variable is never the shell's value). An internal __Bash* variable named through a "
            + "constant goes into InternalVariableAccess with its reason (environment-model.md section 8). Offenders:\n  "
            + string.Join("\n  ", offenders));
        var stale = InternalVariableAccess.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Exempted accesses that no longer exist: " + string.Join(", ", stale));
    }

    // `Get-Variable -Name $n`, `Set-Variable $n`, `$ExecutionContext.SessionState.PSVariable.Get($n)` in the module.
    private static readonly Regex ComputedModuleAccess = new(
        @"\b(?:Get|Set|New|Remove|Clear)-Variable\b(?:\s+-Name)?\s+\$|PSVariable\.(?:Get|GetValue|Set|Remove)\(\s*\$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void NoModuleFunction_ReachesAPowerShellVariableByComputedName()
    {
        string psm1 = Path.Combine(RepoRoot(), "src", "PsBash.Module", "PsBash.psm1");
        var offenders = Code(File.ReadAllText(psm1)).Split('\n')
            .Select((line, i) => (line, n: i + 1))
            .Where(x => ComputedModuleAccess.IsMatch(x.line))
            .Select(x => $"{x.n}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "PsBash.psm1 reaches a PowerShell variable by a computed name. Resolve a bash variable through "
            + "[PsBash.Cmdlets.BashVariableLookup] (environment-model.md section 8). Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>Every construct that binds or reads the bash scalar <c>q</c>, each wrapped in a function too.</summary>
    public static TheoryData<string> Binders() => new()
    {
        "for q in a b; do echo $q ${q} \"$q\"; done",
        "for q in 'x y' z; do echo $q; done",
        "for q; do echo $q; done",
        "for ((q = 0; q < 3; q++)); do echo $q $((q * 2)); done",
        "while read q; do echo $q; done < f",
        "while IFS=: read -r q rest; do echo $q $rest; done < f",
        "printf 'a\\n' | while read q; do echo $q; done",
        "read q < f; echo $q",
        "printf -v q '%s' 1; echo $q",
        "getopts ab q; echo $q",
        "let q=1; echo $q",
        "(( q = 2 )); echo $q",
        "q=1; [[ -v q ]] && echo $q",
        "n=q; echo ${!n}",
        "declare q=1; echo $q",
        "local q=1; echo $((q + 1))",
        "for q in a; do echo $q & done; wait",
    };

    // `$q`, `${q}`, `foreach ($q`, `$q =`: a PowerShell variable named after the bash scalar (case-insensitive, as
    // PowerShell names are). The job capture (`$q` inside an Invoke-BashBackground block) is the one exception: the
    // blocks are taken out, and ABackgroundJobInALoop_IsHandedTheValueAtLaunch pins what they read.
    private static readonly Regex PowerShellVariableNamedQ = new(@"\$(?:\{q\}|q\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(Binders))]
    public void TheEmitter_BindsNoPowerShellVariableForABashScalar(string script)
    {
        foreach (string text in new[] { script, "fn() { " + script + "; }; fn" })
        {
            string ps = BashTranspiler.Transpile(text);
            string outsideJobs = Regex.Replace(ps, @"Invoke-BashBackground\b.*", "", RegexOptions.Singleline);
            var offenders = PowerShellVariableNamedQ.Matches(outsideJobs).Select(m => m.Value).ToList();
            Assert.True(offenders.Count == 0,
                $"`{text}` emitted a PowerShell variable for a bash scalar ({string.Join(", ", offenders)}); the shell "
                + "variable store is its one storage (environment-model.md section 8):\n" + ps);
        }
    }

    // A loop over a PowerShell-owned name iterates a private variable and binds the shell variable; `$PID` / `$input`
    // are never assigned (`for pid in` once ran no iteration: "Cannot overwrite variable PID").
    [Theory]
    [InlineData("pid")]
    [InlineData("input")]
    [InlineData("Host")]
    public void ALoopOverAPowerShellOwnedName_BindsOnlyTheShellVariable(string name)
    {
        string ps = BashTranspiler.Transpile($"for {name} in 1; do echo ${name}; done");
        Assert.DoesNotMatch(new Regex(@"\$(?:\{" + name + @"\}|" + name + @"\b)(?!:)", RegexOptions.IgnoreCase), ps);
        Assert.Contains("BashVariableStore]::Set('" + name + "'", ps);
    }

    // The job reads the loop's value at launch from its own scalar store, copied at the `&` (ScalarOverlay / the job
    // host's environment block): no second, PowerShell copy beside it, which hid the job's own `q=x` from its reads.
    [Fact]
    public void ABackgroundJobInALoop_ReadsTheLoopVariableFromItsOwnStore()
    {
        string ps = BashTranspiler.Transpile("for q in a b; do echo $q & done");
        Assert.DoesNotContain("-Vars", ps);
        Assert.Contains("$env:q", ps);
    }
}
