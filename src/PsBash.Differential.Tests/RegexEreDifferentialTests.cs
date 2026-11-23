using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>[[ s =~ re ]]</c> is glibc POSIX ERE (REG_EXTENDED), not .NET regex: leftmost-longest with POSIX captures,
/// <c>.</c> and <c>[^x]</c> take a newline, <c>^</c>/<c>$</c> anchor only at the true ends, no Perl syntax, and a
/// backslash in the WORD quotes the next character (<c>\d</c> reaches regcomp as <c>d</c>). An invalid regex is
/// status 2 and leaves BASH_REMATCH alone; a failed match empties it. ps-bash ran <c>-cmatch</c> and got most of
/// these wrong (docs/specs/regex-ere.md).
/// </summary>
public class RegexEreDifferentialTests
{
    // Prints the case, the status and BASH_REMATCH as %q words.
    private const string T = """
        t() { printf '%s rc=%s m=%s\n' "$1" "$2" "$(printf '%q,' "${BASH_REMATCH[@]}")"; }
        v() { local n=$1 s=$2 re=$3; [[ $s =~ $re ]]; t "$n" $?; }

        """;

    [SkippableFact]
    public Task Differential_RegexEre_LeftmostLongestWithPosixCaptures() => AssertOracle.EqualAsync(T + """
        [[ ab =~ (a|ab) ]]; t e01 $?
        [[ xabcx =~ (a|ab)(c|bcd)? ]]; t e02 $?
        [[ aaa =~ a*? ]]; t e03 $?
        re='a.*?c'; [[ abcbc =~ $re ]]; t e20 $?
        [[ abc =~ ^(a)|(x)$ ]]; t e24 $?
        [[ ab =~ (a)(x)?b ]]; t e25 $?
        [[ abcd =~ (a|ab)(c|bcd)(d*) ]]; t e33 $?
        v V72 'b' '(a*)*'
        v V74 'abc' '(a|ab)(bc|c)'
        v V75 'aa' '(a*)(a*)'
        v V76 'ab' '((a)|b)*'
        v V78 'aa' '((a?))*'
        v V80 'abc' '(.*)(.*)'
        v V85 'abab' '(a|ab)(c|bcd)?(d*)'
        v V87 'aaa' '(a|aa)*'
        v V89 'abcabc' '(abc|ab|a)*(c)?'
        v V90 'xyx' '(x|xy)(x|yx)?'
        v V93 'aa' '(a|)*'
        v V96 'abb' 'a(b|bb){1,2}'
        v V98 'aba' '(a(b)?)+'
        """);

    [SkippableFact]
    public Task Differential_RegexEre_NewlineIsAnOrdinaryCharacterAndAnchorsAreTheEnds() => AssertOracle.EqualAsync(T + """
        [[ $'a\nb' =~ a.b ]]; t e09 $?
        [[ $'a\n' =~ a$ ]]; t e10 $?
        [[ $'x\na' =~ ^a ]]; t e11 $?
        v V64 $'\n' '^[^a]$'
        v V65 'a^b' 'a^b'
        v V66 'b' 'a|^b'
        v V67 'xay' 'x$y'
        v V71 'ab' '(^a)(b$)'
        """);

    [SkippableFact]
    public Task Differential_RegexEre_BackslashInTheWordQuotesTheNextCharacter() => AssertOracle.EqualAsync(T + """
        [[ 5 =~ ^\d$ ]]; t e04 $?
        [[ d =~ ^\d$ ]]; t e05 $?
        [[ 5 =~ [\d] ]]; t e06 $?
        [[ 'a b' =~ \<b ]]; t e08 $?
        [[ 'a{1' =~ a\{1 ]]; t e15 $?
        [[ abab =~ (ab)\1 ]]; t e16 $?
        [[ x =~ \w ]]; t e18 $?
        [[ 'a b' =~ a\sb ]]; t e19 $?
        [[ abc =~ a\.c ]]; t e23 $?
        [[ ab =~ \(a\) ]]; t e27 $?
        [[ '(a)' =~ \(a\) ]]; t e28 $?
        [[ ab =~ a\Kb ]]; t e32 $?
        [[ 'ab' =~ a\x62 ]]; t e34 $?
        [[ 'a(b' =~ a\(b ]]; echo "e40 $?"
        [[ 'a+' =~ ^a\+$ ]]; echo "e41 $?"
        [[ ab =~ ^a\b ]]; echo "e42 $?"
        [[ 'a}' =~ a\} ]]; t L09 $?
        [[ 'a\b' =~ a\\b ]]; t L12 $?
        [[ 'a|b' =~ a\|b ]]; t L13 $?
        [[ 'a^b' =~ a\^b ]]; t L14 $?
        [[ 'a$b' =~ a\$b ]]; t L15 $?
        [[ 'a[b' =~ a\[b ]]; t L16 $?
        [[ '-' =~ [\-] ]]; t L17 $?
        [[ '\' =~ [\d] ]]; t L19 $?
        """);

    [SkippableFact]
    public Task Differential_RegexEre_QuotedPartsMatchThemselves() => AssertOracle.EqualAsync(T + """
        [[ abcbc =~ "a.*"c ]]; t e21 $?
        [[ 'a.*c' =~ "a.*"c ]]; t e22 $?
        [[ 'a\b' =~ 'a\b' ]]; t L11 $?
        [[ 'x' =~ ["a-z"] ]]; t L20 $?
        [[ '-' =~ ["a-z"] ]]; t L21 $?
        [[ '^' =~ ["^"a] ]]; t L22 $?
        [[ 'b' =~ ["^"a] ]]; t L23 $?
        [[ 'a.c' =~ a"."c ]]; t L25 $?
        [[ ab =~ "" ]]; t L26 $?
        [[ 'a[' =~ a"[" ]]; t L28 $?
        x='a.c'; [[ abc =~ "$x" ]]; t F9 $?
        [[ abc =~ $x ]]; t F10 $?
        [[ 'a.c' =~ ^"$x"$ ]]; t F11 $?
        [[ '|' =~ $'|' ]]; t Q1 $?
        """);

    [SkippableFact]
    public Task Differential_RegexEre_PerlSyntaxIsNotEre() => AssertOracle.EqualAsync(T + """
        [[ é =~ ^[[:alpha:]]$ ]]; t e12 $?
        [[ ab =~ (?:a)b ]]; t e13 $?
        [[ ab =~ a(?=b) ]]; t e14 $?
        [[ ab =~ a(?b) ]]; t F17 $?
        [[ aB =~ ab ]]; t e17 $?
        """);

    [SkippableFact]
    public Task Differential_RegexEre_RegexInAVariable_GnuEscapesBackrefsAndBounds() => AssertOracle.EqualAsync(T + """
        v V01 d '\d'
        v V02 5 '\d'
        v V03 'ab c' '\w+'
        v V05 'a b' 'a\sb'
        v V07 'ab b' '\bb'
        v V09 'ab b' '\<b'
        v V10 'ab b' 'a\>'
        v V11 'ab' '\`a'
        v V13 aa '(a)\1'
        v V14 aaaa '(a*)\1'
        v V23 'xabcabcy' '(a|ab)(c|bcd)\2'
        v V24 aaa 'a{,2}'
        v V26 aaa 'a{,}'
        v V36 a 'a**'
        v V38 'a)' 'a)'
        v V40 a '()'
        v V46 ab '(a){0}b'
        v V47 a 'a{32767}'
        """);

    [SkippableFact]
    public Task Differential_RegexEre_BracketExpressions() => AssertOracle.EqualAsync(T + """
        [[ x =~ x ]]
        [[ 'a\b' =~ a[\]b ]]; t e07 $?
        [[ a =~ [[.a.]] ]]; t e29 $?
        [[ a =~ [[=a=]] ]]; t e30 $?
        [[ 'a]' =~ []a]+ ]]; t e31 $?
        [[ AB =~ [a-z]+ ]]; t e35 $?
        v V20 '\' '[\]'
        v V51 '-' '[a-]'
        v V52 '-' '[]-a]'
        v V53 ']' '[]a]'
        v V54 'x' '[^]a]'
        v V56 '-' '[[.-.]]'
        v V57 'é' '[[=e=]]'
        v V59 'é' '[a-z]'
        v V99 'a' '[[:alpha:][:digit:]]'
        re='a[b'; [[ 'a[b' =~ $re ]]; t F6 $?
        p='[a-'; [[ 'b]' =~ $p"]"] ]]; t F12 $?
        """);

    [SkippableFact]
    public Task Differential_RegexEre_InvalidRegex_IsStatus2AndKeepsBashRematch() => AssertOracle.EqualAsync(T + """
        [[ x =~ (x) ]]
        v V18 a 'a\'
        v V21 'a' '\1(a)'
        v V27 a 'a{'
        v V30 a 'a{2,1}'
        v V32 a '*a'
        v V35 a '^*'
        v V39 a '(a'
        v V48 a 'a{32768}'
        v V49 'a' '[[:foo:]]'
        v V50 'a' '[z-a]'
        v V55 ' ' '[[.space.]]'
        v V60 'β' '[α-ω]'
        v V70 'a' 'a\b*'
        [[ ab =~ a[ ]]; t L27 $?
        bad='('
        if [[ ab =~ $bad ]]; then echo F7 y; else echo "F7 n $?"; fi
        [[ ab =~ $bad ]] || echo "F8 $?"
        """);

    [SkippableFact]
    public Task Differential_RegexEre_FailedMatch_EmptiesBashRematch() => AssertOracle.EqualAsync("""
        [[ ab =~ (a) ]]
        [[ ab =~ x ]]; echo "n01 $? count=${#BASH_REMATCH[@]}"
        [[ ab =~ x ]]; echo "n02 [${BASH_REMATCH[@]}]"
        [[ ab =~ x ]]; printf 'n03 <%s>\n' "${BASH_REMATCH[@]}"
        [[ ab =~ (a)(x)?b ]]; echo "n04 ${#BASH_REMATCH[@]} [${BASH_REMATCH[2]}]"
        f() { [[ ab =~ (b) ]]; }; f; echo "n05 ${BASH_REMATCH[1]}"
        """);

    [SkippableFact]
    public Task Differential_RegexEre_CodePointsAndGlibcClasses() => AssertOracle.EqualAsync(T + """
        [[ λ =~ ^.$ ]]; t e26 $?
        v V61 'é' '^..$'
        v V62 'é' '^.$'
        v V63 '😀' '^.$'
        [[ $'a\tb' =~ a[[:blank:]]b ]]; t F19 $?
        [[ '_' =~ [[:punct:]] ]]; t F21 $?
        [[ 'é' =~ [[:lower:]] ]]; t F22 $?
        [[ 'É' =~ [[:upper:]] ]]; t F23 $?
        [[ '٣' =~ [[:digit:]] ]]; t F24 $?
        [[ '٣' =~ [[:alnum:]] ]]; t F25 $?
        [[ '€' =~ [[:punct:]] ]]; t F28 $?
        [[ 'é' =~ [^a] ]]; t F30 $?
        [[ 'aé' =~ ^a[^x]$ ]]; t F32 $?
        re='\w'; [[ 'é' =~ $re ]]; t F34 $?
        re='\bé'; [[ 'aé' =~ $re ]]; t F35 $?
        """);

    [SkippableFact]
    public Task Differential_RegexEre_NoCaseMatch_FoldsLikeGlibc() => AssertOracle.EqualAsync(T + """
        shopt -s nocasematch
        [[ AB =~ ^ab$ ]]; t e36 $?
        v C01 D '\d'
        v C03 A '[a-z]'
        v C04 a '[A-Z]'
        v C05 A '[[:lower:]]'
        v C07 É 'é'
        v C08 a '[^A]'
        v C09 'ǅ' 'ǆ'
        v C10 'ß' 'SS'
        v C12 'ς' 'Σ'
        v C14 'é' '[É]'
        [[ xAB =~ a(b) ]]; t C15 $?
        """);

    [SkippableFact]
    public Task Differential_RegexEre_BacktrackingBombsFinish() => AssertOracle.EqualAsync("""
        s=$(printf 'a%.0s' {1..3000}); [[ $s =~ ^(a|aa)*$ ]]; echo "P1 $? ${#BASH_REMATCH[1]}"
        [[ ${s}b =~ (a|aa)*c ]]; echo "P2 $?"
        [[ $s =~ (a*)*b ]]; echo "P3 $?"
        [[ $s =~ ((a*)*)*$ ]]; echo "P4 $? ${#BASH_REMATCH[1]}"
        """);
}
