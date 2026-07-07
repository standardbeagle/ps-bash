using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashMktemp</c>
/// (REFACTOR-2 follow-on). Creates a uniquely-named temporary file (or
/// directory with <c>-d</c>) under
/// <c>{Path.GetTempPath()}/ps-bash/proc-sub/</c>, matching the psm1
/// oracle's exact branches:
/// <list type="bullet">
/// <item>No template + no <c>-d</c>: create an empty file with a
/// <see cref="Path.GetRandomFileName"/> name.</item>
/// <item><c>-d</c>: create a directory instead of a file.</item>
/// <item>Template (e.g. <c>myapp.XXXXXX</c>): strip the trailing
/// <c>X</c>+ run, take the basename as a prefix, then suffix
/// <see cref="Path.GetRandomFileName"/>. Note: the psm1 oracle does NOT
/// honor the template directory portion — it always plants the result in
/// <c>ps-bash/proc-sub/</c>. Preserved.</item>
/// </list>
/// <para>
/// Output: a typed <c>PsBash.MktempOutput</c> <see cref="PSObject"/> with
/// <c>Path</c> + <c>BashText</c> properties, matching the oracle's
/// <c>[PSCustomObject]@{PSTypeName='PsBash.MktempOutput'; Path=...;
/// BashText=...}</c> shape byte-for-byte. <c>Set-BashDisplayProperty</c>
/// in the oracle adds a <c>ToString()</c> ScriptMethod returning
/// <c>BashText</c>; we replicate the visible behavior by also assigning
/// the path string to BashText (the default formatter renders it).
/// </para>
/// <para>
/// <b>One colliding flag</b> declared as an explicit
/// <see cref="SwitchParameter"/>: <c>-d</c> prefix-collides with
/// <c>-Debug</c> (same hazard <c>mkdir</c> handled). Any other
/// <c>-</c>-prefixed token (e.g. <c>-u</c>, <c>--suffix=X</c>) falls
/// through the catch-all loop as a template candidate — exactly how the
/// psm1 oracle treated unknown args (last non-<c>-d</c> wins). This is a
/// deliberate parity preservation, not a feature.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMktemp")]
[OutputType("PsBash.MktempOutput")]
public sealed class InvokeBashMktempCommand : PSCmdlet
{
    [Parameter] public SwitchParameter d { get; set; }

    /// <summary>
    /// GNU mktemp's <c>-p DIR</c> (place the temp under DIR). Declared as a decoy
    /// value-bearing parameter because the bare <c>-p</c> prefix-collides with
    /// <c>-ProgressAction</c> (added in PS 7.4) and the binder crashes ("ambiguous")
    /// before <see cref="Arguments"/> sees it. When present, DIR overrides the default
    /// <c>ps-bash/proc-sub/</c> target.
    /// </summary>
    [Parameter] public string? P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "mktemp", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "mktemp"))
            {
                WriteObject(line);
            }
            return;
        }

        bool makeDir = d.IsPresent;
        string? template = null;

        // psm1 oracle: foreach arg, if "-d" set makeDir, else template = arg.
        // The declared SwitchParameter `d` already removed bare "-d" from
        // Arguments, but we still recognize a stray "-d" here for parity in
        // case a token reaches the catch-all (matches the oracle's `-ceq`
        // comparison). Every other token — including unknown flags like
        // `-u` or `--suffix=X` — becomes a template candidate; last wins.
        foreach (var arg in args)
        {
            if (arg == "-d") { makeDir = true; continue; }
            template = arg;
        }

        // -p DIR overrides the default target directory (bound to the P decoy so the
        // bare -p never reaches the crashing binder). Normalize a unix-style path to
        // the host convention, as the other file cmdlets do.
        var subDir = !string.IsNullOrEmpty(P)
            ? FileSystemHelpers.NormalizeOperandPath(P!)
            : Path.Combine(Path.GetTempPath(), "ps-bash", "proc-sub");
        Directory.CreateDirectory(subDir);

        string name = Path.GetRandomFileName();
        if (!string.IsNullOrEmpty(template))
        {
            // Oracle: $prefix = $template -replace 'X+$', ''
            //        $prefix = [System.IO.Path]::GetFileName($prefix)
            //        $name = $prefix + [System.IO.Path]::GetRandomFileName()
            var trimmed = System.Text.RegularExpressions.Regex.Replace(template!, "X+$", "");
            var prefix = Path.GetFileName(trimmed);
            name = prefix + Path.GetRandomFileName();
        }

        var fullPath = Path.Combine(subDir, name);

        try
        {
            // mktemp's contract is a PRIVATE scratch target (it routinely holds secrets), so
            // create it exclusively (O_EXCL — never adopt a pre-existing path) and, on Unix,
            // with owner-only permissions (0700 dir / 0600 file). Default umask + non-exclusive
            // create would leave it group/world-readable and racy.
            if (makeDir)
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(fullPath);
                else
                    Directory.CreateDirectory(fullPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                var fileOpts = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,   // O_EXCL: fail if the path already exists
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                    fileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var fs = new FileStream(fullPath, fileOpts);
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"mktemp: failed to create: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        // Typed PSObject — match the oracle's PSTypeName + property shape.
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.MktempOutput");
        obj.Properties.Add(new PSNoteProperty("Path", fullPath));
        obj.Properties.Add(new PSNoteProperty("BashText", fullPath));
        WriteObject(obj);
    }
}
