using System.IO;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashMore</c> from a psm1 function to a binary cmdlet
/// (<see cref="InvokeBashMoreCommand"/>).
///
/// Oracle: the psm1 function. The interactive paging loop is not exercised
/// — the SDK runspace is never a TTY; the cmdlet emits all lines and
/// returns, matching the oracle's non-interactive branch byte-for-byte.
///
/// Directive-3 axes exercised: empty input, file mode passthrough,
/// pipeline mode passthrough, missing-file error continuation, alias
/// resolution. Directive 12: injection probe on the file operand.
/// </summary>
public class InvokeBashMoreCommandTests
{
    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void More_NoArgs_NoPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashMore");
        Assert.Empty(lines);
    }

    [Fact]
    public void More_FileMode_PassesThroughLines()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "one\ntwo\nthree\n");
            var lines = RunLines($"Invoke-BashMore '{tmp.Replace("'", "''")}'");
            Assert.Equal(new[] { "one", "two", "three" }, lines);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void More_PipelineMode_PassesThroughItems()
    {
        var lines = RunLines("'a','b','c' | Invoke-BashMore");
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void More_MissingFile_EmitsErrorAndNoOutput()
    {
        var lines = RunLines("Invoke-BashMore /no/such/file-abc987.txt 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void More_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashMore --help");
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void More_ViaAlias_ResolvesToCmdlet()
    {
        var lines = RunLines("'aliased' | more");
        Assert.Single(lines);
        Assert.Equal("aliased", lines[0]);
    }

    [Fact]
    public void More_InjectionProbe_OperandStaysLiteral(/* Directive 12 */)
    {
        // The operand contains $()/; payload. The file path is resolved as a
        // literal string, missing-file error is emitted, no PS re-parse.
        var lines = RunLines("Invoke-BashMore '$(throw \"pwn\");rm' 2>$null");
        Assert.DoesNotContain(lines, l => l.Contains("pwn"));
    }
}
