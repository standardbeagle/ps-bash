using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The sequence of output-file suffixes <c>split</c> hands out (GNU coreutils 9.4, verified with
/// <c>split --verbose</c>). Pure state machine so the naming rule is unit-testable on its own.
///
/// <list type="bullet">
/// <item>Fixed length N (an explicit <c>-a N</c>, or <c>--numeric-suffixes=FROM</c>): a plain N-digit
/// counter over the whole alphabet; when it wraps, <see cref="Next"/> returns <c>null</c> and the caller
/// reports <c>output file suffixes exhausted</c>.</item>
/// <item>Auto (no <c>-a</c> / <c>-a 0</c>, no FROM): starts at length 2 and GROWS. The first counter
/// character never reaches the last symbol (<c>z</c> / <c>9</c>); when it would, the suffix becomes that
/// symbol repeated once more plus a counter one longer: <c>xaa..xyz</c>, <c>xzaaa..xzyzz</c>,
/// <c>xzzaaaa..</c>; numerically <c>00..89</c>, <c>9000..9899</c>, <c>990000..</c>. Block k holds
/// k marker symbols then a (k+2)-symbol counter.</item>
/// </list>
/// </summary>
internal sealed class SplitSuffixSequence
{
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string HexDigits = "0123456789abcdef";

    private readonly string _alphabet;
    private readonly bool _auto;
    private int _markers;          // leading 'last symbol' characters (auto mode only)
    private int[] _counter;        // the counter positions, most significant first
    private bool _first = true;
    private bool _exhausted;

    /// <param name="numeric">Digits instead of letters.</param>
    /// <param name="hex">Lowercase hexadecimal digits (<c>-x</c>); the auto marker symbol is <c>f</c> (oracle: <c>xef</c> is followed by <c>xf000</c>).</param>
    /// <param name="length">Suffix length (fixed mode) / starting counter length (auto mode, 2).</param>
    /// <param name="auto">Grow instead of running out.</param>
    /// <param name="from">Decimal start value (numeric mode), right-aligned into the suffix; null = 0.</param>
    public SplitSuffixSequence(bool numeric, int length, bool auto, string? from = null, bool hex = false)
    {
        _alphabet = hex ? HexDigits : numeric ? Digits : Letters;
        _auto = auto;
        _counter = new int[Math.Max(length, 1)];
        if ((numeric || hex) && from is { Length: > 0 })
        {
            // Right-align the FROM digits (the caller already checked from.Length <= length).
            for (int i = 0; i < from.Length; i++)
                _counter[_counter.Length - from.Length + i] = from[i] <= '9' ? from[i] - '0' : from[i] - 'a' + 10;
        }
    }

    /// <summary>The next suffix, or <c>null</c> once a fixed-length sequence has used every name.</summary>
    public string? Next()
    {
        if (_exhausted) return null;
        if (_first) { _first = false; return Current(); }

        int last = _alphabet.Length - 1;
        for (int i = _counter.Length - 1; i >= 0; i--)
        {
            _counter[i]++;
            if (_auto && i == 0 && _counter[0] == last)
            {
                // The first counter position would become the last symbol: that symbol is the
                // "extended" marker. One more marker, and a counter one longer than before.
                _markers++;
                _counter = new int[_counter.Length + 1];
                return Current();
            }
            if (_counter[i] <= last) return Current();
            _counter[i] = 0;
        }

        _exhausted = true;
        return null;
    }

    private string Current()
    {
        var sb = new StringBuilder(_markers + _counter.Length);
        for (int i = 0; i < _markers; i++) sb.Append(_alphabet[_alphabet.Length - 1]);
        foreach (int c in _counter) sb.Append(_alphabet[c]);
        return sb.ToString();
    }
}
