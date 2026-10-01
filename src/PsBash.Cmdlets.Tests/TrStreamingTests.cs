using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>tr</c> STREAMS whatever its table. A table that deletes / translates / squeezes the record
/// terminator used to buffer the WHOLE input (so the answer could be one exact-bytes record that
/// <c>$(...)</c> capture joined correctly), which made <c>tail -f log | tr '\n' ' '</c> print nothing and
/// <c>yes | tr '\n' x | head -c 5</c> never finish. Now each record is transformed as it arrives, squeeze
/// state carries over record boundaries, and command-substitution capture
/// (<see cref="ConvertToBashCaptureCommand"/>) glues exact-bytes records, so the bytes are the same.
/// </summary>
public class TrStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public TrStreamingTests(SharedPwshFixture fixture) => _fixture = fixture;

    /// <summary>Runs a script that must finish; a hang is stopped and reported instead of wedging the suite.</summary>
    private string RunBounded(string script, int seconds = 45)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script);
        var async = pwsh.BeginInvoke();
        if (!async.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))
        {
            pwsh.Stop();
            pwsh.Commands.Clear();
            Assert.Fail($"pipeline did not finish within {seconds}s: {script}");
        }
        var result = pwsh.EndInvoke(async);
        pwsh.Commands.Clear();
        return string.Join("\n", result.Select(o => o?.Properties["BashText"]?.Value?.ToString() ?? o?.ToString() ?? ""));
    }

    [Fact]
    public void NewlineTable_EmitsEachRecordBeforeTheProducerContinues()
    {
        // Downstream runs between the producer's emits, so the log order shows whether tr held records back.
        var script = @"
$global:trlog = [System.Collections.Generic.List[string]]::new()
& { 'a'; $global:trlog.Add('produced-a'); 'b'; $global:trlog.Add('produced-b') } |
    Invoke-BashTr '\n' ',' | ForEach-Object { $global:trlog.Add('got:' + (Get-BashText $_)) }
$global:trlog -join '|'";
        Assert.Equal("got:a,|produced-a|got:b,|produced-b", RunBounded(script));
    }

    [Fact]
    public void DeleteNewline_StreamsToo()
    {
        var script = @"
$global:trlog = [System.Collections.Generic.List[string]]::new()
& { 'a'; $global:trlog.Add('p1'); 'b'; $global:trlog.Add('p2') } |
    Invoke-BashTr '-d' '\n' | ForEach-Object { $global:trlog.Add('got:' + (Get-BashText $_)) }
$global:trlog -join '|'";
        Assert.Equal("got:a|p1|got:b|p2", RunBounded(script));
    }

    [Fact]
    public void YesThroughNewlineTable_ThenHeadC_Terminates()
    {
        Assert.Equal("yxyxy", RunBounded(
            "$r = @(& { Invoke-BashYes } | Invoke-BashTr '\\n' 'x' | Invoke-BashHead '-c' 5); ($r | ForEach-Object { Get-BashText $_ }) -join ''"));
    }

    [Fact]
    public void SqueezeNewline_CarriesRunsAcrossRecords()
    {
        // GNU: printf 'a\n\n\nb\n' | tr -s '\n'  ->  "a\nb\n"
        Assert.Equal("a\nb", RunBounded("'a','','','b' | Invoke-BashTr '-s' '\\n'"));
    }

    [Fact]
    public void TranslateThenSqueeze_CarriesRunsAcrossRecords()
    {
        // printf 'a\n\n\nb\n' | tr -s '\n' ' '  ->  "a b " : the three newlines become ONE space.
        var script = "$r = @('a','','','b' | Invoke-BashTr '-s' '\\n' ' '); ($r | ForEach-Object { Get-BashText $_ }) -join ''";
        Assert.Equal("a b ", RunBounded(script));
    }

    [Fact]
    public void DeleteNewline_RecordsConcatenateExactly()
    {
        Assert.Equal("123", RunBounded("$r = @(1..3 | ForEach-Object { \"$_\" } | Invoke-BashTr '-d' '\\n'); ($r | ForEach-Object { Get-BashText $_ }) -join ''"));
    }

    [Fact]
    public void ConvertToBashCapture_GluesExactRecords_AndKeepsTerminatedOnes()
    {
        var script = @"
$exact = { param($t) [PsBash.Cmdlets.BashRuntime]::NewBashObject($t, 'PsBash.TextOutput', $true) }
$items = @((& $exact 'a,'), (& $exact 'b,'), 'c', (& $exact 'd'))
($items | ConvertTo-BashCapture) -join '|'";
        // a,b,c is one logical line; the trailing exact 'd' is flushed at the end.
        Assert.Equal("a,b,c|d", RunBounded(script));
    }
}
