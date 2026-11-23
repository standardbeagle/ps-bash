using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// ONE pattern, ONE subject, EVERY pattern context: <c>case</c>, <c>[[ == ]]</c>, <c># ## % %%</c>,
/// <c>/ // /# /%</c>, <c>^^ ,,</c> and pathname expansion. Three matchers used to serve these contexts and disagreed
/// with each other and with bash (a reversed range threw in one, extglob alternation took the first arm in another,
/// newlines and <c>&amp;</c> broke a third); now one compiler (BashPattern) serves them all, so a row that differs
/// between two contexts in bash differs the same way here. The regression rows name the bug they pin.
/// </summary>
public class PatternContextDifferentialTests
{
    private const string Prologue = "export LC_ALL=C.UTF-8; shopt -s extglob\n";

    /// <summary>The script that runs <paramref name="pattern"/> (bash source text) against <paramref name="subject"/>
    /// (bash source text of a word) in every context and prints each result quoted.</summary>
    private static string EveryContext(string pattern, string subject) =>
        Prologue
        + $"s={subject}\n"
        + $"case $s in {pattern}) c=Y;; *) c=n;; esac\n"
        + $"if [[ $s == {pattern} ]]; then e=Y; else e=n; fi\n"
        + $"if [[ $s != {pattern} ]]; then ne=Y; else ne=n; fi\n"
        + "printf '%q|' \"$c\" \"$e\" \"$ne\""
        + $" \"${{s#{pattern}}}\" \"${{s##{pattern}}}\" \"${{s%{pattern}}}\" \"${{s%%{pattern}}}\""
        + $" \"${{s/{pattern}/X}}\" \"${{s//{pattern}/X}}\" \"${{s/#{pattern}/X}}\" \"${{s/%{pattern}/X}}\""
        + $" \"${{s^^{pattern}}}\" \"${{s,,{pattern}}}\" \"${{s^{pattern}}}\"\n"
        + "echo\n";

    [SkippableTheory]
    [InlineData("a*", "abc")]
    [InlineData("*c", "abc")]
    [InlineData("?b*", "abc")]
    [InlineData("*b?", "abcbc")]
    [InlineData("[a-c]", "BcA")]
    [InlineData("[!a]", "abc")]
    [InlineData("[z-a]", "mx")]                       // a reversed range is empty, never an error (bug 9)
    [InlineData("[\\d]", "5x")]                       // `\d` in a class is the letter d (bug 10)
    [InlineData("[\\d]", "dx")]
    [InlineData("[]a]", "]ab")]
    [InlineData("[[:upper:]]", "ẞx")]                 // classes follow the locale (bug 10)
    [InlineData("[[:alpha:]]", "éx")]
    [InlineData("[[:lower:]]", "Éa")]
    [InlineData("[[=a=]]", "ab")]                     // equivalence class and collating symbol (bug 10)
    [InlineData("[[.a.]]", "ab")]
    [InlineData("[[:foo:]]x", "xx")]
    [InlineData("@(a|ab)", "abc")]                    // extglob alternation: shortest / longest (bug 8)
    [InlineData("@(ab|a)", "abc")]
    [InlineData("+(a|ab)", "abab")]
    [InlineData("*([0-9])", "123x")]                  // an empty-capable pattern in // (bug 6)
    [InlineData("?(z)", "abc")]
    [InlineData("!(b)", "abc")]
    [InlineData("@(b|bc)", "abcabc")]
    [InlineData("\\*", "'*x'")]                       // an escaped or quoted pattern keeps the /# /% anchor (bug 7)
    [InlineData("\"x\"", "'*x'")]
    [InlineData("a\\ ", "'a b'")]
    [InlineData("'*'", "'a*b'")]
    [InlineData("\"@(a)\"", "'@(a)b'")]              // a quoted extglob operator is inert (bug 13)
    [InlineData("*", "$'x\\ny'")]                     // ? and * span a newline; the end is \z, not $ (bug 2)
    [InlineData("b", "$'ab\\n'")]
    [InlineData("?", "$'\\n'")]
    [InlineData(".*", "a.tar.gz")]
    [InlineData("*.", "a.b.c")]
    [InlineData("\"\"", "abc")]                       // the empty pattern
    [InlineData("*", "''")]
    [InlineData("a\\(", "'a(b)c'")]
    public Task SamePattern_EveryContext_MatchesLikeBash(string pattern, string subject)
        => AssertOracle.EqualAsync(EveryContext(pattern, subject));

    /// <summary>An UNQUOTED expansion in a pattern is pattern text, a QUOTED one literal — in every context (bug 1).</summary>
    [SkippableTheory]
    [InlineData("pre='*/'; p=/usr/lib/x; printf '%s|' \"${p##$pre}\" \"${p##\"$pre\"}\" \"${p#$pre}\"; echo")]
    [InlineData("ext='.*'; fn=a.tar.gz; printf '%s|' \"${fn%$ext}\" \"${fn%%$ext}\" \"${fn%\"$ext\"}\"; echo")]
    [InlineData("pat='?'; fn=a.b; printf '%s|' \"${fn//$pat/-}\" \"${fn//\"$pat\"/-}\" \"${fn^^$pat}\"; echo")]
    [InlineData("pp='@(a|ab)'; v=abc; printf '%s|' \"${v##$pp}\" \"${v#$pp}\" \"${v/$pp/X}\"; case $v in $pp*) echo Y;; *) echo n;; esac")]
    [InlineData("pp='[!]]'; v=x; case $v in $pp) echo Y;; *) echo n;; esac; [[ $v == $pp ]]; echo $?")]
    [InlineData("pp='[z-a]'; t=foo; echo \"${t//$pp/z}\" \"${t#$pp}\"")]
    [InlineData("x='a*'; [[ abc == $x ]]; echo $?; [[ abc == \"$x\" ]]; echo $?; [[ 'a*' == \"$x\" ]]; echo $?")]
    [InlineData("e=; [[ a == \"@(a)\"$e ]]; echo $?; case a in \"+(a)\"$e) echo Y;; *) echo n;; esac")]
    [InlineData("x=ab; echo \"[${x//$unset/r}]\" \"[${x/#$unset/r}]\" \"[${x/%$unset/r}]\"")]
    public Task Expansion_InAPattern_IsPatternTextUnlessQuoted(string script)
        => AssertOracle.EqualAsync(Prologue + script);

    /// <summary>The replacement of <c>${v/p/r}</c> (bash 5.2 patsub_replacement): <c>&amp;</c> is the match, <c>\&amp;</c>
    /// a literal <c>&amp;</c>, a quoted <c>&amp;</c> literal, an <c>&amp;</c> in an unquoted variable's value active, and
    /// nothing is .NET substitution syntax (bug 5).</summary>
    [SkippableTheory]
    [InlineData("v=abc; printf '%s|' \"${v/b/&}\" \"${v/b/\\&}\" \"${v/b/\"&\"}\" \"${v/b/'&'}\" \"${v/b/&&}\"; echo")]
    [InlineData("v=abc; r='&'; printf '%s|' \"${v/b/$r}\" \"${v/b/\"$r\"}\" \"${v/b/[$r]}\"; echo")]
    [InlineData("v=abc; printf '%s|' \"${v/b/$&}\" \"${v/b/\\$1}\" \"${v/b/\\\\}\" \"${v/b/\\\\&}\" \"${v/b/\\x}\"; echo")]
    [InlineData("v=abc; rep='a\\b'; printf '%s|' \"${v/b/$rep}\"; rep='\\&'; printf '%s|' \"${v/b/$rep}\"; echo")]
    [InlineData("v=abcabc; printf '%s|' \"${v//b/<&>}\" \"${v//@(b|bc)/<&>}\" \"${v//[ac]/&&}\"; echo")]
    [InlineData("v=abc; e=; printf '%s|' \"${v/#/[&]}\" \"${v/%/[\\&]}\" \"${e/*/[&]}\" \"${e//*/[\\&]}\"; echo")]
    [InlineData("v=abc; shopt -u patsub_replacement; printf '%s|' \"${v/b/&}\" \"${v/b/[&]}\" \"${v/b/\\&}\"; echo")]
    [InlineData("v='a(b)c'; echo \"${v/(b)/[x]}\" \"${v//[()]/_}\" \"${v/b/(x)}\" \"${v/b/\")\"}\"")]
    [InlineData("v=$'ab\\n'; printf '%q|' \"${v/%/P}\" \"${v%b}\" \"${v//?/.}\"; echo")]
    public Task Substitute_ReplacementTemplate_MatchesBash(string script)
        => AssertOracle.EqualAsync(Prologue + script);

    /// <summary><c>^ ^^ , ,,</c> test EACH character against the WHOLE pattern; <c>^</c>/<c>,</c> look only at the
    /// first character (bug 11). nocasematch folds <c>/</c> and the tests, never <c>#</c>/<c>%</c> (bug 12).</summary>
    [SkippableTheory]
    [InlineData("v=abc; printf '%s|' \"${v^b}\" \"${v^^ab}\" \"${v^^[ab]}\" \"${v^}\" \"${v^^}\"; v=ABC; printf '%s|' \"${v,b}\" \"${v,,BC}\" \"${v,,[BC]}\" \"${v,}\"; echo")]
    [InlineData("v=éa; printf '%s|' \"${v^}\" \"${v^^}\"; v=ẞx; printf '%s|' \"${v^^}\" \"${v,,}\"; echo")]
    [InlineData("a=(abc xbz); echo \"${a[@]/b/Y}\" \"${a[@]#?}\" \"${a[@]^^}\"; set -- foo bar; echo \"${@/o/0}\" \"${*%r}\"")]
    [InlineData("shopt -s nocasematch; v=ABC; printf '%s|' \"${v/b/x}\" \"${v//[a-b]/x}\" \"${v#a}\" \"${v%c}\" \"${v^^b}\"; [[ ABC == abc ]]; echo $?")]
    [InlineData("shopt -s nocasematch; case ABC in abc) echo Y;; *) echo n;; esac; case a in [[:upper:]]) echo Y;; *) echo n;; esac")]
    public Task CaseAndNoCase_MatchBash(string script)
        => AssertOracle.EqualAsync(Prologue + script);

    /// <summary>Bracket edge cases in the tests (bug 10).</summary>
    [SkippableTheory]
    [InlineData("case x in [!]]) echo Y;; *) echo n;; esac; case ']' in [!]]) echo Y;; *) echo n;; esac")]
    [InlineData("[[ x] == [!]]* ]]; echo $?; [[ ']' == []] ]]; echo $?; [[ - == [a\\-z] ]]; echo $?; [[ m == [a\\-z] ]]; echo $?")]
    [InlineData("case a in [[=a=]]) echo Y;; *) echo n;; esac; case ' ' in [[.space.]]) echo Y;; *) echo n;; esac")]
    [InlineData("case 'é' in [[:alpha:]]) echo Y;; *) echo n;; esac; export LC_ALL=C; case 'é' in [[:alpha:]]) echo Y;; *) echo n;; esac")]
    public Task Brackets_MatchBash(string script)
        => AssertOracle.EqualAsync(Prologue + script);

    /// <summary>Pathname expansion, GLOBIGNORE and <c>compgen -X</c> through the same compiler: nocaseglob folds
    /// characters, not classes; a quoted extglob operator is inert; globstar's trailing <c>**/</c> lists directories
    /// only (bugs 10, 13, 14).</summary>
    [SkippableTheory]
    [InlineData("touch Ab ab1; shopt -s nocaseglob; echo [[:upper:]]*; echo [[:lower:]]b; echo A*; shopt -u nocaseglob")]
    [InlineData("touch '@(abc)x' abc; echo \"@(abc)\"*; e=; echo \"@(abc)\"$e*; echo @(abc)")]
    [InlineData("touch 'x(a|b)y' xa; d='(a|b)'; echo x\"$d\"*; echo x@(a|b)*")]
    [InlineData("mkdir -p sub/deep; touch sub/s1 sub/deep/d1; shopt -s globstar; echo sub/**/; echo **/; echo **/d1; echo sub/**")]
    [InlineData("touch one.txt two.md; mkdir foo; touch foo/two.txt; GLOBIGNORE='*.txt'; echo * */*; GLOBIGNORE='[!o]*'; echo *; GLOBIGNORE='f*'; echo */*")]
    [InlineData("echo $(compgen -W 'abc abd xyz a.c' -X 'a?c'); echo $(compgen -W 'abc abd xyz a.c' -X '!@(a*)'); echo $(compgen -W 'abc abd xyz' -X '[!a]*')")]
    [InlineData("touch ab ']x' 'x]' a1; echo [!]]*; echo [z-a]*; echo [[:foo:]]*; echo [\\]]*")]
    [InlineData("touch .a b; echo @(.a|b) ?(.)a; echo @(b|.a); echo +(.)a; echo !(b)")]   // a dot-file through an arm's literal period
    [InlineData("touch ab ac ba; echo !(a)*; echo !(a*)")]
    public Task Pathname_UsesTheSameCompiler(string script)
        => AssertOracle.EqualAsync(Prologue + "cd \"$(mktemp -d)\" || exit\n" + script);

    /// <summary>A bracket assembled around an expansion is a bracket in every context (<c>[$c]</c>); a QUOTED
    /// <c>! ^ -</c> inside it is a member, never a negation or a range (<c>["$x"a]</c> with x=!).</summary>
    [SkippableTheory]
    [InlineData("c=abc; v=xbyaz; [[ b == [$c] ]] && echo m; case b in [$c]) echo c;; esac; echo \"${v//[$c]/-}\" \"${v#*[$c]}\" \"${v%[$c]*}\" \"${v^^[$c]}\"")]
    [InlineData("x='!'; [[ z == [\"$x\"a] ]] || echo lit; [[ '!' == [\"$x\"a] ]] && echo r1; x='^'; [[ z == [\"$x\"a] ]] || echo r2; [[ '^' == [\"$x\"a] ]] && echo r3")]
    [InlineData("x='-'; [[ b == [a\"$x\"c] ]] || echo r4; [[ '-' == [a\"$x\"c] ]] && echo r5; x='a-c'; [[ b == [$x] ]] && echo r6; [[ b == [\"$x\"] ]] || echo r7")]
    [InlineData("x='!a'; [[ b == [$x] ]] && echo r8; [[ b == [a\"-\"c] ]] || echo r9; d=bc; [[ b == [a$d] ]] && echo r11; [[ b == [$d]* ]] && echo r12; x=']'; [[ a == [ab$x ]] && echo r13")]
    public Task BracketFromExpansion_IsABracket(string script)
        => AssertOracle.EqualAsync(Prologue + script);

    /// <summary><c>!( )</c> in the removal and substitution operators and nested (the one construct the backtracking
    /// engine runs).</summary>
    [SkippableTheory]
    [InlineData("v=abcx; echo \"[${v##!(x)}]\" \"[${v#!(x)}]\"; v=xbca; echo \"[${v%%!(a)}]\" \"[${v%!(a)}]\"")]
    [InlineData("for s in a b '' aa x; do [[ $s == !(!(a)) ]]; printf '%s' $?; done; echo; for s in a b ''; do [[ $s == @(!(a)) ]]; printf '%s' $?; done; echo")]
    [InlineData("v=a; echo \"[${v#!(!(a))}]\" \"[${v##!(!(a))}]\"; v=abc; echo \"${v//!(b)/X}\" \"${v/!(a)/X}\"")]
    public Task Negation_MatchesBash(string script)
        => AssertOracle.EqualAsync(Prologue + script);

    /// <summary>
    /// Where an extglob list ends is bash's PATSCAN: any <c>(</c> nests, a bracket hides its <c>)</c> and <c>|</c>, and an
    /// operator whose list never closes makes the rest of the pattern plain text. 40 and 2,000 unclosed <c>@(</c> answer
    /// at once (it was 2^n work, then past the nesting bound).
    /// </summary>
    [SkippableTheory]
    [InlineData("t() { [[ $2 == $1 ]]; printf '%s' $?; }; t '@(a(b)c)' 'a(b)c'; t '@(a(b)c)' 'a(bc)'; t '@(a(x|y))' 'a(x|y)'; t '@(a(x|y))' ax; t '@(a[)]b)' 'a)b'; t '@(a\\)b)' 'a)b'; echo")]
    [InlineData("t() { [[ $2 == $1 ]]; printf '%s' $?; }; t '@(a(b)' 'a(b'; t '@(a(b)' '@(a(b)'; t '@(a*(b)' '@(abb'; t '@(a*(b)' '@(a*(b)'; t '@(a?' '@(ab'; t '*@(a' 'z@(a'; t '*@(a' za; echo")]
    [InlineData("p=$(printf '@(%.0s' {1..40}); [[ x == $p ]]; echo $?; [[ $p == $p ]]; echo $?; v=x; echo \"${v#$p}\"; case $p in $p) echo m;; esac; p=$(printf '@(%.0s' {1..2000}); [[ x == $p ]]; echo $?; [[ $p == $p ]]; echo $?")]
    public Task ExtGlobListBounds_MatchBash(string script)
        => AssertOracle.EqualAsync(Prologue + script);

    /// <summary>
    /// <c>shopt extglob</c> OFF (bash's default in a script): a run-time pattern value is matched without extglob in
    /// <c>case</c>, the parameter-expansion operators, pathname expansion, GLOBIGNORE and <c>compgen -X</c>; <c>[[ ]]</c>
    /// matches as if extglob were on (bash 5.2 manual, "Conditional Constructs").
    /// </summary>
    [SkippableTheory]
    [InlineData("v='+(a)'; case '+(a)' in $v) echo e1;; esac; case aa in $v) echo e2;; esac; [[ '+(a)' == $v ]] && echo e3; [[ aa == $v ]] && echo e4")]
    [InlineData("v='+(a)'; w='+(a)x'; u=aax; echo \"${w#$v}\" \"${u#$v}\"; shopt -s extglob; echo \"${u#$v}\"; case aa in $v) echo e7;; esac")]
    [InlineData("v=ab; echo \"${v#@(a)}\" \"${v/?(a)/X}\" \"${v/*(a)/X}\"; w='@(a)b'; echo \"${w#@(a)}\"")]
    [InlineData("cd \"$(mktemp -d)\" || exit; touch aa ab; g='@(aa|ab)'; echo $g; shopt -s extglob; echo $g; shopt -u extglob; v='*(a)'; touch 'x(a)'; echo x$v; echo $v")]
    [InlineData("cd \"$(mktemp -d)\" || exit; touch aa ab; GLOBIGNORE='+(a)'; echo *; shopt -s extglob; echo *")]
    [InlineData("v='+(a)'; echo $(compgen -W 'aa ab +(a)' -X \"$v\"); shopt -s extglob; echo $(compgen -W 'aa ab +(a)' -X \"$v\")")]
    public Task ExtGlobOff_RunTimeValues_MatchLikeBash(string script)
        => AssertOracle.EqualAsync("export LC_ALL=C.UTF-8\n" + script);
}
