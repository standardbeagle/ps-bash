using System.Collections.ObjectModel;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashTime
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 <c>Invoke-BashTime</c> function. Wraps an inner command,
/// emits a typed PsBash.TimeOutput PSObject (RealTime / Command / ExitCode
/// / BashText), and writes <c>"real    {seconds:N3}s"</c> to
/// <see cref="System.Console.Error"/>.
///
/// Failure-surface axes that apply (Directive 3): empty input (no args →
/// missing-command error), missing target (unknown command → error +
/// ExitCode=1), quoting/injection (Directive 12 — command name containing
/// <c>$()</c> / <c>;</c>). Large/CRLF/unicode/signal/etc. axes do not
/// apply — time wraps another command and inherits whatever the wrapped
/// command does.
/// </summary>
public class InvokeBashTimeCommandTests
{
    private static (Collection<PSObject> Result, IList<ErrorRecord> Errors) Run(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var errs = pwsh.Streams.Error.ToArray();
        return (result, errs);
    }

    [Fact]
    public void Time_WrapsEcho_EmitsTimeOutputObject()
    {
        var (result, _) = Run("Invoke-BashTime echo hi");
        Assert.Single(result);
        var obj = result[0];
        Assert.Contains("PsBash.TimeOutput", obj.TypeNames);
        Assert.Equal("echo", obj.Properties["Command"]?.Value?.ToString());
        Assert.Equal(0, (int)(obj.Properties["ExitCode"]?.Value ?? -1));
        var realTime = (System.TimeSpan)(obj.Properties["RealTime"]?.Value ?? System.TimeSpan.Zero);
        Assert.True(realTime.TotalSeconds >= 0);
        // The wrapped command's BashText is captured. Both PowerShell's
        // built-in `echo` (Write-Output) and the psm1 `Invoke-BashEcho`
        // alias produce "hi" content one way or another.
        var bashText = obj.Properties["BashText"]?.Value?.ToString() ?? "";
        Assert.Contains("hi", bashText);
    }

    [Fact]
    public void Time_NoArgs_EmitsMissingCommandError()
    {
        var (result, _) = Run("Invoke-BashTime");
        // No output object on the missing-command branch.
        Assert.Empty(result);
    }

    [Fact]
    public void Time_UnknownCommand_SetsExitCodeOneAndEmitsError()
    {
        // Wrapping a guaranteed-unresolvable name. The CommandNotFound
        // ErrorRecord must surface through the bash-error sink; the
        // TimeOutput object still emits with ExitCode=1.
        var (result, _) = Run("Invoke-BashTime no-such-command-zzzzz-9q9q9");
        Assert.Single(result);
        var obj = result[0];
        Assert.Equal(1, (int)(obj.Properties["ExitCode"]?.Value ?? -1));
        Assert.Equal("no-such-command-zzzzz-9q9q9",
            obj.Properties["Command"]?.Value?.ToString());
    }

    [Fact]
    public void Time_ViaAlias_StillWorks()
    {
        // The psm1 `Set-Alias time` line still binds to the cmdlet now
        // that the function is gone.
        var (result, _) = Run("time echo aliased");
        Assert.Single(result);
        Assert.Contains("PsBash.TimeOutput", result[0].TypeNames);
        Assert.Equal("echo", result[0].Properties["Command"]?.Value?.ToString());
    }

    [Fact]
    public void Time_HelpFlag_EmitsUsageText()
    {
        var (result, _) = Run("Invoke-BashTime --help");
        Assert.NotEmpty(result);
        var joined = string.Join("\n", result.Select(o => o?.ToString() ?? ""));
        Assert.Contains("time", joined, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---- injection probes (Directive 12) ----

    [Fact]
    public void Time_CommandNameWithScriptBlockChars_IsTreatedAsLiteralName()
    {
        // The command name contains $(throw "pwn"). If the cmdlet
        // concatenated args into a script body, this would throw "pwn"
        // and crash the cmdlet. Instead, it must look up the literal
        // string as a command name (CommandNotFoundException), emit the
        // bash error, and return ExitCode=1 — the same as any other
        // unknown command.
        var (result, _) = Run("Invoke-BashTime '$(throw \"pwn\")'");
        Assert.Single(result);
        var obj = result[0];
        Assert.Equal(1, (int)(obj.Properties["ExitCode"]?.Value ?? -1));
        // The command name must round-trip literally — no expansion.
        Assert.Equal("$(throw \"pwn\")",
            obj.Properties["Command"]?.Value?.ToString());
    }

    [Fact]
    public void Time_CommandNameWithSemicolon_IsTreatedAsLiteralName()
    {
        // If args were concatenated into a script body, `echo;rm -rf /`
        // would run two statements. Instead the literal `echo;rm` must
        // be looked up as a single command name and fail to resolve.
        var (result, _) = Run("Invoke-BashTime 'echo;rm'");
        Assert.Single(result);
        var obj = result[0];
        Assert.Equal(1, (int)(obj.Properties["ExitCode"]?.Value ?? -1));
        Assert.Equal("echo;rm", obj.Properties["Command"]?.Value?.ToString());
    }
}
