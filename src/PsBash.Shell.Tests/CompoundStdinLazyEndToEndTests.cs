using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Compound stdin scope over an UNBOUNDED producer: `yes | { head -n1; }` must terminate (bash: SIGPIPE).
/// Oracle: bash 5.2 (`yes | { head -n1; }` = y, `yes | { read a; read b; echo "$a$b"; }` = yy).
/// </summary>
[Trait("Category", "Integration")]
public class CompoundStdinLazyEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

    private static async Task<(int Exit, string Out)> RunAsync(string script)
    {
        var psi = PsBashTestProcess.Create(["-c", script], ipcEndpoint: IpcEndpoint);
        var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, timeout: TimeSpan.FromSeconds(60));
        return (exit, stdout.Replace("\r\n", "\n"));
    }

    [SkippableTheory]
    [InlineData("yes | { head -n1; }", "y\n")]
    [InlineData("yes abc | { read x; echo got=$x; }", "got=abc\n")]
    [InlineData("yes | ( head -n2 )", "y\ny\n")]
    [InlineData("seq 1 100000000 | { head -n2; }", "1\n2\n")]
    [InlineData("yes | { read a; read b; echo \"$a$b\"; }", "yy\n")]
    public async Task UnboundedProducer_IntoACompoundStage_TerminatesAndFeedsTheReader(string script, string expected)
    {
        var (exit, stdout) = await RunAsync(script);
        Assert.Equal(0, exit);
        Assert.Equal(expected, stdout);
    }

    [SkippableFact]
    public async Task FiniteProducer_IntoACompoundStage_StillSeesEveryRecordInOrder()
    {
        var (_, stdout) = await RunAsync("printf 'b\\na\\nc\\n' | { read x; echo first=$x; sort; }");
        Assert.Equal("first=b\na\nc\n", stdout);
    }
}
