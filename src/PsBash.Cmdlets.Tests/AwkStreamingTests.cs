using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>awk</c> on a pipe feeds records to the machine as they arrive (BEGIN before the first, END
/// after the last) instead of collecting stdin. Streaming is proven by interleaving with the
/// producer: P<i>n</i> = producer emitted n, T<i>n</i> = awk's output reached the consumer.
/// </summary>
public class AwkStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public AwkStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return string.Join("|", result.Select(o => o?.ToString() ?? ""));
    }

    private const string Consumer =
        "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '";

    [Fact]
    public void Awk_PerRecordRules_EmitWhileProducerIsStillRunning()
    {
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..3 | ForEach-Object { $log.Add(\"P$_\"); \"$_\" } } | Invoke-BashAwk '{ print $1 * 10 }' | " + Consumer);
        Assert.Equal("P1 T10 P2 T20 P3 T30", log);
    }

    [Fact]
    public void Awk_BeginRunsBeforeFirstRecord_EndAfterLast()
    {
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..2 | ForEach-Object { $log.Add(\"P$_\"); \"$_\" } } | " +
            "Invoke-BashAwk 'BEGIN { print \"b\" } { s += $1 } END { print \"e\" s }' | " + Consumer);
        Assert.Equal("Tb P1 P2 Te3", log);
    }

    [Fact]
    public void Awk_ExitInBegin_ConsumesNothing_AndSkipsRules()
    {
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..2 | ForEach-Object { $log.Add(\"P$_\"); \"$_\" } } | " +
            "Invoke-BashAwk 'BEGIN { print \"only\"; exit } { print \"rule\" }' | " + Consumer);
        Assert.Equal("Tonly P1 P2", log);
    }

    [Fact]
    public void Awk_NrAndFields_UnchangedOverManyRecords()
    {
        var o = Run("1..100000 | ForEach-Object { \"a $_\" } | Invoke-BashAwk 'END { print NR, $2 }' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal("100000 100000", o);
    }
}
