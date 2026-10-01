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

    /// <summary>Every declared spec (test seam: the shared-id ambiguity guard walks these).</summary>
    internal IReadOnlyList<OptSpec> Specs { get; }

    /// <summary>
    /// Exit status of a usage error (unknown option, missing argument, ambiguous or malformed
    /// long option). GNU coreutils use 1 (EXIT_FAILURE) for tee/cp/mv/rm/mkdir/rmdir/ln/touch
    /// and 2 for ls/grep/diff, so this is per command. Does not apply to valid-but-unsupported
    /// options, which are always <see cref="ArgError.UnsupportedExitCode"/>.
    /// </summary>
    public int UsageExitCode { get; }

    /// <summary>
    /// When set, a digit in option position (<c>-q5</c>, <c>-12x</c>) is an
    /// <see cref="ArgErrorKind.MisplacedDigit"/> error worded "{command}: {this} -- {digit}", as GNU
    /// head ("invalid trailing option") and tail ("option used in invalid context") do because
    /// their getopt strings list the digits only to reject them.
    /// </summary>
    public string? DigitOptionWording { get; }

    /// <summary>Id produced by <c>-NUM</c>, or null when the shorthand is off.</summary>
    public string? NumericShorthandId { get; }

    /// <summary>
    /// When set, a run of digits ANYWHERE in a short bundle (<c>-5</c>, <c>-1n</c>, <c>-n12</c>,
    /// <c>-A1 -12</c>) is a <see cref="OptKind.Value"/> option with this Id whose value is the
    /// contiguous digit run (grep's <c>-NUM</c> context shorthand: each argv element's digits form
    /// ONE number; a later element starts a new one). Takes precedence over
    /// <see cref="NumericShorthandId"/> and over a digit that is also a declared short option.
    /// </summary>
    public string? BundleDigitsId { get; }

    /// <summary>Id of the implicit GNU <c>--help</c> option (see <c>gnuInfoOptions</c>).</summary>
    public const string HelpId = "help";

    /// <summary>Id of the implicit GNU <c>--version</c> option (see <c>gnuInfoOptions</c>).</summary>
    public const string VersionId = "version";

    /// <param name="gnuInfoOptions">
    /// Every GNU tool has <c>--help</c> and <c>--version</c>, and they take part in abbreviation:
    /// <c>--ver</c> is AMBIGUOUS (<c>--verbose</c> / <c>--version</c>) and <c>--vers</c> is
    /// <c>--version</c>. When true they are registered as flags with ids <see cref="HelpId"/> /
    /// <see cref="VersionId"/>; the cmdlet acts on them via
    /// <c>FileSystemHelpers.TryHandleInfoOptions</c>.
    /// </param>
    /// <param name="usageExitCode">Exit status of a usage error; see <see cref="UsageExitCode"/> (default 1).</param>
    public OptSpecSet(
        IEnumerable<OptSpec> specs,
        IEnumerable<string>? validButUnsupported = null,
        bool allowAbbrev = false,
        string? numericShorthandId = null,
        bool gnuInfoOptions = false,
        int usageExitCode = 1,
        IEnumerable<string>? longOptionOrder = null,
        string? digitOptionWording = null,
        string? bundleDigitsId = null)
    {
        BundleDigitsId = bundleDigitsId;
        UsageExitCode = usageExitCode;
        DigitOptionWording = digitOptionWording;
        var declared = new List<string>();
        var specList = new List<OptSpec>();
        Specs = specList;
        foreach (var s in specs)
        {
            specList.Add(s);
            if (s.Short != '\0') _byShort[s.Short] = s;
            if (s.Long is not null)
            {
                if (!_byLong.ContainsKey(s.Long)) declared.Add(s.Long);
                _byLong[s.Long] = s;
            }
        }

        var unsupportedList = new List<string>(validButUnsupported ?? Array.Empty<string>());
        _unsupported = new HashSet<string>(unsupportedList, StringComparer.Ordinal);

        foreach (var u in unsupportedList)
        {
            if (u.StartsWith("--", StringComparison.Ordinal) && u.Length > 2) declared.Add(u.Substring(2));
        }

        if (gnuInfoOptions)
        {
            if (_byLong.TryAdd(HelpId, new OptSpec(HelpId, '\0', "help"))) declared.Add("help");
            if (_byLong.TryAdd(VersionId, new OptSpec(VersionId, '\0', "version"))) declared.Add("version");
        }

        AllowAbbrev = allowAbbrev;
        NumericShorthandId = numericShorthandId;

        // Abbreviation candidates keep GNU's long_options[] order (getopt_long reports them in table
        // order): the explicit longOptionOrder first, then anything it did not name in declaration order.
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (longOptionOrder is not null)
        {
            foreach (var n in longOptionOrder)
            {
                if (declared.Contains(n) && seen.Add(n)) ordered.Add(n);
            }
        }
        foreach (var n in declared)
        {
            if (seen.Add(n)) ordered.Add(n);
        }
        _allLongNames = ordered.ToArray();
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
