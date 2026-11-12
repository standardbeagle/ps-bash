using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Redirects to the shell's own streams and to /dev/full. ps-bash opened <c>/dev/stdout</c>, <c>/dev/stderr</c> and
/// <c>/dev/full</c> as files under <c>C:\dev</c>: a "No such file" error, or output lost into a file where that
/// directory existed. The scripts keep stderr off the compared stdout (bash's write-error text names the builtin), and
/// `>/dev/stdout` runs into `| cat`: the WSL oracle's own stdout is a Windows pipe that /dev/stdout cannot reopen.
/// </summary>
public class DevStreamRedirectDifferentialTests
{
    [Theory]
    [InlineData("{ echo a >/dev/stdout; echo b >/dev/stderr; echo c 1>/dev/fd/2; echo st=$?; } 2>/dev/null | cat")]
    [InlineData("{ ls /nonexist-zz 2>/dev/stdout | sed 's/^/OUT:/'; ls /nonexist-zz 2>/dev/stderr | sed 's/^/OUT:/'; } 2>/dev/null")]
    [InlineData("{ { echo e; ls /nonexist-zz; } &>/dev/stdout | sed 's/^/ALL:/'; x=$(echo cap >/dev/stderr); echo \"x=[$x]\"; } 2>/dev/null")]
    [InlineData("{ echo hi > /dev/full; echo s=$?; printf '%s\\n' hi > /dev/full; echo s=$?; true > /dev/full; echo s=$?; } 2>/dev/null")]
    public Task Redirect_ToDevStreamsAndFull_MatchesBash(string script) => AssertOracle.EqualAsync(script);
}