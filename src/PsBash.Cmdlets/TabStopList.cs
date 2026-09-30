namespace PsBash.Cmdlets;

/// <summary>
/// GNU <c>expand</c> / <c>unexpand</c> <c>-t</c> tab-stop specification (coreutils 9.4). Pure and
/// unit-tested: <c>N</c> is a uniform tab size, <c>N1,N2,...</c> (commas or blanks) an explicit
/// ascending list, and the LAST list element may be prefixed <c>/N</c> ("then a tab every N
/// columns") or <c>+N</c> ("then a tab every N columns relative to the last explicit stop").
/// Several <c>-t</c> options are concatenated as one list. Past the last stop of a plain list there
/// are no stops: <c>expand</c> turns a tab into a single space and <c>unexpand</c> converts nothing.
/// </summary>
internal sealed class TabStopList
{
    private readonly int[] _stops;
    private readonly int _uniform;    // >0: every _uniform columns
    private readonly int _extend;     // >0: '/N' after the last explicit stop
    private readonly int _increment;  // >0: '+N' after the last explicit stop

    private TabStopList(int[] stops, int uniform, int extend, int increment)
    {
        _stops = stops;
        _uniform = uniform;
        _extend = extend;
        _increment = increment;
    }

    /// <summary>The default: a tab every 8 columns.</summary>
    public static TabStopList Default { get; } = new(Array.Empty<int>(), 8, 0, 0);

    /// <summary>A uniform tab size (also the legacy single-number <c>-t N</c> / <c>-N</c>).</summary>
    public static TabStopList Uniform(int size) => new(Array.Empty<int>(), size, 0, 0);

    /// <summary>
    /// Parse every <c>-t</c> value (in argv order) as one concatenated spec. On failure
    /// <paramref name="error"/> carries the GNU message text (without the command prefix).
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> values, out TabStopList list, out string? error)
    {
        list = Default;
        error = null;
        var elements = new List<string>();
        foreach (var v in values)
        {
            foreach (var part in v.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                elements.Add(part);
        }

        if (elements.Count == 0)
        {
            error = "tab size cannot be 0";
            return false;
        }

        var stops = new List<int>();
        int extend = 0, increment = 0;
        int prev = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            string e = elements[i];
            char prefix = e[0];
            bool special = prefix is '/' or '+';
            string digits = special ? e.Substring(1) : e;
            if (special && i != elements.Count - 1)
            {
                error = $"'{prefix}' specifier only allowed with the last value";
                return false;
            }

            if (digits.Length == 0)
            {
                error = $"tab size contains invalid character(s): '{e}'";
                return false;
            }
            long n = 0;
            foreach (char c in digits)
            {
                if (c < '0' || c > '9')
                {
                    error = $"tab size contains invalid character(s): '{e}'";
                    return false;
                }
                n = Math.Min(n * 10 + (c - '0'), int.MaxValue);
            }
            if (n == 0)
            {
                error = "tab size cannot be 0";
                return false;
            }

            if (special)
            {
                if (prefix == '/') extend = (int)n; else increment = (int)n;
                continue;
            }
            if (n <= prev)
            {
                error = "tab sizes must be ascending";
                return false;
            }
            prev = (int)n;
            stops.Add((int)n);
        }

        // A lone plain number is a uniform tab size, not a one-stop list.
        if (stops.Count == 1 && extend == 0 && increment == 0 && elements.Count == 1)
        {
            list = Uniform(stops[0]);
            return true;
        }
        list = new TabStopList(stops.ToArray(), 0, extend, increment);
        return true;
    }

    /// <summary>The next tab stop strictly after <paramref name="col"/>, or -1 when there is none.</summary>
    public int NextStop(int col)
    {
        if (_uniform > 0) return col - col % _uniform + _uniform;
        foreach (int s in _stops)
        {
            if (s > col) return s;
        }
        if (_extend > 0) return (col / _extend + 1) * _extend;
        if (_increment > 0)
        {
            int last = _stops.Length > 0 ? _stops[^1] : 0;
            return last + ((col - last) / _increment + 1) * _increment;
        }
        return -1;
    }

    /// <summary>Columns a tab occupies at <paramref name="col"/> (expand): to the next stop, else one space.</summary>
    public int SpacesAt(int col)
    {
        int next = NextStop(col);
        return next < 0 ? 1 : next - col;
    }
}
