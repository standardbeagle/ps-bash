using Xunit;

namespace PsBash.Cmdlets.Tests;

public class InvokeBashSourceCommandTests
{
    [Fact]
    public void SourceEnvFile_SetsEnvVarInCallerScope()
    {
        using var pwsh = PwshTestFixture.Create();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_test_{Guid.NewGuid()}.env");
        File.WriteAllText(tempFile, "export PSBASH_SOURCE_TEST_FOO=bar");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_TEST_FOO", null);
            pwsh.AddScript($"Invoke-BashSource '{tempFile.Replace("'", "''")}'").Invoke();
            pwsh.Commands.Clear();
            var result = pwsh.AddScript("$env:PSBASH_SOURCE_TEST_FOO").Invoke();
            Assert.Single(result);
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
        using var pwsh = PwshTestFixture.Create();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_test_{Guid.NewGuid()}.ps1");
        File.WriteAllText(tempFile, "$env:PSBASH_SOURCE_PS1_TEST = 'fromps1'");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_PS1_TEST", null);
            pwsh.AddScript($"Invoke-BashSource '{tempFile.Replace("'", "''")}'").Invoke();
            pwsh.Commands.Clear();
            var result = pwsh.AddScript("$env:PSBASH_SOURCE_PS1_TEST").Invoke();
            Assert.Single(result);
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
        using var pwsh = PwshTestFixture.Create();
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
        using var pwsh = PwshTestFixture.Create();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_test_{Guid.NewGuid()}.sh");
        File.WriteAllText(tempFile, "export PSBASH_SOURCE_BASH_TEST=baz");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_BASH_TEST", null);
            pwsh.AddScript($"Invoke-BashSource '{tempFile.Replace("'", "''")}'").Invoke();
            pwsh.Commands.Clear();
            var result = pwsh.AddScript("$env:PSBASH_SOURCE_BASH_TEST").Invoke();
            Assert.Single(result);
            Assert.Equal("baz", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_SOURCE_BASH_TEST", null);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SourceNonExistentPs1File_WritesError()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.Streams.Error.Clear();
        pwsh.AddScript("Invoke-BashSource 'C:/nonexistent/path/missing.ps1'").Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.Streams.Error.ReadAll();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Exception.Message.Contains("missing.ps1"));
    }

    [Fact]
    public void SourceNonExistentShFile_WritesError()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.Streams.Error.Clear();
        pwsh.AddScript("Invoke-BashSource 'C:/nonexistent/path/missing.sh'").Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.Streams.Error.ReadAll();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Exception.Message.Contains("missing.sh"));
    }
}
