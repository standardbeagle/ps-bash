using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class TestBashSyntaxCommandTests
{
    private static PowerShell CreatePwsh()
    {
        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var pwsh = PowerShell.Create();
        pwsh.Runspace = runspace;

        var cmdletPsd1 = Path.Combine(AppContext.BaseDirectory, "PsBash.Cmdlets.psd1");
        pwsh.AddCommand("Import-Module").AddParameter("Name", cmdletPsd1).Invoke();
        pwsh.Commands.Clear();

        return pwsh;
    }

    [Fact]
    public void ValidSyntax_ReturnsTrue()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript("Test-BashSyntax 'if [ $x = 1 ]; then echo hi; fi'").Invoke();
        Assert.Single(result);
        Assert.Equal(true, result[0].BaseObject);
    }

    [Fact]
    public void InvalidSyntax_ReturnsFalseAndWritesError()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript("Test-BashSyntax 'if [ $x = 1 ] then'").Invoke();
        pwsh.Commands.Clear();

        Assert.Single(result);
        Assert.Equal(false, result[0].BaseObject);

        var errorRecord = pwsh.Streams.Error.LastOrDefault();
        Assert.NotNull(errorRecord);
        Assert.Contains("Expected 'fi'", errorRecord.Exception.Message);
    }
}
