using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>gtui</c> — an interactive git-status pane (the first lazygit-style TUI for ps-bash). Navigate
/// changed files with ↑↓ / j k, stage / unstage with <c>s</c> / <c>u</c> / Space (the view refreshes
/// after each), Enter expands a row's detail, <c>r</c> refreshes, <c>q</c> quits. Coloured by the
/// <c>git</c> stylesheet through the same Strata + Spectre interactive loop as <c>Show-Styled</c>.
/// Mutating actions are limited to staging (git add / reset HEAD) — use native <c>git</c> for
/// commits / rebases / pushes. Strata-gated (ships only with the styling cmdlets).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashGitTui")]
public sealed class InvokeBashGitTuiCommand : PSCmdlet
{
    protected override void EndProcessing()
    {
        string? cwd = null;
        try { cwd = SessionState.Path.CurrentFileSystemLocation.Path; }
        catch { /* provider path unavailable — git inherits the process cwd */ }

        if (StyledInteractiveSession.RunGitStatus(cwd) < 0)
        {
            WriteObject(BashRuntime.NewBashObject(
                "gtui: needs an interactive terminal. Use `psgit status | Format-Styled git` for a static view."));
        }
    }
}
