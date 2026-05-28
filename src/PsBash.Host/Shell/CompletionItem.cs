namespace PsBash.Host.Shell;

/// <summary>
/// A single completion candidate, with the insert text and the list-display text kept as
/// SEPARATE fields on purpose.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InsertText"/> is the ONLY text written into the command line when the candidate is
/// accepted. <see cref="DisplayText"/> is what the completion list shows — it may carry extra
/// annotation a user should see but never type, e.g. a flag's description
/// (<c>"-name  - name pattern"</c>).
/// </para>
/// <para>
/// The split is the whole point of this type. The line editor's apply path reads
/// <see cref="InsertText"/> and its list renderer reads <see cref="DisplayText"/>, so a provider
/// physically cannot make its annotation get typed into the buffer. The old contract — a bare
/// <c>string</c> that doubled as both — let <c>CompleteFlags</c> glue the description onto the
/// candidate, and accepting it inserted <c>"-name  - name pattern"</c> verbatim. A description
/// now lives in a field the apply path never touches.
/// </para>
/// <para>Mirrors PowerShell's <c>CompletionResult</c> (CompletionText vs ListItemText).</para>
/// </remarks>
public sealed record CompletionItem(string InsertText, string DisplayText)
{
    /// <summary>
    /// A plain candidate whose list label is exactly the text inserted (paths, command names,
    /// word-list entries). This is the safe default: there is no annotation to leak.
    /// </summary>
    public CompletionItem(string text) : this(text, text) { }

    /// <summary>
    /// A candidate that inserts <paramref name="insert"/> but is LISTED as <paramref name="display"/>.
    /// Use this — never string concatenation — when a candidate carries a description the user
    /// must not type (e.g. flag help).
    /// </summary>
    public static CompletionItem Labeled(string insert, string display) => new(insert, display);
}
