using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// One arithmetic evaluator in every context (docs/specs/emitter-strategy.md "One arithmetic evaluator"): a slice offset
/// or length of a string, an array or the positionals, an indexed subscript read, written or unset, <c>declare -i</c>,
/// an indirect element, and <c>$(( ))</c> / <c>(( ))</c> / <c>let</c> themselves. Each context had its own
/// <c>int.TryParse</c> / <c>[long]</c> / <c>-match '^-?[0-9]+$'</c> shortcut, which read <c>010</c> as 10 (bash: octal
/// 8), let <c>08</c> through (bash: an error) and could not take an expression (<c>${a[@]:1+1:2}</c> was the whole
/// array). Recorded from bash 5.2 (cassette). Streams are captured apart (<c>&gt;out 2&gt;err</c>); every failing
/// expansion ends its line (error-handling.md "Known differences").
/// </summary>
public class ArithOneEvaluatorDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";
    private const string Run = "bash s.sh >out 2>err; cat err; echo ---; cat out";

    /// <summary>Slice bounds of a string, an array and the positionals are arithmetic expressions.</summary>
    [SkippableFact]
    public Task SliceBounds_AreArithmeticExpressions()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "s=abcdefghijkl; a=(a b c d e f g h i j k l)\n"
            + "echo \"${s:010}\" \"${s:1:010}\" \"${s: -010}\"\n"
            + "echo \"${a[@]:010:2}\"\n"
            + "i=2; j='1+1'\n"
            + "echo \"${a[@]:i}\"\n"
            + "echo \"${a[@]:1+1:2}\" \"${a[@]:$j:2}\" \"${s:$j:2}\" \"${s:j:2}\" \"${s:i+1:i}\"\n"
            + "set -- a b c d e; echo \"${@:1+1:2}\" \"${*:j:2}\" \"${@:$#}\"\n"
            + "echo \"${s:9223372036854775807}|${s:2:9223372036854775807}|${a[@]:9223372036854775807}|\"\n"
            + "echo \"${s:08}\"\n"
            + "echo \"rc=$?\"\n"
            + "echo \"${s:1:08}\"\n"
            + "echo \"${a[@]:08}\"\n"
            + "echo \"${a[@]:0:08}\"\n"
            + "echo \"${@:08}\"\n"
            + "echo end\n"
            + "EOF\n"
            + Run + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>An indexed subscript read, written, unset, declared or named indirectly is an arithmetic expression.</summary>
    [SkippableFact]
    public Task Subscripts_AreArithmeticExpressions()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "a=(a b c d e f g h i j k l)\n"
            + "k=010; echo \"${a[$k]}\" \"${a[k]}\" \"${a[010]}\" \"${a[$k]:-none}\" \"${a[k]:-none}\"\n"
            + "declare -A m=([010]=x); echo \"${m[010]}\" \"${m[$k]}\"\n"
            + "b=(a b c d e f g h i j k l); i=-010; unset 'b[i]'; echo \"${b[@]}\"\n"
            + "c=(a b c d e f g h i j k l); unset 'c[010]'; echo \"${c[@]}\"\n"
            + "d=(); d[010]=x; echo \"${!d[@]}\"\n"
            + "ref='a[010]'; echo \"${!ref}\"\n"
            + "declare -i n=010; echo \"$n\"\n"
            + "h=ff; echo $((16#$h)) $((16#$h + 1)); (( v = 16#$h )); echo \"$v\"\n"
            + "echo $((16#FF)) $((16#Ff)) $((36#Z)) $((20#J)) $((64#Z))\n"
            + "i=08; echo \"${a[$i]}\"\n"
            + "echo \"rc=$?\"\n"
            + "echo end\n"
            + "EOF\n"
            + Run + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// The evaluator's edges in each command: a huge exponent, -2**63 / -1 (also in a function, which it aborted),
    /// a compound assignment's read order, <c>++</c> beside a constant, and bash's messages.
    /// </summary>
    [SkippableFact]
    public Task Evaluator_EdgesMatchBash()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "echo $(( (-1)**9223372036854775807 ))\n"
            + "echo $(( (-2**63)/-1 )) $(( (-2**63)%-1 ))\n"
            + "x=$(( -2**63 )); (( x /= -1 )); echo \"$x $?\"; x=$(( -2**63 )); let 'x %= -1'; echo \"$x $?\"\n"
            + "f() { local y=$(( -2**63 / -1 )); echo \"in f $y\"; }; f; echo \"after f\"\n"
            + "x=1; echo $((x += (x=10))); x=1; echo $((x += x++)); x=3; echo $((x+=x*=2))\n"
            + "echo $((++5)) $((--5)) $((1--1)) $((1++1))\n"
            + "echo $(( 0 ? 1 : x = 5 ))\n"
            + "echo $(( 1=2 ))\n"
            + "echo $(( 5++ ))\n"
            + "echo $(( 1 ? : 2 ))\n"
            + "r1=r2; r2=r1; echo $((r1+1))\n"
            + "echo end\n"
            + "EOF\n"
            + Run + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// An element target's subscript is evaluated ONCE: at the read for a compound op or <c>++</c>/<c>--</c>, at the
    /// write (after the right side) for a plain <c>=</c>. Only a bare name may be assigned (<c>1 + x = 5</c> is an
    /// error). An indexed literal key is arithmetic (<c>[010]=x</c> is element 8). A variable slice bound takes the
    /// plain-decimal shortcut yet still evaluates <c>010</c>, <c>' 1 '</c>, <c>1+1</c>. A failing expansion abandons
    /// the rest of its line, also in <c>[[ ]]</c>, a function body, <c>$( )</c> and <c>( )</c>.
    /// </summary>
    [SkippableFact]
    public Task LvaluesLiteralKeysSliceVarsAndAbandonment_MatchBash()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "i=0; a=(10 20 30); (( a[i++] += i )); echo \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); (( a[i++]++ )); echo \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); (( ++a[i++] )); echo \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); (( a[i] = i++ )); echo \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); (( a[i++] = 9 )); echo \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); echo $(( a[i++] *= 5 )) \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); let 'a[i++]+=10'; echo \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); (( a[i++] += a[i++] )); echo \"$i ${a[*]}\"\n"
            + "i=0; a=(1 2 3); x='a[i++]'; (( x++ )); echo \"$i ${a[*]} $x\"\n"
            + "a=(1 2 3); (( a[-1]++ )); echo \"${a[*]}\"\n"
            + "declare -A m=([k]=5); k=k; (( m[$k]++ )); echo \"${m[k]}\"\n"
            + "x=1; echo $((1 + x = 5)) $x\n"
            + "echo $((2 * x = 5 + 1))\n"
            + "echo $((1 + x += 5))\n"
            + "(( 3 + x = 4 )); echo \"st=$? x=$x\"\n"
            + "declare -i n=5; declare n+=010; echo \"$n\"; declare n=010; echo \"$n\"; n+=010; echo \"$n\"\n"
            + "b=([010]=x [2]=y); declare -p b\n"
            + "c=([08]=x [1]=y); declare -p c; echo after-c\n"
            + "a=(a b c d e); i=1; echo \"${a[@]:$i:2}\"; set -- p q r s; echo \"${@:$i:1}\" \"${@:$#}\"\n"
            + "i=010; echo \"${a[@]:$i}|\"; i=' 1 '; echo \"${a[@]:$i:1}\"; j='1+1'; echo \"${a[@]:$j:1}\"; i=-2; echo \"${a[@]:$i}\"\n"
            + "( set -u; echo \"${a[@]:$zz}\"; echo not-here ); echo \"st=$?\"\n"
            + "s=abcdef; x=${s:08}; echo \"[$x]\"; echo same-line\n"
            + "if [[ ${s:08} == a ]]; then echo y; fi; echo same\n"
            + "f(){ echo ${s:08}; echo in-f; }; f; echo after\n"
            + "y=$(echo ${s:08}; echo inner); echo \"[$y]\"\n"
            + "( echo ${s:08}; echo inner ); echo outer\n"
            + "echo end\n"
            + "EOF\n"
            + Run + Done,
            timeout: TimeSpan.FromSeconds(60));
}
