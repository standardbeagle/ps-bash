using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Bash data must never become PowerShell code (docs/specs/emitter-strategy.md §Bash data never becomes PowerShell code).
/// Each payload here is bash DATA shaped to end a PowerShell string or statement (a Unicode quote mark, PowerShell-only
/// whitespace, an escaped CR, a <c>$</c> before a non-name character, a <c>)</c> inside <c>"$( )"</c>). Before the fix
/// several ran <c>Write-Output PWNED-…</c> as PowerShell (a run-time command word quoted with only ASCII <c>'</c>
/// doubled, a <c>~user</c> spliced raw, NBSP not escaped, <c>flock -c</c> running any unparseable string as
/// PowerShell); the rest were PowerShell parse errors or read PowerShell variables (<c>$^</c>). Static guard:
/// Host.Tests BashDataNeverCodeGuardTests.
/// </summary>
public class BashDataNeverCodeDifferentialTests
{
    private const string Rsq = "\u2019";
    private const string Rdq = "\u201D";
    private const string Nbsp = "\u00A0";

    private static Task Eq(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));

    // Resolve-BashCommand built `$__bw = @('w1', ...)` doubling only ASCII ', so a U+2019 in a word closed the literal.
    [SkippableFact]
    public Task RunTimeCommandWords_StayData()
        => Eq("c='a" + Rsq + ";Write-Output PWNED-r1;" + Rsq + "b'; echo | \"$c\" 2>&1 | sed 's/^.*line [0-9]*: //'; echo st=$?\n"
            + "{ \"$c\"; } < /dev/null 2>&1 | sed 's/^.*line [0-9]*: //'\n"
            + "set -- printf '[%s]\\n' 'x" + Rsq + ";Write-Output PWNED-r3;" + Rsq + "y'; echo | \"$@\"\n"
            + "w='printf [%s]\\n a" + Rsq + ";Write-Output" + Nbsp + "PWNED-r4;" + Rsq + "b'; $w\n"
            + "f() { printf '[%s]\\n' \"$@\"; }; c=f; echo | $c 'a" + Rsq + ";Write-Output PWNED-r5;" + Rsq + "'\n");

    // `~user` was spliced into the command line raw: `~x’` opened a PowerShell string that a later ’ closed.
    [SkippableFact]
    public Task TildeUser_IsOneLiteralWord()
        => Eq("echo ~a" + Rsq + "b; echo ~x" + Rdq + "y\n"
            + "echo ~x" + Rsq + " ';Write-Output PWNED-t3;#'\n"
            + "echo ~x" + Rsq + "/a\necho ';Write-Output PWNED-t4;#'\n"
            + "echo ~a\\'b ~a{b ~a\\$b\n");

    // PowerShell splits words at NBSP, U+2000-U+200A, U+3000, NEL, U+2028/9, VT, FF; bash does not. Unescaped, `a<NBSP>#c`
    // commented out the rest of the line and `a<NBSP>@x` splatted.
    [SkippableFact]
    public Task UnicodeWhitespace_DoesNotSplitAWord()
        => Eq("echo a" + Nbsp + "#c 'x\n;Write-Output PWNED-u1;#'\n"
            + "printf '[%s]\\n' a" + Nbsp + "@PSVersionTable z" + Nbsp + "@args\n"
            + "printf '[%s]\\n' q" + Nbsp + "-n w\u3000--x a\u2003b c\u2028d e\u0085f\n"
            + "x=a" + Nbsp + "b; printf '[%s]\\n' $x \"$x\" pre" + Nbsp + "$x\n"
            + "printf '[%s]\\n' a" + Nbsp + "$(echo X)b \\" + Nbsp + "q\n"
            + "for w in a" + Nbsp + "b c\u3000d; do printf '[%s]' \"$w\"; done; echo\n"
            + "printf '[%s]\\n' x\u000By z\u000Cw | od -c | head -2\n");

    // `flock -c STRING` ran any string the bash parser rejected as PowerShell. Now only the launcher's one policy
    // (PowerShellFallback) may, and text that does not look like PowerShell is the parse error, status 2.
    [SkippableFact]
    public Task FlockCommandString_ThatDoesNotParse_IsAParseError()
        => Eq("cd \"$(mktemp -d)\" || exit\n"
            + "flock ./lk -c \"echo PWNED-flock2 )\" 2>/dev/null; echo \"st $?\"\n"
            + "flock ./lk -c \"echo ok-flock3\"; echo \"st $?\"\n"
            + "cd / ");

    // Emitted PowerShell that did not parse: `$:`, a number-shaped command name (`1e5=1` parsed as an assignment to a
    // number), and a `)` in a string nested inside "$( )" (PowerShell counts parens there without honouring quotes).
    [SkippableFact]
    public Task DataThatBrokeThePowerShellParse_RunsAsBash()
        => Eq("echo $: x\n"
            + "1e5=1 2>&1 | sed 's/^.*line [0-9]*: //'; echo \"st ${PIPESTATUS[0]}\"\n"
            + "08=x 2>&1 | sed 's/^.*line [0-9]*: //'; echo \"st ${PIPESTATUS[0]}\"\n"
            + "echo 1e5=1 08=x; 1e5=1 2>/dev/null; echo st=$?; 08=x 2>/dev/null; echo st=$?\n"
            + "echo \"${u:-x)}\"\n"
            + "echo \"${u:-x" + Rsq + ")+@(Write-Output PWNED-p5)+@(" + Rsq + "}\"\n"
            + "declare -A m=([k]=v); echo \"${m['x" + Rsq + ")+@(Write-Output PWNED-p7)+@(" + Rsq + "']}\" ok\n"
            + "u=1; echo \"${u:+a)b}\" \"${v:=c)d}\" \"${v}\" \"${w-(e}\"\n"
            + "x=abc; echo \"${x/b/)}\" \"${x#a)}\" \"${x:-\\\")}\"\n"
            + "x='a(b)c'; echo \"${x/(/[}\" \"${x//)/]}\" \"${x#a(}\" \"${x%'c'}\" \"${#x}\"\n"
            + "set -- 'p)q'; echo \"${1:-'z'}\" \"${2:-($1)}\" \"pre${3:-')'}post\"\n"
            + "u=1; echo \"${u:+'q'}\" \"[${u:+($u)}]\" \"${v2:='a)'}\" \"$v2\"; y=\"${w:-'(z)'}\"; echo \"$y\"\n");

    // A backtick before a raw CR is PowerShell's line continuation: `a\<CR>b` lost the CR and glued the next text on.
    [SkippableFact]
    public Task EscapedCarriageReturn_IsOneCharacter()
        => Eq("echo a\\\rb | od -c | head -1\n"
            + "echo a\rb | od -c | head -1\n"
            + "printf '[%s]' a\\\rx y\\\r | od -c | head -1\n");

    // `$` before a character that cannot start a bash name is a literal `$`; emitted bare, `$^` / `$é` read PowerShell
    // variables (the last token of the previous line, an unset name).
    [SkippableFact]
    public Task DollarBeforeANonNameCharacter_IsALiteralDollar()
        => Eq("echo $^ x\n"
            + "echo $\u00E9$^\n"
            + "echo a$ $ b \"$\" '$' $% $= $, $. $/ $+ $]\n"
            + "echo \"$^ $: $\u00E9\" x$^y\n"
            + "printf '[%s]\\n' a @x @ b@c --% ; echo @ --%\n"
            + "x=1; echo $x#b ${x}#c a#b\n");
}
