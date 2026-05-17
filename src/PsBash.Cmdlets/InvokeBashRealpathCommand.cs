using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRealpath</c> function
/// (REFACTOR-2). Resolves each non-flag operand to an absolute, canonical
/// path, matching GNU coreutils <c>realpath</c>'s default behavior.
///
/// Behavioral parity oracle: the original psm1 function. For each operand:
/// <list type="bullet">
/// <item>First attempt <c>Resolve-Path</c> via the session's path intrinsics
/// (handles existing files, links, PSDrives).</item>
/// <item>On failure, fall back to
/// <c>SessionState.Path.GetUnresolvedProviderPathFromPSPath</c>, which
/// computes the canonical path string even for paths that do not exist —
/// matching the psm1 oracle's catch-block fallback.</item>
/// </list>
/// Operands starting with <c>-</c> are skipped, exactly as the psm1 oracle
/// did (it never implemented <c>-e</c> / <c>-m</c> / <c>--relative-to</c>,
/// so any flag is silently ignored). Output goes through
/// <see cref="BashRuntime.NewBashObject"/> with default
/// <c>PsBash.TextOutput</c> — bare strings per resolved path.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRealpath")]
[OutputType(typeof(string))]
public sealed class InvokeBashRealpathCommand : PSCmdlet
{
    /// <summary>
    /// GNU realpath's <c>-e</c> (canonicalize-existing). The psm1 oracle never
    /// implemented its semantics — every <c>-</c>-prefixed token was silently
    /// skipped. We preserve that no-op behavior, but <c>-e</c> must be declared
    /// as an explicit <see cref="SwitchParameter"/> here: under
    /// <see cref="PSCmdlet"/> parameter binding, the bare token <c>-e</c> is a
    /// prefix of the common parameters <c>-ErrorAction</c> /
    /// <c>-ErrorVariable</c>, so an unbound <c>-e</c> would fail binding with
    /// "ambiguous parameter name" before reaching <see cref="Arguments"/>.
    /// An exact parameter-name match beats a common-parameter prefix match,
    /// so declaring <c>e</c> here lets <c>realpath -e foo</c> parse the way
    /// the psm1 oracle did.
    /// </summary>
    [Parameter]
    public SwitchParameter e { get; set; }

    /// <summary>
    /// GNU realpath's <c>-m</c> (canonicalize-missing). Same prefix-collision
    /// rationale as <see cref="e"/> — <c>-m</c> is unambiguous on its own but
    /// declared explicitly for symmetry and to keep the no-op silent.
    /// </summary>
    [Parameter]
    public SwitchParameter m { get; set; }

    /// <summary>
    /// GNU realpath's <c>-s</c> / <c>--strip</c> / <c>--no-symlinks</c>.
    /// </summary>
    [Parameter]
    public SwitchParameter s { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "realpath"))
            {
                WriteObject(line);
            }
            return;
        }

        foreach (var path in args)
        {
            if (path.StartsWith('-')) continue;

            string full;
            try
            {
                // Resolve-Path equivalent: PathInfo via the session's
                // path intrinsics. We take the first match's Path
                // (the PSPath form) to mirror Resolve-Path's default.
                var resolved = SessionState.Path.GetResolvedPSPathFromPSPath(path);
                full = resolved.Count > 0 ? resolved[0].Path : path;
            }
            catch
            {
                full = SessionState.Path.GetUnresolvedProviderPathFromPSPath(path);
            }

            WriteObject(BashRuntime.NewBashObject(full));
        }
    }
}
