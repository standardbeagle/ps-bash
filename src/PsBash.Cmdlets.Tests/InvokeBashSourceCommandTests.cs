using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class InvokeBashSourceCommandTests
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
    public void SourceEnvFile_SetsEnvVarInCallerScope()
    {
        using var pwsh = CreatePwsh();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_test_{Guid.NewGuid()}.env");
        File.WriteAllText(tempFile, "export PSBASH_SOURCE_TEST_FOO=bar");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_TEST_FOO", null);
            var result = pwsh.AddScript(
                $"Invoke-BashSource '{tempFile.Replace("'", "''")}'; $env:PSBASH_SOURCE_TEST_FOO")
                .Invoke();
            Assert.NotEmpty(result);
            Assert.Equal("bar", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_TEST_FOO", null);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SourcePs1File_DotSourcesNatively()
    {
        using var pwsh = CreatePwsh();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_test_{Guid.NewGuid()}.ps1");
        File.WriteAllText(tempFile, "$env:PSBASH_SOURCE_PS1_TEST = 'fromps1'");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_PS1_TEST", null);
            var result = pwsh.AddScript(
                $"Invoke-BashSource '{tempFile.Replace("'", "''")}'; $env:PSBASH_SOURCE_PS1_TEST")
                .Invoke();
            Assert.NotEmpty(result);
            Assert.Equal("fromps1", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_PS1_TEST", null);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SourceWithArguments_SetsPositionalParams()
    {
        using var pwsh = CreatePwsh();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_test_{Guid.NewGuid()}.sh");
        File.WriteAllText(tempFile, "export PSBASH_SOURCE_ARG_TEST=$1");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_ARG_TEST", null);
            pwsh.AddScript(
                $"Invoke-BashSource '{tempFile.Replace("'", "''")}' hello")
                .Invoke();
            pwsh.Commands.Clear();
            var result = pwsh.AddScript("$env:PSBASH_SOURCE_ARG_TEST").Invoke();
            Assert.Single(result);
            Assert.Equal("hello", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_ARG_TEST", null);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SourceBashScript_TranspilesAndEvals()
    {
        using var pwsh = CreatePwsh();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_test_{Guid.NewGuid()}.sh");
        File.WriteAllText(tempFile, "export PSBASH_SOURCE_BASH_TEST=baz");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_BASH_TEST", null);
            var result = pwsh.AddScript(
                $"Invoke-BashSource '{tempFile.Replace("'", "''")}'; $env:PSBASH_SOURCE_BASH_TEST")
                .Invoke();
            Assert.NotEmpty(result);
            Assert.Equal("baz", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_BASH_TEST", null);
            File.Delete(tempFile);
        }
    }
}
