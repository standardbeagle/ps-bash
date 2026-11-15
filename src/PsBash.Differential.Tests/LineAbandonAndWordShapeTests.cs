using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Errors that abandon only the rest of their input line, and word shapes the lexer and word parser read the way bash
/// does: a failglob error or a bad substitution skips the commands after it on the same line and the script goes on
/// (status 1); NAME[ at command position reads to its ], blanks and all; an unquoted variable inside an extglob is
/// split before the pattern is formed; a tilde before a brace expansion waits for the braces; an alias's here-document
/// body comes from the lines after the alias use; an unterminated quote is a syntax error (status 2).
/// </summary>
public class LineAbandonAndWordShapeTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    /// <summary><paramref name="inner"/> run as a script file with its error stream discarded: the error texts name the
    /// script and line differently ("s.sh: line 2:" against "ps-bash:"), the stdout and the status are what is compared.</summary>
    private static Task EqScript(string inner) =>
        Eq("d=$(mktemp -d); cd \"$d\"; cat > s.sh <<'PSBASH_EOF'\n" + inner + "\nPSBASH_EOF\n"
            + "bash s.sh 2>/dev/null; echo \"rc=$?\"; cd /; rm -rf \"$d\"");

    [SkippableFact]
    public Task Failglob_AbandonsTheRestOfTheLine_NotTheNextLine()
        => EqScript("shopt -s failglob\necho *.ZZ; echo same=$?\necho next=$?\necho *.ZZ || echo alt && echo more\necho ok\n"
            + "eval 'echo *.ZZ; echo in'; echo after=$?");

    [SkippableFact]
    public Task BadSubstitution_AbandonsTheRestOfTheLine_AndTheScriptGoesOn()
        => EqScript("a=(1 2 3)\necho one; echo ${a[]}; echo same\necho next=$?\neval 'echo z ${(m)foo} z'; echo st=$?\n"
            + "echo -{z..A}-; echo same2\necho range=$?\nx=${q:?gone}\necho notreached");

    [SkippableFact]
    public Task SpacedSubscript_AtCommandPosition_IsOneWord()
        => Eq("a[5 + 1]=; a[5 / 1]=y; echo len=${#a[@]} ${a[5]}\nif true; then c[2 * 2]=q; fi; echo c=${c[4]}\n"
            + "x=1 b[1 + 1]=z; echo b=${b[2]}\necho a[1 2]");

    [SkippableFact]
    public Task SpacedSubscript_WithoutEquals_IsACommandName()
        => EqScript("a[5 + 3] x\necho st=$?\na[5 + 3]+=y; echo st=$?");

    [SkippableFact]
    public Task SpacedSubscript_Unclosed_IsASyntaxError()
        => EqScript("a[5 +");

    [SkippableFact]
    public Task ExtglobWithUnquotedVariable_IsSplitBeforeThePattern()
        => Eq("shopt -s extglob\nd=$(mktemp -d); cd \"$d\"; touch 'abc)d' foo.c foo.h\n"
            + "IFS=c ABC='c?d'; echo ab+($ABC)\nIFS=x; ABC=cxd; for i in +($ABC); do echo =$i=; done\n"
            + "IFS=' '; ext=c; echo foo.?($ext|h)\ncd /; rm -rf \"$d\"");

    [SkippableFact]
    public Task DoubleBracketBangParen_BeforeExtglob_IsNegation()
        => Eq("empty=''; str=x\n[[ !($empty) ]] && echo T1\n[[ !($str) ]] || echo F2\nshopt -s extglob\n"
            + "[[ !($empty) ]] && echo T3\n[[ !($str) ]] && echo T4");

    [SkippableFact]
    public Task TildeBeforeBraceExpansion_ExpandsEachWord()
        => Eq("HOME=/home/bob\necho ~{/src,/lib} ~{a,b}x\nfor x in ~{/src,/lib}; do echo \"-- $x\"; done\n"
            + "x=~{/a,/b}; echo \"asg $x\"\nHOME=/home/bar\nx=~:${undef-~:~}; echo \"$x\"");

    [SkippableFact]
    public Task DynamicDeclaration_DoesNotTildeExpandTheValue()
        => Eq("HOME=/home/bob\nbinding='const=~/src'; readonly \"$binding\"; echo \"$const\"\n"
            + "f() { local \"$1\"; echo \"x=[$x]\"; }; f 'x=y a=b'\nnames='p q'; declare $names; declare -p p q");

    [SkippableFact]
    public Task Length_Unquoted_IsSplit_AndBracketClassNameEscapes()
        => Eq("v=hello; IFS=5; echo ${#v}; echo \"${#v}\"; IFS=' '\nd=$(mktemp -d); touch \"$d/foo.-\"\n"
            + "cd \"$d\"; echo *.[[:punct:]] *.[[:punct\\:]]; cd /; rm -rf \"$d\"");

    [SkippableFact]
    public Task GlobInsideBraceExpansion_IsPathnameExpanded()
        => Eq("shopt -s globstar\nd=$(mktemp -d); cd \"$d\"; mkdir -p c/sub; touch c/sub/leaf.md\n"
            + "echo {**/*.*,} | sed 's/ *$//'\necho {foo,bar}.c {*.md,nope}\ncd /; rm -rf \"$d\"");

    [SkippableFact]
    public Task AliasHereDoc_BodyComesFromTheFollowingLines()
        => EqScript("shopt -s expand_aliases\nalias c='cat <<EOF\n$(echo hi)\nEOF\n'\nc\nbody line\nEOF\necho st=$?");

    [SkippableFact]
    public Task UnterminatedQuote_IsASyntaxError()
        => EqScript("echo 'abc");

    [SkippableFact]
    public Task AliasWithAnOpenQuote_IsASyntaxError()
        => EqScript("shopt -s expand_aliases\nalias e_='echo \"'\nvar=x\ne_ '${var}\"'\necho after");
}
