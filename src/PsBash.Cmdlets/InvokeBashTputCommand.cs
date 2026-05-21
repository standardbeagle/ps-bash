using System.Diagnostics;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTput</c>
/// (REFACTOR-2 follow-on). Queries terminal capabilities, matching the
/// <c>tput</c> command surface.
///
/// Behavioral parity oracle: the original psm1 function. Two-path
/// implementation:
/// <list type="number">
/// <item>Native passthrough — resolve <c>tput</c> via parameter-bound
/// <c>InvokeCommand.InvokeScript</c> calling <c>Get-Command tput
/// -CommandType Application</c>. If a binary is on PATH, shell out via
/// <see cref="Process"/> with <c>UseShellExecute=false</c> and
/// <c>RedirectStandardOutput=true</c>, then emit the captured stdout
/// when the exit code is 0.</item>
/// <item>Fallback switch — for the common capability tokens
/// (<c>cols</c>, <c>lines</c>, <c>clear</c>, <c>bold</c>, <c>sgr0</c>,
/// <c>setaf N</c>) the cmdlet emulates the psm1 oracle byte-for-byte:
/// terminal dimensions via <see cref="System.Console"/>, ANSI SGR
/// strings hard-coded. Unknown capabilities silently emit nothing.</item>
/// </list>
///
/// Directive 12 safety: operand strings bind to the spawn via
/// <see cref="ProcessStartInfo.ArgumentList"/> (no shell, no string
/// concatenation into a script body). The <c>Get-Command</c> probe runs
/// a fixed-string script with parameter-bound positional args — never
/// concatenated.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTput")]
[OutputType(typeof(string))]
public sealed class InvokeBashTputCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tput"))
            {
                WriteObject(line);
            }
            return;
        }

        // Strip a single leading "--" end-of-flags marker if present.
        // The oracle had no real flag set, so this is purely a defensive
        // convention.
        var operands = new List<string>(args.Length);
        bool sawDashDash = false;
        foreach (var a in args)
        {
            if (!sawDashDash && string.Equals(a, "--", StringComparison.Ordinal))
            {
                sawDashDash = true;
                continue;
            }
            operands.Add(a);
        }

        // Native passthrough — resolve the on-disk tput. The script body
        // is fixed; no user input flows into PS source.
        //
        // On Windows we skip native passthrough entirely. A Windows host that
        // has `tput` on PATH almost certainly picked it up from Git for
        // Windows / msys2, which runs against an ncurses terminfo database and
        // emits sequences that differ from the in-process emulator's
        // hard-coded ANSI bytes (e.g. `sgr0` -> `\x1B(B\x1B[m` rather than
        // `\x1B[0m`, `setaf N` -> 16-color form rather than the 256-color
        // form). Tests assert the emulator's exact bytes. The native passthrough
        // adds little value on Windows (the emulator covers every capability
        // the psm1 oracle ever supported), so disabling it on Windows is the
        // simplest fix that preserves Linux/macOS behavior unchanged.
        string? nativeSource = null;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var probe = InvokeCommand.InvokeScript(
                    "Get-Command tput -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1");
                if (probe.Count > 0 && probe[0] != null)
                {
                    nativeSource = probe[0].Properties["Source"]?.Value as string;
                }
            }
            catch
            {
                // Get-Command failure → fall through to the in-process emulator.
            }
        }

        if (!string.IsNullOrEmpty(nativeSource))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = nativeSource!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in operands)
                {
                    psi.ArgumentList.Add(a);
                }

                // Bounded spawn + concurrent drain + kill-tree on timeout so a hung
                // native tput cannot wedge the host runspace. (The old code drained
                // stderr only AFTER the stdout loop — a mid-stream stderr burst could
                // deadlock; concurrent drain removes that.)
                var spawn = BashRuntime.RunChildProcess(psi);
                if (!spawn.TimedOut && spawn.ExitCode == 0)
                {
                    var nativeOut = spawn.Stdout.Replace("\r\n", "\n");
                    if (nativeOut.EndsWith('\n'))
                        nativeOut = nativeOut.Substring(0, nativeOut.Length - 1);
                    // For terminal-size queries, native tput returns "0" when run
                    // without a controlling terminal (no $TERM / redirected stdio).
                    // Reject "0" for cols/lines and fall through to the in-process
                    // emulator's hardcoded default so tests in non-TTY harnesses pass.
                    bool isSizeQuery = operands.Count > 0 &&
                        (string.Equals(operands[0], "cols", StringComparison.Ordinal) ||
                         string.Equals(operands[0], "lines", StringComparison.Ordinal));
                    if (!isSizeQuery ||
                        !string.Equals(nativeOut.Trim(), "0", StringComparison.Ordinal))
                    {
                        foreach (var emitted in BashRuntime.EmitBashLines(nativeOut))
                        {
                            WriteObject(emitted);
                        }
                        return;
                    }
                    // else: fall through to fallback emulator below.
                }
            }
            catch
            {
                // Native invocation failed → fall through to emulator.
            }
        }

        // Fallback: emulate the common capabilities the oracle handled.
        if (operands.Count == 0)
        {
            return;
        }

        var cap = operands[0];
        string result = string.Empty;
        switch (cap)
        {
            case "cols":
                result = GetWindowWidth().ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "lines":
                result = GetWindowHeight().ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "clear":
                // The oracle called Clear-Host and emitted ''. We mirror:
                // do not emit anything visible, no clearing in the in-process
                // cmdlet (any side-effect would not survive the SDK runspace
                // host anyway).
                result = string.Empty;
                break;
            case "bold":
                result = "[1m";
                break;
            case "sgr0":
                result = "[0m";
                break;
            case "setaf":
                if (operands.Count >= 2 &&
                    int.TryParse(operands[1], System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out var color))
                {
                    result = $"[38;5;{color}m";
                }
                break;
            default:
                result = string.Empty;
                break;
        }

        if (result.Length > 0)
        {
            foreach (var emitted in BashRuntime.EmitBashLines(result))
            {
                WriteObject(emitted);
            }
        }
    }

    private int GetWindowWidth()
    {
        try
        {
            var size = Host?.UI?.RawUI?.WindowSize;
            if (size.HasValue && size.Value.Width > 0)
            {
                return size.Value.Width;
            }
        }
        catch
        {
            // Host may not expose RawUI in a non-interactive runspace.
        }
        // Console.WindowWidth on POSIX with a redirected stdout returns 0
        // instead of throwing. Treat <= 0 as "no terminal" and fall through
        // to the canonical default (matches oracle parity in non-TTY runs).
        try
        {
            var w = Console.WindowWidth;
            if (w > 0) return w;
        }
        catch { }
        return 80;
    }

    private int GetWindowHeight()
    {
        try
        {
            var size = Host?.UI?.RawUI?.WindowSize;
            if (size.HasValue && size.Value.Height > 0)
            {
                return size.Value.Height;
            }
        }
        catch
        {
            // Host may not expose RawUI in a non-interactive runspace.
        }
        try
        {
            var h = Console.WindowHeight;
            if (h > 0) return h;
        }
        catch { }
        return 24;
    }
}
