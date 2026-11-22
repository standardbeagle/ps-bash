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

    [SkippableFact]
    public Task DollarQuote_MidWord_IsQuoting()
        => Eq("n=3; unset u\np a$\"*\" x$\"y\" a$'\\x41'b a$'b'c x$\"$n\"y\necho \"${u:-$'t\\x41'}\"");
}
