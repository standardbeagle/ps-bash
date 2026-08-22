using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>avtui</c> — an interactive media gallery: every video / audio / image in a directory, probed
/// and listed, with single-key capture. <c>↑↓</c> / <c>j k</c> move, Enter expands a row's full probe
/// detail, <c>s</c> grabs a still from the middle of the focused clip, <c>g</c> builds a demo GIF,
/// <c>c</c> a browser-safe MP4, <c>r</c> rescans, <c>q</c> quits — and each new artifact appears in
/// the list as soon as it is written. Coloured by the <c>av</c> stylesheet through the same Strata +
/// Spectre loop as <c>Show-Styled</c>. Strata-gated (ships only with the styling cmdlets).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFfmpegTui")]
public sealed class InvokeBashFfmpegTuiCommand : PSCmdlet
{
    /// <summary>The directory (or single file) to open. Omitted → the current directory.</summary>
    [Parameter(Position = 0, ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        string? cwd = null;
        try { cwd = SessionState.Path.CurrentFileSystemLocation.Path; }
        catch { /* provider path unavailable — ffprobe inherits the process cwd */ }

        var operand = Arguments?.FirstOrDefault(a => !a.StartsWith('-'));
        var target = operand is null
            ? cwd ?? Directory.GetCurrentDirectory()
            : Path.IsPathRooted(operand) ? operand : Path.Combine(cwd ?? Directory.GetCurrentDirectory(), operand);

        if (StyledInteractiveSession.RunMediaGallery(target, cwd) < 0)
        {
            WriteObject(BashRuntime.NewBashObject(
                "avtui: needs an interactive terminal. Use `psav probe *.mp4 | Format-Styled av` for a static view."));
        }
    }
}
