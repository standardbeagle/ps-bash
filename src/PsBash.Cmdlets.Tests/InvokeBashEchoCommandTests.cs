using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the migration of <c>Invoke-BashEcho</c> from
/// PsBash.psm1 to the binary cmdlet <see cref="InvokeBashEchoCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashEcho</c> — joins operands with a single space,
/// expands C-style escapes only under <c>-e</c> (<c>-E</c> is a default-state
/// no-op), and appends a trailing newline unless <c>-n</c>.
///
/// Flags are passed in their quoted form (e.g. <c>'-e'</c>) because that is how
/// the emitter routes them: <c>-e</c>/<c>-E</c> prefix-collide with the
/// <c>-Error*</c> common parameters, so a bare <c>-e</c> would fail the binder
/// even here. The cmdlet parses them case-sensitively out of Arguments.
/// </summary>
public class InvokeBashEchoCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashEchoCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    [Fact]
    public void Echo_SingleOperand_EmitsIt()
        => Assert.Equal(new[] { "hello" }, RunLines("Invoke-BashEcho hello"));

    [Fact]
    public void Echo_MultipleOperands_JoinedBySingleSpace()
        => Assert.Equal(new[] { "hello world" }, RunLines("Invoke-BashEcho hello world"));

    [Fact]
    public void Echo_NoArgs_EmitsOneEmptyLine()
        => Assert.Equal(new[] { "" }, RunLines("Invoke-BashEcho"));

    [Fact]
    public void Echo_DashE_ExpandsTabEscape()
        => Assert.Equal(new[] { "a\tb" }, RunLines("Invoke-BashEcho '-e' 'a\\tb'"));

    [Fact]
    public void Echo_DashE_NewlineEscape_SplitsIntoLines()
        => Assert.Equal(new[] { "line1", "line2" }, RunLines("Invoke-BashEcho '-e' 'line1\\nline2'"));

    [Fact]
    public void Echo_WithoutDashE_LeavesBackslashEscapesLiteral()
        => Assert.Equal(new[] { "a\\tb" }, RunLines("Invoke-BashEcho 'a\\tb'"));

    [Fact]
    public void Echo_DashEUpper_DoesNotExpand()
        => Assert.Equal(new[] { "a\\tb" }, RunLines("Invoke-BashEcho '-E' 'a\\tb'"));

    [Fact]
    public void Echo_DashN_StillEmitsContent()
        // -n suppresses the trailing newline (a property of the object, not the
        // BashText payload); the content itself is unchanged.
        => Assert.Equal(new[] { "hello" }, RunLines("Invoke-BashEcho '-n' hello"));

    [Fact]
    public void Echo_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashEcho --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("echo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Echo_AliasResolution_NoFlags()
        // `Set-Alias echo Invoke-BashEcho` resolves to the cmdlet (the psm1
        // function is gone). Flag forms go through the transpiler's force-quote,
        // so the alias is exercised here only for the no-flag case.
        => Assert.Equal(new[] { "hi there" }, RunLines("echo hi there"));

    [Fact]
    public void Echo_DoubleDash_EndsFlagParsing()
        // `echo -- -n` → "-n" (operands after --). The emitter force-quotes `--`
        // (so PowerShell's binder doesn't swallow it); tested in that quoted form.
        => Assert.Equal(new[] { "-n" }, RunLines("Invoke-BashEcho '--' '-n'"));
}
