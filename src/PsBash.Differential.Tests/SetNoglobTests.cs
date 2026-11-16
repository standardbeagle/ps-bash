using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>set -f</c> / <c>set -o noglob</c> were refused at parse time.</summary>
public class SetNoglobTests
{
    private const string Setup = "d=$(mktemp -d); cd $d; touch a.txt b.txt; ";
    private const string Teardown = "; cd /; rm -rf $d";

    [SkippableFact] public Task SetF_KeepsPatternLiteral() => AssertOracle.EqualAsync(Setup + "set -f; echo *.txt" + Teardown);
    [SkippableFact] public Task SetPlusF_RestoresGlobbing() => AssertOracle.EqualAsync(Setup + "set -f; set +f; echo *.txt" + Teardown);
    [SkippableFact] public Task LongOption_Noglob() => AssertOracle.EqualAsync(Setup + "set -o noglob; echo *.txt; set +o noglob; echo *.txt" + Teardown);
    [SkippableFact] public Task ExpansionResult_IsNotGlobbed() => AssertOracle.EqualAsync(Setup + "set -f; x='*.txt'; echo $x; for f in *.txt; do echo \"f=$f\"; done" + Teardown);
    [SkippableFact] public Task Subshell_DoesNotLeak() => AssertOracle.EqualAsync(Setup + "( set -f; echo *.txt ); echo *.txt" + Teardown);
}
