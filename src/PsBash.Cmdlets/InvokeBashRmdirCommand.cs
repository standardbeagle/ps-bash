using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRmdir</c>
/// (REFACTOR-2). Removes each operand directory, matching GNU coreutils
/// <c>rmdir</c> semantics — only empty directories are removed, with
/// <c>-p</c> chaining up through empty parent dirs and <c>-v</c> emitting
/// a verbose line per removal.
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved:
/// <list type="bullet">
/// <item>No operands → bash-style "missing operand" error.</item>
/// <item>Target missing → bash-style "No such file or directory".</item>
/// <item>Target is a file → "Not a directory" error and $LASTEXITCODE=1.</item>
/// <item>Target is a non-empty directory → "Directory not empty" error and
/// $LASTEXITCODE=1.</item>
/// <item>With <c>-p</c>, after the leaf is removed, walk parent dirs upward
/// and remove each one that is empty. Stop on the first non-empty.</item>
/// </list>
/// <para>
/// <b>Two colliding flags</b> declared explicitly — same hazards as
/// <see cref="InvokeBashMkdirCommand"/>: <c>-p</c> vs
/// <c>-ProgressAction</c> / <c>-PipelineVariable</c>; <c>-v</c> vs
/// <c>-Verbose</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRmdir")]
[OutputType(typeof(string))]
public sealed class InvokeBashRmdirCommand : PSCmdlet
{
    [Parameter] public SwitchParameter p { get; set; }
    [Parameter] public SwitchParameter v { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "rmdir", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "rmdir"))
            {
                WriteObject(line);
            }
            return;
        }

        bool removeParents = p.IsPresent;
        bool verbose = v.IsPresent;

        var operands = new List<string>();
        foreach (var a in args) operands.Add(a);

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "rmdir: missing operand");
            return;
        }

        bool hadError = false;

        foreach (var dir in operands)
        {
            var absolute = SessionState.Path.GetUnresolvedProviderPathFromPSPath(dir);

            if (!Directory.Exists(absolute))
            {
                if (File.Exists(absolute))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rmdir: failed to remove '{dir}': Not a directory");
                }
                else
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rmdir: failed to remove '{dir}': No such file or directory");
                }
                hadError = true;
                continue;
            }

            try
            {
                if (HasAnyEntries(absolute))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rmdir: failed to remove '{dir}': Directory not empty");
                    hadError = true;
                    continue;
                }

                Directory.Delete(absolute);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"rmdir: failed to remove '{dir}': {ex.Message}");
                hadError = true;
                continue;
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"rmdir: removing directory, '{FileSystemHelpers.ToBashPath(dir)}'\n"));
            }

            if (removeParents)
            {
                var parent = Path.GetDirectoryName(absolute);
                while (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    if (HasAnyEntries(parent)) break;

                    if (verbose)
                    {
                        WriteObject(BashRuntime.NewBashObject(
                            $"rmdir: removing directory, '{FileSystemHelpers.ToBashPath(parent)}'\n"));
                    }
                    try { Directory.Delete(parent); }
                    catch { break; }
                    parent = Path.GetDirectoryName(parent);
                }
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    // EnumerateFileSystemEntries returns hidden + system entries by default
    // on Windows, matching the psm1 oracle's Get-ChildItem -Force semantics.
    private static bool HasAnyEntries(string dir)
    {
        using var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
        return e.MoveNext();
    }
}
