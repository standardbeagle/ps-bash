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
}
