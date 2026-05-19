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
        string? nativeSource = null;
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

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var sb = new StringBuilder();
                    string? line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        if (sb.Length > 0) sb.Append(Environment.NewLine);
                        sb.Append(line);
                    }
                    // Drain stderr so the child can exit even if it wrote
                    // something there; oracle suppressed it with 2>$null.
                    _ = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode == 0)
                    {
                        var nativeOut = sb.ToString();
                        // For terminal-size queries, native tput returns "0"
                        // when run without a controlling terminal (no $TERM /
                        // redirected stdio). Reject "0" for cols/lines and
                        // fall through to the in-process emulator's hardcoded
                        // default so tests in non-TTY harnesses still pass.
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
