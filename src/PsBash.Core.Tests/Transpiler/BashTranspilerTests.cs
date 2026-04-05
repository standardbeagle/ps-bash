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
        Assert.Equal("cat $env:TEMP\\log.txt | Invoke-BashGrep \"error\"", result);
    }

    [Fact]
    public void FileTestWithVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("[ -f /etc/config ] && echo $MSG");
        Assert.Equal("[void](Test-Path \"/etc/config\" -PathType Leaf) && echo $env:MSG", result);
    }

    [Fact]
    public void HomePathWithPipe_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("ls ~/.config | head -n 5");
        Assert.Equal("ls $HOME\\.config | Invoke-BashHead -n 5", result);
    }

    [Fact]
    public void ComplexPipeline_TransformsAll()
    {
        var result = BashTranspiler.Transpile("cat /tmp/data.csv | grep -v header | sort | uniq | wc -l");
        Assert.Equal(
            "cat $env:TEMP\\data.csv | Invoke-BashGrep -NotMatch \"header\" | Invoke-BashSort | Invoke-BashUniq | Invoke-BashWc -l",
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
        Assert.Equal("cat file | Invoke-BashSed 's/old/new/' | Invoke-BashAwk '{print $1}'", result);
    }

    [Fact]
    public void FileTestEmptyVar_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("[ -z \"$HOME\" ] && echo empty");
        Assert.Equal("[void]([string]::IsNullOrEmpty($HOME)) && echo empty", result);
    }

    [Fact]
    public void FileTestWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -f ./README.md ] && echo \"exists\"");
        Assert.Equal("[void](Test-Path \"./README.md\" -PathType Leaf) && echo \"exists\"", result);
    }

    [Fact]
    public void DirTestWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -d ./src ] && echo \"is dir\"");
        Assert.Equal("[void](Test-Path \"./src\" -PathType Container) && echo \"is dir\"", result);
    }

    [Fact]
    public void ExportWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("export FOO=\"bar\" && echo $FOO");
        Assert.Equal("[void]($env:FOO = \"bar\") && echo $env:FOO", result);
    }

    [Fact]
    public void FileTestWithOr_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -f missing ] || echo \"not found\"");
        Assert.Equal("[void](Test-Path \"missing\" -PathType Leaf) || echo \"not found\"", result);
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
            // for loops are not yet in parser-v2; auto should fall back to regex
            var input = "for i in 1 2 3; do echo $i; done";
            Environment.SetEnvironmentVariable("PSBASH_PARSER", "v1");
            var regexResult = BashTranspiler.Transpile(input);

            Environment.SetEnvironmentVariable("PSBASH_PARSER", "auto");
            var autoResult = BashTranspiler.Transpile(input);

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
            // Default is auto mode: v2 parser with v1 regex fallback
            var result = BashTranspiler.Transpile("echo hello");
            Assert.Equal("echo hello", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_PARSER", prev);
        }
    }
}
