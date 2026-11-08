using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

public class SpacedSubscriptAssignTests
{
    [Fact] public Task Spaces_InSubscript_Assign() => AssertOracle.EqualAsync("a[1 * 1]=x; a[ 1 + 2 ]=z; echo status=$?; declare -p a");
    [Fact] public Task Spaces_InSubscript_Append() => AssertOracle.EqualAsync("i=2; a=(p q r); a[ i + 0 ]+=Z; declare -p a");
    [Fact] public Task Spaces_InSubscript_DoubleQuotedWord() => AssertOracle.EqualAsync("a[2 + \"3\"]=5; declare -p a");
    [Fact] public Task Argument_IsNotAnAssignment() => AssertOracle.EqualAsync("echo a[1 + 2]=x");
}

public class ElementPrefixAssignTests
{
    [Fact] public Task ElementPrefix_UnknownCommand_Status127() => AssertOracle.EqualAsync("{ a[1]=v nosuchcmd_zz; } 2>/dev/null; echo s=$?");
    [Fact] public Task ElementPrefix_RunsBuiltin() => AssertOracle.EqualAsync("{ a[1]=v echo hi; } 2>/dev/null; echo s=$?");
}
