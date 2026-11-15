using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>"${a[@]}"</c> and <c>"$@"</c> beside other words were joined into ONE argument.</summary>
public class QuotedArrayArgsTests
{
    [Fact] public Task Array_BesideOtherArg() => AssertOracle.EqualAsync("c=(a b c); printf '[%s]' \"${c[@]}\" z; echo");
    [Fact] public Task Positional_BesideOtherArg() => AssertOracle.EqualAsync("set -- 'x y' z; printf '[%s]' \"$@\" end; echo");
    [Fact] public Task EmptyArray_ContributesNothing() => AssertOracle.EqualAsync("e=(); printf '[%s]' \"${e[@]}\" end; echo");
    [Fact] public Task Array_Alone() => AssertOracle.EqualAsync("c=(a b c); printf '[%s]' \"${c[@]}\"; echo");
    [Fact] public Task ForList_Flattens() => AssertOracle.EqualAsync("c=(a b c); for i in \"${c[@]}\" q; do echo -n \"<$i>\"; done; echo");
    [Fact] public Task ArrayLiteral_Flattens() => AssertOracle.EqualAsync("c=(a 'b b' c); d=(\"${c[@]}\" w); echo \"${#d[@]}\"; f=(${c[@]} m); echo \"${#f[@]}\"; g=({1..3} n); echo \"${#g[@]}\"");
    [Fact] public Task StarJoined_StaysOneWord() => AssertOracle.EqualAsync("c=(a b c); printf '[%s]' \"${c[*]}\" z; echo");

    // A lone "$@" / "${a[@]}" as a mapped command's ONLY argument was emitted as one double-quoted string: `cat "$@"` with
    // two files looked for a file named "p q".
    [Fact] public Task Positional_AloneToMappedCommand() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && printf 'a\\n' > p && printf 'b\\n' > q; c() { cat \"$@\"; }; c p q; echo \"st=$?\"");
    [Fact] public Task Array_AloneToMappedCommand() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && printf 'a\\n' > p && printf 'b\\n' > q; fs=(p q); cat \"${fs[@]}\"; echo \"st=$?\"");
    [Fact] public Task EmptyPositional_AloneIsNoArgument() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && touch z; l() { ls \"$@\"; }; l; echo \"st=$?\"");

    // A function called as `f -i x` received a -i that PowerShell had marked as a parameter name, and splatting it into
    // `grep "$@"` bound it as one: grep printed "invalid option -- ' '", head "invalid number of lines".
    [Fact] public Task DashOptions_ThroughAFunction_ReachTheCommand() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" && printf 'Foo\\nbar\\n' > g; f() { grep \"$@\"; }; f -i foo g; echo \"st=$?\"; f -v -e Foo g; echo \"st=$?\"; "
        + "f -o -w bar g; echo \"st=$?\"; h() { head \"$@\"; }; h -n 1 g; k() { grep -c \"$@\"; }; k o g");
}
