using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Exit status of odd command words and of `return N` outside 0..255.</summary>
public class CommandWordStatusTests
{
    [Fact] public Task Return_256_Wraps() => AssertOracle.EqualAsync("f() { return 256; }; f; echo s=$?");
    [Fact] public Task Return_257_Wraps() => AssertOracle.EqualAsync("f() { return 257; }; f; echo s=$?");
    [Fact] public Task Return_Negative_Wraps() => AssertOracle.EqualAsync("f() { return -1; }; f; echo s=$?");
    [Fact] public Task EmptyQuotedCommand_Is127() => AssertOracle.EqualAsync("{ ''; } 2>/dev/null; echo s=$?");
    [Fact] public Task EmptyQuotedCommand_InIf_IsFalse() => AssertOracle.EqualAsync("if ''; then echo T; else echo F; fi 2>/dev/null");
    [Fact] public Task CommandSubOnly_True() => AssertOracle.EqualAsync("$(true); echo s=$?");
    [Fact] public Task CommandSubOnly_False() => AssertOracle.EqualAsync("$(false); echo s=$?");
    [Fact] public Task CommandSubOnly_Exit42() => AssertOracle.EqualAsync("$(exit 42); echo s=$?");
}
