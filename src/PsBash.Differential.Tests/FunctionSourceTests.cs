using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>declare -f</c> / <c>typeset -f</c> / <c>type</c> print a function's BASH source (it used to be the emitted
/// PowerShell), so the text can be evaluated again: <c>eval "$(declare -f f)"</c>, <c>bash -c "$(declare -f f); f"</c>,
/// <c>ssh host "$(declare -f f); f"</c>. The layout is the source as written, so these cases either re-run the text
/// or use a body bash prints unchanged.
/// </summary>
public class FunctionSourceTests
{
    [Theory]
    [InlineData("f() ( echo 'subshell body' ); code=$(typeset -f f); bash -c \"$code; f\"")]
    [InlineData("outer() {\n  echo outer\n  : prefix; inner() {\n    echo inner\n  }\n}\ncode=$(typeset -f outer)\nbash -c \"$code; outer; inner\"")]
    [InlineData("g() { echo \"a b\" | while read x y; do echo \"$y-$x\"; done; }; eval \"$(declare -f g | sed 's/^g /h /')\"; h")]
    [InlineData("k() { cat <<E\ndoc $1\nE\n}\neval \"$(declare -f k | sed 's/^k /k2 /')\"; k2 arg")]
    [InlineData("p() { echo \"p: $*\" >&2; } 2>&1\neval \"$(declare -f p | sed 's/^p /q /')\"; q hi")]
    [InlineData("n() {\n    if [ \"$1\" = x ]; then\n        echo \"is x\"\n    fi\n}\ndeclare -f n > n.sh; bash -c '. ./n.sh; n x'")]
    [InlineData("s() { local v=1; echo \"$v $FUNCNAME\"; }; eval \"$(declare -f s | sed 's/^s /t /')\"; t")]
    [InlineData("q() { echo \"it's \\\"quoted\\\" \\$HOME #not-a-comment\"; }; eval \"$(declare -f q | sed 's/^q /q2 /')\"; q2")]
    public Task DeclareF_TextRunsAgain(string script) => AssertOracle.EqualAsync(script);

    // Bodies bash prints exactly as written: one command per line, four-space indent.
    [Theory]
    [InlineData("f() {\n    echo one\n}\ndeclare -f f")]
    [InlineData("f () \n{ \n    echo one;\n    echo two\n}\ntypeset -f f")]
    [InlineData("f() {\n    echo one\n}\ntype f")]
    [InlineData("f() { echo one; }; declare -f f; declare -F f; type -t f")]
    [InlineData("k() {\n    cat <<E\ndoc $1\nE\n\n}\ndeclare -f k")]
    [InlineData("declare -f nosuchfn; echo st=$?")]
    public Task DeclareF_PrintsBashLayout(string script) => AssertOracle.EqualAsync(script);

    // A bare $FUNCNAME is the running function (element 0), empty outside one.
    [Theory]
    [InlineData("f() { echo \"[$FUNCNAME][${FUNCNAME}]\" $FUNCNAME; g; }; g() { local me=$FUNCNAME; echo \"[$me][${FUNCNAME[1]}]\"; }; f")]
    [InlineData("g() { [[ $FUNCNAME == g ]] && echo yes; echo \"${FUNCNAME:-none} ${FUNCNAME:0:1}\"; }; g; echo \"[$FUNCNAME][${FUNCNAME:-none}]\"")]
    [InlineData("log() { echo \"$FUNCNAME: $*\"; }; log a b")]
    [InlineData("set -u; f() { echo \"in $FUNCNAME\"; }; f; { echo \"$FUNCNAME\"; } 2>/dev/null; echo not-reached")]
    public Task BareFuncname_IsTheRunningFunction(string script) => AssertOracle.EqualAsync(script);
}
