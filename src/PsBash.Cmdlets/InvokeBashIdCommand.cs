using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using PsBash.Cmdlets.Args;

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
/// ARGV (Windows): the shared ordered parser (<see cref="IdSpec"/>), so bundles (<c>-un</c>) and long forms work
/// and <c>-g</c> / <c>-G</c> stay distinct. The transpiler passes every word quoted
/// (<c>PsEmitter.OrderedArgCommands</c>); the <c>a</c> decoy is for a DIRECT <c>-a</c>, which would otherwise
/// prefix-match <c>-Arguments</c>.
///
/// The <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> function
/// via parameter-bound <c>InvokeCommand.InvokeScript</c> (AOT-safe: fixed
/// script body, user-controlled tokens never concatenated into the body).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashId")]
[OutputType(typeof(string))]
public sealed class InvokeBashIdCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Decoy: a bare <c>-a</c> prefix-matches <c>-Arguments</c>. Put back in front of the arguments.</summary>
    [Parameter] public SwitchParameter a { get; set; }

    private const string OptUser = "user", OptGroup = "group", OptGroups = "groups", OptName = "name", OptReal = "real";

    /// <summary>id's option surface on Windows (the Unix path hands argv to the system id).</summary>
    internal static readonly OptSpecSet IdSpec = new(
        new[]
        {
            new OptSpec(OptUser, 'u', "user"),
            new OptSpec(OptGroup, 'g', "group"),
            new OptSpec(OptGroups, 'G', "groups"),
            new OptSpec(OptName, 'n', "name"),
            new OptSpec(OptReal, 'r', "real"),
            new OptSpec("ignored", 'a', null), // GNU: accepted and ignored, for compatibility
        },
        validButUnsupported: new[] { "-z", "--zero", "-Z", "--context" },
        allowAbbrev: true,
        gnuInfoOptions: true);

    protected override void ProcessRecord()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (a.IsPresent, "-a"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "id", args)) return;
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
        // The shared ordered parser: bundles (`-un`), long forms, case-sensitive `-g` / `-G`. REGRESSION: an exact-word
        // switch took `-un` for a user name ("id: '-un': no such user").
        var parsed = ArgParser.Parse(args, IdSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "id", parsed)) return;
        bool showUid = parsed.Has(OptUser), showGid = parsed.Has(OptGroup), showGroups = parsed.Has(OptGroups);
        bool showName = parsed.Has(OptName);
        int only = (showUid ? 1 : 0) + (showGid ? 1 : 0) + (showGroups ? 1 : 0);
        if (only > 1)
        {
            FileSystemHelpers.WriteBashError(this, "id: cannot print \"only\" of more than one choice");
            return;
        }
        if (only == 0 && (showName || parsed.Has(OptReal)))
        {
            FileSystemHelpers.WriteBashError(this, "id: cannot print only names or real IDs in default format");
            return;
        }
        var operands = parsed.Operands();
        string? userName = operands.Count > 0 ? operands[0] : null;

        // GNU: a NAME no account answers to is `id: ‘NAME’: no such user`, status 1. REGRESSION: it silently printed
        // the CURRENT user's identity. (An existing account WindowsIdentity cannot open — a local user without S4U
        // logon rights — still falls back to the current identity; see runtime-command-reference.md.)
        if (!string.IsNullOrEmpty(userName) && !AccountExists(userName))
        {
            FileSystemHelpers.WriteBashError(this, $"id: {GnuQuote.Locale(userName)}: no such user");
            return;
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

    /// <summary>True when the account database resolves <paramref name="name"/> (a user name, DOMAIN\user or SID).</summary>
    [SupportedOSPlatform("windows")]
    private static bool AccountExists(string name)
    {
        try
        {
            _ = new NTAccount(name).Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException)
        {
            return false;
        }
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
            var psi = new ProcessStartInfo(PsBash.Core.Platform.SystemTools.Resolve("id"))
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
            // Bounded spawn with concurrent stream drain + kill-tree on timeout
            // (BashRuntime.RunChildProcess) so a hung /usr/bin/id can never wedge
            // the single-threaded host runspace.
            var run = BashRuntime.RunChildProcess(psi);
            // The system id's own diagnostics and status (`id nosuchuser`: "no such user", 1) reach the script.
            if (run.Stderr.Length > 0) FileSystemHelpers.WriteStderr(this, run.Stderr.TrimEnd('\r', '\n'));
            var trimmed = run.Stdout.TrimEnd('\r', '\n');
            if (run.Stdout.Length > 0) WriteObject(BashRuntime.NewBashObject(trimmed));
            if (run.TimedOut || run.ExitCode != 0) FileSystemHelpers.SetLastExitCode(this, run.TimedOut ? 1 : run.ExitCode);
        }
        catch (Exception)
        {
            // Fall through silently — matches the psm1 oracle's lack of explicit error handling.
        }
    }
}
