using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>tail</c> on a pipe must not collect its input. <c>-n +N</c> / <c>-c +N</c> stream (output is
/// observed interleaved with the producer), <c>-n N</c> / <c>-c N</c> keep O(N) rings (a huge N
/// must not preallocate, and results are unchanged).
/// </summary>
public class TailStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public TailStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void TailFromLine_EmitsWhileProducerIsStillRunning()
    {
        // P<n> = producer emitted n, T<n> = tail's output reached the consumer. A collect-then-emit
        // tail would log P1..P5 before any T.
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..5 | ForEach-Object { $log.Add(\"P$_\"); \"$_\" } } | Invoke-BashTail -n +2 | " +
            "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '");
        Assert.Equal(new[] { "P1 P2 T2 P3 T3 P4 T4 P5 T5" }, log);
    }

    [Fact]
    public void TailFromByte_EmitsWhileProducerIsStillRunning()
    {
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..3 | ForEach-Object { $log.Add(\"P$_\"); \"$_\" } } | Invoke-BashTail -c +3 | " +
            "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '");
        // stream "1\n2\n3\n": byte 3 is '2'
        Assert.Equal(new[] { "P1 P2 T2 P3 T3" }, log);
    }

    [Fact]
    public void TailLines_HugeCount_DoesNotPreallocate()
    {
        // The old ring was `new object[count]`: 2e9 slots = 16 GB, an OutOfMemory for 10 lines.
        var lines = Run("1..10 | ForEach-Object { \"l$_\" } | Invoke-BashTail -n 2000000000 | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(Enumerable.Range(1, 10).Select(i => "l" + i).ToArray(), lines);
    }

    [Fact]
    public void TailLines_Ring_KeepsLastN_OverManyRecords()
    {
        var lines = Run("1..200000 | ForEach-Object { \"l$_\" } | Invoke-BashTail -n 3 | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "l199998", "l199999", "l200000" }, lines);
    }

    [Fact]
    public void TailBytes_Ring_KeepsLastNBytes_AcrossRecords()
    {
        // stream: "aaa\nbbb\nccc\n" -> last 6 bytes = "b\nccc\n"
        var lines = Run("'aaa','bbb','ccc' | Invoke-BashTail -c 6 | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "b", "ccc" }, lines);
    }

    [Fact]
    public void TailBytes_Ring_ManyRecords_OnlyTheTail()
    {
        var lines = Run("1..100000 | ForEach-Object { \"row$_\" } | Invoke-BashTail -c 9 | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "100000" }, lines.Select(l => l[^6..]).ToArray());
    }
}
