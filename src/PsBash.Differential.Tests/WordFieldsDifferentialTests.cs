using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// bash word expansion (§3.5) of one word made of several parts: every unquoted expansion's result is IFS-split, a quoted
/// list (<c>"$@"</c>, <c>"${a[@]}"</c>, <c>"${@^^}"</c>) joins its first and last element to the text beside it, quoted
/// characters never glob, and an empty field of split text alone is removed. Each word shape used to have rules of its
/// own, so most shapes kept one field: with x='a b', <c>$x$x</c>, <c>~/$x</c>, <c>$x*</c>, <c>"$@"$x</c> and
/// <c>{1,2}$x</c> did not split, and <c>"${@/a/X}"</c> was one string. One run-time primitive now expands them all
/// (<c>PsBash.Cmdlets.BashWordFields</c>; guard <c>Core.Tests/WordFieldsGuardTests</c>).
/// </summary>
public class WordFieldsDifferentialTests
{
    // Each case runs in a fresh directory: the glob rows create files and list them.
    private const string P = "cd \"$(mktemp -d)\"\np() { printf '%s' \"$#:\"; [ $# -gt 0 ] && printf '<%s>' \"$@\"; echo; }; x='a b'; e=; g='*'; HOME=/home/hh\n";

    // R2: two or more expansions in one word.
    [SkippableTheory]
    [InlineData(P + "p $x$x; p ${x}${x}; p $x'q'$x; p a$x$x")]
    [InlineData(P + "p $(echo a b)$x; p `echo a b`$x; p ${x/a/c d}${e}")]
    [InlineData(P + "p ~/$x; p $e$e; p \"\"$e$e; p $e\"\"$e")]
    [InlineData(P + "touch b1 bq q; p $g$g; p $x*; p *$x; set -f; p $x*; set +f")]
    [InlineData(P + "IFS=1; p $((11+0)) $((10+0)); p x$((11+0)); unset IFS; p $((1+1))")]
    [InlineData(P + "for w in $x$x ~/$x $e$e; do printf '[%s]' \"$w\"; done; echo")]
    [InlineData(P + "a=($x$x ~/$x $e$e \"$x\"$x); p \"${a[@]}\"")]
    public Task TwoOrMoreExpansions_SplitEachResult(string script) => AssertOracle.EqualAsync(script);

    // R3: quoted per-element forms are one field per element; text and other lists join the first and last.
    [SkippableTheory]
    [InlineData(P + "set -- 'a b' '' c; p \"${@/a/X}\"; p \"${@#a}\"; p \"${@^^}\"; p \"${@@Q}\"")]
    [InlineData(P + "a=('a b' '' c); p \"${a[@]@Q}\"; p \"${a[@]/a/X}\"; p \"${a[@]%c}\"; p \"${a[@]:1}\"")]
    [InlineData(P + "set -- 'a b' '' c; p \"$@$@\"; p \"<$@>\"; p \"${@:2}\"; p \"$e${@}x\"")]
    [InlineData(P + "set -- 1 2; p \"$@\"$x; p $x\"$@\"; p \"$@\"\"$@\"")]
    [InlineData(P + "set -- 'a b' '' c; p \"${@:-d}\"; set --; p \"${@:-d}\"; p \"${@:+alt}\"; p \"$@\"; p k\"$@\"")]
    [InlineData(P + "a=('a b' '' c); p \"${a[@]:-d}\"; p x\"${a[@]:+w}\"y; b=(); p \"${b[@]:-d}\"; p \"${b[@]:+w}\"")]
    public Task QuotedListForms_GiveOneFieldPerElement(string script) => AssertOracle.EqualAsync(script);

    // An unset or empty array in quotes is no word ("${z[@]}" passed one null word: printf '%q' failed, $# was 1);
    // beside text it is the text; "${z[*]}" is one empty word. BASH_REMATCH before any =~ is the same.
    [SkippableTheory]
    [InlineData(P + "unset z; p \"${z[@]}\"; p \"a${z[@]}b\"; p \"${z[*]}\"; p x\"${z[@]}\"; printf '%q,' \"${z[@]}\"; echo")]
    [InlineData(P + "z=(); p \"${z[@]}\"; p \"a${z[@]}b\"; p \"${z[*]}\"; p \"${BASH_REMATCH[@]}\"; for w in \"${z[@]}\"; do echo \"[$w]\"; done; y=(\"${z[@]}\"); p \"${#y[@]}\"")]
    public Task UnsetOrEmptyArrayInQuotes_IsNoWord(string script) => AssertOracle.EqualAsync(script);

    // R5: a default word keeps its own quoting; its unquoted expansions split. `:=` assigns and the result is split whole.
    [SkippableTheory]
    [InlineData(P + "p ${e:-'a b'}; p ${e:-a\\ b}; p ${e:-a'b c'd}; p ${e:-\"a b\"}")]
    [InlineData(P + "ee='m n'; p ${ee:-\"*\"}; p ${u:-\"a b\"$x}; p ${u:-$x}; p ${x:+$x}; p ${x:+\"$x\"}")]
    [InlineData(P + "p ${e:='v w'}; p ${u2:=p q}; echo \"[$e][$u2]\"")]
    [InlineData(P + "touch m1 m2; p ${u:-m*}; p ${u:-'m*'}; p ${u:-\"m\"*}")]
    public Task DefaultOperatorWords_KeepTheirQuoting(string script) => AssertOracle.EqualAsync(script);

    // R6: `$'…'` in a quoted expansion's word is still ANSI-C quoting (extquote).
    [SkippableFact]
    public Task AnsiCQuoteInAQuotedDefaultWord_IsDecoded() => AssertOracle.EqualAsync(
        P + "p \"${e:-$'t\\x41'}\"; p \"${e:-$'a\\tb'}x\"; p ${e:-$'q\\x42'}; p \"${e:-'t\\x41'}\"; p ${e:-'t\\x41'}");

    // R8: every quoted `*` form joins with the first IFS character.
    [SkippableTheory]
    [InlineData(P + "set -- a b c; IFS=-; p \"${*:2}\" \"$*\" \"${*}\" \"${*^^}\" \"${*/a/X}\" \"${*:-d}\" $*")]
    [InlineData(P + "a=(a b c); IFS=-; p \"${a[*]}\" \"${a[*]:1}\" \"${a[*]:-d}\" \"${a[*]/a/X}\" \"${a[*]@Q}\"")]
    [InlineData(P + "set -- a b c; IFS=; p \"${*:2}\" \"$*\" \"${*}\"; a=(a b); p \"${a[*]}\" \"${a[*]:-d}\"")]
    public Task QuotedStarForms_JoinWithTheFirstIfsCharacter(string script) => AssertOracle.EqualAsync(script);

    // R4: a brace expansion's words each expand and split.
    [SkippableFact]
    public Task BraceWordsWithExpansions_SplitEachWord() => AssertOracle.EqualAsync(
        P + "p {1,2}$x; p {a,$(echo q r)}; p {a,${x}}; p {a,b}\"$x\"; p x{,$e}y");

    // IFS rules over joined parts: non-whitespace delimiters keep empty fields, a quoted null anchors one.
    [SkippableTheory]
    [InlineData(P + "IFS=:; v='a::b'; p $v; p x$v:; p $v$v; p \"\"$v; unset IFS")]
    [InlineData(P + "y='  abc  '; p \"\"$y; p $y\"\"; p '*'$e; touch bz; p \"*\"$g; p b$g; p ${x}z?")]
    [InlineData(P + "p $(printf 'a\\nb')x; IFS=; p $x$x; p $(echo a b)$x; unset IFS; p $x\"\"$x")]
    public Task IfsRulesOverJoinedParts_MatchBash(string script) => AssertOracle.EqualAsync(script);

    // Injection probes: a split value is data, never code or a pattern of quotes.
    [SkippableFact]
    public Task SplitValues_AreNeverCode() => AssertOracle.EqualAsync(
        P + "n='a;echo pwn'; m='$(echo pwn)'; q='\"x y\"'; p $n$m; p $q$q; p `echo '$x'`$x; set -- \"$m\" '`echo pwn`'; p \"$@\"$m");
}
