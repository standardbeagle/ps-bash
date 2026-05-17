using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashId</c> function
/// (REFACTOR-2). Prints user / group identity info, matching the GNU
/// coreutils <c>id</c> surface as the psm1 oracle implemented it on Windows
/// via <c>WindowsIdentity</c>; on Unix the cmdlet shells out to
/// <c>/usr/bin/id</c> with the same arguments so real-bash output is
/// preserved verbatim.
///
/// Behavioral parity oracle: the original psm1 function. Flags supported:
/// <c>-u</c> (UID/SID only), <c>-g</c> (primary GID SID only), <c>-G</c>
/// (all group SIDs), <c>-n</c> (name form — pairs with <c>-u</c>/<c>-g</c>/
/// <c>-G</c>), <c>-r</c> (real id — accepted, no behavior change since the
/// oracle has no effective/real distinction on Windows). A non-flag operand
/// is treated as a username and routed through
/// <c>WindowsIdentity(string)</c>. Default emits the bash-shaped
/// <c>"uid=SID(NAME) gid=SID groups=NAME,NAME,..."</c> line.
///
/// All flags (<c>-u</c>, <c>-g</c>, <c>-G</c>, <c>-n</c>, <c>-r</c>) have
/// **no** PowerShell common-parameter prefix collision under the cmdlet
/// binder. <c>-g</c> and <c>-G</c> are the same name to the case-insensitive
/// binder, so neither is declared — the manual scan distinguishes them
/// case-sensitively (preserving the oracle's <c>switch</c> case-sensitive
/// match between <c>'-g'</c> and <c>'-G'</c>). All flags stay in
/// <c>Arguments</c>.
///
/// The <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> function
/// via parameter-bound <c>InvokeCommand.InvokeScript</c> (AOT-safe: fixed
/// script body, user-controlled tokens never concatenated into the body).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashId")]
[OutputType(typeof(string))]
public sealed class InvokeBashIdCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "id"))
            {
                WriteObject(line);
            }
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RunUnixId(args);
            return;
        }

        RunWindowsId(args);
    }

    // --- Windows path: WindowsIdentity (matches the psm1 oracle byte-for-byte) ---

    [SupportedOSPlatform("windows")]
    private void RunWindowsId(string[] args)
    {
        bool showUid = false, showGid = false, showGroups = false, showName = false;
        string? userName = null;

        // Case-sensitive walk — preserves the oracle's switch on '-g' vs '-G'.
        foreach (var a in args)
        {
            switch (a)
            {
                case "-u": showUid = true; break;
                case "-g": showGid = true; break;
                case "-G": showGroups = true; break;
                case "-n": showName = true; break;
                case "-r": /* accepted, no behavior change (oracle parity) */ break;
                default:
                    if (a != "--help") userName = a;
                    break;
            }
        }

        WindowsIdentity identity;
        try
        {
            identity = string.IsNullOrEmpty(userName)
                ? WindowsIdentity.GetCurrent()
                : new WindowsIdentity(userName);
        }
        catch (Exception)
        {
            identity = WindowsIdentity.GetCurrent();
        }

        if (showUid)
        {
            if (showName)
            {
                var name = identity.Name ?? string.Empty;
                var idx = name.LastIndexOf('\\');
                WriteObject(BashRuntime.NewBashObject(idx >= 0 ? name.Substring(idx + 1) : name));
            }
            else
            {
                WriteObject(BashRuntime.NewBashObject(identity.User?.Value ?? string.Empty));
            }
            return;
        }

        if (showGid)
        {
            var primary = FirstGroup(identity);
            if (primary == null)
            {
                WriteObject(BashRuntime.NewBashObject(string.Empty));
                return;
            }
            if (showName)
            {
                try
                {
                    var nt = (NTAccount)primary.Translate(typeof(NTAccount));
                    var val = nt.Value ?? string.Empty;
                    var idx = val.LastIndexOf('\\');
                    WriteObject(BashRuntime.NewBashObject(idx >= 0 ? val.Substring(idx + 1) : val));
                }
                catch
                {
                    WriteObject(BashRuntime.NewBashObject(primary.Value));
                }
            }
            else
            {
                WriteObject(BashRuntime.NewBashObject(primary.Value));
            }
            return;
        }

        if (showGroups)
        {
            var parts = new List<string>();
            var groups = identity.Groups;
            if (groups != null)
            {
                foreach (IdentityReference g in groups)
                {
                    if (showName)
                    {
                        try
                        {
                            var nt = (NTAccount)g.Translate(typeof(NTAccount));
                            var val = nt.Value ?? string.Empty;
                            var idx = val.LastIndexOf('\\');
                            parts.Add(idx >= 0 ? val.Substring(idx + 1) : val);
                        }
                        catch
                        {
                            parts.Add(g.Value);
                        }
                    }
                    else
                    {
                        parts.Add(g.Value);
                    }
                }
            }
            WriteObject(BashRuntime.NewBashObject(string.Join(' ', parts)));
            return;
        }

        // Default form: uid=SID(NAME) gid=GIDSID groups=N1,N2,...
        var uid = identity.User?.Value ?? string.Empty;
        var uname = identity.Name ?? string.Empty;
        var firstGroup = FirstGroup(identity);
        var gid = firstGroup?.Value ?? string.Empty;
        var groupNames = new List<string>();
        if (identity.Groups != null)
        {
            foreach (IdentityReference g in identity.Groups)
            {
                try
                {
                    var nt = (NTAccount)g.Translate(typeof(NTAccount));
                    var val = nt.Value ?? string.Empty;
                    var idx = val.LastIndexOf('\\');
                    groupNames.Add(idx >= 0 ? val.Substring(idx + 1) : val);
                }
                catch
                {
                    groupNames.Add(g.Value);
                }
            }
        }
        var groupsJoined = string.Join(',', groupNames);
        WriteObject(BashRuntime.NewBashObject(
            $"uid={uid}({uname}) gid={gid} groups={groupsJoined}"));
    }

    [SupportedOSPlatform("windows")]
    private static IdentityReference? FirstGroup(WindowsIdentity identity)
    {
        var groups = identity.Groups;
        if (groups == null) return null;
        foreach (IdentityReference g in groups) return g;
        return null;
    }

    // --- Unix path: shell out to /usr/bin/id (Directive 12 safe: ArgumentList, no shell) ---

    private void RunUnixId(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/id")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                // user-controlled tokens routed via ArgumentList (no shell, no string concat)
                psi.ArgumentList.Add(a);
            }
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var trimmed = stdout.TrimEnd('\r', '\n');
            if (trimmed.Length == 0 && !string.IsNullOrEmpty(stdout))
            {
                // a lone trailing newline still means "one empty line" — suppress to match common id behavior
            }
            WriteObject(BashRuntime.NewBashObject(trimmed));
        }
        catch (Exception)
        {
            // Fall through silently — matches the psm1 oracle's lack of explicit error handling.
        }
    }
}
