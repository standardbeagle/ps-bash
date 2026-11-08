using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`echo a | seq 2` / `sleep 0.1 | seq 3`: a command that ignores stdin must still run once with its upstream drained
/// (its cmdlet has no pipeline-input parameter, so PowerShell would reject or never run it).</summary>
public class StdinIgnoringPipeStageTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("echo a | seq 2")]
    [InlineData("sleep 0.1 | seq 3")]
    [InlineData("echo a | pwd")]
    public void Transpile_StdinIgnoringStage_DrainsUpstreamThenRunsOnce(string bash)
        => Assert.Contains("& { $null = @($input); ", T(bash));

    [Theory]
    [InlineData("seq 3 | cat")]          // first stage: nothing to drain
    [InlineData("echo a | grep a")]      // reads stdin
    public void Transpile_OtherStages_AreNotWrapped(string bash)
        => Assert.DoesNotContain("$null = @($input)", T(bash));
}
