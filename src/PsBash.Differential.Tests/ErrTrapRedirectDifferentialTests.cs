using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// The ERR trap and redirects / PIPESTATUS. A compound command whose stdout file cannot be opened fails (status 1,
/// the body never runs) and runs the trap with $LINENO of the command before it; ps-bash ran nothing. A plain
/// command's PIPESTATUS was recorded after the trap ran, so the trap saw the previous pipeline's. The scripts
/// silence stderr: bash prefixes the open error with <c>line N:</c>.
/// </summary>
public class ErrTrapRedirectDifferentialTests
{
    private const string Trap = "trap 'echo \"err $LINENO\"' ERR\n";

    [Theory]
    [InlineData("{\nfalse\n{ false\n  true\n} > /nodir/f\necho \"st $?\"\n} 2>/dev/null")]
    [InlineData("{\n{ echo x > /nodir/x; echo in; } > /dev/null\necho a\nfor i in 1; do echo y; done > /nodir/f\necho b\n} 2>/dev/null")]
    [InlineData("{\necho a\nset -e\n{ echo z; } > /nodir/f\necho no\n} 2>/dev/null")]
    public Task ErrTrap_RunsWhenACompoundsRedirectFails(string script) => AssertOracle.EqualAsync(Trap + script);

    /// <summary>bash runs the trap twice for a failed pipeline whose last stage is a subshell, once otherwise.</summary>
    [Fact]
    public Task ErrTrap_RunsTwiceForAPipelineEndingInASubshell() =>
        AssertOracle.EqualAsync("trap 'echo \"err $LINENO\"' ERR\ntrue | (false)\n(false) | true\n(false)\ntrue | (exit 3) | cat\n"
            + "true | { false; }\nf() { true | (false); }; f\necho ---\nset -o pipefail\n(false) | (true)\ntrue | (false)\necho end");
    [Fact]
    public Task ErrTrap_SeesThePlainCommandsPipestatus() =>
        AssertOracle.EqualAsync("trap 'echo \"err [${PIPESTATUS[@]}]\"' ERR\nfalse\necho B | grep zzz\ntrue\necho C");
}