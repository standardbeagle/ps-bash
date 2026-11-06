using PsBash.Testing;
using Xunit;

namespace PsBash.Differential.Tests;

public class TmpMacProbeTests
{
    [SkippableFact]
    public async Task Probe()
    {
        var runner = PsBashRunner.Create().WithTimeout(120);
        Skip.If(runner.TryResolveBinary() is null, "no binary");
        var script = """
            ms() { echo $(( $(date +%s%N) / 1000000 )); }
            d=$(mktemp -d); cd "$d"; printf '012345678\n' > f
            t=$(ms); split -n 700 f; echo "split $(( $(ms) - t ))ms files=$(ls | wc -l)"
            t=$(ms); find . > /dev/null; echo "find $(( $(ms) - t ))ms"
            t=$(ms); find . | LC_ALL=C sort > /dev/null; echo "find|sort $(( $(ms) - t ))ms"
            t=$(ms); for p in ./x*; do :; done; echo "loop $(( $(ms) - t ))ms"
            t=$(ms); for p in ./x*; do [ -d "$p" ]; done; echo "test-d $(( $(ms) - t ))ms"
            t=$(ms); for p in ./x*; do base64 -w0 "$p" > /dev/null; done; echo "base64 $(( $(ms) - t ))ms"
            t=$(ms); for p in ./x*; do v=$(base64 -w0 "$p"); done; echo "cmdsub-base64 $(( $(ms) - t ))ms"
            t=$(ms); for p in ./x*; do v=$(echo hi); done; echo "cmdsub-echo $(( $(ms) - t ))ms"
            t=$(ms); find . | LC_ALL=C sort | while IFS= read -r p; do :; done; echo "readloop $(( $(ms) - t ))ms"
            cd /; rm -rf "$d"
            """;
        var r = await runner.RunScriptAsync(script);
        Assert.Fail($"exit={r.ExitCode} wall={r.WallMs}\n{r.Stdout}\n--stderr--\n{r.Stderr}");
    }
}
