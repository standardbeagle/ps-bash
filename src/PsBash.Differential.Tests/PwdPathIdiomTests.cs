using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Scripts take <c>$PWD</c>, <c>~</c> and the paths commands print apart on <c>/</c>: <c>${PWD##*/}</c>,
/// <c>${PWD%/*}</c>, <c>[[ $PWD == */x ]]</c>, <c>IFS=/ read</c>. ps-bash held <c>$PWD</c> as <c>C:\Users\x</c>, so
/// every one of them saw a single component (the whole path). Paths ps-bash produces now read <c>C:/Users/x</c>
/// (docs/specs/paths.md). Each case compares only names below a fresh temp directory, never an absolute path,
/// so bash and Windows agree.
/// </summary>
public class PwdPathIdiomTests
{
    private const string Setup =
        "b=\"$(mktemp -d)\"; mkdir -p \"$b/one/two\" \"$b/sp ace\"; cd \"$b/one\"\n";

    [Theory]
    [InlineData("echo \"${PWD##*/}\"")]
    [InlineData("p=${PWD%/*}; [ \"${p##*/}\" = \"${b##*/}\" ] && echo parent-ok")]
    [InlineData("[[ $PWD == */one ]] && echo match; [[ $PWD == */two ]] || echo no-match")]
    [InlineData("case $PWD in */one) echo case-ok;; *) echo case-bad;; esac")]
    [InlineData("cd two; echo \"${PWD##*/}\"; cd - >/dev/null; echo \"${PWD##*/} ${OLDPWD##*/}\"")]
    [InlineData("x=$(cd two; cd -); echo \"${x##*/}\"")]
    [InlineData("IFS=/ read -ra parts <<< \"$PWD\"; echo \"${parts[${#parts[@]}-1]}\"")]
    [InlineData("basename \"$PWD\"; d=$(dirname \"$PWD\"); echo \"${d##*/}\" | grep -c \"${b##*/}\"")]
    [InlineData("d=~+; echo \"${d##*/}\"; cd two; e=~-; echo \"${e##*/}\"")]
    [InlineData("HOME=$b/one; cd ~/two; echo \"${PWD##*/}\"; cd; echo \"${PWD##*/}\"")]
    [InlineData("cd \"$b/sp ace\"; echo \"${PWD##*/}\"; cd ../one/two; echo \"${PWD##*/}\"")]
    [InlineData("r=$(realpath two); echo \"${r##*/}\"; t=$(mktemp); [ \"${t%/*}\" != \"$t\" ] && echo has-slash")]
    [InlineData("for f in \"$PWD\"/*; do echo \"${f##*/}\"; done")]
    // Unquoted: the backslashes of C:\x were glob escapes, so `$b/*` matched nothing and printed C:Usersx.../*.
    [InlineData("for f in $b/*; do echo \"${f##*/}\"; done")]
    [InlineData("printf '%s\\n' \"$PWD\" \"$OLDPWD\" ~ \"$HOME\" \"$(pwd)\" \"$(mktemp)\" | grep -c '\\\\'")]
    public Task PathIdiom_SplitsOnSlash(string script) => AssertOracle.EqualAsync(Setup + script);
}
