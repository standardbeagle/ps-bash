using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;
using PsParser = System.Management.Automation.Language.Parser;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Guard for "a launch spends script time between one <c>&amp;</c> and the next" (docs/specs/background-jobs.md section
/// 12.5, cause 8): the in-process launch path is C#, start to end. On Windows every script compile and every
/// script-to-.NET method call is an AMSI call under PowerShell's process-wide lock; the psm1 launch made about five per
/// <c>&amp;</c>, so three launches in a row came 4-35 ms apart and <c>for i in 3 2 1; do { sleep 0.0$i; echo $i; } &amp;
/// done; wait</c> printed out of bash's order (spec/oils/background.spec.sh "wait for N parallel jobs and check failure").
/// The emitted <c>cmd &amp;</c> is one command, <c>Invoke-BashBackground</c>, which is the binary cmdlet
/// (<see cref="InvokeBashBackgroundCommand"/>), not a psm1 function that would shadow it; the module starts no
/// in-process job itself; and <see cref="BashBgJobTable.LaunchInProcess"/> runs no script.
/// <para>Oracle note (qa-rubric Directive 1): a structural rule about where the launch runs; the bash-visible result is
/// the spec case's and BackgroundJobLiveOutputTests'.</para>
/// </summary>
public class BackgroundLaunchGuardTests
{
    private const string Launcher = "Invoke-BashBackground";

    [Theory]
    [InlineData("sleep 1 &")]
    [InlineData("for i in 3 2 1; do { sleep 0.0$i; echo $i; } & done")]
    [InlineData("cd /tmp &")]
    [InlineData("x=$(echo a &)")]
    public void Transpile_BackgroundJob_IsOneLauncherCallWithNoMethodCall(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        var ast = PsParser.ParseInput(ps, out _, out var errors);
        Assert.Empty(errors);
        var launches = ast.FindAll(a => a is CommandAst c && c.GetCommandName() == Launcher, true).Cast<CommandAst>().ToList();
        Assert.NotEmpty(launches);
        foreach (var launch in launches)
        {
            // Everything in the call but the job's body (the { } and the -Vars values the loop hands over) is a
            // constant: no method call, no type expression, nothing that reaches .NET from script.
            var outside = launch.FindAll(a => a is InvokeMemberExpressionAst or TypeExpressionAst, true)
                .Where(a => !InBody(a, launch))
                .Select(a => a.Extent.Text)
                .ToList();
            Assert.True(outside.Count == 0, $"`{bash}` launches with script-to-.NET calls: {string.Join(", ", outside)}\n{ps}");
        }
    }

    private static bool InBody(Ast a, CommandAst launch)
    {
        for (var p = a.Parent; p is not null && p != launch; p = p.Parent)
            if (p is ScriptBlockExpressionAst or HashtableAst) return true;
        return false;
    }

    [Fact]
    public void Launcher_IsTheBinaryCmdlet()
    {
        var attr = (CmdletAttribute)Attribute.GetCustomAttribute(typeof(InvokeBashBackgroundCommand), typeof(CmdletAttribute))!;
        Assert.Equal(Launcher, attr.VerbName + "-" + attr.NounName);
    }

    /// <summary>A psm1 function of the same name beats the cmdlet (PowerShell's command precedence) and brings the
    /// script launch back. The module starts no in-process job (no BeginInvoke/BeginLive) and keeps no id counter.</summary>
    [Fact]
    public void Module_HasNoLaunchFunction()
    {
        var psm1 = Path.Combine(RepoRoot(), "src", "PsBash.Module", "PsBash.psm1");
        var ast = PsParser.ParseFile(psm1, out _, out var errors);
        Assert.Empty(errors);
        Assert.Null(ast.Find(a => a is FunctionDefinitionAst f
            && string.Equals(f.Name, Launcher, StringComparison.OrdinalIgnoreCase), true));
        var starts = ast.FindAll(a => a is InvokeMemberExpressionAst m && (m.Member as StringConstantExpressionAst)?.Value
                is "BeginInvoke" or "BeginLive" or "NewJob", true)
            .Select(a => $"line {a.Extent.StartLineNumber}: {a.Extent.Text}")
            .ToList();
        Assert.True(starts.Count == 0, "The module starts an in-process job in script; that is BashBgJobTable.LaunchInProcess:\n"
            + string.Join("\n", starts));
        Assert.Contains("[PsBash.Cmdlets.BashBgJobTable]::new($script:BashBgJobs", File.ReadAllText(psm1));
    }

    [Fact]
    public void LaunchInProcess_RunsNoScript()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", "BashBgJobTable.cs"));
        var m = Regex.Match(src, @"internal void LaunchInProcess\(.*?\n    \}\n", RegexOptions.Singleline);
        Assert.True(m.Success, "BashBgJobTable.LaunchInProcess not found");
        foreach (var banned in new[] { "InvokeScript", "AddScript", "ScriptBlock.Create", "InvokeCommand", ".Invoke(" })
            Assert.DoesNotContain(banned, m.Value);
        Assert.Contains("BashJobRunspaces.NewJob(", m.Value);
    }

    [Fact]
    public void NextJobNumber_IsOnePastTheHighest()
    {
        Assert.Equal(1, BashBgJobTable.NextJobNumber([]));
        var a = new PSObject(); a.Properties.Add(new PSNoteProperty("JobNumber", 1));
        var b = new PSObject(); b.Properties.Add(new PSNoteProperty("JobNumber", 3));
        var noNumber = new PSObject(); // a record without one counts as its position (Get-BashJobNumber)
        Assert.Equal(4, BashBgJobTable.NextJobNumber([a, b]));
        Assert.Equal(4, BashBgJobTable.NextJobNumber([b, a]));
        Assert.Equal(3, BashBgJobTable.NextJobNumber([a, noNumber]));
    }

    [Fact]
    public void Record_HasExitedAndKill_AreTheProcessLikeSurface()
    {
        using var ps = PowerShell.Create();
        var status = System.Collections.Hashtable.Synchronized(new System.Collections.Hashtable());
        var job = BashBgJobTable.NewRecord(1000, ps, status, live: false, text: "sleep 1");
        Assert.False((bool)job.Properties["HasExited"].Value);
        Assert.Equal(1000, job.Properties["Id"].Value);
        Assert.Equal("sleep 1", job.Properties["Text"].Value);
        job.Methods["Kill"].Invoke();
        Assert.True((bool)job.Properties["Done"].Value);
        Assert.True((bool)job.Properties["HasExited"].Value);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
