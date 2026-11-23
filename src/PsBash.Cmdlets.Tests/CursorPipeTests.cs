using System.Diagnostics;
using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The one pipe a stdin cursor's input is in while a program reads it (<see cref="CursorPipe"/>, through
/// <see cref="NativeStdinBridge.ForChild"/>): the shell's reads and its programs' share ONE position, and the input keeps
/// its order across holders. ps-bash's own surface (the cursor and the pipe); the behavior against bash is Differential
/// <c>JobStdinDifferentialTests</c> and Shell.Tests <c>JobStdinEndToEndTests</c> (qa-rubric Directive 1).
/// </summary>
public class CursorPipeTests
{
    private static StdinCursor Lines(params string[] lines)
    {
        var cursor = new StdinCursor();
        foreach (var line in lines) cursor.Enqueue(line);
        return cursor;
    }

    private static List<string> ReadAll(StdinCursor cursor)
    {
        var all = new List<string>();
        while (cursor.TryDequeue(out var record)) all.Add(BashRuntime.GetBashText(record));
        return all;
    }

    // REGRESSION: `printf 'a\nb\nc\n' | while read x; do /usr/bin/sleep 1 & echo "$x"; done` printed a, b: the program's
    // pump had taken the rest into its pipe, and the shell's next read found the cursor empty and done. The shell reads the
    // pipe while a program holds it, and the rest is back once the program is gone.
    [Fact]
    public void TheShellsRead_WhileAProgramHoldsThePipe_ReadsThePipe()
    {
        var cursor = Lines("a", "b", "c");
        var program = NativeStdinBridge.ForChild(cursor);
        Assert.NotNull(program);
        Assert.True(cursor.TryDequeue(out var first));
        Assert.Equal("a", BashRuntime.GetBashText(first));
        program!.Exit();
        Assert.Equal(["b", "c"], ReadAll(cursor));
    }

    [Fact]
    public void PushFrontAndCount_WhileAProgramHoldsThePipe_ComeBeforeThePipe()
    {
        var cursor = Lines("a", "b", "c");
        var program = NativeStdinBridge.ForChild(cursor);
        Assert.NotNull(program);
        Assert.Equal(1, cursor.Count);
        Assert.True(cursor.TryDequeue(out var first));
        Assert.Equal("a", BashRuntime.GetBashText(first));
        cursor.PushFront(["x"]);
        Assert.True(cursor.TryDequeue(out var back));
        Assert.Equal("x", BashRuntime.GetBashText(back));
        cursor.PushFront(["y"]);
        program!.Exit();
        Assert.Equal(["y", "b", "c"], ReadAll(cursor));
    }

    // REGRESSION: `seq 50000 | { /usr/bin/true & /usr/bin/true; /usr/bin/true; wait; read a; read b; }` gave a=2 b=1: a
    // program that started while the last one's pipe was being put back opened a second pipe and took the records behind
    // it first. Holders come and go from several threads; the input keeps its order.
    [Fact]
    public async Task ProgramsComingAndGoing_OnSeveralThreads_KeepTheOrder()
    {
        var want = Enumerable.Range(1, 5000).Select(i => i.ToString()).ToArray();
        for (int round = 0; round < 20; round++)
        {
            var cursor = Lines(want);
            var holders = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 25; i++) NativeStdinBridge.ForChild(cursor)?.Exit();
            })).ToArray();
            await Task.WhenAll(holders).WaitAsync(TimeSpan.FromSeconds(60)); // TimeoutException: a holder never finished
            Assert.Equal(want, ReadAll(cursor));
        }
    }

    // Two programs reading one stdin read one pipe: what the first left is the second's, the rest goes back after both.
    // REGRESSION: `echo hi | { /usr/bin/cat & wait; }` printed nothing (the job's program kept the process stdin).
    [SkippableFact]
    public void TwoPrograms_OneStdin_ReadOnePipe()
    {
        Skip.If(OperatingSystem.IsWindows(), "/usr/bin/head -c1 reads one byte; the pipe itself is the same on Windows");
        var cursor = new StdinCursor();
        cursor.Enqueue(BashRuntime.TextRecord("abc", unterminated: true));
        var first = NativeStdinBridge.ForChild(cursor);
        var second = NativeStdinBridge.ForChild(cursor);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first!.ChildStdin, second!.ChildStdin);

        Assert.Equal("a", HeadOneByte(first));
        first.Exit();
        Assert.Equal("b", HeadOneByte(second));
        second.Exit();
        Assert.True(cursor.TryDequeue(out var rest));
        Assert.Equal("c", BashRuntime.GetBashText(rest));
        Assert.False(cursor.TryDequeue(out _));
    }

    private static string HeadOneByte(NativeStdinBridge bridge)
    {
        var psi = new ProcessStartInfo("/usr/bin/head") { ArgumentList = { "-c1" } };
        using var child = ChildStdio.Start(psi, new ChildStdioMode(Stdin: bridge.ChildStdin));
        string output = child.StandardOutput.ReadToEnd();
        Assert.True(child.WaitForExit(30_000), "head never exited");
        return output;
    }
}
