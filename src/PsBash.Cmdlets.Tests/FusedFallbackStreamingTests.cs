using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The fused lane's FALLBACK (a stage's core declined, so the inner PowerShell pipeline runs) streams its
/// output in ~32 KiB frames as they fill, instead of holding <c>InvokeScript</c>'s whole result Collection
/// until the pipeline ends. Asserted by ORDER (a frame reaches the consumer while the producer is still
/// running) and by the exact rendered bytes.
/// </summary>
public class FusedFallbackStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public FusedFallbackStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        // A sink-instance fault used to surface only as an error record (the output still arrived).
        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Fallback_WritesAFrameAsSoonAsOneIsFull_WhileTheProducerIsStillRunning()
    {
        // Each record is 20000 chars; a frame is cut at >= 32768 chars, so every second record fills one.
        // A collect-everything fallback logs P1..P6 before the first T.
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "Invoke-BashFusedPipeline { 1..6 | ForEach-Object { $log.Add(\"P$_\"); ('x' * 20000) } | Invoke-BashCat } | " +
            "ForEach-Object { $log.Add('T') }; $log -join ' '");
        Assert.Equal("P1 P2 T P3 P4 T P5 P6 T", log[0]);
    }

    [Fact]
    public void Fallback_RendersTerminatedAndUnterminatedRecords_ExactlyLikeTheHostSerializer()
    {
        // printf's multi-line record and `echo -n` carry NoTrailingNewline (no boundary); a bare string gets
        // Environment.NewLine. One frame holds the lot.
        var got = Run(
            "$f = @(Invoke-BashFusedPipeline { Invoke-BashPrintf 'a\\nb'; 'plain'; Invoke-BashEcho '-n' 'z' }); " +
            "$f.Count.ToString() + ' ' + (($f[0].BashText -replace \"`r\", '<CR>') -replace \"`n\", '<LF>')");
        string nl = Environment.NewLine.Replace("\r", "<CR>").Replace("\n", "<LF>");
        Assert.Equal($"1 a<LF>bplain{nl}z", got[0]);
    }

    [Fact]
    public void Fallback_LastExitCode_IsTheInnerPipelinesLastStage()
    {
        var got = Run(
            "$null = Invoke-BashFusedPipeline { 'abc' | Invoke-BashGrep 'zzz' }; \"$($global:LASTEXITCODE)\"");
        Assert.Equal("1", got[0]);
    }

    [Fact]
    public void Fallback_StopsTheProducer_WhenTheConsumerStopsEarly()
    {
        // Select-Object -First 1 stops the pipeline after the first frame: the producer must not run to the end.
        var got = Run(
            "$global:produced = 0; " +
            "$first = Invoke-BashFusedPipeline { 1..5000 | ForEach-Object { $global:produced++; ('y' * 20000) } | Invoke-BashCat } | Select-Object -First 1; " +
            "\"$($global:produced -lt 5000) $($null -ne $first)\"");
        Assert.Equal("True True", got[0]);
    }
}
