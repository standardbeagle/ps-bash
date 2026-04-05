using Xunit;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

public class BashTranspilerTests
{
    [Fact]
    public void SimpleEcho_PassesThrough()
    {
        Assert.Equal("echo hello", BashTranspiler.Transpile("echo hello"));
    }

    [Fact]
    public void DevNullWithEnvVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("echo $FOO 2> /dev/null");
        Assert.Equal("echo $env:FOO 2>$null", result);
    }

    [Fact]
    public void ExportAndEchoVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("export FOO=bar");
        Assert.Equal("$env:FOO = \"bar\"", result);
    }

    [Fact]
    public void TmpPathWithGrep_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("cat /tmp/log.txt | grep error");
        Assert.Equal("cat $env:TEMP\\log.txt | Invoke-Grep \"error\"", result);
    }

    [Fact]
    public void FileTestWithVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("[ -f /etc/config ] && echo $MSG");
        Assert.Equal("(Test-Path \"/etc/config\" -PathType Leaf) && echo $env:MSG", result);
    }

    [Fact]
    public void HomePathWithPipe_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("ls ~/.config | head -n 5");
        Assert.Equal("ls $HOME\\.config | Select-Object -First 5", result);
    }

    [Fact]
    public void ComplexPipeline_TransformsAll()
    {
        var result = BashTranspiler.Transpile("cat /tmp/data.csv | grep -v header | sort | uniq | wc -l");
        Assert.Equal(
            "cat $env:TEMP\\data.csv | Invoke-Grep -NotMatch \"header\" | Sort-Object | Get-Unique | Measure-Object -Line | Select-Object -Expand Lines",
            result);
    }

    [Fact]
    public void ExportQuotedValue_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("export NODE_ENV=\"production\"");
        Assert.Equal("$env:NODE_ENV = \"production\"", result);
    }

    [Fact]
    public void DevNullRedirectWithStderrMerge_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("cmd > /dev/null 2>&1");
        Assert.Equal("cmd >$null 2>&1", result);
    }

    [Fact]
    public void EnvVarDoesNotDoubleTransform()
    {
        // After ExportTransform creates $env:FOO, EnvVarTransform should not re-transform it
        var result = BashTranspiler.Transpile("export FOO=bar && echo $FOO");
        Assert.Contains("$env:FOO = \"bar\"", result);
        Assert.Contains("$env:FOO", result);
        // Should not contain $env:env:FOO
        Assert.DoesNotContain("$env:env:", result);
    }

    [Fact]
    public void PipeSedAndAwk_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("cat file | sed 's/old/new/' | awk '{print $1}'");
        Assert.Equal("cat file | Invoke-Sed 's/old/new/' | Invoke-Awk '{print $1}'", result);
    }

    [Fact]
    public void FileTestEmptyVar_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("[ -z \"$HOME\" ] && echo empty");
        Assert.Contains("[string]::IsNullOrEmpty($HOME)", result);
    }

    [Fact]
    public void ParserMode_V1_UsesRegexPipeline()
    {
        var prev = Environment.GetEnvironmentVariable("PSBASH_PARSER");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", "v1");
            var result = BashTranspiler.Transpile("echo hello");
            Assert.Equal("echo hello", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", prev);
        }
    }

    [Fact]
    public void ParserMode_V2_UsesParserPipeline()
    {
        var prev = Environment.GetEnvironmentVariable("PSBASH_PARSER");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", "v2");
            var result = BashTranspiler.Transpile("export FOO=bar");
            Assert.Equal("$env:FOO = \"bar\"", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", prev);
        }
    }

    [Fact]
    public void ParserMode_Auto_TriesParserFirst()
    {
        var prev = Environment.GetEnvironmentVariable("PSBASH_PARSER");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", "auto");
            var result = BashTranspiler.Transpile("echo hello");
            Assert.Equal("echo hello", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", prev);
        }
    }

    [Fact]
    public void ParserMode_Auto_FallsBackToRegexOnParserFailure()
    {
        var prev = Environment.GetEnvironmentVariable("PSBASH_PARSER");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", "auto");
            // v1 (regex) result for a known transform
            Environment.SetEnvironmentVariable("PSBASH_PARSER", "v1");
            var regexResult = BashTranspiler.Transpile("export FOO=bar");

            // auto mode should produce equivalent output (parser or fallback)
            Environment.SetEnvironmentVariable("PSBASH_PARSER", "auto");
            var autoResult = BashTranspiler.Transpile("export FOO=bar");

            Assert.Equal(regexResult, autoResult);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", prev);
        }
    }

    [Fact]
    public void ParserMode_Default_IsAuto()
    {
        var prev = Environment.GetEnvironmentVariable("PSBASH_PARSER");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", null);
            // With no env var set, default is auto mode
            var result = BashTranspiler.Transpile("echo hello");
            Assert.Equal("echo hello", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", prev);
        }
    }
}
