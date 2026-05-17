using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashReadlink</c> function
/// (REFACTOR-2). Prints the resolved symbolic-link target or canonical file
/// name for each operand, matching GNU coreutils <c>readlink</c>'s default
/// behavior as implemented by the psm1 oracle.
///
/// Behavioral parity oracle: the original psm1 function.
/// <list type="bullet">
/// <item>Default (no <c>-f</c>): for each operand, call <c>Get-Item</c>; if the
/// item is missing, emit a bash-style error and continue. Otherwise emit
/// <c>FileSystemInfo.LinkTarget</c> (the symlink target) when present, or the
/// item's <c>FullName</c> for a non-symlink — mirroring the psm1 oracle's
/// <c>if ($item.Target) { $item.Target } else { $item.FullName }</c> branch.</item>
/// <item><c>-f</c> (canonicalize): call <c>Resolve-Path</c>; if it fails, emit
/// the same bash-style error. Otherwise emit the canonical path string.</item>
/// </list>
/// Output is a typed <c>PsBash.ReadlinkOutput</c> PSObject with <c>Path</c> and
/// <c>BashText</c> note properties, matching the psm1 oracle's PSCustomObject.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashReadlink")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashReadlinkCommand : PSCmdlet
{
    /// <summary>
    /// GNU readlink's <c>-f</c> (canonicalize). The bare flag <c>-f</c> has no
    /// PowerShell common-parameter prefix collision, but we declare it as an
    /// explicit <see cref="SwitchParameter"/> so the binder treats it as a
    /// known parameter instead of routing it through
    /// <see cref="Arguments"/>. The psm1 oracle distinguishes <c>-f</c> with
    /// <c>-ceq</c> (case-sensitive equals), so we honor only the lowercase
    /// short form here too.
    /// </summary>
    [Parameter]
    public SwitchParameter f { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "readlink"))
            {
                WriteObject(line);
            }
            return;
        }

        // psm1 oracle: every non-"-f" token is an operand. Even unknown
        // -prefixed tokens are operands (and will fail Get-Item). We preserve
        // this exactly — only "-f" is consumed as the canonicalize flag.
        var operands = new List<string>();
        foreach (var a in args)
        {
            if (a == "-f") continue;
            operands.Add(a);
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "readlink: missing operand");
            return;
        }

        foreach (var path in operands)
        {
            string text;
            if (f.IsPresent)
            {
                try
                {
                    var resolved = SessionState.Path.GetResolvedPSPathFromPSPath(path);
                    if (resolved.Count == 0)
                    {
                        FileSystemHelpers.WriteBashError(this,
                            $"readlink: {path}: No such file or directory");
                        continue;
                    }
                    text = resolved[0].ProviderPath;
                }
                catch
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"readlink: {path}: No such file or directory");
                    continue;
                }
            }
            else
            {
                string fullPath;
                try
                {
                    fullPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(path);
                }
                catch
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"readlink: {path}: No such file or directory");
                    continue;
                }

                FileSystemInfo? info = null;
                try
                {
                    if (Directory.Exists(fullPath))
                    {
                        info = new DirectoryInfo(fullPath);
                    }
                    else if (File.Exists(fullPath))
                    {
                        info = new FileInfo(fullPath);
                    }
                }
                catch
                {
                    info = null;
                }

                if (info == null)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"readlink: {path}: No such file or directory");
                    continue;
                }

                // psm1 oracle: `if ($item.Target) { $item.Target } else { $item.FullName }`.
                // FileSystemInfo.LinkTarget is the .NET API for reading a
                // symlink's target string (null for non-links).
                var target = info.LinkTarget;
                text = !string.IsNullOrEmpty(target) ? target! : info.FullName;
            }

            var output = new PSObject();
            output.TypeNames.Insert(0, "PsBash.ReadlinkOutput");
            output.Properties.Add(new PSNoteProperty("Path", text));
            output.Properties.Add(new PSNoteProperty("BashText", text));
            WriteObject(output);
        }
    }
}
