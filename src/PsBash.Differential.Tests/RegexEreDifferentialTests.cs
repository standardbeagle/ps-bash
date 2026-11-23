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

    // The operand word `${u:-w}` / `${u+w}` takes keeps its own quoting in the =~ word (an escaped or quoted character
    // matches literally, an unquoted one is regex text); the variable's value is regex text. Which one it is depends on
    // `u` at run time. `${u:=w}` is the variable's new value, regex text.
    [SkippableFact]
    public Task Differential_RegexEre_DefaultOperandKeepsItsQuoting() => AssertOracle.EqualAsync(T + """
        u=; [[ 'a{1,2}}' =~ ^${u:-a\{1,2\}}\}$ ]]; t U01 $?
        u=; [[ 'aa}' =~ ^${u:-a\{1,2\}}\}$ ]]; t U02 $?
        u='a{2}'; [[ 'aa}' =~ ^${u:-x}\}$ ]]; t U03 $?
        u='a{2}'; [[ 'a{2}}' =~ ^${u:-x}\}$ ]]; t U04 $?
        u=; [[ a.b =~ ^${u:-a'.'b}$ ]]; t U05 $?
        u=; [[ axb =~ ^${u:-a'.'b}$ ]]; t U06 $?
        u=; [[ axb =~ ^${u:-a.b}$ ]]; t U07 $?
        u=; [[ axb =~ ^${u:-"a.b"}$ ]]; t U08 $?
        u=; v='x.y'; [[ xzy =~ ^${u:-$v}$ ]]; t U10 $?
        u=; v='x.y'; [[ xzy =~ ^${u:-"$v"}$ ]]; t U11 $?
        u=1; [[ 'x.' =~ ^${u:+x\.}$ ]]; t U12 $?
        u=1; [[ 'xy' =~ ^${u:+x\.}$ ]]; t U13 $?
        u=; [[ '' =~ ^${u:+x\.}$ ]]; t U14 $?
        u=; [[ 'a\b' =~ ^${u:-a\\b}$ ]]; t U15 $?
        u=; [[ 'a*' =~ ^${u:-a\*}$ ]]; t U16 $?
        u=; [[ 'aaa' =~ ^${u:-a\*}$ ]]; t U17 $?
        u=; [[ 'axb' =~ "^${u:-a.b}$" ]]; t U20 $?
        u=; [[ 'a.b' =~ ^${u-a\.b}$ ]]; t U21 $?
        unset u; [[ 'axb' =~ ^${u=a\.b}$ ]]; t U24 $?; echo "u=$u"
        u=; w=; [[ 'a.' =~ ^${u:-${w:-a\.}}$ ]]; t U25 $?
        u=; w=; [[ 'ax' =~ ^${u:-${w:-a\.}}$ ]]; t U26 $?
        u=; [[ 'a[b' =~ ^${u:-a\[b}$ ]]; t U29 $?
        unset u; [[ 'a(b)' =~ ^${u-a\((b)\)}$ ]]; t U31 $?
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

    // A back-reference compares the text its group took: the old search took a reference as any length and trusted
    // a walk that can exit early, so (a*)*(x)\2 matched xy.
    [SkippableFact]
    public Task Differential_RegexEre_BackReferencesAreExact() => AssertOracle.EqualAsync(T + """
        re='(a*)*(x)\2'; [[ xy =~ $re ]]; echo "R01 $? ${#BASH_REMATCH[@]}"
        line='key=value; other=thing; key=value; x'; re='(key=[a-z]+).*\1'; [[ $line =~ $re ]]; echo "R09 $? ${BASH_REMATCH[1]}"
        re='([a-z]+) \1'; [[ 'the the cat sat sat' =~ $re ]]; t R10 $?
        re='(a|ab)(c|bcd)(d*)\2'; [[ abcdbcd =~ $re ]]; t R11 $?
        re='(a*)+\1b'; [[ aab =~ $re ]]; t R12 $?
        re='((a)|b)*\2'; [[ abba =~ $re ]]; t R13 $?
        re='(a)|\1x'; [[ x =~ $re ]]; t R14 $?
        re='(a)?\1'; [[ b =~ $re ]]; t R15 $?
        re='()\1*'; [[ b =~ $re ]]; t R16 $?
        re='(.)(.)\2\1'; [[ xabbay =~ $re ]]; t R17 $?
        re='(a+)(b+)\1\2'; [[ aabbaabbx =~ $re ]]; t R18 $?
        re='^(.*)\1$'; [[ abcabc =~ $re ]]; t R19 $?
        re='(.*)\1'; [[ xyzxyzq =~ $re ]]; t R20 $?
        re='([a-c]*)\1'; [[ abcabcabc =~ $re ]]; t R21 $?
        re='(x)?(y)?\2'; [[ yy =~ $re ]]; t R22 $?
        re='(a)\1{2}'; [[ aaaa =~ $re ]]; t R23 $?
        re='(a)(\1)*'; [[ aaaa =~ $re ]]; t R24 $?
        re='(ab|a)(bc|c)?\1'; [[ abcab =~ $re ]]; t R25 $?
        """);

    // The back-reference search is bounded by its distinct states, not starts x ends x subject: these finish (the
    // old search was cubic, and allocated a subject-sized array per start).
    [SkippableFact]
    public Task Differential_RegexEre_BackReferenceBombsFinish() => AssertOracle.EqualAsync("""
        s=$(printf 'ab%.0s' {1..500}); re='(.)\1'; [[ $s =~ $re ]]; echo "R02 $?"; [[ ${s}xx =~ $re ]]; echo "R03 $? ${BASH_REMATCH[0]}"
        s=$(head -c 1000000 /dev/zero | tr '\0' a); re='(x)\1'; [[ $s =~ $re ]]; echo "R04 $?"
        re='(a)\1'; [[ $s =~ $re ]]; echo "R05 $? ${BASH_REMATCH[0]}"
        s=$(head -c 4000 /dev/zero | tr '\0' a); re='(a*)\1$'; [[ $s =~ $re ]]; echo "R06 $? ${#BASH_REMATCH[0]} ${#BASH_REMATCH[1]}"
        re='^(a+)\1$'; [[ ${s}a =~ $re ]]; echo "R07 $?"
        re='(a*)*b\1'; [[ $s =~ $re ]]; echo "R08 $?"
        long=$(printf 'word%d ' {1..200}); re='(word[0-9]+) .* \1 '; [[ "$long word7 " =~ $re ]]; echo "R26 $? ${BASH_REMATCH[1]}"
        re='(.)\1'; [[ $long =~ $re ]]; echo "R27 $? ${BASH_REMATCH[0]}"
        """);

    // Hyphens as members and endpoints, collating symbols as endpoints, multibyte endpoints (REG_ECOLLATE in
    // C.UTF-8), and an invalid byte: only ever itself in the subject, its byte value as an endpoint. The invalid-byte
    // cases are written inline: through a variable ps-bash still turns $'\xff' into U+FFFD (a store bug, not =~).
    [SkippableFact]
    public Task Differential_RegexEre_BracketRangesWithHyphensAndMultibyte() => AssertOracle.EqualAsync(T + """
        export LC_ALL=C.UTF-8
        [[ x =~ x ]]
        [[ - =~ [a---z] ]]; echo "B01 $?"
        [[ b =~ [a---z] ]]; echo "B02 $?"
        [[ β =~ [α-ω] ]]; echo "B03 $?"
        v B05 ',' '[!--]'
        v B06 . '[--/]'
        v B07 + '[%--]'
        v B08 - '[--@]'
        v B09 a '[a-a]'
        v B10 b '[[.-.]-z]'
        v B11 b '[a-[.z.]]'
        v B12 - '[--]'
        v B13 - '[^-a]'
        v B14 d '[a-c-e]'
        v B15 - '[a-c-]'
        v B16 b '[---]'
        v B17 - '[---]'
        v B18 a '[[.a.]-[.c.]]'
        v B19 é '[[.é.]]'
        [[ x =~ [[.$'\xff'.]] ]]; echo "B20 $?"
        [[ $'\xff' =~ [[.$'\xff'.]] ]]; echo "B21 $?"
        [[ $'\xff' =~ $'\xff' ]]; echo "B22 $?"
        [[ x =~ [[=$'\xff'=]] ]]; echo "B23 $?"
        [[ x =~ [[:$'\xff':]] ]]; echo "B24 $?"
        v B25 b '[a-]b]'
        v B27 ']' '[]-a]'
        v B28 '^' '[]-^]'
        v B29 'b' '[α-a]'
        v B30 'b' '[a-α]'
        v B31 'β' '[[.α.]-ω]'
        v B32 'β' '[α-[.ω.]]'
        [[ $'\xff' =~ [$'\xff'] ]]; echo "B33 $?"
        [[ $'\xff' =~ [a-$'\xff'] ]]; echo "B34 $?"
        [[ b =~ [[.$'\xff'.]-z] ]]; echo "B35 $?"
        [[ é =~ [a-$'\xff'] ]]; echo "B36 $?"
        [[ b =~ [a-$'\xff'] ]]; echo "B37 $?"
        [[ $'\x80' =~ [$'\x80'-$'\xff'] ]]; echo "B38 $?"
        [[ é =~ [$'\x80'-$'\xff'] ]]; echo "B39 $?"
        [[ b =~ [$'\xff'-z] ]]; echo "B40 $?"
        [[ ÿ =~ [a-$'\xff'] ]]; echo "B41 $?"
        [[ b =~ [a-[.$'\xff'.]] ]]; echo "B44 $?"
        [[ é =~ [^$'\xff'] ]]; echo "B45 $?"
        [[ $'\xff' =~ [^$'\xff'] ]]; echo "B46 $?"
        [[ $'\xff' =~ [^a] ]]; echo "B47 $?"
        [[ $'\xff' =~ . ]]; echo "B48 $?"
        [[ $'\xfe' =~ [$'\xff'-$'\xfe'] ]]; echo "B49 $?"
        """);
}
