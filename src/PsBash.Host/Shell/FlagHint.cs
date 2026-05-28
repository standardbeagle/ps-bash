namespace PsBash.Host.Shell;

/// <summary>
/// One row of the floating doc panel, unified across sources (bash flag specs and live
/// PowerShell cmdlet parameters) so the renderer and the future man-page drill-down (P4) treat
/// them the same.
/// </summary>
/// <param name="Insert">The bare flag/parameter to type into the buffer when selected, e.g.
/// <c>-name</c> or <c>-CommonTCPPort</c> — never the argument/type/description.</param>
/// <param name="Head">
/// The left column: the flag/parameter with its argument or type, e.g. <c>-name PATTERN</c>
/// (bash) or <c>-CommonTCPPort &lt;String&gt;</c> (PowerShell).
/// </param>
/// <param name="Desc">The short summary / value-set, e.g. a description or <c>HTTP, RDP, SMB</c>.</param>
/// <param name="Detail">Optional long description for the man-page view (P4).</param>
/// <param name="Examples">Optional example invocations for the man-page view (P4).</param>
internal sealed record FlagHint(
    string Insert,
    string Head,
    string Desc,
    string? Detail = null,
    IReadOnlyList<string>? Examples = null);
