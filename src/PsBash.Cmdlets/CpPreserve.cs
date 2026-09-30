using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>What <c>cp</c> does about the source's permission bits.</summary>
internal enum CpModePolicy
{
    /// <summary>Not asked: a new file gets the source's bits masked by the umask; an existing destination keeps its own.</summary>
    Default,

    /// <summary><c>--preserve=mode</c> / <c>-p</c> / <c>-a</c>: the destination gets exactly the source's bits.</summary>
    Preserve,

    /// <summary><c>--no-preserve=mode</c>: a new file gets 0666 (0777 for a directory) masked by the umask.</summary>
    Clear,
}

/// <summary>
/// The attribute set <c>cp</c> was asked to preserve, resolved from the command line.
/// <c>-p</c> / a bare <c>--preserve</c> = mode,ownership,timestamps; <c>-a</c> = all; the
/// <c>--preserve=LIST</c> and <c>--no-preserve=LIST</c> options name attributes (mode, timestamps,
/// ownership, links, context, xattr, all — unique prefixes accepted) and are applied IN ORDER, so the
/// last option naming an attribute wins (<c>--no-preserve=timestamps -p</c> preserves them again).
/// Only mode and timestamps have an effect: ownership, links and xattr are accepted and do nothing
/// here (no error, like GNU on a filesystem that cannot hold them); <c>context</c> requested BY NAME
/// is GNU's "SELinux-enabled kernel" error, while <c>all</c> quietly skips it.
/// </summary>
internal sealed class CpPreserve
{
    public CpModePolicy Mode { get; private set; } = CpModePolicy.Default;

    public bool Timestamps { get; private set; }

    /// <summary><c>--preserve=context</c> was named explicitly (never via <c>all</c>).</summary>
    public bool ContextRequested { get; private set; }

    /// <summary>True when any attribute needs work after the copy.</summary>
    public bool Any => Mode != CpModePolicy.Default || Timestamps;

    private enum Attr { Mode, Timestamps, Ownership, Links, Context, Xattr, All }

    private static readonly (string Name, Attr Value)[] AttrWords =
    {
        ("mode", Attr.Mode), ("timestamps", Attr.Timestamps), ("ownership", Attr.Ownership),
        ("links", Attr.Links), ("context", Attr.Context), ("xattr", Attr.Xattr), ("all", Attr.All),
    };

    private const string ValidBlock =
        "  - 'mode'\n  - 'timestamps'\n  - 'ownership'\n  - 'links'\n  - 'context'\n  - 'xattr'\n  - 'all'";

    /// <summary>
    /// Resolves the preserve state from <paramref name="parsed"/>'s tokens (<paramref name="preserveId"/> =
    /// <c>-p</c>, <paramref name="archiveId"/> = <c>-a</c>, <paramref name="preserveListId"/> = <c>--preserve[=LIST]</c>,
    /// <paramref name="noPreserveId"/> = <c>--no-preserve=LIST</c>). False with the GNU usage message on a bad word.
    /// </summary>
    public static bool TryFrom(ParsedArgs parsed, string preserveId, string archiveId, string preserveListId,
        string noPreserveId, out CpPreserve result, out string? error)
    {
        result = new CpPreserve();
        error = null;
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            if (t.OptId == preserveId)
            {
                result.Set(Attr.Mode, true); result.Set(Attr.Timestamps, true); result.Set(Attr.Ownership, true);
            }
            else if (t.OptId == archiveId)
            {
                result.Set(Attr.All, true);
            }
            else if (t.OptId == preserveListId)
            {
                if (t.Value is null)
                {
                    result.Set(Attr.Mode, true); result.Set(Attr.Timestamps, true); result.Set(Attr.Ownership, true);
                }
                else if (!result.ApplyList("preserve", t.Value, on: true, out error)) return false;
            }
            else if (t.OptId == noPreserveId)
            {
                if (!result.ApplyList("no-preserve", t.Value ?? "", on: false, out error)) return false;
            }
        }
        return true;
    }

    private bool ApplyList(string option, string list, bool on, out string? error)
    {
        error = null;
        var words = new List<Attr>();
        foreach (var word in list.Split(','))
        {
            if (!GnuArgMatch.TryMatch("cp", option, word, AttrWords, ValidBlock, out var attr, out error)) return false;
            words.Add(attr);
        }
        foreach (var a in words)
        {
            if (a == Attr.Context && on) ContextRequested = true;
            Set(a, on);
        }
        return true;
    }

    private void Set(Attr attr, bool on)
    {
        switch (attr)
        {
            case Attr.Mode: Mode = on ? CpModePolicy.Preserve : CpModePolicy.Clear; break;
            case Attr.Timestamps: Timestamps = on; break;
            case Attr.Context: if (!on) ContextRequested = false; break;
            case Attr.All:
                // `all` preserves everything that exists here; context is silently skipped.
                Mode = on ? CpModePolicy.Preserve : CpModePolicy.Clear;
                Timestamps = on;
                if (!on) ContextRequested = false;
                break;
            // ownership, links, xattr: nothing to do on these filesystems.
        }
    }
}
