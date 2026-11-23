using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Word shapes the lexer and word parser read the way bash 5.2 does (docs/specs/parser-grammar.md §Parameter expansion
/// extent, §Brace sequence expressions, §Tilde prefixes): a `${…}` ends at its first unquoted `}` (a bare `{` does not
/// nest), a brace sequence is exactly bash's (step, padding, letters through ASCII, anything else literal, nested the
/// same as top level), a tilde after `=` and `:` of an assignment expands, and `$"…"` / `$'…'` are quoting mid-word.
/// Guards: Core.Tests DollarBraceExtentGuardTests, BraceSequenceGuardTests.
/// </summary>
public class WordParseExtentDifferentialTests
{
    private const string P = "p() { printf '<%s>' \"$@\"; printf ' #%s\\n' \"$#\"; }\n";

    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(P + script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task DollarBrace_FirstUnquotedCloseEndsIt()
        => Eq("x='a b'; unset u\n"
            + "p ${x/ /{1,2\\}}; echo after\n"
            + "p ${x/ /\\}} \"${x/ /{\\}}\" ${x:-{a,b\\}}\n"
            + "p ${u:-{a,b}} ${u:-{a}b} ${u:-{} ${u:-${u:-{a}}}\n"
            + "p \"${u:-$(echo })}\" \"${u:-`echo }`}\" ${u:-$'\\'}'}\n"
            + "p \"${x:-'}'}\" \"${u:-'}'}\" \"${u:-\"{}\"}\" \"${u:-\\{\\}}\"\n"
            + "y=${u:-a:{b}:c}; p \"$y\"; a=(1); p ${a[0]:-{z}}\n"
            + "p $(echo ${u:-)}); p \"$(echo \"${u:-)}\")\"\n"
            + "p {a,${x/ /\\}}} {a,${u:-{b}} {a,$(echo },)} x{a,${u:-b,c}}y {a,${x/ /,}}\n"
            + "cat <<EOF\n${u:-{h}} ${x/ /\\}} ${u:-{a,b\\}}\nEOF\n"
            + "echo end");

    [SkippableFact]
    public Task BraceSequence_IsBashsExpression_NestedOrNot()
        => Eq("n=3\n"
            + "p {1..$n} {a..$n} {$n..5} x{1..$n}y {1..3..$n}\n"
            + "p {x,{1..10..3}} {x,{01..03}} {a,{c..g..2}} {a,b{0001..3}}\n"
            + "p {Z..b..3}; p {Z..b}; p {z..A}\n"
            + "p {1..10..-3} {1..3..0} {-3..3..2} {+1..3} {-01..2} {01..10..3} {x,{001..3..2}} {-05..5..5}\n"
            + "p {1..3..x} {1..3..} {..3} {1..} {1..2..3..4} {@..C} {9..a} {1...3}\n"
            + "p {x{a,b}y} {{a,b}} {1..3}{..}");

    // Compared, not printed: $PWD is a C:/ path on Windows.
    [SkippableFact]
    public Task Tilde_AfterAssignmentEqualsAndColon_Expands()
        => Eq("HOME=/hh; OLDPWD=/old\n"
            + "w=a:~+:~-; [ \"$w\" = \"a:$PWD:/old\" ] && echo ok1\n"
            + "w=a:~:~/x:~+/x:~-/x; [ \"$w\" = \"a:/hh:/hh/x:$PWD/x:/old/x\" ] && echo ok2\n"
            + "export e=a:~+; [ \"$e\" = \"a:$PWD\" ] && echo ok3\n"
            + "f() { local l=~-:~; echo \"$l\"; }; f\n"
            + "declare d=~+:~-; [ \"$d\" = \"$PWD:/old\" ] && echo ok4\n"
            + "arr=(~- x:~-); p \"${arr[@]}\"; p a=~ b=~-:~ ~nosuchuserx");

    // bash brace_gobbler: a `${…}` inside a brace expansion is ONE nesting level; its operand is neither split nor expanded.
    [SkippableFact]
    public Task BraceExpansion_DollarBraceIsOneLevel()
        => Eq("unset x u; echo {a,${x:-{b,c}}}\n"
            + "p {a,${x:-{b,c}}}; p {a,${x:-b,c}}; p x{a,${u:-{b}}}y; p {a,${u:-x}}{1,2}");

    // bash 5.2 keeps a tilde on the right of an ASSOCIATIVE `[k]=v` literal as written.
    [SkippableFact]
    public Task AssocLiteral_TildeStaysAsWritten()
        => Eq("declare -A m=([k]=~root); echo \"${m[k]}\"\n"
            + "declare -A m2=([k]=~root/x [j]=a:~root [n]=~nosuchuserx); echo \"${m2[k]} ${m2[j]} ${m2[n]}\"");

    // A named array's subscript is one unit: `}`, `]` inside quotes / backticks / `$( )` / `$'…'` are key text.
    [SkippableFact]
    public Task Subscript_QuotedCloseIsKeyText()
        => Eq("declare -A m5; m5[']']=v; m5['}']=w; m5[x]=X\n"
            + "echo ${m5[$'\\x5d']}; echo \"${m5[`echo ]`]}\" ${m5[`echo ]`]} \"${m5[$(echo ])]}\"\n"
            + "echo \"${m5[}]}\" ${m5[}]} \"${m5['}']}\" ${m5[\"}\"]} ${m5[\\}]}\n"
            + "echo \"${m5[$'\\x7d']}\" ${m5[$'\\x7d']} \"${#m5[}]}\" ${m5[}]:-d} \"${m5[}]/w/z}\"\n"
            + "i=0; a=(q r); echo \"${a[$((i+0))]}\" \"${a[ ${i} ]}\" \"${m5[x]:-{z}}\" \"${m5[y]:-{z}}\"\n"
            // Unquoted the `$'a]b'` is the key (1); in "…" it is translated first, `${m[a]b]}`: a bad substitution.
            + "declare -A m; m[$'a]b']=1; echo ${m[$'a]b']}; echo \"${m[$'a]b']}\"; echo same-line\necho after");

    // Inside "…" a `$'…'` of a `${…}` is translated FIRST and the text expands: `$'\x5d'` makes `${m[]]}`, a bad
    // substitution that abandons the line; `$'\x24x'` is `$x`.
    [SkippableFact]
    public Task DoubleQuoted_AnsiCInBracedVar_TranslatedFirst()
        => Eq("x=abc; unset u; echo \"${u:-$'\\x24x'}\" \"${u:-$'a\\x41'}\"\n"
            + "declare -A m5; m5[']']=v; echo \"${m5[$'\\x5d']}\"; echo same-line\n"
            + "echo after");

    // No brace expansion on the right of an assignment: the value is the text as written.
    [SkippableFact]
    public Task AssignmentValue_BraceTextVerbatim()
        => Eq("x={-01..2}; echo \"$x\"; x={1..010}; echo \"$x\"; x={1..5..0}; echo \"$x\"; x={+1..3}; echo \"$x\"\n"
            + "x={1..3}; echo \"$x\"; x=a{01..3}b; echo \"$x\"; x={a,b}; echo \"$x\"; x={a..c..1}; echo \"$x\"; x={'a',b}; echo \"$x\"");

    // `[[ s =~ re ]]`: an expansion in the regex word ends where the lexer ends it (`${u:-"}"}`, `$(echo ")")`).
    [SkippableFact]
    public Task RegexWord_ExpansionExtentIsTheLexers()
        => Eq("unset u; [[ aa =~ ^${u:-a{1,2}}$ ]] && echo y || echo n\n"
            + "[[ a{1,2} =~ ^${u:-a{1,2}}$ ]] && echo y2 || echo n2\n"
            + "[[ '}' =~ ^${u:-\"}\"}$ ]] && echo y4 || echo n4\n"
            + "[[ ')' =~ ^$(echo \")\")$ ]] && echo y5 || echo n5");

    // A here-document's `${…}` operand: `'` and `\x` are text, `"` is removed, and `$'…'` is NOT translated.
    [SkippableFact]
    public Task HereDoc_BracedVarOperand_KeepsAnsiCText()
        => Eq("unset u; x=abc\ncat <<EOF\n"
            + "1 ${u:-$'a\\x41'} ${u:-'q'} ${u:-\"q\"} ${u:-\\x} ${u:-\\$x} ${u:-$x}\n"
            + "2 ${x/b/'z'} ${x/b/\"z\"} ${x#'a'} ${x#\"a\"} ${x/b/$'\\x41'}\n"
            + "\"${u:-$'a\\x41'}\"\nEOF\n"
            + "echo \"3 ${u:-$'a\\x41'} ${u:-'q'} ${u:-\"q\"} ${u:-\\x} ${x/b/'z'} ${x#'a'} ${x/b/$'\\x41'}\"");

    [SkippableFact]
    public Task DollarQuote_MidWord_IsQuoting()
        => Eq("n=3; unset u\np a$\"*\" x$\"y\" a$'\\x41'b a$'b'c x$\"$n\"y\necho \"${u:-$'t\\x41'}\"");
}
