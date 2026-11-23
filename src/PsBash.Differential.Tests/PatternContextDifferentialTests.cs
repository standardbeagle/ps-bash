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
    public Task Pathname_UsesTheSameCompiler(string script)
        => AssertOracle.EqualAsync(Prologue + "cd \"$(mktemp -d)\" || exit\n" + script);
}
