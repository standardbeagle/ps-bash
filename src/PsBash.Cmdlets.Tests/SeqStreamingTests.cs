using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>seq</c> must not materialize its range: <c>seq 1 100000000 | head -n 1</c> has to return
/// at once (and in tiny memory) on both the fused lane and the unfused PowerShell pipeline.
/// Streaming is asserted by a wall-clock bound that a 100M-element List (~minutes, GBs) cannot meet.
/// </summary>
public class SeqStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public SeqStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private (string[] Lines, TimeSpan Elapsed) Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var sw = Stopwatch.StartNew();
        var task = Task.Run(() => pwsh.AddScript(script).Invoke());
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "pipeline did not terminate: seq is not streaming");
        sw.Stop();
        pwsh.Commands.Clear();
        return (task.Result.Select(o => o?.ToString() ?? "").ToArray(), sw.Elapsed);
    }

    [Fact]
    public void Seq_HugeRange_UnfusedPipeline_HeadStopsEarly()
    {
        var (lines, elapsed) = Run("Invoke-BashSeq 1 100000000 | Invoke-BashHead -n 1 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "1" }, lines);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
    }

    [Fact]
    public void Seq_HugeRange_FusedLane_HeadStopsEarly()
    {
        var (lines, elapsed) = Run(
            "Invoke-BashFusedPipeline { Invoke-BashSeq 1 100000000 | Invoke-BashHead -n 1 } | ForEach-Object { \"$_\" }");
        Assert.Equal(new[] { "1" }, lines.Select(l => l.Trim()).ToArray());
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
    }

    [Fact]
    public void Seq_FormattingUnchanged_WidthSeparatorFloat()
    {
        var (lines, _) = Run(
            "(Invoke-BashSeq -w 8 11 | Select-Object -Last 2 | ForEach-Object { $_.BashText }); " +
            "Invoke-BashSeq -s ',' 3; " +
            "(Invoke-BashSeq 0 0.25 1 | ForEach-Object { $_.BashText }) -join ' '");
        Assert.Equal(new[] { "10", "11", "1,2,3", "0.00 0.25 0.50 0.75 1.00" }, lines);
    }
}
