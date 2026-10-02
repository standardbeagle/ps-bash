using System.Linq;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU's <c>XARGMATCH</c> for an option whose argument is one of a fixed set of words
/// (<c>rm --interactive=WHEN</c>, <c>touch --time=WORD</c>, later <c>cp --preserve=LIST</c>). An
/// EXACT name wins (<c>no</c> beats being a prefix of <c>none</c>); otherwise a prefix selects the
/// entry when every name it could mean carries the SAME value (<c>n</c> for never/no/none is fine);
/// a prefix shared by names with different values — including the empty string — is
/// <c>ambiguous</c>, and a non-prefix is <c>invalid</c>. Both are a usage error whose text lists the
/// valid words, exactly as GNU prints it (straight quotes: ps-bash's diagnostics never use the
/// locale's curly ones).
/// </summary>
internal static class GnuArgMatch
{
    /// <param name="command">The command name for the message prefix (<c>rm</c>).</param>
    /// <param name="option">The long option WITHOUT dashes (<c>interactive</c>).</param>
    /// <param name="arg">The word the user typed.</param>
    /// <param name="table">Every accepted spelling and what it means.</param>
    /// <param name="validBlock">The "Valid arguments are:" body, one <c>"  - 'a', 'b'"</c> line per
    /// meaning — GNU groups the synonyms, so the caller supplies the grouping.</param>
    public static bool TryMatch<T>(string command, string option, string arg,
        IReadOnlyList<(string Name, T Value)> table, string validBlock, out T value, out string? error,
        string? subject = null)
    {
        error = null;
        foreach (var (name, v) in table)
        {
            if (name == arg) { value = v; return true; }
        }

        var hits = new List<(string Name, T Value)>();
        foreach (var e in table)
        {
            if (e.Name.StartsWith(arg, StringComparison.Ordinal)) hits.Add(e);
        }

        if (hits.Count > 0 && hits.All(h => EqualityComparer<T>.Default.Equals(h.Value, hits[0].Value)))
        {
            value = hits[0].Value;
            return true;
        }

        value = default!;
        var kind = hits.Count == 0 ? "invalid" : "ambiguous";
        // `subject` replaces the "--option" text for arguments that are not an option's (GNU's backup
        // type: `for 'backup type'`, `for '$VERSION_CONTROL'`).
        error = $"{command}: {kind} argument '{arg}' for '{subject ?? "--" + option}'\n"
            + "Valid arguments are:\n"
            + validBlock + "\n"
            + $"Try '{command} --help' for more information.";
        return false;
    }
}
