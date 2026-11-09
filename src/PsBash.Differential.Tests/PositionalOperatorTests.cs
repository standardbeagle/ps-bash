using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A removal / replacement / case operator on <c>$@</c> or <c>$*</c> applies to each parameter, not to the joined string.</summary>
public class PositionalOperatorTests
{
    [Fact]
    public Task PatsubOnPositionals_IsPerParameter() => AssertOracle.EqualAsync(
        "set -- a b c d\necho ${*/b c/ghi}\necho ${@/b c/ghi}\necho ${*/b/X}\necho \"${*/b/X}\"\necho \"${@/b/X}\"\nIFS=:; echo \"${*/b/X}\"");

    [Fact]
    public Task RemovalOnPositionals_TwoListWordsInOneCommand() => AssertOracle.EqualAsync(
        "set -- a b c d\necho ${*#a} ${@%d}\necho \"${*#a}\"\nset -- Ab cd\necho ${@^} ${*,,}");

    [Fact]
    public Task QuotedAtSlice_IsOneWordPerParameter() => AssertOracle.EqualAsync(
        "f() { for i in \"${@:3:5}\"; do echo =$i=; done; for i in \"${*:3:2}\"; do echo =$i=; done; printf \"<%s>\" \"${@:2:2}\"; echo; printf \"<%s>\" \"${@:2}\"; echo; }\nf ab cd ef gh ij kl\nset -- 1 2 3 4\nprintf \"<%s>\" \"${@:2}\"; echo");
}
