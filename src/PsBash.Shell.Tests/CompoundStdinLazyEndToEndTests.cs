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

    [SkippableTheory]
    [InlineData("seq 3 | ( while read f; do :; done; echo done )", "done\n")]
    [InlineData("seq 3 | { while read f; do echo got$f; done; echo end; }", "got1\ngot2\ngot3\nend\n")]
    [InlineData("i=0; seq 3 | ( while read f; do i=$((i+1)); done; echo $i )", "3\n")]
    [InlineData("seq 3 | { cat; echo end; }", "1\n2\n3\nend\n")]
    public async Task FiniteLazyProducer_DrainedToEndOfInput_Terminates(string script, string expected)
    {
        // The lazy producer's output collection was never completed when the producer finished, so a reader
        // that runs to EOF (not just `head`/`read`) waited forever — oils loop.spec "while in pipe with subshell".
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
