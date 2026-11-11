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

    // Unquoted $* / $@ (alone or with text glued on) is the parameters joined with IFS[0], then split on IFS:
    // IFS=x and `"" ""` is ONE empty field; `=$*=` iterated once over the literal PS text `=$(if …)=`.
    [SkippableTheory]
    [InlineData("p(){ printf '<%s>' \"$@\"; echo \"|$#\"; }; IFS=x; set -- a '' b; p $*; p =$*=; p $@; set -- '' ''; p $*; p =$*=")]
    [InlineData("p(){ printf '<%s>' \"$@\"; echo \"|$#\"; }; IFS=; set -- a '' b; p $*; p =$@=; set -- '' ''; p $*; p =$*=")]
    [InlineData("p(){ printf '<%s>' \"$@\"; echo \"|$#\"; }; set -- 'a b' ''; p =$*=; p =$@=; set --; p =$*=; p $*; IFS=x; p =$*=")]
    [InlineData("IFS=x; f(){ for i in $*; do echo -$i-; done; for i in =$*=; do echo -$i-; done; }; f '' ''")]
    [InlineData("set -- '*.zzq' 'b c'; for i in $@; do echo \"[$i]\"; done; for i in a$1 =$2=; do echo \"[$i]\"; done")]
    public Task UnquotedPositionals_JoinThenSplit(string script) => AssertOracle.EqualAsync(script);

    // `for i in a$x` single-quoted the item, so the loop ran once over the literal text `a$env:x`.
    [SkippableTheory]
    [InlineData("x='1 2'; for i in a$x; do echo \"[$i]\"; done")]
    [InlineData("x='1 2'; for i in a$x b 'q r'; do echo \"[$i]\"; done")]
    [InlineData("y=lit; for i in pre$y.bak \"$y\"x; do echo \"[$i]\"; done")]
    [InlineData("x=; for i in a$x $x; do echo \"[$i]\"; done")]
    public Task ForListVariableWithText_IsSplit(string script) => AssertOracle.EqualAsync(script);

    [Fact]
    public Task QuotedAtSlice_IsOneWordPerParameter() => AssertOracle.EqualAsync(
        "f() { for i in \"${@:3:5}\"; do echo =$i=; done; for i in \"${*:3:2}\"; do echo =$i=; done; printf \"<%s>\" \"${@:2:2}\"; echo; printf \"<%s>\" \"${@:2}\"; echo; }\nf ab cd ef gh ij kl\nset -- 1 2 3 4\nprintf \"<%s>\" \"${@:2}\"; echo");

    // bash's set builtin: a lone `+` is an ignored flag; a lone `-` ends the options, turns off -x / -v, and replaces
    // $@ only when words follow it. Both used to work only as the FIRST word (`set +` was refused at parse time).
    [SkippableTheory]
    [InlineData("set - +; echo \"[$*]\"; set + -; echo \"[$*]\"")]
    [InlineData("{ set -x - -v; } 2>/dev/null; case $- in *x*) echo on;; esac; echo \"[$*]\"")]
    [InlineData("set -- k; set +; echo \"[$*]\"; set + a b; echo \"[$*]\"; set -; echo \"[$*]\"")]
    [InlineData("set -e + -- p q; case $- in *e*) echo e-on;; esac; echo \"[$*]\"")]
    [InlineData("{ set -x + q r; f=$-; set +x; } 2>/dev/null; case $f in *x*) echo x-on;; esac; echo \"[$*]\"")]
    public Task SetLoneDashOrPlus_AmongOptions(string script) => AssertOracle.EqualAsync(script);
}
