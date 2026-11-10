using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>chmod</c> (it did not exist: <c>chmod +x script</c> was "command not found"). Usage errors and the owner-write
/// bit behave as in GNU; the other bits have nothing to change on Windows, so these cases assert status and messages,
/// not <c>ls -l</c> columns.
/// </summary>
public class ChmodTests
{
    [Theory]
    [InlineData("touch f; chmod +x f; echo st=$?")]
    [InlineData("touch a b; chmod u+x,g-w a b; echo st=$?")]
    [InlineData("touch f; chmod 755 f; echo st=$?")]
    [InlineData("touch f; chmod -x f; echo st=$?")]
    [InlineData("touch f; chmod -- 644 f; echo st=$?")]
    [InlineData("touch f; chmod a+rx,u+w f && echo ok")]
    [InlineData("mkdir -p d/e; touch d/e/f; chmod -R go-w d; echo st=$?")]
    [InlineData("name='my file'; touch \"$name\"; chmod +x \"$name\"; echo st=$?")]
    [InlineData("touch f; chmod 444 f; { echo x > f; } 2>/dev/null; echo st=$?; chmod 644 f; echo y > f; cat f")]
    [InlineData("touch f; chmod a-w f; rm -f f; [ -e f ] || echo gone")]
    public Task Chmod_ChangesMode(string script) => AssertOracle.EqualAsync(script);

    [Theory]
    [InlineData("chmod 2>/dev/null; echo st=$?")]
    [InlineData("chmod 644 2>/dev/null; echo st=$?")]
    [InlineData("touch f; chmod zzz f 2>/dev/null; echo st=$?")]
    [InlineData("touch f; chmod 9999 f 2>/dev/null; echo st=$?")]
    [InlineData("chmod 644 nosuch 2>&1; echo st=$?")]
    [InlineData("touch f; chmod 644 nosuch f 2>&1; echo st=$?")]
    [InlineData("chmod -f 644 nosuch 2>&1; echo st=$?")]
    [InlineData("touch f; chmod -q 644 f 2>&1; echo st=$?")]
    public Task Chmod_UsageErrors(string script) => AssertOracle.EqualAsync(script);
}
