using System.Diagnostics;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU <c>timeout [OPTION] DURATION COMMAND [ARG]...</c>: run COMMAND and stop it when DURATION passes (exit 124, or
/// the command's own status with <c>--preserve-status</c>). Windows has its own <c>timeout.exe</c> (a keypress wait that
/// fails under redirected input), so the name must not fall through to it. The command runs in a nested ps-bash, as
/// <c>bash -c</c> does: a builtin (<c>sleep</c>), an external program and a nested <c>bash -c</c> all behave, and the
/// whole process tree is killed when the budget ends (<see cref="BashRuntime.RunChildProcess(ProcessStartInfo, TimeSpan?, string?, ChildStdioMode)"/>).
/// <c>-s SIGNAL</c>, <c>-k DURATION</c> and <c>--foreground</c> are accepted; Windows has one way to stop a process.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTimeout")]
public sealed class InvokeBashTimeoutCommand : BashCmdlet
{
    /// <summary>Decoy for a direct bare <c>-v</c> (prefix of the common parameter -Verbose).</summary>
    [Parameter]
    public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    private StringBuilder? _stdin;

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        _stdin ??= new StringBuilder();
        _stdin.Append(BashRuntime.RecordBytes(InputObject));
    }

    protected override void EndProcessing()
    {
        var a = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "timeout", a)) return;

        if (!Parse(a, out var o, out var error))
        {
            FileSystemHelpers.WriteBashError(this, (error.Length > 0 ? "timeout: " + error + "\n" : "") + "Try 'timeout --help' for more information.");
            FileSystemHelpers.SetLastExitCode(this, 125);
            return;
        }

        // The command line, quoted word by word, runs as bash text in a nested pipeline of THIS runspace (a nested
        // ps-bash adds seconds of startup to every call and eats short budgets). A timer stops that pipeline:
        // cooperative cmdlets (sleep, the readers) see Stopping, and an external program is killed by the stop.
        string script;
        // `timeout 5 prog 2>&1`: the merge is the command's own, so a program gets ONE pipe for stdout and stderr
        // (Invoke-BashNative; docs/specs/stdio-messaging.md §4) instead of two read back in a racing order.
        bool joined = StderrMerge.JoinsStdout(this);
        // `timeout 5 prog 2>f`: the nested pipeline's stderr comes back as error records and leaves through this
        // cmdlet's error stream, where the redirect downstream sees it (a nested program wrote to the host's stderr).
        bool splitStderr = !joined && StderrMerge.Redirected(this);
        try
        {
            string line = string.Join(" ", o.Command.Select(ShellQuote));
            if (joined) line += " 2>&1";
            // Piped-in text becomes the command's stdin as a here-string (which adds the final newline itself).
            if (_stdin is { Length: > 0 })
                line += " <<< " + ShellQuote(_stdin.ToString().EndsWith('\n') ? _stdin.ToString(0, _stdin.Length - 1) : _stdin.ToString());
            script = PsBash.Core.Transpiler.BashTranspiler.Transpile(line, PsBash.Core.Transpiler.TranspileContext.Eval) ?? "";
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, "timeout: " + ex.Message);
            FileSystemHelpers.SetLastExitCode(this, 125);
            return;
        }

        bool timedOut = false;
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript(script, useLocalScope: true);
        if (splitStderr) ps.Commands.Commands[0].MergeMyResults(System.Management.Automation.Runspaces.PipelineResultTypes.Error, System.Management.Automation.Runspaces.PipelineResultTypes.Output);
        System.Threading.Timer? timer = null;
        if (o.Duration > TimeSpan.Zero)
            timer = new System.Threading.Timer(_ => { timedOut = true; try { ps.Stop(); } catch { /* already finished */ } },
                null, o.Duration, System.Threading.Timeout.InfiniteTimeSpan);
        try
        {
            foreach (var r in ps.Invoke())
            {
                if (splitStderr && r?.BaseObject is ErrorRecord err) FileSystemHelpers.WriteErrorRecord(this, err);
                else WriteObject(r);
            }
        }
        catch (PipelineStoppedException) when (timedOut)
        {
            // stopped by the timer: the status below is 124
        }
        catch (RuntimeException ex) when (ex.InnerException is CommandNotFoundException cnf || ex is CommandNotFoundException)
        {
            string name = (ex as CommandNotFoundException ?? ex.InnerException as CommandNotFoundException)?.CommandName ?? o.Command[0];
            FileSystemHelpers.WriteBashError(this, $"timeout: failed to run command ‘{name}’: No such file or directory");
            FileSystemHelpers.SetLastExitCode(this, 127);
            return;
        }
        finally { timer?.Dispose(); }

        if (timedOut)
        {
            FileSystemHelpers.SetLastExitCode(this, o.PreserveStatus ? 143 : 124);
            return;
        }
        var code = SessionState.PSVariable.GetValue("global:LASTEXITCODE");
        FileSystemHelpers.SetLastExitCode(this, code is int n ? n : 0);
    }

    /// <summary>A bash single-quoted word: the nested ps-bash must see exactly the operands the caller passed.</summary>
    internal static string ShellQuote(string s)
        => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or ':' or '+' or '@' or '%' or '-' or '=' or ',')
            ? s
            : "'" + s.Replace("'", "'\\''") + "'";

    internal sealed record Options(TimeSpan Duration, bool PreserveStatus, string[] Command);

    /// <summary>GNU duration: a number (fractions allowed) with an optional s/m/h/d suffix. Null when malformed.</summary>
    internal static TimeSpan? ParseDuration(string s)
    {
        if (s.Length == 0) return null;
        double unit = 1;
        string num = s;
        switch (s[^1])
        {
            case 's': num = s[..^1]; break;
            case 'm': num = s[..^1]; unit = 60; break;
            case 'h': num = s[..^1]; unit = 3600; break;
            case 'd': num = s[..^1]; unit = 86400; break;
        }
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            || v < 0 || double.IsNaN(v) || double.IsInfinity(v))
            return null;
        double seconds = Math.Min(v * unit, TimeSpan.MaxValue.TotalSeconds / 2);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>timeout's long value options, joined to their short letter (<c>--kill-after=5</c> → <c>-k5</c>).</summary>
    private static readonly Args.LongOptionAlias[] ValueOptions = { new("--kill-after", 'k'), new("--signal", 's') };

    internal static bool Parse(string[] a, out Options o, out string error)
    {
        bool preserve = false;
        int i = 0;
        o = new Options(TimeSpan.Zero, false, Array.Empty<string>());
        error = "";
        for (; i < a.Length; i++)
        {
            string x = a[i];
            if (x == "--") { i++; break; }
            if (!x.StartsWith('-') || x.Length < 2) break;
            if (x is "--preserve-status") { preserve = true; continue; }
            if (x is "--foreground" or "-v" or "--verbose") continue;
            // Every spelling of a value option (`-k D`, `-kD`, `--kill-after=D`, `--kill-after D`; same for -s) is
            // validated here, once: `--kill-after=x` used to be skipped unchecked.
            if (x is "-s" or "-k")
            {
                if (i + 1 >= a.Length) { error = $"option requires an argument -- '{x[1]}'"; return false; }
                x += a[++i];
            }
            else if (Args.LongOptionAliases.Join(a, ref i, ValueOptions) is { } joined) x = joined;
            else if (x is "--signal" or "--kill-after") { error = $"option '{x}' requires an argument"; return false; }
            if (x.Length > 2 && x[0] == '-' && x[1] is 'k' or 's')
            {
                string v = x[2..];
                if (x[1] == 'k' && ParseDuration(v) is null) { error = $"invalid time interval \u2018{v}\u2019"; return false; }
                if (x[1] == 's' && !InvokeBashKillCommand.TryResolveSignalSpec(v, out _)) { error = $"\u2018{v}\u2019: invalid signal"; return false; }
                continue;
            }
            error = $"invalid option -- '{x.TrimStart('-')}'";
            return false;
        }
        if (i >= a.Length) { error = ""; return false; }
        var duration = ParseDuration(a[i]);
        if (duration is null) { error = $"invalid time interval \u2018{a[i]}\u2019"; return false; }
        i++;
        if (i >= a.Length) { error = ""; return false; }
        o = new Options(duration.Value, preserve, a[i..]);
        return true;
    }
}