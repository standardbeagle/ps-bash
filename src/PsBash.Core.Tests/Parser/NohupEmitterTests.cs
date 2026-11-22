using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Parser;

public class NohupEmitterTests
{
    private static string Emit(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Background_nohup_is_not_a_runspace_job()
    {
        var ps = Emit("nohup sleep 5 > log.txt 2>&1 &");
        Assert.Contains("Invoke-BashNohup", ps);
        Assert.DoesNotContain("Invoke-BashBackground", ps);
        Assert.Contains("--psbash-out", ps);
        Assert.Contains("--psbash-err", ps);
    }

    [Fact] public void Plain_nohup_maps_to_the_cmdlet() =>
        Assert.Contains("Invoke-BashNohup", Emit("nohup sleep 5"));

    // Only `nohup CMD &` returns before CMD ends; a foreground nohup waits and takes CMD's status (GNU), so it carries
    // no --psbash-bg, with or without a redirect, standalone or as a pipe stage.
    [Theory]
    [InlineData("nohup sleep 5 &")]
    [InlineData("nohup sleep 5 > log.txt &")]
    [InlineData("nohup sleep 5 2>/dev/null &")]
    public void Transpile_BackgroundNohup_IsMarkedBackground(string bash) =>
        Assert.Contains("Invoke-BashNohup '--psbash-bg'", Emit(bash));

    [Theory]
    [InlineData("nohup sleep 5")]
    [InlineData("nohup ls /x > out.txt 2>&1")]
    [InlineData("nohup true | cat")]
    [InlineData("x=$(nohup echo hi)")]
    public void Transpile_ForegroundNohup_IsNotMarkedBackground(string bash) =>
        Assert.DoesNotContain("--psbash-bg", Emit(bash));

    // Class: the stderr file of a detached nohup child was opened one way for `2>` and `2>>` (never truncated on
    // either OS). The emitter says which: `2>>` adds --psbash-err-append, `2>`/`2>|` do not.
    [Theory]
    [InlineData("nohup ls /x > o.txt 2>> e.txt", true)]
    [InlineData("nohup ls /x > o.txt 2>> e.txt &", true)]
    [InlineData("nohup ls /x > o.txt 2> e.txt", false)]
    [InlineData("nohup ls /x >> o.txt 2> e.txt", false)]
    [InlineData("nohup ls /x > o.txt 2>| e.txt", false)]
    public void Transpile_NohupStderrFile_SaysWhetherItAppends(string bash, bool appends)
    {
        var ps = Emit(bash);
        Assert.Contains("'--psbash-err'", ps);
        Assert.Equal(appends, ps.Contains("'--psbash-err-append'", System.StringComparison.Ordinal));
    }
}
