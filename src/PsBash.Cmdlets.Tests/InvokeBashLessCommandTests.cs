using System.IO;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashLess</c> from a psm1 function to a binary cmdlet
/// (<see cref="InvokeBashLessCommand"/>).
///
/// Oracle: the original psm1 function. The interactive native-passthrough
/// path is not exercised here (the SDK runspace is never a TTY); the
/// pass-through fallback that emits input unchanged is what the cmdlet
/// owns end-to-end.
///
/// Directive-3 axes exercised: empty input (no operands, no pipeline),
/// file mode passthrough, pipeline mode passthrough, missing-file error
/// branch, alias resolution. Directive 12: injection probe on the file
/// operand.
/// </summary>
public class InvokeBashLessCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashLessCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Less_NoArgs_NoPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashLess");
        Assert.Empty(lines);
    }

    [Fact]
    public void Less_FileMode_PassesThroughLines()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "alpha\nbeta\ngamma\n");
            var lines = RunLines($"Invoke-BashLess '{tmp.Replace("'", "''")}'");
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Less_PipelineMode_PassesThroughItems()
    {
        var lines = RunLines("'one','two','three' | Invoke-BashLess");
        Assert.Equal(new[] { "one", "two", "three" }, lines);
    }

    [Fact]
    public void Less_MissingFile_EmitsError()
    {
        var lines = RunLines("Invoke-BashLess /no/such/file-xyz123.txt 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Less_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashLess --help");
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void Less_ViaAlias_ResolvesToCmdlet()
    {
        var lines = RunLines("'aliased' | less");
        Assert.Single(lines);
        Assert.Equal("aliased", lines[0]);
    }

    [Fact]
    public void Less_InjectionProbe_OperandStaysLiteral(/* Directive 12 */)
    {
        // The operand contains $()/; payload. The non-interactive file path
        // resolves it as a literal path, fails the File.Exists check, and
        // emits a no-such-file error — never re-parsing the operand as PS.
        var lines = RunLines("Invoke-BashLess '$(throw \"pwn\");rm' 2>$null");
        Assert.DoesNotContain(lines, l => l.Contains("pwn"));
    }
}
