namespace PsBash.Cmdlets.Args;

/// <summary>How an option takes its argument. Deliberately NOT a [Flags] enum.</summary>
public enum OptKind
{
    /// <summary>Takes no argument (<c>-a</c>, <c>--append</c>).</summary>
    Flag,

    /// <summary>
    /// Requires an argument: <c>-n5</c> / <c>-n 5</c> (a value flag ends its short bundle) and
    /// <c>--lines=5</c> / <c>--lines 5</c>. Like getopt, the next argv element is consumed even
    /// when it starts with a dash.
    /// </summary>
    Value,

    /// <summary>
    /// Argument is optional and only ever attached: <c>-cVALUE</c> / <c>--color=VALUE</c>.
    /// The next argv element is never consumed.
    /// </summary>
    OptionalValue,
}

/// <summary>
/// One recognised option. <paramref name="Id"/> is what the command switches on; several specs may
/// share an Id (<c>-r</c> and <c>-R</c> are both "recursive"). <paramref name="Short"/> is
/// <c>'\0'</c> for none, <paramref name="Long"/> is the name WITHOUT the leading <c>--</c> (or null).
/// </summary>
public readonly record struct OptSpec(string Id, char Short, string? Long, OptKind Kind = OptKind.Flag);

/// <summary>
/// The immutable description of a command's option surface, built ONCE (static readonly) and shared
/// by every <see cref="ArgParser.Parse"/> call.
/// <list type="bullet">
/// <item><b>Valid-but-unsupported</b> names (GNU flags ps-bash refuses): written exactly as the user
/// types them (<c>"-i"</c>, <c>"--interactive"</c>). Parsing one is an error
/// (<see cref="ArgErrorKind.ValidButUnsupported"/>), never an operand.</item>
/// <item><b>allowAbbrev</b>: like getopt_long, a unique prefix of a long option selects it
/// (<c>--app</c> = <c>--append</c>); a prefix matching several is <see cref="ArgErrorKind.Ambiguous"/>.
/// Unsupported long names take part, as in GNU.</item>
/// <item><b>numericShorthandId</b>: when set, <c>-5</c> / <c>-25</c> (a dash then ONLY digits)
/// is an <see cref="OptKind.Value"/> option with that Id and the digits as value (head/tail).</item>
/// </list>
/// </summary>
public sealed class OptSpecSet
{
    private readonly Dictionary<char, OptSpec> _byShort = new();
    private readonly Dictionary<string, OptSpec> _byLong = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsupported;
    private readonly string[] _allLongNames; // supported + unsupported, for abbreviation

    public bool AllowAbbrev { get; }

    /// <summary>Id produced by <c>-NUM</c>, or null when the shorthand is off.</summary>
    public string? NumericShorthandId { get; }

    public OptSpecSet(
        IEnumerable<OptSpec> specs,
        IEnumerable<string>? validButUnsupported = null,
        bool allowAbbrev = false,
        string? numericShorthandId = null)
    {
        foreach (var s in specs)
        {
            if (s.Short != '\0') _byShort[s.Short] = s;
            if (s.Long is not null) _byLong[s.Long] = s;
        }

        _unsupported = new HashSet<string>(validButUnsupported ?? Array.Empty<string>(), StringComparer.Ordinal);
        AllowAbbrev = allowAbbrev;
        NumericShorthandId = numericShorthandId;

        var names = new HashSet<string>(_byLong.Keys, StringComparer.Ordinal);
        foreach (var u in _unsupported)
        {
            if (u.StartsWith("--", StringComparison.Ordinal) && u.Length > 2) names.Add(u.Substring(2));
        }
        _allLongNames = names.ToArray();
        Array.Sort(_allLongNames, StringComparer.Ordinal);
    }

    internal bool TryGetShort(char c, out OptSpec spec) => _byShort.TryGetValue(c, out spec);

    internal bool TryGetLong(string name, out OptSpec spec) => _byLong.TryGetValue(name, out spec);

    /// <summary>True when the exact token (<c>-i</c>, <c>--interactive</c>) is a refused GNU option.</summary>
    internal bool IsUnsupported(string token) => _unsupported.Contains(token);

    /// <summary>Every long name (without dashes) that starts with <paramref name="prefix"/>.</summary>
    internal List<string> LongNamesWithPrefix(string prefix)
    {
        var hits = new List<string>();
        foreach (var n in _allLongNames)
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal)) hits.Add(n);
        }
        return hits;
    }
}
