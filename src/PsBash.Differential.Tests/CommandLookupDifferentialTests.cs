using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>command -v</c>/<c>-V</c> look NAME up in bash's order (alias, keyword, function, builtin, file), as
/// <c>type</c> does:
/// <list type="bullet">
/// <item>A builtin the emitter writes inline (<c>true</c>, <c>eval</c>) printed nothing, status 1.</item>
/// <item><c>-v</c> prints an alias as <c>alias NAME='VALUE'</c>.</item>
/// <item><c>-V</c> follows "f is a function" with the definition and reports a miss on stderr.</item>
/// </list>
/// <c>builtin NAME</c> refuses a name that is not a builtin ("not a shell builtin", status 1); it ran the file.
/// The stderr line prefix differs (<c>bash: line N:</c>), so stderr is matched by its tail or discarded.
/// </summary>
public class CommandLookupDifferentialTests
{
    [Theory]
    [InlineData("myfunc() { echo x; }\ncommand -v true eval echo myfunc for ZZZ; echo \"s=$?\"\ncommand -v ''; echo \"empty=$?\"\ncommand -v ZZZ; echo \"miss=$?\"")]
    [InlineData("shopt -s expand_aliases\nalias ll='ls -l'\nalias q=\"it's\"\ncommand -v ll q\ncommand -V ll")]
    [InlineData("myfunc() { echo x; }\ncommand -V true; command -V myfunc; command -V for\ncommand -V nonexistent 2>/dev/null; echo \"s=$?\"\ncommand -V nonexistent 2>&1 | grep -o 'nonexistent: not found'")]
    [InlineData("builtin ls 2>/dev/null; echo \"s=$?\"\nbuiltin ls 2>&1 | grep -o 'ls: not a shell builtin'\nbuiltin echo ok; echo \"s=$?\"\nf() { builtin nosuch 2>/dev/null || echo \"refused $?\"; }; f")]
    // A name is data: `;`, `$( )` and backticks in it are never run.
    [InlineData("command -v 'a;echo pwned' '$(echo pwned2)' '`echo pwned3`'; echo \"s=$?\"\nbuiltin 'x;echo pwned4' 2>/dev/null; echo \"s=$?\"")]
    public Task Command_LookupFollowsBashOrder(string script) => AssertOracle.EqualAsync(script);
}
