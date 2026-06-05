using System.Linq;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashLn</c> (REFACTOR-2).
/// Creates a hard link or symbolic link from the second operand to the
/// first, matching the GNU coreutils <c>ln</c> argument order
/// (<c>ln TARGET LINK_NAME</c>). Supports <c>-s</c> (symbolic), <c>-f</c>
/// (force — remove existing link target first), <c>-v</c> (verbose).
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved:
/// <list type="bullet">
/// <item>Fewer than 2 operands → "missing file operand" error.</item>
/// <item>With <c>-f</c>, an existing link name is removed before creation.</item>
/// <item>Without <c>-f</c>, an existing link name → "File exists" error
/// and return.</item>
/// <item><c>-s</c> → <see cref="File.CreateSymbolicLink"/> /
/// <see cref="Directory.CreateSymbolicLink"/> depending on target type.</item>
/// <item>No <c>-s</c> → <see cref="File.CreateHardLink"/> (.NET 11+, fall
/// back to a P/Invoke on older runtimes — but the project's target framework
/// pins us above the bar).</item>
/// <item>Verbose mode emits <c>'link' -&gt; 'target'\n</c> for symlinks,
/// <c>'link' =&gt; 'target'\n</c> for hard links.</item>
/// </list>
/// <para>
/// <b>One colliding flag</b> declared explicitly: <c>-v</c> vs
/// <c>-Verbose</c>. <c>-s</c> / <c>-f</c> have no common-parameter
/// collision and stay in <c>Arguments</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashLn")]
[OutputType(typeof(string))]
public sealed class InvokeBashLnCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "ln", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "ln"))
            {
                WriteObject(line);
            }
            return;
        }

        bool symbolic = false;
        bool force = false;
        bool verbose = v.IsPresent;
        var operands = new List<string>();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-s": symbolic = true; break;
                case "-f": force = true; break;
                case "-v": verbose = true; break;
                default:
                    // Bundled short flags: -sf, -sv, -fv, -svf, etc.
                    if (arg.Length > 2 && arg[0] == '-'
                        && arg.Substring(1).All(c => c == 's' || c == 'f' || c == 'v'))
                    {
                        foreach (var c in arg.Substring(1))
                        {
                            if (c == 's') symbolic = true;
                            else if (c == 'f') force = true;
                            else if (c == 'v') verbose = true;
                        }
                    }
                    else
                    {
                        operands.Add(arg);
                    }
                    break;
            }
        }

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "ln: missing file operand");
            return;
        }

        var target = operands[0];
        var linkName = operands[1];
        var linkAbsolute = SessionState.Path.GetUnresolvedProviderPathFromPSPath(linkName);

        if (force && (File.Exists(linkAbsolute) || Directory.Exists(linkAbsolute)))
        {
            try
            {
                if (Directory.Exists(linkAbsolute) && !IsReparsePoint(linkAbsolute))
                {
                    Directory.Delete(linkAbsolute, recursive: true);
                }
                else
                {
                    File.Delete(linkAbsolute);
                }
            }
            catch (Exception ex)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"ln: cannot remove existing '{linkName}': {ex.Message}");
                return;
            }
        }

        if (File.Exists(linkAbsolute) || Directory.Exists(linkAbsolute))
        {
            FileSystemHelpers.WriteBashError(this,
                $"ln: failed to create {(symbolic ? "symbolic " : "")}link '{linkName}': File exists");
            return;
        }

        try
        {
            if (symbolic)
            {
                // Target may not exist for a symlink, so we have to guess
                // file vs directory link type from whatever does exist at
                // the target path. If the target doesn't exist, default to
                // a file symlink (bash ln does the same).
                var targetAbsolute = Path.IsPathRooted(target)
                    ? target
                    : Path.Combine(Path.GetDirectoryName(linkAbsolute) ?? "", target);

                if (Directory.Exists(targetAbsolute))
                {
                    Directory.CreateSymbolicLink(linkAbsolute, target);
                }
                else
                {
                    File.CreateSymbolicLink(linkAbsolute, target);
                }
            }
            else
            {
                // Hard link via P/Invoke-free path: File.CreateHardLink is
                // not in .NET stdlib, so we go through the Win32 helper.
                // POSIX uses link(2) via System.IO.File.CreateSymbolicLink
                // ... actually we shell out to ln via psm1 fallback on POSIX
                // since System.IO lacks a hard-link API. On Windows we use
                // the Win32 CreateHardLink. The psm1 oracle used
                // New-Item -ItemType HardLink which delegates to the same
                // OS calls — replicate via InvokeCommand.InvokeScript with
                // a parameter-bound body.
                var rc = InvokeCommand.InvokeScript(
                    "param($lk, $tg) New-Item -ItemType HardLink -Path $lk -Target $tg -Force | Out-Null; $?",
                    linkAbsolute, target);
                if (rc.Count == 0 || !(rc[0] is PSObject po && po.BaseObject is bool ok && ok))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"ln: failed to create hard link '{linkName}': New-Item returned failure");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            FileSystemHelpers.WriteBashError(this,
                $"ln: failed to create {(symbolic ? "symbolic " : "")}link '{linkName}': {ex.Message}");
            return;
        }

        if (verbose)
        {
            var bashLink = FileSystemHelpers.ToBashPath(linkName);
            var bashTarget = FileSystemHelpers.ToBashPath(target);
            WriteObject(BashRuntime.NewBashObject(
                symbolic ? $"'{bashLink}' -> '{bashTarget}'\n"
                         : $"'{bashLink}' => '{bashTarget}'\n"));
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}
