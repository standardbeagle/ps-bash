using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class ConvertToPowerShellCommandTests
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
    public void PipelineInput_ReturnsTranspiledString()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript("'ls -la | grep .txt' | ConvertTo-PowerShell").Invoke();
        Assert.Single(result);
        Assert.Equal("Invoke-BashLs -la | Invoke-BashGrep .txt", result[0].ToString());
    }

    [Fact]
    public void WithMap_ReturnsObjectWithPowerShellAndMap()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript("ConvertTo-PowerShell 'echo hello' -WithMap").Invoke();
        Assert.Single(result);
        var obj = result[0];
        Assert.NotNull(obj);
        var psObj = (PSObject)obj;
        Assert.Equal("Invoke-BashEcho hello", psObj.Properties["PowerShell"]?.Value?.ToString());
        var map = psObj.Properties["Map"]?.Value as System.Collections.IEnumerable;
        Assert.NotNull(map);
        int count = 0;
        foreach (var _ in map) count++;
        Assert.Equal(1, count);
    }
}
