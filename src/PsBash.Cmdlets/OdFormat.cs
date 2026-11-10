using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>One <c>-t</c> output type of <c>od</c>: its kind letter and unit size in bytes.</summary>
internal readonly record struct OdType(char Kind, int Size)
{
    /// <summary>Printed width of one unit, including the separating space (GNU coreutils <c>od.c</c> tables).</summary>
    public int FieldWidth => Kind switch
    {
        'c' or 'a' => 4,
        'x' => Size switch { 1 => 3, 2 => 5, 4 => 9, _ => 17 },
        'o' => Size switch { 1 => 4, 2 => 7, 4 => 12, _ => 23 },
        'd' => Size switch { 1 => 5, 2 => 7, 4 => 12, _ => 21 },
        _ => Size switch { 1 => 4, 2 => 6, 4 => 11, _ => 21 },   // u
    };
}

/// <summary>What an <c>od</c> command line asks for. Pure data: built by <see cref="OdFormat.Plan"/>.</summary>
internal sealed class OdPlan
{
    public char AddressRadix = 'o';            // o d x n
    public List<OdType> Types = new();
    public long Skip;
    public long Limit = -1;
    public int Width = 16;
    public bool ShowDuplicates;
    public List<string> Operands = new();
    public string? Error;
    public bool Help;
    public bool Version;
}

/// <summary>
/// The pure half of <c>od</c> (GNU coreutils 9.4 behaviour): argv to <see cref="OdPlan"/>, bytes to lines. No I/O.
/// </summary>
internal static class OdFormat
{
    private static readonly string[] AsciiNames =
    {
        "nul", "soh", "stx", "etx", "eot", "enq", "ack", "bel", "bs", "ht", "nl", "vt", "ff", "cr", "so", "si",
        "dle", "dc1", "dc2", "dc3", "dc4", "nak", "syn", "etb", "can", "em", "sub", "esc", "fs", "gs", "rs", "us", "sp",
    };

    public static OdPlan Plan(IReadOnlyList<string> argv)
    {
        var plan = new OdPlan();
        bool operandsOnly = false;
        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];
            if (operandsOnly || a == "-" || !a.StartsWith('-')) { plan.Operands.Add(a); continue; }
            if (a == "--") { operandsOnly = true; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                string name = a, value = "";
                bool hasValue = false;
                int eq = a.IndexOf('=');
                if (eq >= 0) { name = a[..eq]; value = a[(eq + 1)..]; hasValue = true; }
                string? Next() => hasValue ? value : i + 1 < argv.Count ? argv[++i] : null;
                switch (name)
                {
                    case "--help": plan.Help = true; break;
                    case "--version": plan.Version = true; break;
                    case "--output-duplicates": plan.ShowDuplicates = true; break;
                    case "--address-radix": if (!SetRadix(plan, Next())) return plan; break;
                    case "--format": if (!AddTypes(plan, Next())) return plan; break;
                    case "--skip-bytes": if (!SetCount(plan, Next(), skip: true)) return plan; break;
                    case "--read-bytes": if (!SetCount(plan, Next(), skip: false)) return plan; break;
                    case "--width": if (!SetWidth(plan, hasValue ? value : "32")) return plan; break;
                    default: plan.Error = $"od: unrecognized option '{a}'"; return plan;
                }
                continue;
            }
            for (int k = 1; k < a.Length; k++)
            {
                char f = a[k];
                // The rest of the word, else the next word, is the value of -A -t -j -N; -w takes only an attached one.
                string? Value()
                {
                    string? v = k + 1 < a.Length ? a[(k + 1)..] : i + 1 < argv.Count ? argv[++i] : null;
                    k = a.Length;
                    return v;
                }
                switch (f)
                {
                    case 'A': if (!SetRadix(plan, Value())) return plan; break;
                    case 't': if (!AddTypes(plan, Value())) return plan; break;
                    case 'j': if (!SetCount(plan, Value(), skip: true)) return plan; break;
                    case 'N': if (!SetCount(plan, Value(), skip: false)) return plan; break;
                    case 'w':
                        string w = k + 1 < a.Length ? a[(k + 1)..] : "32";
                        k = a.Length;
                        if (!SetWidth(plan, w)) return plan;
                        break;
                    case 'v': plan.ShowDuplicates = true; break;
                    case 'a': plan.Types.Add(new OdType('a', 1)); break;
                    case 'b': plan.Types.Add(new OdType('o', 1)); break;
                    case 'c': plan.Types.Add(new OdType('c', 1)); break;
                    case 'd': plan.Types.Add(new OdType('u', 2)); break;
                    case 'o': plan.Types.Add(new OdType('o', 2)); break;
                    case 's': plan.Types.Add(new OdType('d', 2)); break;
                    case 'x': plan.Types.Add(new OdType('x', 2)); break;
                    case 'i': plan.Types.Add(new OdType('d', 4)); break;
                    case 'l': plan.Types.Add(new OdType('d', 8)); break;
                    default: plan.Error = $"od: invalid option -- '{f}'"; return plan;
                }
            }
        }
        if (plan.Types.Count == 0) plan.Types.Add(new OdType('o', 2));
        return plan;
    }

    private static bool SetRadix(OdPlan plan, string? v)
    {
        if (v is null) { plan.Error = "od: option requires an argument -- 'A'"; return false; }
        if (v.Length == 1 && v[0] is 'o' or 'd' or 'x' or 'n') { plan.AddressRadix = v[0]; return true; }
        plan.Error = $"od: invalid output address radix '{(v.Length > 0 ? v[0] : ' ')}'; it must be one character from [doxn]";
        return false;
    }

    private static bool SetCount(OdPlan plan, string? v, bool skip)
    {
        if (v is not null && TryParseCount(v, out long n)) { if (skip) plan.Skip = n; else plan.Limit = n; return true; }
        plan.Error = v is null ? $"od: option requires an argument -- '{(skip ? 'j' : 'N')}'" : $"od: invalid {(skip ? "-j" : "-N")} argument '{v}'";
        return false;
    }

    private static bool SetWidth(OdPlan plan, string v)
    {
        if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int w) && w > 0) { plan.Width = w; return true; }
        plan.Error = $"od: invalid -w argument '{v}'";
        return false;
    }

    /// <summary>Decimal, <c>0x</c> hex or leading-<c>0</c> octal, with an optional <c>b</c> (512) / <c>k</c> / <c>m</c> multiplier.</summary>
    private static bool TryParseCount(string v, out long n)
    {
        n = 0;
        long mult = 1;
        if (v.Length > 1 && !v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            char last = v[^1];
            if (last is 'b') { mult = 512; v = v[..^1]; }
            else if (last is 'k' or 'K') { mult = 1024; v = v[..^1]; }
            else if (last is 'm' or 'M') { mult = 1024 * 1024; v = v[..^1]; }
        }
        bool ok = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(v.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n)
            : v.Length > 1 && v[0] == '0'
                ? TryParseOctal(v, out n)
                : long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        n *= mult;
        return ok;
    }

    private static bool TryParseOctal(string v, out long n)
    {
        n = 0;
        foreach (char ch in v)
        {
            if (ch is < '0' or > '7') return false;
            n = n * 8 + (ch - '0');
        }
        return true;
    }

    /// <summary><c>-t</c> value: one or more of <c>a c d[SIZE] o[SIZE] u[SIZE] x[SIZE]</c> (SIZE = 1 2 4 8 or C S I L); a trailing <c>z</c> is accepted and ignored.</summary>
    private static bool AddTypes(OdPlan plan, string? spec)
    {
        if (string.IsNullOrEmpty(spec)) { plan.Error = "od: option requires an argument -- 't'"; return false; }
        int i = 0;
        while (i < spec.Length)
        {
            char kind = spec[i++];
            if (kind is 'a' or 'c') { plan.Types.Add(new OdType(kind, 1)); }
            else if (kind is 'd' or 'o' or 'u' or 'x')
            {
                int size = 4;   // sizeof(int), od's default for a bare d / o / u / x
                if (i < spec.Length && char.IsAsciiDigit(spec[i]))
                {
                    int start = i;
                    while (i < spec.Length && char.IsAsciiDigit(spec[i])) i++;
                    size = int.Parse(spec.AsSpan(start, i - start), CultureInfo.InvariantCulture);
                }
                else if (i < spec.Length && spec[i] is 'C' or 'S' or 'I' or 'L')
                    size = spec[i++] switch { 'C' => 1, 'S' => 2, 'I' => 4, _ => 8 };
                if (size is not (1 or 2 or 4 or 8)) { plan.Error = $"od: invalid type string '{spec}';\nthis system doesn't provide a {size}-byte integral type"; return false; }
                plan.Types.Add(new OdType(kind, size));
            }
            else { plan.Error = $"od: invalid character '{kind}' in type string '{spec}'"; return false; }
            if (i < spec.Length && spec[i] == 'z') i++;
        }
        return true;
    }

    /// <summary>Every output line for <paramref name="data"/> (already skipped / limited by the caller).</summary>
    public static IEnumerable<string> Lines(OdPlan plan, byte[] data, long startOffset)
    {
        // Columns of every type line up: a block of `block` bytes (the largest unit) is as wide as the widest
        // type needs for it, and each type spreads that width over its own units.
        int block = 1;
        foreach (var t in plan.Types) block = Math.Max(block, t.Size);
        int blockWidth = 0;
        foreach (var t in plan.Types) blockWidth = Math.Max(blockWidth, t.FieldWidth * (block / t.Size));
        int width = Math.Max(block, plan.Width - plan.Width % block);
        var sb = new StringBuilder();
        int prevStart = -1;
        bool starred = false;
        for (int pos = 0; pos < data.Length; pos += width)
        {
            int len = Math.Min(width, data.Length - pos);
            if (!plan.ShowDuplicates && len == width && prevStart >= 0
                && data.AsSpan(pos, len).SequenceEqual(data.AsSpan(prevStart, len)))
            {
                if (!starred) { starred = true; yield return "*"; }
                continue;
            }
            starred = false;
            prevStart = pos;
            for (int ti = 0; ti < plan.Types.Count; ti++)
            {
                var t = plan.Types[ti];
                sb.Clear();
                sb.Append(ti == 0 ? Address(plan.AddressRadix, startOffset + pos) : AddressPad(plan.AddressRadix));
                for (int k = 0; k < len; k += t.Size)
                    sb.Append(Unit(t, data, pos + k, Math.Min(t.Size, len - k)).PadLeft(blockWidth / (block / t.Size)));
                yield return sb.ToString();
            }
        }
        if (plan.AddressRadix != 'n') yield return Address(plan.AddressRadix, startOffset + data.Length);
    }

    private static string Address(char radix, long offset) => radix switch
    {
        'o' => Convert.ToString(offset, 8).PadLeft(7, '0'),
        'd' => offset.ToString("D7", CultureInfo.InvariantCulture),
        'x' => offset.ToString("x6", CultureInfo.InvariantCulture),
        _ => "",
    };

    private static string AddressPad(char radix) => radix switch { 'o' or 'd' => "       ", 'x' => "      ", _ => "" };

    private static string Unit(OdType t, byte[] data, int at, int available)
    {
        if (t.Kind == 'c') return CharName(data[at]);
        if (t.Kind == 'a') { int c = data[at] & 0x7f; return c <= 32 ? AsciiNames[c] : c == 127 ? "del" : ((char)c).ToString(); }
        ulong v = 0;   // little-endian; a short final unit is zero-padded
        for (int b = 0; b < available; b++) v |= (ulong)data[at + b] << (8 * b);
        switch (t.Kind)
        {
            case 'x': return v.ToString("x" + (t.Size * 2), CultureInfo.InvariantCulture);
            case 'o': return Convert.ToString(unchecked((long)v), 8).PadLeft(t.Size switch { 1 => 3, 2 => 6, 4 => 11, _ => 22 }, '0');
            case 'u': return v.ToString(CultureInfo.InvariantCulture);
            default:
                long s = t.Size switch { 1 => (sbyte)v, 2 => (short)v, 4 => (int)v, _ => unchecked((long)v) };
                return s.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string CharName(byte b) => b switch
    {
        0 => "\\0", 7 => "\\a", 8 => "\\b", 9 => "\\t", 10 => "\\n", 11 => "\\v", 12 => "\\f", 13 => "\\r",
        >= 0x20 and < 0x7f => ((char)b).ToString(),
        _ => Convert.ToString(b, 8).PadLeft(3, '0'),
    };
}
