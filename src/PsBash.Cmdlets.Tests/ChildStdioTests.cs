using System.Diagnostics;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="ChildStdio"/>, the one place a child's stdio is made (<c>docs/specs/stdio-messaging.md</c> §4). Merged,
/// the child has ONE pipe for fd 1 and fd 2, so what it wrote alternately comes back in exactly that order, every
/// time; separate, there are two pipes. Hand-written asserts: this is the spawn primitive's contract (the bash-level
/// behavior is pinned by <c>StderrMergeOrderDifferentialTests</c>).
/// </summary>
public class ChildStdioTests
{
    private const int Lines = 200;

    /// <summary>A child that writes o1 e1 o2 e2 ... with each write flushed at once (a shell's echo).</summary>
    private static ProcessStartInfo Alternating()
    {
        var psi = new ProcessStartInfo { CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /q /c \"for /l %i in (1,1," + Lines + ") do @(echo o%i& echo e%i 1>&2)\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("i=1; while [ $i -le " + Lines + " ]; do echo o$i; echo e$i >&2; i=$((i+1)); done");
        }
        return psi;
    }

    private static string Expected()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= Lines; i++) sb.Append('o').Append(i).Append('\n').Append('e').Append(i).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Merged_OneStream_KeepsTheChildsWriteOrder()
    {
        using var child = ChildStdio.Start(Alternating(), ChildStdioMode.Merged);
        child.StandardInput?.Close();
        string all = child.StandardOutput.ReadToEnd();
        child.WaitForExit();

        Assert.Null(child.StandardError);
        Assert.True(child.StderrMerged);
        Assert.Equal(Expected(), all.Replace("\r\n", "\n").Replace(" \n", "\n"));
        Assert.Equal(0, child.ExitCode);
    }

    [Fact]
    public async Task Separate_TwoStreams_EachHoldsItsOwnLines()
    {
        using var child = ChildStdio.Start(Alternating(), ChildStdioMode.Separate);
        child.StandardInput?.Close();
        var err = Task.Run(() => child.StandardError!.ReadToEnd());
        string output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();

        Assert.NotNull(child.StandardError);
        Assert.DoesNotContain("e1", output, StringComparison.Ordinal);
        Assert.StartsWith("e1", (await err).TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void Merged_ExitStatusAndArguments_AreTheChilds()
    {
        var psi = new ProcessStartInfo { CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /c \"echo out& echo err 1>&2& exit 7\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("printf '%s|' \"$@\"; echo; echo err >&2; exit 7");
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add("a b");
            psi.ArgumentList.Add("");
            psi.ArgumentList.Add("-e");
        }
        using var child = ChildStdio.Start(psi, ChildStdioMode.Merged);
        child.StandardInput?.Close();
        string all = child.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
        child.WaitForExit();

        Assert.Equal(7, child.ExitCode);
        Assert.EndsWith("err\n", all.Replace(" \n", "\n"), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.StartsWith("a b||-e|\n", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Merged_PipedStdin_ReachesTheChild()
    {
        var psi = new ProcessStartInfo { CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /c \"findstr x& echo done 1>&2\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("grep x; echo done >&2");
        }
        using var child = ChildStdio.Start(psi, ChildStdioMode.Merged);
        child.StandardInput!.Write("ax\nb\ncx\n");
        child.StandardInput.Close();
        string all = child.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
        child.WaitForExit();

        Assert.Equal("ax\ncx\ndone\n", all.Replace(" \n", "\n"));
    }

    /// <summary>The SMA internals the merge predicate reads still exist (a PowerShell upgrade that moves them must fail here, not silently fall back to two pipes).</summary>
    [Fact]
    public void StderrMerge_CanReadThePipelinesMergeState()
    {
        Assert.True(StderrMerge.CanDetect);
    }
}
