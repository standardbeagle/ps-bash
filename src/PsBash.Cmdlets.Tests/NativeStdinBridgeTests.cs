using System.Collections.Concurrent;
using System.Management.Automation;
using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>Enter-/Exit-BashNativeStdin</c>: a native program gets the shell's shared stdin as its REAL process stdin.
/// Oracle: bash 5.2 — a child inherits fd 0; one that never reads it exits at once and leaves the stream for
/// the next command (`printf 'a\nb\n' | bash -c 'true; cat'` → a b); one that reads it consumes what it read.
/// Regression: the old `feed | prog` form probed the stdin before starting the program, which blocked forever on
/// an open, silent stdin (an agent shell tool's), so every native command hung.
/// </summary>
public class NativeStdinBridgeTests : IClassFixture<SharedPwshFixture>
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly SharedPwshFixture _fixture;

    public NativeStdinBridgeTests(SharedPwshFixture fixture) => _fixture = fixture;

    // A program that never reads stdin, and one that reads it and prints the matching lines.
    private static readonly (string Name, string Args) Ignorer =
        OperatingSystem.IsWindows() ? ("cmd.exe", "/c echo ran") : ("/bin/echo", "ran");
    private static readonly (string Name, string Args) Reader =
        OperatingSystem.IsWindows() ? ("findstr.exe", "alp") : ("/usr/bin/grep", "alp");

    private static string Bridged((string Name, string Args) prog) =>
        "$h = Enter-BashNativeStdin '" + prog.Name + "'; try { & '" + prog.Name + "' " + prog.Args
        + " } finally { Exit-BashNativeStdin $h }";

    /// <summary>A cancellable source the test feeds by hand: silent until told otherwise.</summary>
    private sealed class HandFedSource : IStdinRecordSource
    {
        internal readonly BlockingCollection<object> Items = new();

        public bool TryReadNext(CancellationToken ct, out object? record)
        {
            try
            {
                if (Items.TryTake(out var item, Timeout.Infinite, ct)) { record = item; return true; }
            }
            catch (InvalidOperationException) { }
            record = null;
            return false;
        }

        public void Dispose() { }
    }

    private static string[] Invoke(PowerShell pwsh, string script)
    {
        var run = Task.Run(() => pwsh.AddScript(script).Invoke());
        Assert.True(run.Wait(Limit), $"did not finish within {Limit.TotalSeconds:0}s (hung on stdin?): {script}");
        pwsh.Commands.Clear();
        return run.Result.Select(o => o?.ToString()?.Trim() ?? "").Where(s => s.Length > 0).ToArray();
    }

    [Fact]
    public void ProgramThatIgnoresStdin_OpenSilentStdin_FinishesAndTakesNothing()
    {
        var pwsh = _fixture.AcquireFresh();
        var source = new HandFedSource();
        var cursor = new StdinCursor(source);
        pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", cursor);
        try
        {
            Assert.Equal(new[] { "ran" }, Invoke(pwsh, Bridged(Ignorer)));

            // Nothing was consumed: what arrives later is still the next record.
            source.Items.Add("later");
            source.Items.CompleteAdding();
            Assert.True(cursor.TryDequeue(CancellationToken.None, out var next));
            Assert.Equal("later", next);
        }
        finally
        {
            pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", null);
        }
    }

    [Fact]
    public void ProgramThatIgnoresStdin_PendingInput_IsPutBackForTheNextCommand()
    {
        var pwsh = _fixture.AcquireFresh();
        var queue = new Queue<object>(new object[] { "kept1", "kept2" });
        pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", queue);
        try
        {
            Invoke(pwsh, Bridged(Ignorer));

            Assert.Equal(new object[] { "kept1", "kept2" }, queue.ToArray());
        }
        finally
        {
            pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", null);
        }
    }

    [Fact]
    public void ProgramThatReadsStdin_GetsTheSharedStdinAndSeesEndOfInput()
    {
        var pwsh = _fixture.AcquireFresh();
        var source = new HandFedSource();
        source.Items.Add("alpha");
        source.Items.Add("beta");
        source.Items.Add("alpine");
        source.Items.CompleteAdding();
        pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", new StdinCursor(source));
        try
        {
            Assert.Equal(new[] { "alpha", "alpine" }, Invoke(pwsh, Bridged(Reader)));
        }
        finally
        {
            pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", null);
        }
    }

    [Fact]
    public void RecordBytes_KeepTheTerminatorTheRecordCarries()
    {
        Assert.Equal("a\n"u8.ToArray(), NativeStdinBridge.RecordBytes("a"));
        Assert.Equal("a"u8.ToArray(), NativeStdinBridge.RecordBytes(BashRuntime.TextRecord("a", unterminated: true)));
        Assert.Equal(new byte[] { 0xE9, (byte)'\n' }, NativeStdinBridge.RecordBytes(RawBytes.GetString(new byte[] { 0xE9 })));
    }

    [Fact]
    public void Enter_NameThatIsAFunction_InstallsNothing()
    {
        // A function of the same name runs instead of the program and reads its stdin through the pipeline;
        // its stdin must not be pumped away underneath it.
        var pwsh = _fixture.AcquireFresh();
        pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", new Queue<object>(new object[] { "x" }));
        try
        {
            Assert.Empty(Invoke(pwsh, "function psbnative_fn { 'fn' }; Enter-BashNativeStdin 'psbnative_fn'"));
        }
        finally
        {
            pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", null);
        }
    }

    [Fact]
    public void Enter_NoSharedStdin_InstallsNothing()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.Runspace.SessionStateProxy.SetVariable("__BashStdIn", null);

        Assert.Empty(Invoke(pwsh, "Enter-BashNativeStdin '" + Ignorer.Name + "'"));
    }

    [Fact]
    public void Exit_NullBridge_IsANoOp()
    {
        Assert.Empty(Invoke(_fixture.AcquireFresh(), "Exit-BashNativeStdin $null"));
    }
}
