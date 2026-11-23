using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A word's VALUE survives its quoting exactly (bash 5.2). REGRESSIONS:
/// <list type="bullet">
/// <item>a command name holding a newline (<c>$'\nWrite-Output X'</c>) printed itself with status 0 instead of
/// <c>$'\nWrite-Output X': command not found</c> (127); primitive <c>PsBuild.CommandWordReadsAsExpression</c>, the
/// message <c>CommandNotFoundReport.NotFoundMessage</c>;</item>
/// <item>a CR before an LF was dropped on the way to the host (<c>v=$'a\r\nb'</c> lost its CR, so
/// <c>[[ $v == $'a\r\nb' ]]</c> failed); primitive <c>HostProtocol.AppendCommandBody</c>;</item>
/// <item>a double-quoted <c>${v/p/"r"}</c> replacement kept its quotes and backslashes; now the pattern compiler
/// (<c>PsEmitter.Patterns.cs</c>);</item>
/// <item><c>declare -A m=(['a]=b']=c)</c> keyed <c>a</c> with value <c>'b]=c</c>; primitive
/// <c>BashLexer.SubscriptClose</c>;</item>
/// <item>a quoted or escaped command name (<c>"ls"</c>, <c>\ls</c>, <c>$'cd'</c>, <c>"$f"</c>) skipped the script's
/// function of that name: PowerShell's <c>&amp; "ls"</c> takes an alias first, and a bash alias (a global function)
/// answered a quoted name; primitives <c>PsEmitter.QuotedCommandName</c> and <c>Resolve-BashCommand</c> (a bash
/// function is returned as itself).</item>
/// </list>
/// </summary>
public class QuotingValueDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task CommandWord_WithANewline_IsNotFound() => Same(
        "\"$(printf 'a\\nWrite-Output PWNED-n19')\" 2>&1 | sed 's/^.*line [0-9]*: //'\n"
        + "$'\\nWrite-Output PWNED-n20' 2>&1 | sed 's/^.*line [0-9]*: //'\n"
        + "$'a\\n\"b' 2>&1 | sed 's/^.*line [0-9]*: //'\n"
        + "$'\\n$(Write-Output PWNED-n22)' 2>&1 | sed 's/^.*line [0-9]*: //'\n"
        + "$'ab' 2>/dev/null; echo \"st=$?\"\n"
        + "$'a b' x 2>/dev/null; echo \"st=$?\"\n"
        + "f() { $'\\nWrite-Output PWNED-f'; echo \"fst=$?\"; }; f 2>/dev/null\n");

    [SkippableFact]
    public Task CarriageReturn_BeforeANewline_IsKept() => Same(
        "v=$'a\\r\\nb'; echo $v | od -c | head -2\n"
        + "printf %s \"$v\" | od -c | head -2\n"
        + "echo \"len=${#v}\"\n"
        + "[[ $'a\\r\\nb' == $'a\\r\\nb' ]] && echo m1; [[ $'a\\nb' == $'a\\nb' ]] && echo m2; [[ $'a\\rb' == $'a\\rb' ]] && echo m3\n"
        + "[[ $v == $'a\\r\\nb' ]] && echo m4; x=\"$v\"; [ \"$x\" = $'a\\r\\nb' ] && echo m5\n");

    [SkippableFact]
    public Task PatternReplacement_DoubleQuotedReplacement_LosesItsQuoting() => Same(
        "v=abc; echo ${v/b/\"a\\\";x;\\\"b\"}\n"
        + "v=abc; echo ${v/b/\"q\\`\"}\n"
        + "v=abc; echo \"${v/b/\"a\\\"b\"}\"\n"
        + "v=abc; echo \"${v//b/\"\\\\\"}\"\n");

    [SkippableFact]
    public Task ArrayLiteral_QuotedSubscriptWithCloseAndEquals_IsOneKey() => Same(
        "declare -A m=(['a]=b']=c); declare -p m\n"
        + "declare -A n=([\"x]=y\"]=1 [k]=2); declare -p n\n"
        + "declare -A p; p['q]=r']=s; declare -p p\n"
        + "declare -A t=([$'u]=v']=w); declare -p t\n"
        + "i=1; a=([i+1]=x [3]=y); declare -p a\n");

    // Each function shadows a PowerShell alias name (or a bash keyword: select). Every spelling must reach the function.
    [SkippableFact]
    public Task QuotedCommandName_ResolvesLikeTheBareName() => Same("""
        ( cd() { printf 'F-cd %s\n' "$*"; }; "cd" a; 'cd' b; \cd c; $'cd' d; f=cd; "$f" e; $f g; cd h; c""d i )
        ( ls() { printf 'F-ls %s\n' "$*"; }; "ls" a; 'ls' b; \ls c; $'ls' d; f=ls; "$f" e; ls h )
        ( echo() { printf 'F-echo %s\n' "$*"; }; "echo" a; 'echo' b; \echo c; $'echo' d; f=echo; "$f" e; echo h )
        ( cat() { printf 'F-cat %s\n' "$*"; }; "cat" a; 'cat' b; \cat c; f=cat; "$f" e; cat h )
        ( sort() { printf 'F-sort %s\n' "$*"; }; "sort" a; 'sort' b; \sort c; f=sort; "$f" e; sort h )
        ( where() { printf 'F-where %s\n' "$*"; }; "where" a; 'where' b; \where c; f=where; "$f" e; where h )
        ( set() { printf 'F-set %s\n' "$*"; }; "set" a; 'set' b; \set c; f=set; "$f" e; set h )
        ( h() { printf 'F-h %s\n' "$*"; }; "h" a; 'h' b; \h c; f=h; "$f" e; h x )
        ( r() { printf 'F-r %s\n' "$*"; }; "r" a; 'r' b; \r c; f=r; "$f" e; r x )
        ( function select { printf 'F-select %s\n' "$*"; }; "select" a; 'select' b; \select c; f=select; "$f" e )
        ( eval 'h() { printf "F-h-eval %s\n" "$*"; }'; "h" a; h b; eval 'cd() { printf "F-cd-eval %s\n" "$*"; }'; "cd" c; cd d )
        ( cat() { printf 'F-cat-pipe %s\n' "$*"; }; printf 'x\n' | "cat" p; "echo" q | 'cat' )
        "true"; echo "true=$?"; 'false'; echo "false=$?"; "printf" '%s\n' prog-ok; 'env' true; echo "env=$?"
        "nosuch_q1" 2>/dev/null; echo "nf1=$?"; \nosuch_q2 2>/dev/null; echo "nf2=$?"
        shopt -s expand_aliases
        alias greet='printf "A-greet %s\n"'
        greet a
        "greet" b 2>/dev/null; echo "alias-quoted=$?"
        \greet c 2>/dev/null; echo "alias-escaped=$?"
        g=greet; $g d 2>/dev/null; echo "alias-expanded=$?"

        """);
}
