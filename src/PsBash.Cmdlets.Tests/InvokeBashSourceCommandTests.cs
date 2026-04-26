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
        var missingPath = Path.Combine(Path.GetTempPath(), $"psbash_missing_{Guid.NewGuid()}.ps1");
        pwsh.Streams.Error.Clear();
        pwsh.AddScript($"Invoke-BashSource '{missingPath.Replace("'", "''")}'").Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.Streams.Error.ReadAll();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Exception.Message.Contains(".ps1"));
    }

    [Fact]
    public void SourceNonExistentShFile_WritesError()
    {
        using var pwsh = PwshTestFixture.Create();
        var missingPath = Path.Combine(Path.GetTempPath(), $"psbash_missing_{Guid.NewGuid()}.sh");
        pwsh.Streams.Error.Clear();
        pwsh.AddScript($"Invoke-BashSource '{missingPath.Replace("'", "''")}'").Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.Streams.Error.ReadAll();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Exception.Message.Contains(".sh"));
    }

    [Fact]
    public void SourceShFile_RelativePath_ResolvesAgainstCwd()
    {
        // Arrange: write a .sh file in a temp directory and source it via a relative path.
        // Verify: env var set by the sourced script is visible in caller scope.
        // ps-bash-specific: GetUnresolvedProviderPathFromPSPath resolves relative to PS $PWD.
        using var pwsh = PwshTestFixture.Create();
        var tempDir = Path.Combine(Path.GetTempPath(), $"psbash_rel_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var scriptName = "rel_source_test.sh";
        var scriptPath = Path.Combine(tempDir, scriptName);
        File.WriteAllText(scriptPath, "export PSBASH_REL_SOURCE_TEST=relative_ok");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_REL_SOURCE_TEST", null);
            // Change PS working directory to tempDir, then source by bare filename.
            pwsh.AddScript($"Set-Location '{tempDir.Replace("'", "''")}'").Invoke();
            pwsh.Commands.Clear();
            pwsh.AddScript($"Invoke-BashSource '{scriptName}'").Invoke();
            pwsh.Commands.Clear();
            var result = pwsh.AddScript("$env:PSBASH_REL_SOURCE_TEST").Invoke();
            Assert.Single(result);
            Assert.Equal("relative_ok", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_REL_SOURCE_TEST", null);
            File.Delete(scriptPath);
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void SourceWithMultipleArguments_SetsAllPositionalParams()
    {
        // Arrange: script uses $1 and $2 (via $global:BashPositional) to build a string.
        // Verify: both positional params are available when multiple args are passed.
        // ps-bash-specific: BashPositional array is the mechanism; no bash subprocess.
        using var pwsh = PwshTestFixture.Create();
        var tempFile = Path.Combine(Path.GetTempPath(), $"psbash_source_multiarg_{Guid.NewGuid()}.sh");
        // Script exports FIRST=$1 and SECOND=$2 so caller can inspect both.
        File.WriteAllText(tempFile, "export PSBASH_MULTIARG_FIRST=$1\nexport PSBASH_MULTIARG_SECOND=$2");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_MULTIARG_FIRST", null);
            Environment.SetEnvironmentVariable("PSBASH_MULTIARG_SECOND", null);
            pwsh.AddScript(
                $"Invoke-BashSource '{tempFile.Replace("'", "''")}' alpha beta")
                .Invoke();
            pwsh.Commands.Clear();
            var first = pwsh.AddScript("$env:PSBASH_MULTIARG_FIRST").Invoke();
            pwsh.Commands.Clear();
            var second = pwsh.AddScript("$env:PSBASH_MULTIARG_SECOND").Invoke();
            Assert.Single(first);
            Assert.Equal("alpha", first[0].ToString());
            Assert.Single(second);
            Assert.Equal("beta", second[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_MULTIARG_FIRST", null);
            Environment.SetEnvironmentVariable("PSBASH_MULTIARG_SECOND", null);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void NestedSource_ScriptASourcesScriptB_BExportsVisibleInCaller()
    {
        // Arrange: script A sources script B; script B exports an env var.
        // Verify: the env var exported by B is visible in the outer (caller) scope.
        // ps-bash-specific: tests that recursive Invoke-BashSource shares the runspace scope.
        using var pwsh = PwshTestFixture.Create();
        var tempDir = Path.GetTempPath();
        var scriptBName = $"psbash_nested_b_{Guid.NewGuid()}.sh";
        var scriptAName = $"psbash_nested_a_{Guid.NewGuid()}.sh";
        var scriptBPath = Path.Combine(tempDir, scriptBName);
        var scriptAPath = Path.Combine(tempDir, scriptAName);
        File.WriteAllText(scriptBPath, "export PSBASH_NESTED_TEST=from_b");
        // Script A sources script B using its absolute path.
        File.WriteAllText(scriptAPath, $"source '{scriptBPath.Replace("'", "\\'")}'");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_NESTED_TEST", null);
            pwsh.AddScript($"Invoke-BashSource '{scriptAPath.Replace("'", "''")}'").Invoke();
            pwsh.Commands.Clear();
            var result = pwsh.AddScript("$env:PSBASH_NESTED_TEST").Invoke();
            Assert.Single(result);
            Assert.Equal("from_b", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_NESTED_TEST", null);
            File.Delete(scriptAPath);
            File.Delete(scriptBPath);
        }
    }
}
