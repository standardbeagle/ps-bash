using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Bash BUILTIN option semantics for echo / printf / test, which are NOT getopt tools:
/// every dash word must reach the builtin verbatim (PsEmitter.OrderedArgCommands).
/// Output is bracketed so a missing/extra trailing newline is visible.
/// </summary>
public class BuiltinArgsDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableTheory]
    [InlineData("echo -n -e 'a\\tb'; echo '|'")]
    [InlineData("echo -ne 'a\\tb'; echo '|'")]
    [InlineData("echo -en x; echo '|'")]
    [InlineData("echo -nx; echo '|'")]
    [InlineData("echo -- a")]
    [InlineData("echo -e -n x; echo '|'")]
    [InlineData("echo -")]
    [InlineData("echo -E 'a\\tb'")]
    [InlineData("echo --help")]
    [InlineData("echo -n- a")]
    [InlineData("echo -neE 'x\\ty'; echo '|'")]
    [InlineData("echo -e -- x")]
    [InlineData("echo -x -n a")]
    [InlineData("echo -e; echo '|'")]
    [InlineData("echo -e 'a\\cb' c; echo '|'")]
    [InlineData("echo -eE 'a\\tb'")]
    [InlineData("echo -Ee 'a\\tb'")]
    [InlineData("echo -e -E 'a\\tb'")]
    [InlineData("echo -E -e 'a\\tb'")]
    [InlineData("echo --version")]
    [InlineData("echo -e-n x")]
    [InlineData("echo hello -n")]
    public Task Echo_BuiltinOptionScan(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("printf -x 2>/dev/null; echo rc=$?")]
    [InlineData("printf -v 2>/dev/null; echo rc=$?")]
    [InlineData("printf -- 2>/dev/null; echo rc=$?")]
    [InlineData("printf 2>/dev/null; echo rc=$?")]
    [InlineData("printf -- -x; echo")]
    [InlineData("printf -- '%s\\n' -n")]
    [InlineData("printf '%s\\n' -n -v --")]
    [InlineData("printf -- --; echo")]
    [InlineData("printf -; echo")]
    [InlineData("printf -xv 2>/dev/null; echo rc=$?")]
    [InlineData("printf '-n\\n' 2>/dev/null; echo rc=$?")]
    [InlineData("printf -v 1bad x 2>/dev/null; echo rc=$?")]
    [InlineData("printf -v x '%s-%d' a 5; echo \"x=[$x]\"")]
    [InlineData("printf -v x '%s\\n' a; echo \"x=[$x]\"")]
    [InlineData("printf -v x -- '-%s' a; echo \"x=[$x]\"")]
    [InlineData("printf -vx '%s' hi; echo \"x=[$x]\"")]
    [InlineData("printf -v x -v y z; echo \"x=[$x] y=[$y]\"")]
    [InlineData("x=keep; printf -v x 2>/dev/null; echo \"x=[$x] rc=$?\"")]
    [InlineData("printf -v x ''; echo \"x=[$x]\"")]
    [InlineData("printf -v x '%s %s' a b c d; echo \"x=[$x]\"")]
    public Task Printf_BuiltinOptionScan(string script) => Eq(script);
}