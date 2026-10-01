using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>grep</c> on a pipe must not collect its input: output appears while the producer still runs, an
/// early-satisfied grep (<c>-q</c>, <c>-m N</c>) stops the upstream, and the -B/-A context window is a
/// bounded ring/countdown. Goes through <c>Invoke-BashGrep</c> as the module exposes it (the psm1 proxy in
/// front of the cmdlet), so both layers must stream.
/// </summary>
public class GrepStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public GrepStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var task = Task.Run(() => pwsh.AddScript(script).Invoke());
        Assert.True(task.Wait(TimeSpan.FromSeconds(60)), "grep did not terminate (upstream not stopped?)");
        pwsh.Commands.Clear();
        return task.Result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Plain_EmitsWhileProducerIsStillRunning()
    {
        // P<n> = producer emitted n, T<n> = grep's output reached the consumer. A collect-then-match
        // grep logs P1..P4 before any T.
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..4 | ForEach-Object { $log.Add(\"P$_\"); \"line$_\" } } | Invoke-BashGrep line | " +
            "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '");
        Assert.Equal(new[] { "P1 Tline1 P2 Tline2 P3 Tline3 P4 Tline4" }, log);
    }

    [Fact]
    public void MaxCount_StopsTheUpstream()
    {
        // An endless producer: grep -m 2 must end the pipeline after the 2nd match. The producer
        // counter proves it was cut off early rather than drained.
        var r = Run(
            "$g = Get-Command Invoke-BashGrep -CommandType Cmdlet; $global:gn = 0; " +
            "$out = @(& { while ($true) { $global:gn++; \"x$($global:gn)\" } } | & $g -m 2 x | ForEach-Object { $_.ToString().Trim() }); " +
            "($out -join ',') + ' ' + ($global:gn -lt 50) + ' ' + $LASTEXITCODE");
        Assert.Equal(new[] { "x1,x2 True 0" }, r);
    }

    [Fact]
    public void Quiet_StopsTheUpstream_AndPrintsNothing()
    {
        var r = Run(
            "$g = Get-Command Invoke-BashGrep -CommandType Cmdlet; $global:gn = 0; " +
            "$out = @(& { while ($true) { $global:gn++; \"x$($global:gn)\" } } | & $g -q x3); " +
            "$out.Count.ToString() + ' ' + ($global:gn -lt 50) + ' ' + $LASTEXITCODE");
        Assert.Equal(new[] { "0 True 0" }, r);
    }

    [Fact]
    public void Proxy_MaxCount_DrainsInsteadOfKillingTheStatement()
    {
        // Behind the psm1 proxy the cmdlet cannot stop the caller's upstream (the signal would be routed to
        // the proxy's inner pipeline and kill the statement); it must just ignore the rest of its input.
        var r = Run("$o = @(1..50 | ForEach-Object { \"x$_\" } | Invoke-BashGrep -m 2 x | ForEach-Object { $_.ToString().Trim() }); ($o -join ',') + ' ' + $LASTEXITCODE");
        Assert.Equal(new[] { "x1,x2 0" }, r);
    }

    [Fact]
    public void Proxy_DownstreamHead_StopsTheStreamingChain()
    {
        var r = Run(
            "$global:gn = 0; " +
            "$o = @(& { while ($true) { $global:gn++; \"x$($global:gn)\" } } | Invoke-BashGrep x | Invoke-BashHead -n 2 | ForEach-Object { $_.ToString().Trim() }); " +
            "($o -join ',') + ' ' + ($global:gn -lt 50)");
        Assert.Equal(new[] { "x1,x2 True" }, r);
    }

    [Fact]
    public void Quiet_NoMatch_ExitsOne()
    {
        var r = Run("'a','b' | Invoke-BashGrep -q z; $LASTEXITCODE");
        Assert.Equal(new[] { "1" }, r);
    }

    [Fact]
    public void Count_StreamsAndCountsEverything()
    {
        var r = Run("1..1000 | ForEach-Object { \"n$_\" } | Invoke-BashGrep -c '5' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "271" }, r);
    }

    [Fact]
    public void MaxCountZero_SelectsNothing_ExitsOne()
    {
        var r = Run("$o = @('a','a' | Invoke-BashGrep -m 0 a); $o.Count.ToString() + ' ' + $LASTEXITCODE");
        Assert.Equal(new[] { "0 1" }, r);
    }

    [Fact]
    public void Plain_StillPassesOriginalObjects()
    {
        var r = Run("$o = New-BashObject -BashText 'keep me'; $r = @($o | Invoke-BashGrep keep); [object]::ReferenceEquals($r[0], $o).ToString() + ' ' + $r.Count");
        Assert.Equal(new[] { "True 1" }, r);
    }

    [Fact]
    public void Context_Before_UsesRing_AndSeparatesGroups()
    {
        var r = Run("1..20 | ForEach-Object { \"l$_\" } | Invoke-BashGrep -n -B 1 -e 'l5$' -e 'l15$' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "4-l4", "5:l5", "--", "14-l14", "15:l15" }, r);
    }

    [Fact]
    public void Context_After_MergesOverlappingWindows()
    {
        var r = Run("1..10 | ForEach-Object { \"l$_\" } | Invoke-BashGrep -A 2 -e 'l2$' -e 'l4$' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "l2", "l3", "l4", "l5", "l6" }, r);
    }

    [Fact]
    public void Context_After_EmitsWhileProducerIsStillRunning()
    {
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..4 | ForEach-Object { $log.Add(\"P$_\"); \"l$_\" } } | Invoke-BashGrep -A 1 'l1' | " +
            "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '");
        // the match prints at once; its context line prints as soon as the next record arrives
        Assert.Equal(new[] { "P1 Tl1 P2 Tl2 P3 P4" }, log);
    }

    [Fact]
    public void Context_MaxCount_KeepsTrailingWindowOfTheLastMatch_ThenStops()
    {
        var r = Run(
            "$g = Get-Command Invoke-BashGrep -CommandType Cmdlet; $global:gn = 0; " +
            "$o = @(& { while ($true) { $global:gn++; \"m$($global:gn)\" } } | & $g -m 1 -A 2 m | ForEach-Object { $_.ToString().Trim() }); " +
            "($o -join ',') + ' ' + ($global:gn -lt 50)");
        Assert.Equal(new[] { "m1,m2,m3 True" }, r);
    }

    [Fact]
    public void Context_Before_ManyRecords_OnlyTheWindow()
    {
        var r = Run("1..300000 | ForEach-Object { \"r$_\" } | Invoke-BashGrep -B 2 'r300000$' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "r299998", "r299999", "r300000" }, r);
    }

    [Fact]
    public void OnlyMatching_WithNumbers_Streams()
    {
        var r = Run("'a1b22','zz','c333' | Invoke-BashGrep -E -on '[0-9]+' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "1:1", "1:22", "3:333" }, r);
    }
}
