using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// errtrace (<c>set -E</c> / <c>set -o errtrace</c>) makes the ERR trap fire inside functions, subshells and background
/// jobs. <c>set -o errtrace</c> only reached the shopt table and <c>set -eE</c> dropped the <c>E</c>, so the trap never
/// ran there. A background job is a subshell: <c>set -e</c> ends the job (not the script) on a failure; the job's body
/// had no check and the in-process job's runspace had none of the parent's options.
/// </summary>
public class ErrtraceDifferentialTests
{
    private const string Trap = "trap 'echo \"err $LINENO\"' ERR\n";

    [Theory]
    [InlineData("f() { false; true; }\nf\nset -o errtrace\nf\necho ok")]
    [InlineData("set -eE\nf() { false\n  echo no; }\nf || echo \"rc $?\"\necho ok")]
    [InlineData("set -E\n( false; echo sub )\nx=$( false; echo cs )\necho \"$x\"")]
    [InlineData("set -E\n{ false; echo job; } & wait\nfalse & wait\nset +E\n{ false; echo quiet; } & wait")]
    [InlineData("f() { false; true; }\nset -E\nset +o errtrace\nf\necho ok")]
    public Task Errtrace_TrapFiresInsideFunctionsSubshellsAndJobs(string script) => AssertOracle.EqualAsync(Trap + script);

    [Theory]
    [InlineData("set -e\n{ false; echo x; } & wait $! || echo \"st $?\"\necho after")]
    [InlineData("set -e\n{ exit 3; echo no; } & wait $! || echo \"st $?\"")]
    [InlineData("set -u\n{ echo \"${nope}\"; echo no; } 2>/dev/null & wait $! || echo \"st $?\"")]
    [InlineData("set -e\n( false; echo sub ) || echo \"st $?\"\n{ true; echo y; } & wait $!; echo \"st $?\"")]
    public Task BackgroundJob_RunsUnderTheParentsOptions(string script) => AssertOracle.EqualAsync(script);

    [Theory]
    [InlineData("set -eE; echo $-; echo $SHELLOPTS; [[ -o errtrace ]] && echo on; set +E; [[ -o errtrace ]] || echo off")]
    [InlineData("set -o errtrace; shopt -po errtrace; set +o errtrace; shopt -po errtrace; o=pipefail; [[ -o $o ]]; echo $?")]
    public Task Errtrace_IsVisibleAsAnOption(string script) => AssertOracle.EqualAsync(script);
}
