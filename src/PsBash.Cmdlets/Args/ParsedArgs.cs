namespace PsBash.Cmdlets.Args;

public enum ArgTokKind
{
    /// <summary>A non-option word (file name, pattern, lone <c>-</c>, anything after <c>--</c>).</summary>
    Operand,

    /// <summary>A recognised option. One token per option, so a bundle <c>-abc</c> yields three.</summary>
    Option,

    /// <summary>The <c>--</c> marker itself. Everything after it is an <see cref="Operand"/>.</summary>
    DoubleDash,
}

/// <summary>
/// One parsed argv element (or, for a bundle, one option letter of it), in ORIGINAL order.
/// <paramref name="Raw"/> is the argv element it came from; <paramref name="Value"/> is the
/// consumed argument of a value option (attached or taken from the next element).
/// </summary>
public readonly record struct ArgToken(
    ArgTokKind Kind, int ArgIndex, string? OptId, string? Value, string Raw, bool AfterDoubleDash);

public enum ArgErrorKind
{
    /// <summary>Not an option this command has ever heard of (<c>invalid option -- 'x'</c>).</summary>
    Unrecognized,

    /// <summary>A real GNU option ps-bash refuses to implement — refused loudly, never silently.</summary>
    ValidButUnsupported,

    /// <summary>A value option with nothing to take as its value.</summary>
    MissingValue,

    /// <summary>A long-option prefix that matches several options.</summary>
    Ambiguous,

    /// <summary><c>--append=x</c>: an attached value on an option that takes none.</summary>
    UnexpectedValue,

    /// <summary>
    /// A digit where an option letter was expected (<c>head -q5</c>, <c>tail -12x</c>), for the tools
    /// whose getopt string lists the digits only to reject them. <see cref="ArgError.Detail"/> carries
    /// the tool's wording ("invalid trailing option" for head, "option used in invalid context" for tail).
    /// </summary>
    MisplacedDigit,
}

/// <summary>
/// The first scan error. <paramref name="Token"/> is the offending argv element (or, for
/// <see cref="ArgErrorKind.ValidButUnsupported"/>, the canonical option name);
/// <paramref name="BadChar"/> is the offending short letter when the error is about a short option.
/// The exit status is decided by <see cref="ParsedArgs.ErrorExitCode"/>: ps-bash's own refusal of a
/// real GNU option (<see cref="ArgErrorKind.ValidButUnsupported"/>) is always
/// <see cref="UnsupportedExitCode"/>; every other usage error takes the command's
/// <see cref="OptSpecSet.UsageExitCode"/> (GNU: 1 for coreutils file tools, 2 for ls/grep/diff).
/// </summary>
public readonly record struct ArgError(
    ArgErrorKind Kind, string Token, char BadChar, int ArgIndex, string? Detail = null)
{
    /// <summary>Exit status for a valid-but-unsupported option: ps-bash policy (deliberately not GNU, which would honour the flag).</summary>
    public const int UnsupportedExitCode = 2;

    /// <summary>The GNU-worded message for <paramref name="command"/> (no trailing newline).</summary>
    public string Message(string command) => Kind switch
    {
        ArgErrorKind.ValidButUnsupported =>
            $"{command}: option '{Token}' is recognized but not supported by ps-bash",
        ArgErrorKind.Unrecognized when BadChar == '\0' =>
            $"{command}: unrecognized option '{Token}'",
        ArgErrorKind.Unrecognized =>
            $"{command}: invalid option -- '{BadChar}'",
        ArgErrorKind.MissingValue when BadChar == '\0' =>
            $"{command}: option '{Token}' requires an argument",
        ArgErrorKind.MissingValue =>
            $"{command}: option requires an argument -- '{BadChar}'",
        ArgErrorKind.Ambiguous =>
            $"{command}: option '{Token}' is ambiguous; possibilities:{Detail}",
        ArgErrorKind.UnexpectedValue =>
            $"{command}: option '{Token}' doesn't allow an argument",
        ArgErrorKind.MisplacedDigit =>
            $"{command}: {Detail} -- {BadChar}",
        _ => $"{command}: invalid option",
    };
}

/// <summary>
/// Result of <see cref="ArgParser.Parse"/>: every token in original order, plus the first error.
/// Scanning stops at the first error, so on error <see cref="Tokens"/> holds only what preceded it.
/// </summary>
public sealed class ParsedArgs
{
    private readonly List<ArgToken> _tokens;

    internal ParsedArgs(List<ArgToken> tokens, ArgError? error, int usageExitCode)
    {
        _tokens = tokens;
        Error = error;
        _usageExitCode = usageExitCode;
    }

    private readonly int _usageExitCode;

    /// <summary>
    /// Exit status for <see cref="Error"/>: <see cref="ArgError.UnsupportedExitCode"/> for a
    /// valid-but-unsupported option, otherwise the spec's <see cref="OptSpecSet.UsageExitCode"/>.
    /// </summary>
    public int ErrorExitCode =>
        Error is { Kind: ArgErrorKind.ValidButUnsupported } ? ArgError.UnsupportedExitCode : _usageExitCode;

    public IReadOnlyList<ArgToken> Tokens => _tokens;

    public ArgError? Error { get; }

    public bool HasError => Error is not null;

    /// <summary>True when the option <paramref name="id"/> appeared at least once.</summary>
    public bool Has(string id)
    {
        foreach (var t in _tokens)
        {
            if (t.Kind == ArgTokKind.Option && t.OptId == id) return true;
        }
        return false;
    }

    /// <summary>The last occurrence of option <paramref name="id"/> (last one wins), or null.</summary>
    public ArgToken? Last(string id)
    {
        for (int i = _tokens.Count - 1; i >= 0; i--)
        {
            if (_tokens[i].Kind == ArgTokKind.Option && _tokens[i].OptId == id) return _tokens[i];
        }
        return null;
    }

    /// <summary>Every occurrence of option <paramref name="id"/>, in order.</summary>
    public IEnumerable<ArgToken> All(string id)
    {
        foreach (var t in _tokens)
        {
            if (t.Kind == ArgTokKind.Option && t.OptId == id) yield return t;
        }
    }

    /// <summary>Operands in original order (options and the <c>--</c> marker removed).</summary>
    public List<string> Operands()
    {
        var list = new List<string>();
        foreach (var t in _tokens)
        {
            if (t.Kind == ArgTokKind.Operand) list.Add(t.Raw);
        }
        return list;
    }
}
