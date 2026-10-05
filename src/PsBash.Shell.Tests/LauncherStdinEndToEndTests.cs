using System.Diagnostics;
using System.Text;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// The launcher's OWN stdin is the stdin of a <c>-c</c> command (bash: <c>printf 'b\na\n' | bash -c sort</c>),
/// forwarded to the host as raw bytes while the command runs (HostProtocol <c>STDIN:</c> frames), pulled
/// lazily by the first stdin reader, end of input propagated. Oracle: bash 5.2 (`printf 'b\na\n' | bash -c sort`
/// = a b; `yes | bash -c 'head -n1'` terminates).
/// </summary>
[Trait("Category", "Integration")]
public class LauncherStdinEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private sealed record Run(int ExitCode, byte[] Stdout, string Stderr);

    /// <summary>
    /// Start <c>ps-bash -c script</c> with redirected stdin and hand the stdin stream to <paramref name="feed"/>,
    /// which writes whatever it likes and decides when (or whether) to close it.
    /// </summary>
    private static async Task<Run> RunAsync(string script, Func<Stream, Task> feed, string? workingDirectory = null)
    {
        var psi = PsBashTestProcess.Create(["-c", script], workingDirectory, ipcEndpoint: IpcEndpoint);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.Latin1;
        psi.StandardErrorEncoding = Encoding.Latin1;
        psi.UseShellExecute = false;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ps-bash");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            var feeder = Task.Run(async () =>
            {
                try { await feed(process.StandardInput.BaseStream); }
                catch (IOException) { /* the command finished and closed its end: expected for early-exit readers */ }
                catch (ObjectDisposedException) { }
            });
            using var cts = new CancellationTokenSource(Limit);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"ps-bash -c '{script}' did not exit within {Limit.TotalSeconds:0}s (stdin forwarding hung?)");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new Run(process.ExitCode, Encoding.Latin1.GetBytes(stdout), stderr);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    private static Func<Stream, Task> Text(string text) => async s =>
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await s.WriteAsync(bytes);
        await s.FlushAsync();
        s.Close();
    };

    private static Func<Stream, Task> Bytes(byte[] bytes) => async s =>
    {
        await s.WriteAsync(bytes);
        await s.FlushAsync();
        s.Close();
    };

    private static string Lf(byte[] stdout) =>
        Encoding.Latin1.GetString(stdout).Replace("\r\n", "\n");

    [SkippableFact]
    public async Task PipedStdin_Sort_SortsTheForwardedLines()
    {
        var r = await RunAsync("sort", Text("b\na\nc\n"));
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("a\nb\nc\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_ReadThenCat_ReadTakesOneLineAndCatGetsTheRest()
    {
        var r = await RunAsync("read x; echo \"first=$x\"; cat", Text("one\ntwo\nthree\n"));
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("first=one\ntwo\nthree\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_LoneRead_BindsTheFirstLine()
    {
        var r = await RunAsync("read x; echo \"got=$x\"", Text("hello world\nignored\n"));
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("got=hello world\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_WhileRead_LoopsOverEveryForwardedLine()
    {
        var r = await RunAsync("while read -r l; do echo \"[$l]\"; done", Text("a b\nc\n"));
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("[a b]\n[c]\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_ThroughAPipeline_FeedsTheFirstStage()
    {
        var r = await RunAsync("sort | head -n 2", Text("d\nb\na\nc\n"));
        Assert.Equal("a\nb\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_HeadOfAnInfiniteProducer_StopsAtTheFirstLineAndTheLauncherExits()
    {
        // bash: `yes | bash -c 'head -n1'` prints y and terminates. Here the producer is the test process, writing
        // until the launcher goes away; the stdin must be pulled lazily and EOF/early-exit must not hang anything.
        var r = await RunAsync("head -n 1", async s =>
        {
            var chunk = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("y\n", 4096)));
            while (true)
            {
                await s.WriteAsync(chunk);
                await s.FlushAsync();
            }
        });
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("y\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_EveryByteValue_RoundTripsThroughCatToAFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-stdin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            var rnd = new byte[70_000]; // spans several forwarded chunks
            new Random(7).NextBytes(rnd);
            var input = all.Concat(rnd).ToArray(); // no final newline guaranteed to be absent below
            var r = await RunAsync("cat > out.bin", Bytes(input), dir);
            Assert.True(r.ExitCode == 0, r.Stderr);
            Assert.Equal(input, File.ReadAllBytes(Path.Combine(dir, "out.bin")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task PipedStdin_MissingFinalNewline_IsKeptExactly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-stdin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var r = await RunAsync("cat > out.bin", Bytes("a\nb"u8.ToArray()), dir);
            Assert.True(r.ExitCode == 0, r.Stderr);
            Assert.Equal("a\nb"u8.ToArray(), File.ReadAllBytes(Path.Combine(dir, "out.bin")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task OpenStdinThatIsNeverWritten_CommandThatIgnoresStdin_DoesNotBlock()
    {
        // A pipe the parent never closes (the Bash tool / a CI runner): a command that does not read stdin must
        // finish by itself. The writer here neither writes nor closes.
        var release = new TaskCompletionSource();
        var run = RunAsync("echo done", async _ => await release.Task);
        try
        {
            var r = await run;
            Assert.Equal(0, r.ExitCode);
            Assert.Equal("done\n", Lf(r.Stdout));
        }
        finally { release.TrySetResult(); }
    }

    // A program that never reads stdin, and one that copies the lines matching "alp" from it.
    private static readonly string NativeIgnorer = OperatingSystem.IsWindows() ? "cmd.exe /c echo ran" : "/bin/echo ran";
    private static readonly string NativeReader = OperatingSystem.IsWindows() ? "findstr.exe alp" : "/usr/bin/grep alp";

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenStdinThatIsNeverWritten_NativeCommandThatIgnoresStdin_DoesNotBlock(bool viaEvalDevNull)
    {
        // REGRESSION (v0.11.0): a native command probed the forwarded stdin BEFORE it started (`Count -gt 0`
        // blocked until a record arrived or the stdin closed), so under an agent's shell tool — whose stdin is an
        // open pipe nobody writes — `git --version` hung forever. The second shape is that tool's own wrapper,
        // `eval '<cmd>' < /dev/null`, whose redirect used to be dropped.
        var script = viaEvalDevNull ? $"eval '{NativeIgnorer}' < /dev/null" : NativeIgnorer;
        var release = new TaskCompletionSource();
        var run = RunAsync(script, async _ => await release.Task);
        try
        {
            var r = await run;
            Assert.True(r.ExitCode == 0, r.Stderr);
            Assert.Equal("ran\n", Lf(r.Stdout));
        }
        finally { release.TrySetResult(); }
    }

    [SkippableFact]
    public async Task PipedStdin_NativeThatReadsStdin_GetsTheForwardedLines()
    {
        var r = await RunAsync(NativeReader, Text("alpha\nbeta\nalpine\n"));
        Assert.True(r.ExitCode == 0, r.Stderr);
        Assert.Equal("alpha\nalpine\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_NativeThatIgnoresStdin_LeavesItForTheNextCommand()
    {
        // bash: `printf 'kept\n' | bash -c 'true; cat'` → kept. The program's stdin is a pipe the forwarded data is
        // pumped into; what it did not read is put back in front of the shared stdin.
        var r = await RunAsync(NativeIgnorer + " >/dev/null; cat", Text("kept1\nkept2\n"));
        Assert.True(r.ExitCode == 0, r.Stderr);
        Assert.Equal("kept1\nkept2\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task PipedStdin_ReadFromDevNull_SeesEndOfInputAndLeavesTheStdinAlone()
    {
        // bash: `printf 'one\n' | bash -c 'read x </dev/null; echo "$? [$x]"; read y; echo "[$y]"'` → `1 []`, `[one]`.
        var r = await RunAsync("read x </dev/null; echo \"$? [$x]\"; read y; echo \"[$y]\"", Text("one\n"));
        Assert.Equal("1 []\n[one]\n", Lf(r.Stdout));
    }

    [SkippableFact]
    public async Task EmptyStdin_Cat_PrintsNothingAndExitsZero()
    {
        var r = await RunAsync("cat", Text(""));
        Assert.Equal(0, r.ExitCode);
        Assert.Empty(r.Stdout);
    }
}
