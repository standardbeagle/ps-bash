using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A builtin's own error carries the shell's prefix with the line (<c>s.sh: line 22: cd: /x: No such file or
/// directory</c>), its usage line none. cd, unalias and <c>[: a: integer expression expected</c> wrote no prefix; type,
/// declare, alias, help, builtin, local and wait hard-coded <c>bash: </c>; <c>[ 1 -eq 1</c> named an earlier line; a
/// readonly <c>local</c> in a function named the caller's line; <c>local -p NAME</c> in a function without locals ran a
/// program <c>local</c> (status 127). docs/specs/error-handling.md "Diagnostic prefix". Recorded from bash 5.2
/// (cassette). Streams are captured apart (<c>&gt;out 2&gt;err</c>): only the order within each is compared.
/// </summary>
public class BuiltinDiagnosticDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";

    [SkippableFact]
    public Task BuiltinError_IsTheShellsDiagnostic()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "unalias\n"
            + "echo \"rc=$?\"\n"
            + "test a -eq 1\n"
            + "echo \"rc=$?\"\n"
            + "[ a -eq 1 ]\n"
            + "echo \"rc=$?\"\n"
            + "[ 1 -eq 1\n"
            + "echo \"rc=$?\"\n"
            + "test 1 -eq\n"
            + "echo \"rc=$?\"\n"
            + "wait %5\n"
            + "echo \"rc=$?\"\n"
            + "wait xyz\n"
            + "echo \"rc=$?\"\n"
            + "readonly y=1\n"
            + "f() { local y=2; echo \"in rc=$?\"; }\n"
            + "f\n"
            + "help -Q\n"
            + "echo \"rc=$?\"\n"
            + "declare -Q\n"
            + "echo \"rc=$?\"\n"
            + "cd /nonexist_zz\n"
            + "echo \"rc=$?\"\n"
            + "cd a b\n"
            + "echo \"rc=$?\"\n"
            + "builtin nosuch\n"
            + "echo \"rc=$?\"\n"
            + "g() { local -p nosuchv; echo \"rc=$?\"; }\n"
            + "g\n"
            + "type nosuchcmd\n"
            + "echo \"rc=$?\"\n"
            + "alias nosuchal\n"
            + "echo \"rc=$?\"\n"
            + "EOF\n"
            + "bash s.sh >out 2>err; cat err; echo ---; cat out" + Done,
            timeout: TimeSpan.FromSeconds(60));
}
