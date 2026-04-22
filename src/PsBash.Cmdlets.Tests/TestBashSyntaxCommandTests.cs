using Xunit;

namespace PsBash.Cmdlets.Tests;

public class TestBashSyntaxCommandTests
{
    [Fact]
    public void ValidSyntax_ReturnsTrue()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("Test-BashSyntax 'if [ $x = 1 ]; then echo hi; fi'").Invoke();
        Assert.Single(result);
        Assert.Equal(true, result[0].BaseObject);
    }

    [Fact]
    public void InvalidSyntax_ReturnsFalseAndWritesError()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("Test-BashSyntax 'if [ $x = 1 ] then'").Invoke();
        pwsh.Commands.Clear();

        Assert.Single(result);
        Assert.Equal(false, result[0].BaseObject);

        var errorRecord = pwsh.Streams.Error.LastOrDefault();
        Assert.NotNull(errorRecord);
        Assert.Contains("Expected 'fi'", errorRecord.Exception.Message);
    }
}
