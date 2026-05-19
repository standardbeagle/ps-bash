using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// Invoke-BashCommand from PsBash.psm1 to a binary cmdlet
/// (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function. It accepted any <c>-</c>-prefixed token as a
/// flag; verbose iff <c>-v</c> or <c>-V</c> was present; for each operand it
/// emitted (only under verbose) the alias definition / function name /
/// source of <c>Get-Command NAME</c>; on a miss it set <c>$LASTEXITCODE = 1</c>
/// and returned.
///
/// Applicable failure-surface axes (Directive 3): missing target (axis 14),
/// quoting / injection (axis 12). Streaming / signal axes do not apply —
/// command is in-process metadata lookup, no pipeline.
/// </summary>
public class InvokeBashCommandCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashCommandCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        return pwsh.AddScript(script).Invoke();
    }

    private string[] RunLines(string script)
    {
        return RunRaw(script)
            .Select(o =>
            {
                if (o == null) return "";
                var bt = o.Properties["BashText"]?.Value as string;
                return bt ?? o.ToString() ?? "";
            })
            .ToArray();
    }

    private (string[] lines, int exitCode) RunAndCaptureExit(string script)
    {
        var pwsh = _fixture.AcquireFresh();

        var results = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var lines = results
            .Select(o =>
            {
                if (o == null) return "";
                var bt = o.Properties["BashText"]?.Value as string;
                return bt ?? o.ToString() ?? "";
            })
            .ToArray();

        var exitRes = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        int exit = 0;
        if (exitRes.Count > 0 && exitRes[0]?.BaseObject is int code) exit = code;

        return (lines, exit);
    }

    [Fact]
    public void Command_DashV_KnownCmdlet_EmitsSource()
    {
        // Get-Item is a real PowerShell cmdlet — Get-Command resolves it with
        // CommandType=Cmdlet, oracle emits $cmd.Source under -v.
        var lines = RunLines("Invoke-BashCommand -v Get-Item");
        Assert.Single(lines);
        // Source is a string (module name or empty for built-ins) — assert it
        // came through as a typed BashText line, not null.
        Assert.NotNull(lines[0]);
    }

    [Fact]
    public void Command_DashV_BashAlias_EmitsAliasDefinition()
    {
        // `ls` is a psm1 alias for Invoke-BashLs — oracle emits $cmd.Definition.
        var lines = RunLines("Invoke-BashCommand -v ls");
        Assert.Single(lines);
        Assert.Equal("Invoke-BashLs", lines[0]);
    }

    [Fact]
    public void Command_DashV_MissingName_NoOutputExitOne()
    {
        // The oracle: not found → set $LASTEXITCODE = 1 and return early.
        var (lines, exit) = RunAndCaptureExit(
            "Invoke-BashCommand -v definitely_not_a_real_command_xyz");
        Assert.Empty(lines);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Command_BigV_BashAlias_EmitsAliasDefinition()
    {
        // -V is treated identically to -v by the oracle (both set verbose).
        // The case-insensitive cmdlet binder collapses them onto the same V
        // switch — preserved parity.
        var lines = RunLines("Invoke-BashCommand -V ls");
        Assert.Single(lines);
        Assert.Equal("Invoke-BashLs", lines[0]);
    }

    [Fact]
    public void Command_NoVerbose_KnownName_NoOutput()
    {
        // Without -v / -V the oracle's lookup runs but emit-bash-line is
        // skipped — the run-command form. The oracle never actually executed
        // the wrapped command (documented gap); we preserve that by emitting
        // no output here.
        var lines = RunLines("Invoke-BashCommand ls");
        Assert.Empty(lines);
    }

    [Fact]
    public void Command_DashV_BashFunction_EmitsName()
    {
        // Show-BashHelp is a psm1 function — Get-Command reports it as a
        // function, oracle emits $cmd.Name.
        var lines = RunLines("Invoke-BashCommand -v Show-BashHelp");
        Assert.Single(lines);
        Assert.Equal("Show-BashHelp", lines[0]);
    }

    [Fact]
    public void Command_DashV_MultipleOperands_EmitsEachInOrder()
    {
        // Two operands, both resolvable — emit definition/name/source in
        // sequence.
        var lines = RunLines("Invoke-BashCommand -v ls cat");
        Assert.Equal(2, lines.Length);
        Assert.Equal("Invoke-BashLs", lines[0]);
        Assert.Equal("Invoke-BashCat", lines[1]);
    }

    [Fact]
    public void Command_DashV_FirstMissingSecondPresent_StopsAtFirstMiss()
    {
        // The oracle: on miss it RETURNS — no further operands are checked.
        // Preserved here. Output is empty and exit code is 1.
        var (lines, exit) = RunAndCaptureExit(
            "Invoke-BashCommand -v missing_xyzzy_zzz ls");
        Assert.Empty(lines);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Command_ViaAlias_Works()
    {
        // The `command` alias (declared in psm1) must resolve to the cmdlet.
        var lines = RunLines("command -v ls");
        Assert.Single(lines);
        Assert.Equal("Invoke-BashLs", lines[0]);
    }

    [Fact]
    public void Command_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashCommand --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Command_NoArgs_NoOutput()
    {
        // The oracle's loop iterates zero operands → no output, no error,
        // exit stays 0.
        var (lines, exit) = RunAndCaptureExit("Invoke-BashCommand");
        Assert.Empty(lines);
        Assert.Equal(0, exit);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Command_NameWithScriptblockChars_TreatedAsLiteralName()
    {
        // A command name containing $() / ; must not be re-parsed as
        // PowerShell. ResolveCommand binds the name as a parameter through
        // InvokeCommand.InvokeScript("param($n) Get-Command $n ...", name)
        // — the $n binding is a string literal, no nested evaluation.
        //
        // Asserts: pwsh.Invoke() returns normally (no RuntimeException
        // carrying "pwn") AND the name lands in the not-found branch
        // (zero success-pipeline objects, exit 1). Negative-assertion is the
        // security probe per the playbook.
        var (lines, exit) = RunAndCaptureExit(
            "Invoke-BashCommand -v '$(throw \"pwn\")'");
        Assert.Empty(lines);
        Assert.Equal(1, exit);
    }
}
