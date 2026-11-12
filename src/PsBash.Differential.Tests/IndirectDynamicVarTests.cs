using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>${!ref}</c> naming a variable bash computes on read (LINENO, RANDOM, SECONDS, ...) expands to its
/// value, as the direct <c>$NAME</c> does; it was empty.</summary>
public class IndirectDynamicVarTests
{
    [Fact]
    public Task IndirectLineno_IsTheStatementLine() => AssertOracle.EqualAsync(
        "ref='LINENO'\necho lineno=${!ref}\nf() {\n  local r=LINENO\n  echo in=${!r} ${!r:-x}\n}\nf");

    [Fact]
    public Task IndirectComputedVars_AreNumbers() => AssertOracle.EqualAsync(
        "for n in RANDOM SECONDS EPOCHSECONDS EPOCHREALTIME BASHPID UID EUID; do\n  v=${!n}\n"
        + "  case $v in '') echo \"$n empty\";; *[!0-9.]*) echo \"$n bad\";; *) echo \"$n ok\";; esac\ndone");
}
