using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU <c>timeout DURATION COMMAND</c>. Windows ships its own timeout.exe (a keypress wait), so the name must reach
/// the cmdlet. Oracle: coreutils timeout 9.x (exit 124 on expiry, 125 for a usage error, the command's status otherwise).
/// </summary>
public class InvokeBashTimeoutCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashTimeoutCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Theory]
    [InlineData("5", 5.0)]
    [InlineData("0.5", 0.5)]
    [InlineData("2s", 2.0)]
    [InlineData("1m", 60.0)]
    [InlineData("1h", 3600.0)]
    [InlineData("1d", 86400.0)]
    [InlineData("0", 0.0)]
    public void ParseDuration_AcceptsNumbersWithAnOptionalUnit(string text, double seconds)
        => Assert.Equal(seconds, InvokeBashTimeoutCommand.ParseDuration(text)!.Value.TotalSeconds, 3);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("5x")]
    [InlineData("NaN")]
    public void ParseDuration_RejectsMalformedIntervals(string text)
        => Assert.Null(InvokeBashTimeoutCommand.ParseDuration(text));

    [Fact]
    public void Parse_OptionsThenDurationThenCommand_KeepsTheCommandVerbatim()
    {
        Assert.True(InvokeBashTimeoutCommand.Parse(new[] { "--preserve-status", "-k", "1", "-s", "KILL", "2", "grep", "-v", "x" }, out var o, out _));
        Assert.True(o.PreserveStatus);
        Assert.Equal(new[] { "grep", "-v", "x" }, o.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("abc true")]
    [InlineData("--bogus 5 true")]
    public void Parse_UsageErrors_AreRefused(string args)
        => Assert.False(InvokeBashTimeoutCommand.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), out _, out _));

    [Theory]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(whoami)", "'$(whoami)'")]
    [InlineData("plain-word_1.txt", "plain-word_1.txt")]
    [InlineData("", "''")]
    public void ShellQuote_QuotesOnlyWhatNeedsIt(string word, string quoted)
        => Assert.Equal(quoted, InvokeBashTimeoutCommand.ShellQuote(word));

    [Fact]
    public void Run_CommandFinishesInTime_PassesItsOutputAndStatus()
        => Run("Invoke-BashTimeout 10 echo 'a b'").AssertSuccess();

    [Fact]
    public void Run_CommandOutlastsTheBudget_ExitsWith124()
        => Run("Invoke-BashTimeout 0.3 sleep 10").AssertFailed(124, "");

    [Fact]
    public void Run_PreserveStatus_ReportsTheTerminationSignalStatus()
        => Run("Invoke-BashTimeout '--preserve-status' 0.3 sleep 10").AssertFailed(143, "");

    [Fact]
    public void Run_UsageError_Is125()
        => Run("Invoke-BashTimeout abc true").AssertFailed(125, "timeout: invalid time interval");

    [Fact]
    public void Run_InjectionInTheOperand_StaysALiteralArgument()
        => Run("Invoke-BashTimeout 10 echo '$(exit 9); `id`'").AssertSuccess();
}