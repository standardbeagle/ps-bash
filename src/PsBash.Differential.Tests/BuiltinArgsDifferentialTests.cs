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

    // test / [ : no options at all, every word (dash-leading or not) is part of the expression.
    [SkippableTheory]
    [InlineData("[ -n -e ]; echo $?")]
    [InlineData("[ -z -z ]; echo $?")]
    [InlineData("x=-f; [ \"$x\" = -f ]; echo $?")]
    [InlineData("x=-f; [ \"$x\" != -f ]; echo $?")]
    [InlineData("[ ! -z x ]; echo $?")]
    [InlineData("[ ! -n '' ]; echo $?")]
    [InlineData("[ -o ]; echo $?")]
    [InlineData("[ -a ]; echo $?")]
    [InlineData("[ -f = -f ]; echo $?")]
    [InlineData("[ -n -a -n ]; echo $?")]
    [InlineData("[ -n x -a -n y ]; echo $?")]
    [InlineData("[ -n x -a -z y ]; echo $?")]
    [InlineData("[ -z x -o -n y ]; echo $?")]
    [InlineData("[ ! -z x -a ! -z y ]; echo $?")]
    [InlineData("[ \\( -n x \\) -a -n y ]; echo $?")]
    [InlineData("[ 1 -eq 1 -a 2 -lt 3 ]; echo $?")]
    [InlineData("[ -e -e ]; echo $?")]
    [InlineData("[ -d -d ]; echo $?")]
    [InlineData("[ -w -w ]; echo $?")]
    [InlineData("[ x = -e ]; echo $?")]
    [InlineData("test -n -e; echo $?")]
    [InlineData("test -z -z; echo $?")]
    [InlineData("test -o; echo $?")]
    [InlineData("test ! -z x; echo $?")]
    [InlineData("test -a -a -a; echo $?")]
    [InlineData("test -o -o -o -o; echo $?")]
    [InlineData("test -n -n -a -n; echo $?")]
    [InlineData("test 5 -gt 3 -a 2 -lt 4; echo $?")]
    [InlineData("test x = -f; echo $?")]
    [InlineData("test --help; echo $?")]
    [InlineData("test --version; echo $?")]
    [InlineData("test -h; echo $?")]
    [InlineData("test a b 2>/dev/null; echo $?")]
    [InlineData("test 1 -eq x 2>/dev/null; echo $?")]
    [InlineData("test -n x y 2>/dev/null; echo $?")]
    [InlineData("test a = b c 2>/dev/null; echo $?")]
    [InlineData("test -n -n -n -n 2>/dev/null; echo $?")]
    [InlineData("test ! 2>/dev/null; echo $?")]
    [InlineData("test 2>/dev/null; echo $?")]
    [InlineData("test ''; echo $?")]
    [InlineData("d=$(mktemp -d) && printf '' > \"$d/f\" && mkdir \"$d/dir\" && test -e \"$d/f\" -a -d \"$d/dir\"; echo $?; test -e \"$d/nope\" -o -f \"$d/f\"; echo $?; test -w \"$d/nope\"; echo $?; ls \"$d\"")]
    [InlineData("d=$(mktemp -d) && printf '' > \"$d/f\" && test -s \"$d/f\"; echo $?; echo x > \"$d/f\"; test -s \"$d/f\"; echo $?; test -r \"$d/f\" -a -w \"$d/f\"; echo $?")]
    [InlineData("d=$(mktemp -d) && printf '' > \"$d/a\" && test \"$d/a\" -ef \"$d/a\"; echo $?; test \"$d/a\" -nt \"$d/missing\"; echo $?; test \"$d/missing\" -ot \"$d/a\"; echo $?")]
    public Task Test_BuiltinHasNoOptions(string script) => Eq(script);
}