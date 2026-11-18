using System.Diagnostics;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="StreamingChildProcess"/> against real children: its buffer holds a fast child back instead of growing
/// (back-pressure, like a full pipe), and pipe EOF ends the reading, not the wait for the exit status.
/// <para>
/// Hand-written asserts: the buffer is ps-bash's own mechanism. The exit-status rule is bash's (oracle, bash 5.2:
/// <c>bash -c 'exec &gt;&amp;- 2&gt;&amp;-; sleep 1; exit 3'; echo $?</c> prints 3 after one second), pinned end to end by
/// <c>NestedShellStreamingDifferentialTests</c>; here the child is a plain process so the case does not depend on
/// how a nested ps-bash closes its own descriptors.
/// </para>
/// </summary>
public class StreamingChildLifetimeTests : IDisposable
{
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ps-bash", "streaming-child-" + Guid.NewGuid().ToString("N"));

    public StreamingChildLifetimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Collector : IChildOutputSink
    {
        public readonly List<string> Stdout = new();
        public readonly StringBuilder Stderr = new();
        public bool Stopping => false;
        void IChildOutputSink.Stdout(object record) => Stdout.Add(BashRuntime.GetBashText(record));
        void IChildOutputSink.Stderr(string text) => Stderr.Append(text);
    }

    private static ProcessStartInfo CatFile(string path) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "type", path } }
        : new ProcessStartInfo("/bin/cat") { ArgumentList = { path } };

    // A fast child and nobody draining: the buffer fills, then the child blocks on its own write and cannot finish.
    // Before the fix the readers queued all 4 MB (memory without bound) and the child exited at once.
    [Fact]
    public void FastChild_NobodyDrains_BufferStaysBounded_ChildIsHeldBack_ThenEverythingArrivesInOrder()
    {
        const int Lines = 200_000; // 20 bytes each: 4 MB, far beyond the buffer plus any OS pipe buffer
        var path = Path.Combine(_dir, "big.txt");
        var sb = new StringBuilder();
        for (int i = 0; i < Lines; i++) sb.Append("line ").Append(i.ToString("D14")).Append('\n');
        File.WriteAllText(path, sb.ToString());

        using var child = BashRuntime.StartChildProcessStreaming(CatFile(path), keepStdinOpen: false);
        Assert.True(SpinWait.SpinUntil(() => child.BufferedChars >= StreamingChildProcess.BufferChars - StreamingChildProcess.ReadChars, StartLimit),
            $"the child's output never filled the buffer (buffered {child.BufferedChars} chars)");
        Assert.False(SpinWait.SpinUntil(() => child.ChildHasExited, TimeSpan.FromSeconds(2)),
            "the child finished writing 4 MB while nobody drained: nothing held it back");
        Assert.InRange(child.BufferedChars, 0, StreamingChildProcess.BufferChars + StreamingChildProcess.ReadChars);

        var sink = new Collector();
        int status = child.Complete(sink);

        Assert.Equal(0, status);
        Assert.Equal(Lines, sink.Stdout.Count);
        Assert.Equal("line " + 0.ToString("D14"), sink.Stdout[0]);
        Assert.Equal("line " + (Lines - 1).ToString("D14"), sink.Stdout[^1]);
        Assert.Equal("", sink.Stderr.ToString());
    }

    /// <summary>A child that closes its stdout and stderr, keeps running <paramref name="holdSeconds"/>, then exits 3.</summary>
    private ProcessStartInfo ClosesItsOutputThenExits3(int holdSeconds)
    {
        if (!OperatingSystem.IsWindows())
            return new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"exec >&- 2>&-; sleep {holdSeconds}; exit 3" } };
        // Windows PowerShell keeps duplicates of its std handles, so closing GetStdHandle(-11/-12) alone leaves the pipes
        // open (probed: EOF only at exit). Close every pipe handle the process holds: that is its stdin/stdout/stderr
        // and their duplicates (it needs none of them to sleep and exit).
        var script = Path.Combine(_dir, "close-output.ps1");
        File.WriteAllText(script,
            "Add-Type -Namespace PsBashTest -Name Std -MemberDefinition '[DllImport(\"kernel32.dll\")] public static extern int GetFileType(IntPtr h); [DllImport(\"kernel32.dll\")] public static extern bool CloseHandle(IntPtr h);'\n"
            + "for ($h = 4; $h -lt 65536; $h += 4) { $p = [IntPtr]$h; if ([PsBashTest.Std]::GetFileType($p) -eq 3) { [void][PsBashTest.Std]::CloseHandle($p) } }\n"
            + $"Start-Sleep -Seconds {holdSeconds}\n"
            + "exit 3\n");
        return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script },
        };
    }

    // bash waits for the process, not for its pipes: the status is the child's own. Before the fix EOF ended the wait,
    // a 2 s bound expired while the child still ran, and the result was 124 (and the child was killed).
    [Fact]
    public void ChildClosesItsOutput_KeepsRunning_StatusIsItsOwnExit()
    {
        const int HoldSeconds = 6; // well past the old 2 s bound after EOF
        using var child = BashRuntime.StartChildProcessStreaming(ClosesItsOutputThenExits3(HoldSeconds), keepStdinOpen: false);

        var sink = new Collector();
        int status = child.Complete(sink);

        Assert.Equal(3, status);
        Assert.True(child.ChildHasExited, "Complete returned before the child exited");
        Assert.Empty(sink.Stdout);
    }

    // The wait for the exit after EOF is still a stop point: Kill (StopProcessing) ends it and the tree.
    [Fact]
    public void ChildClosesItsOutput_WaitForExit_EndsOnKill()
    {
        const int HoldSeconds = 120;
        using var child = BashRuntime.StartChildProcessStreaming(ClosesItsOutputThenExits3(HoldSeconds), keepStdinOpen: false);
        var killer = new Thread(() =>
        {
            // Kill once the child's pipes are closed, i.e. Complete is in its wait for the exit.
            SpinWait.SpinUntil(() => child.OutputClosed, StartLimit);
            child.Kill();
        }) { IsBackground = true };
        killer.Start();

        var sw = Stopwatch.StartNew();
        child.Complete(new Collector());
        sw.Stop();
        killer.Join();

        Assert.True(child.OutputClosed, "the child never closed its output; nothing was tested");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(HoldSeconds / 2), $"the kill did not end the wait; took {sw.ElapsedMilliseconds}ms");
        Assert.True(SpinWait.SpinUntil(() => child.ChildHasExited, TimeSpan.FromSeconds(40)), "the child survived the kill");
    }
}
