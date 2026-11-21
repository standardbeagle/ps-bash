using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A background job is a process of its own in bash: it has its own <c>$BASHPID</c>, a signal it sends itself ends only
/// it, and <c>wait</c> reports 128+N. REGRESSION: an in-process job read the shell's pid as <c>$BASHPID</c>, so
/// <c>{ kill -USR1 $BASHPID; } &amp; wait $!</c> signalled the SHELL (the script died with 138), and a job that did end
/// itself surfaced in <c>wait</c> as EndInvoke's "pipeline has been stopped" text with status 1. The pieces that make the
/// one path (spec background-jobs.md §3) are pinned here so a sibling path cannot drop one:
/// (1) <c>BashJobRunspaces.NewJob</c> gives every in-process job a synthetic <c>$BASHPID</c>;
/// (2) <c>ShellSignalTraps.Terminate</c> ends a job like its <c>exit</c> (output kept) before any pipeline stop, and
/// records the signal in <c>ShellSignalTraps.JobSignalVariable</c>;
/// (3) the job wrapper (<c>JobScript.Text</c>) hands that variable to <c>wait</c>, and <c>Complete-BashBgJob</c> prints no exception text
/// for a job a signal ended. Oracle note (qa-rubric Directive 1): a repo-structure invariant; the behaviour is
/// BackgroundJobSelfSignalDifferentialTests.
/// </summary>
public class JobSelfSignalGuardTests
{
    [Fact]
    public void EveryInProcessJob_GetsItsOwnBashPid()
    {
        string src = Read("src", "PsBash.Cmdlets", "BashJobRunspaces.cs");
        string newJob = Body(src, "public static PowerShell NewJob(");
        Assert.Contains("BashRuntime.SubshellPidVariable", newJob);
        Assert.Contains("BashRuntime.NextSubshellPid()", newJob);
    }

    [Fact]
    public void Terminate_EndsAJobLikeItsExit_BeforeAnyPipelineStop()
    {
        string terminate = Body(Read("src", "PsBash.Cmdlets", "ShellSignalTraps.cs"), "internal static Exception Terminate(");
        int job = terminate.IndexOf("InBackgroundJob(cmdlet)", StringComparison.Ordinal);
        int stop = terminate.IndexOf("new PipelineStoppedException", StringComparison.Ordinal);
        Assert.True(job >= 0 && stop > job, "Terminate must handle a background job before the pipeline stop");
        Assert.Contains("JobSignalVariable", terminate);
        Assert.Contains("\"exit \"", terminate);
    }

    // (4) A foreground subshell is the same case: `( kill -TERM $BASHPID )` ended the whole script (143) instead of the
    // subshell. kill marks a self-signal that named the subshell's own $BASHPID, and Terminate throws it to the subshell
    // scope as that scope's `exit`, before the job and script paths.
    [Fact]
    public void ASubshellSignallingItself_EndsOnlyThatSubshell()
    {
        string terminate = Body(Read("src", "PsBash.Cmdlets", "ShellSignalTraps.cs"), "internal static Exception Terminate(");
        int sub = terminate.IndexOf("throw new BashSubshellExitException(status)", StringComparison.Ordinal);
        int job = terminate.IndexOf("if (inJob)", StringComparison.Ordinal);
        int stop = terminate.IndexOf("new PipelineStoppedException", StringComparison.Ordinal);
        Assert.True(sub >= 0 && job > sub && stop > job, "Terminate: the subshell exit, then the job, then the script stop");

        string kill = Read("src", "PsBash.Cmdlets", "InvokeBashKillCommand.cs");
        Assert.Contains("ShellSignalTraps.Terminate(this, sigNum, innermostSubshell: selfIsSubshell)", kill);
        Assert.Matches(new Regex(@"procId == BashRuntime\.BashPid\(SessionState\)\)\s*\{\s*if \(!existenceOnly\) signalSelf = selfIsSubshell = true;"), kill);
    }

    [Fact]
    public void JobWrapper_HandsTheSignalToWait_AndWaitPrintsNoExceptionText()
    {
        Assert.Matches(new Regex(@"\$__bgStatus\.Signal\s*=\s*\$global:" + Regex.Escape(ShellSignalTraps.JobSignalVariable) + @"\b"), JobScript.Text);

        string psm1 = Read("src", "PsBash.Module", "PsBash.psm1");

        string complete = Body(psm1, "function Complete-BashBgJob {");
        var catchBlock = Regex.Match(complete, @"\}\s*catch\s*\{(?<c>.*?)\}\s*finally", RegexOptions.Singleline);
        Assert.True(catchBlock.Success, "Complete-BashBgJob has a catch block");
        string c = catchBlock.Groups["c"].Value;
        int signal = c.IndexOf("if ($selfSignal)", StringComparison.Ordinal);
        int text = c.IndexOf("Write-BashHostStderr", StringComparison.Ordinal);
        Assert.True(signal >= 0 && text > signal, "a job a signal ended must be handled before the exception text is printed");
    }

    // The text from the declaration to its matching close brace.
    private static string Body(string src, string declaration)
    {
        int start = src.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"`{declaration}` not found");
        int open = src.IndexOf('{', start + declaration.Length - (declaration.EndsWith('{') ? 1 : 0));
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[start..(i + 1)];
        }
        return src[start..];
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

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
