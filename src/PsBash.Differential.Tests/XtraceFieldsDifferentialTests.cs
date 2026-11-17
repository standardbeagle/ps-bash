using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>set -x</c> traces the words a command RUNS with, after expansion: an unquoted word that expands to nothing is no
/// field (<c>$unset -c 'echo 2'</c> traces <c>+ -c 'echo 2'</c>; it traced <c>+ '' -c 'echo 2'</c>, oils
/// sh-options-bash "xtrace"), a split word is its fields, a quoted empty word stays <c>''</c>, an empty <c>"$@"</c>
/// vanishes, and an assignment traces <c>name=value</c>. Recorded from bash 5.2 (cassette).
/// </summary>
public class XtraceFieldsDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";

    [SkippableFact]
    public Task Xtrace_TracesTheExpandedFields()
        => AssertOracle.EqualAsync(Work + "{\n"
            + "arr=(); ar2=(1 \"2 3\"); sp=\"p q\"\n"
            + "f() { $unset echo infn \"$@\"; }\n"
            + "set -x\n"
            + "$unset -c 'echo 2'\n"
            + "echo a $unset b\n"
            + "echo \"\" x \"$unset\"\n"
            + "echo \"$@\" y\n"
            + "echo \"${arr[@]}\" z\n"
            + "echo $sp\n"
            + "echo \"${ar2[@]}\" ${ar2[@]}\n"
            + "f\n"
            + "v=$unset\n"
            + "w=\"$sp\"\n"
            + ": ${unset}\n"
            + "echo $unset\"\" q\n"
            + "set +x\n"
            + "} 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));
}
