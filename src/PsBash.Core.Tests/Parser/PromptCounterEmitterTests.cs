using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// What the emitted script tells the runtime so that prompt <c>\j</c>, <c>\#</c>, <c>\!</c> and <c>fg</c> can be bash's
/// (oils prompt.spec "\j for number of jobs", "\! for history number", "\# for command number"). Hand-written: the
/// report calls and the job's <c>-Text</c> are ps-bash surface; the behaviour is oracle-checked by
/// <c>Differential.Tests/BackgroundJobDifferentialTests</c> and the spec cases.
/// </summary>
public class PromptCounterEmitterTests
{
    private static string Emit(string bash) => PsEmitter.Transpile(bash)!;

    // fg prints the job's command as written; the emitted job carried no text, so fg could only say "bash-bg".
    [Theory]
    [InlineData("sleep 5 &", "sleep 5")]
    [InlineData("{ sleep 2; echo x; } &", "{ sleep 2; echo x; }")]
    [InlineData("echo \"it's\" $(date) &", "echo \"it's\" $(date)")]
    [InlineData("for i in 1 2; do sleep $i & done", "sleep $i")]
    public void Parse_BackgroundJob_KeepsItsCommandAsWritten(string bash, string expected)
    {
        var job = FindJob(BashParser.Parse(bash)!);
        Assert.Equal(expected, job.Source);
    }

    [Fact]
    public void Transpile_BackgroundJob_PassesItsTextSingleQuoted()
        => Assert.EndsWith(" -Text 'echo \"it''s\"'", Emit("echo \"it's\" &"));

    [Fact]
    public void Parse_BackgroundJobText_IsNotPartOfNodeEquality()
    {
        var job = (Command.Background)BashParser.Parse("sleep 1 &")!;
        Assert.Equal(new Command.Background(job.Inner), job);
        Assert.Equal(new Command.Background(job.Inner).GetHashCode(), job.GetHashCode());
    }

    private static Command.Background FindJob(Command c) => c switch
    {
        Command.Background b => b,
        Command.ForIn f => FindJob(f.Body),
        Command.CommandList l => l.Commands.Select(TryFind).First(x => x is not null)!,
        _ => throw new InvalidOperationException("no job in " + c.GetType().Name),
    };

    private static Command.Background? TryFind(Command c)
    {
        try { return FindJob(c); } catch (InvalidOperationException) { return null; }
    }
}
