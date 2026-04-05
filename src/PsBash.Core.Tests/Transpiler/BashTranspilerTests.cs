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
        Assert.Equal("cat $env:TEMP\\log.txt | Invoke-BashGrep error", result);
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
            "cat $env:TEMP\\data.csv | Invoke-BashGrep -v header | Invoke-BashSort | Invoke-BashUniq | Invoke-BashWc -l",
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
        var result = BashTranspiler.Transpile("export FOO=bar && echo $FOO");
        Assert.Contains("$env:FOO = \"bar\"", result);
        Assert.Contains("$env:FOO", result);
        Assert.DoesNotContain("$env:env:", result);
    }

    [Fact]
    public void PipeSedAndAwk_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("cat file | sed 's/old/new/' | awk '{print $1}'");
        Assert.Equal("cat file | Invoke-BashSed 's/old/new/' | Invoke-BashAwk '{print $1}'", result);
    }

    [Fact]
    public void AwkWithFlags_PreservesExpression()
    {
        var result = BashTranspiler.Transpile("echo \"a,b,c\" | awk -F, '{print $1, $3}'");
        Assert.Equal("echo \"a,b,c\" | Invoke-BashAwk \"-F,\" '{print $1, $3}'", result);
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
    public void TmpPath_TransformsToEnvTemp()
    {
        var result = BashTranspiler.Transpile("echo /tmp/test");
        Assert.Contains("$env:TEMP", result);
    }
}
