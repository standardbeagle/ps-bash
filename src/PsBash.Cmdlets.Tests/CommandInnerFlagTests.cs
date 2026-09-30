using System.Collections.ObjectModel;
using System.Management.Automation;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The bash <c>command</c> builtin: its own options (<c>-p -v -V</c>, bundles) are read only up to the
/// first operand; everything after the command name is the inner command's argv, verbatim. Before, the
/// inner <c>-d</c> / <c>-i</c> / <c>-v</c> could be taken by the PowerShell binder (-Debug, -InformationAction,
/// -Verbose) or by the cmdlet's own <c>-v</c> scan, and the wrapped command was never run at all.
/// <c>command</c> is on <c>PsEmitter.OrderedArgCommands</c>, so every dash literal arrives single-quoted.
///
/// Each test transpiles REAL bash text with <see cref="PsEmitter"/> and runs the emitted PowerShell
/// (the bug lived in the emitter/cmdlet seam). Oracle (qa-rubric Directive 1): expected values were
/// taken from bash 5.2 via <c>wsl.exe -d Ubuntu-24.04 -- bash -c '…'</c>: <c>command ls -d f</c> -> <c>f</c>,
/// <c>command -p echo -e 'a\tb'</c> -> <c>a&lt;TAB&gt;b</c>, <c>command grep -i x f</c> -> the matching lines,
/// <c>command echo -v</c> -> <c>-v</c>, <c>command -x ls</c> -> "invalid option" + usage, exit 2,
/// <c>command nosuch</c> -> "command not found", exit 127. (`command -v`/`-V` print the runtime's own
/// definition here, not a filesystem path, so those stay parity-with-oracle tests in
/// InvokeBashCommandCommandTests.)
/// </summary>
public class CommandInnerFlagTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public CommandInnerFlagTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-cmd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "f"), "x1\nX2\ny\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (string[] Lines, int Exit, string Stderr) Run(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        var pwsh = _fixture.AcquireFresh();
        var r = CmdResult.Run(pwsh, $"Set-Location '{_dir}'; {ps}");
        return (r.Lines.Select(l => l.TrimEnd('\n')).ToArray(), r.ExitCode, r.Stderr);
    }

    [Fact]
    public void Transpile_CommandLsDashD_InnerFlagReachesLs()
    {
        // The reported bug: `-d` was taken by the binder (-Debug) and lost.
        // (`ls -d .` itself prints the directory NAME here instead of `.` — a separate, pre-existing
        // ls divergence — so the operand is a plain file.)
        var (lines, exit, err) = Run("command ls -d f");
        Assert.True(exit == 0, err);
        Assert.Equal(new[] { "f" }, lines);
    }

    [Fact]
    public void Transpile_CommandDashP_Echo_InnerDashEIsAnEchoFlag()
    {
        var (lines, exit, err) = Run("command -p echo -e 'a\\tb'");
        Assert.True(exit == 0, err);
        Assert.Equal(new[] { "a\tb" }, lines);
    }

    [Fact]
    public void Transpile_CommandGrepDashI_InnerFlagReachesGrep()
    {
        var (lines, exit, err) = Run("command grep -i x f");
        Assert.True(exit == 0, err);
        Assert.Equal(new[] { "x1", "X2" }, lines);
    }

    [Fact]
    public void Transpile_CommandEchoDashV_InnerDashVIsNotCommandsOwn()
    {
        // bash: `command echo -v` prints "-v" — a -v AFTER the command name is echo's argument.
        var (lines, exit, err) = Run("command echo -v");
        Assert.True(exit == 0, err);
        Assert.Equal(new[] { "-v" }, lines);
    }

    [Fact]
    public void Transpile_CommandDoubleDash_EndsCommandOptions()
    {
        var (lines, exit, err) = Run("command -- ls -d f");
        Assert.True(exit == 0, err);
        Assert.Equal(new[] { "f" }, lines);
    }

    [Theory]
    [InlineData("command -v ls")]
    [InlineData("command -V ls")]
    [InlineData("command -pv ls")]
    public void Transpile_CommandLookupForms_ReportTheAlias(string bash)
    {
        // Parity with the psm1 oracle: -v and -V both emit the alias definition (bundles too).
        var (lines, exit, err) = Run(bash);
        Assert.True(exit == 0, err);
        Assert.Equal(new[] { "Invoke-BashLs" }, lines);
    }

    [Fact]
    public void Transpile_CommandLookup_MissingName_ExitsOneSilently()
    {
        var (lines, exit, _) = Run("command -v definitely_not_a_command_xyz");
        Assert.Empty(lines);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Transpile_CommandUnknownOption_IsInvalidOptionExitTwo()
    {
        var (lines, exit, err) = Run("command -x ls");
        Assert.Empty(lines);
        Assert.Equal(2, exit);
        Assert.Contains("command: -x: invalid option", err);
        Assert.Contains("command: usage: command [-pVv] command [arg ...]", err);
    }

    [Fact]
    public void Transpile_CommandNoSuchCommand_Exits127()
    {
        var (lines, exit, err) = Run("command definitely_not_a_command_xyz");
        Assert.Empty(lines);
        Assert.Equal(127, exit);
        Assert.Contains("definitely_not_a_command_xyz: command not found", err);
    }

    [Fact]
    public void Transpile_CommandAlone_IsANoOp()
    {
        var (lines, exit, _) = Run("command");
        Assert.Empty(lines);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Transpile_CommandBypassesFunctions()
    {
        // bash: functions are skipped by `command`, so a function named like an external is not run.
        var pwsh = _fixture.AcquireFresh();
        var r = CmdResult.Run(pwsh, "function zzfn { 'FUNC' }; " + PsEmitter.Transpile("command zzfn"));
        Assert.Equal(127, r.ExitCode);
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void Transpile_CommandForwardsPipelineInput()
    {
        var (lines, exit, err) = Run("printf 'a\\nB\\nc\\n' | command grep -i b");
        Assert.True(exit == 0, err);
        Assert.Equal(new[] { "B" }, lines);
    }

}
