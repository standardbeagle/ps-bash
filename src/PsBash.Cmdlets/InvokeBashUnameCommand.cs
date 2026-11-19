using System;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashUname</c> function
/// (REFACTOR-2). Prints system information, matching the GNU coreutils
/// <c>uname</c> surface. On Linux and macOS it reports the HOST kernel
/// (<see cref="PsBash.Core.Platform.KernelIdentity"/>, <see cref="FormatHost"/>);
/// the MINGW identity below is the Windows answer only.
///
/// Behavioral parity oracle: the original psm1 function. Flags supported are
/// <c>-s</c> (kernel name, default), <c>-n</c> (network/host name), <c>-r</c>
/// (kernel release), <c>-m</c> (machine arch), and <c>-a</c> (all combined).
/// Bundled short-flag forms are accepted (e.g. <c>-snr</c>). The oracle uses
/// MSYS/MINGW-style values regardless of host platform — the system-name string
/// is always <c>MINGW64_NT-{ver}</c>, the host name is lowercased
/// <see cref="System.Environment.MachineName"/>, the release is the .NET
/// OSVersion <c>Major.Minor.Build</c>, and arch is <c>x86_64</c> or <c>i686</c>
/// based on <see cref="System.Environment.Is64BitProcess"/>. <c>-a</c> appends
/// a trailing literal <c>MINGW64</c>. The cmdlet reproduces this byte-for-byte
/// so transpiled bash scripts that grep for these tokens keep working.
///
/// No PowerShell common-parameter prefix collisions exist for <c>-s -n -r -m
/// -a</c>, so all flags stay in <c>Arguments</c> and are scanned by a manual
/// regex + literal-match loop matching the oracle. Declaring them as
/// <see cref="SwitchParameter"/> would block bundled forms like <c>-snr</c>
/// from binding, so we deliberately do not.
///
/// The <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> function
/// via parameter-bound <c>InvokeCommand.InvokeScript</c> (AOT-safe: fixed
/// script body, user-controlled tokens never concatenated into the body).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUname")]
[OutputType(typeof(string))]
public sealed class InvokeBashUnameCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// <c>-a</c> declared as an explicit <see cref="SwitchParameter"/> because
    /// the bare token <c>-a</c> would otherwise prefix-match the cmdlet's own
    /// <c>-Arguments</c> parameter under PowerShell parameter binding (it is
    /// the only declared parameter starting with 'a'), causing a "Missing an
    /// argument for parameter 'Arguments'" error. Bundled forms like
    /// <c>-snra</c> do not prefix-match <c>-Arguments</c>, so they still land
    /// in <see cref="Arguments"/> and are decoded post-parse alongside the
    /// other bundle forms.
    /// </summary>
    [Parameter]
    public SwitchParameter a { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "uname", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "uname"))
            {
                WriteObject(line);
            }
            return;
        }

        bool flagS = false, flagN = false, flagR = false, flagM = false;
        bool flagA = a.IsPresent;

        // Linux / macOS: the host kernel (uname(2) via KernelIdentity), as coreutils prints it.
        if (PsBash.Core.Platform.KernelIdentity.Host() is { } uts)
        {
            if (FormatHost(uts, OperatingSystem.IsMacOS(), args, flagA) is { } line)
                WriteObject(BashRuntime.NewBashObject(line));
            return;
        }

        foreach (var arg in args)
        {
            if (arg.Length >= 2 && arg[0] == '-' && IsShortFlagBundle(arg))
            {
                foreach (var ch in arg.AsSpan(1))
                {
                    switch (ch)
                    {
                        case 's': flagS = true; break;
                        case 'n': flagN = true; break;
                        case 'r': flagR = true; break;
                        case 'm': flagM = true; break;
                        case 'a': flagA = true; break;
                    }
                }
            }
            // Note: single-flag forms (-s, -n, etc.) are subsumed by the
            // bundle branch above since they too match [-snrma]+.
        }

        var ver = System.Environment.OSVersion.Version;
        var release = $"{ver.Major}.{ver.Minor}.{ver.Build}";
        var sysName = $"MINGW64_NT-{release}";
        var hostName = System.Environment.MachineName.ToLowerInvariant();
        var arch = System.Environment.Is64BitProcess ? "x86_64" : "i686";

        string text;
        if (flagA)
        {
            text = $"{sysName} {hostName} {release} {arch} MINGW64";
        }
        else
        {
            var anyFlag = flagS || flagN || flagR || flagM;
            if (!anyFlag) flagS = true;
            var parts = new System.Collections.Generic.List<string>(4);
            if (flagS) parts.Add(sysName);
            if (flagN) parts.Add(hostName);
            if (flagR) parts.Add(release);
            if (flagM) parts.Add(arch);
            text = string.Join(' ', parts);
        }

        WriteObject(BashRuntime.NewBashObject(text));
    }

    /// <summary>
    /// coreutils <c>uname</c> over a host kernel identity: fields in GNU's order <c>-s -n -r -v -m -p -i -o</c>
    /// (long forms too), <c>-s</c> when none is asked for. <c>-a</c> is all of them on Linux (<c>-p</c> / <c>-i</c> =
    /// the machine, as Debian, Ubuntu and Fedora build it); on macOS <c>-s -n -r -v -m</c> (BSD uname). An unknown
    /// option yields null (the cmdlet prints nothing, as before). Pure (unit-test seam).
    /// </summary>
    internal static string? FormatHost(PsBash.Core.Platform.KernelIdentity.Uts uts, bool darwin, IReadOnlyList<string> args, bool all = false)
    {
        // s n r v m p i o
        var want = new bool[8];
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--all": all = true; continue;
                case "--kernel-name": want[0] = true; continue;
                case "--nodename": want[1] = true; continue;
                case "--kernel-release": want[2] = true; continue;
                case "--kernel-version": want[3] = true; continue;
                case "--machine": want[4] = true; continue;
                case "--processor": want[5] = true; continue;
                case "--hardware-platform": want[6] = true; continue;
                case "--operating-system": want[7] = true; continue;
            }
            if (arg.Length < 2 || arg[0] != '-' || arg[1] == '-') continue;
            foreach (char ch in arg.AsSpan(1))
            {
                int k = "snrvmpio".IndexOf(ch);
                if (ch == 'a') all = true;
                else if (k >= 0) want[k] = true;
                else return null;
            }
        }
        if (all)
        {
            for (int k = 0; k < (darwin ? 5 : 8); k++) want[k] = true;
        }
        else if (Array.IndexOf(want, true) < 0)
        {
            want[0] = true;
        }
        string[] values = [uts.SysName, uts.NodeName, uts.Release, uts.Version, uts.Machine, uts.Machine, uts.Machine,
            darwin ? uts.SysName : "GNU/Linux"];
        var parts = new List<string>(8);
        for (int k = 0; k < 8; k++)
            if (want[k]) parts.Add(values[k]);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// True when <paramref name="arg"/> matches the oracle's
    /// <c>^-([snrma]+)$</c> regex — one or more of the recognized short flag
    /// characters following a single leading dash. Unrecognized tokens (e.g.
    /// <c>-x</c>, <c>--foo</c>, plain operands) are silently ignored, matching
    /// the psm1 oracle's "unknown args fall through" behavior.
    /// </summary>
    private static bool IsShortFlagBundle(string arg)
    {
        if (arg.Length < 2 || arg[0] != '-') return false;
        for (int i = 1; i < arg.Length; i++)
        {
            var c = arg[i];
            if (c != 's' && c != 'n' && c != 'r' && c != 'm' && c != 'a') return false;
        }
        return true;
    }
}
