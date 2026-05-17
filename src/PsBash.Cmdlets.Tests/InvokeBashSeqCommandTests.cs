using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashSeq</c> from PsBash.psm1 to a binary cmdlet
/// (<see cref="PsBash.Cmdlets.InvokeBashSeqCommand"/>).
///
/// Oracle: the psm1 function. Tests cover the three operand forms
/// (one / two / three values), <c>-s</c> separator, <c>-w</c> zero-pad,
/// decimal step, alias resolution, <c>--help</c>, and the Directive-12
/// injection probe.
/// </summary>
public class InvokeBashSeqCommandTests
{
    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Seq_OneArg_EmitsOneToLast()
    {
        var lines = RunLines("Invoke-BashSeq 5 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, lines);
    }

    [Fact]
    public void Seq_TwoArgs_EmitsFirstToLast()
    {
        var lines = RunLines("Invoke-BashSeq 2 5 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "2", "3", "4", "5" }, lines);
    }

    [Fact]
    public void Seq_ThreeArgs_HonorsStep()
    {
        var lines = RunLines("Invoke-BashSeq 1 2 9 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "1", "3", "5", "7", "9" }, lines);
    }

    [Fact]
    public void Seq_NegativeStep_Descends()
    {
        var lines = RunLines("Invoke-BashSeq 5 -1 1 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "5", "4", "3", "2", "1" }, lines);
    }

    [Fact]
    public void Seq_SeparatorFlag_JoinsValues()
    {
        // -s emits a single joined string (TextOutput fast path), no per-value
        // typed object. Match the oracle's join-with-separator output shape.
        var lines = RunLines("Invoke-BashSeq -s ',' 3");
        Assert.Single(lines);
        Assert.Equal("1,2,3", lines[0]);
    }

    [Fact]
    public void Seq_SeparatorLongFormEquals_JoinsValues()
    {
        var lines = RunLines("Invoke-BashSeq --separator=- 3");
        Assert.Single(lines);
        Assert.Equal("1-2-3", lines[0]);
    }

    [Fact]
    public void Seq_EqualWidth_ZeroPadsIntegers()
    {
        // padWidth = max(|first|,|last|).ToString().Length — for 1..10 → 2.
        var lines = RunLines("Invoke-BashSeq -w 1 10 | ForEach-Object { $_.BashText }");
        Assert.Equal(
            new[] { "01", "02", "03", "04", "05", "06", "07", "08", "09", "10" },
            lines);
    }

    [Fact]
    public void Seq_DecimalStep_FormatsWithCommonDecimalPlaces()
    {
        // Oracle: when any operand contains '.', max-decimal-places across
        // operands determines the FN format. Here "0.5" → 1 decimal place.
        var lines = RunLines("Invoke-BashSeq 1 0.5 2 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "1.0", "1.5", "2.0" }, lines);
    }

    [Fact]
    public void Seq_Empty_NoArgs_EmitsSingleOne()
    {
        // Oracle defaults: first=1, increment=1, last=1 → emit "1".
        var lines = RunLines("Invoke-BashSeq | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "1" }, lines);
    }

    [Fact]
    public void Seq_TypedOutputCarriesPsBashSeqOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "(Invoke-BashSeq 3)[0].PSTypeNames -join ','").Invoke();
        Assert.Single(result);
        var types = result[0]?.ToString() ?? "";
        Assert.Contains("PsBash.SeqOutput", types);
    }

    [Fact]
    public void Seq_TypedOutput_ExposesValueAndIndex()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "(Invoke-BashSeq 3) | ForEach-Object { \"$($_.Index):$($_.Value)\" }").Invoke();
        Assert.Equal(3, result.Count);
        Assert.Equal("0:1", result[0]?.ToString());
        Assert.Equal("1:2", result[1]?.ToString());
        Assert.Equal("2:3", result[2]?.ToString());
    }

    [Fact]
    public void Seq_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashSeq --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("seq", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Seq_ViaAlias_Works()
    {
        var lines = RunLines("seq 3 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "1", "2", "3" }, lines);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Seq_SeparatorWithScriptblockChars_TreatedAsLiteral()
    {
        // A separator string containing PowerShell scriptblock + $() chars
        // must be emitted as a literal string between values, never
        // evaluated. The joined output stays a single literal string.
        var lines = RunLines("Invoke-BashSeq -s '$(throw \"pwn\")' 2");
        Assert.Single(lines);
        Assert.Equal("1$(throw \"pwn\")2", lines[0]);
    }
}
