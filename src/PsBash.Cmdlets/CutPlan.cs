using System.Text;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>What <c>cut</c> selects from each line.</summary>
internal enum CutMode { Bytes, Chars, Fields }

/// <summary>
/// The pure per-line engine of <c>cut</c>, shared by <see cref="InvokeBashCutCommand"/> and the
/// fused line-stream core (<c>CutStage</c>) so the two lanes cannot drift.
///
/// GNU semantics (coreutils 9.4, oracle-checked): the list is SORTED and overlapping ranges are
/// MERGED (adjacent ranges stay separate), so output is in input order and each position is written
/// once (<c>-f3,1</c> is fields 1 then 3; <c>-f1,1</c> prints field 1 once). In field mode a line
/// that contains no delimiter is printed WHOLE unless <c>-s</c> (then it is dropped), and it is
/// printed whole even under <c>--complement</c>. <c>--output-delimiter</c> joins every selected
/// field; in byte/char mode it is written between separate (non-merged) ranges, only when the next
/// range actually yields a character. Positions past the end of a line are dropped.
///
/// <para>Bytes vs characters: <c>-b</c> counts UTF-8 bytes. GNU 9.4 also counts bytes for
/// <c>-c</c>; ps-bash deliberately keeps <c>-c</c> as characters (Unicode scalars), which is what
/// callers mean and only differs for non-ASCII text. A <c>-b</c> slice that cuts a multi-byte
/// character has no .NET string representation; it is decoded as Latin-1 (same known gap as
/// <c>printf '\xe9'</c>, see runtime-functions.md "Raw bytes").</para>
/// </summary>
internal sealed class CutPlan
{
    public CutMode Mode { get; }
    public string Delimiter { get; }
    public bool Complement { get; }
    public bool OnlyDelimited { get; }
    public string? OutputDelimiter { get; }

    private readonly (int Lo, int Hi)[] _ranges; // sorted, overlap-merged, 1-based inclusive; Hi == int.MaxValue = open
    private readonly List<(int Lo, int Hi)> _groups = new();

    public CutPlan(CutMode mode, List<(int Lo, int Hi)> ranges, string delimiter, bool complement,
                   bool onlyDelimited, string? outputDelimiter)
    {
        Mode = mode;
        Delimiter = delimiter;
        Complement = complement;
        OnlyDelimited = onlyDelimited;
        OutputDelimiter = outputDelimiter;
        _ranges = MergeRanges(ranges);
    }

    internal static (int Lo, int Hi)[] MergeRanges(List<(int Lo, int Hi)> ranges)
    {
        var sorted = new List<(int Lo, int Hi)>(ranges);
        sorted.Sort((a, b) => a.Lo != b.Lo ? a.Lo.CompareTo(b.Lo) : a.Hi.CompareTo(b.Hi));
        var merged = new List<(int Lo, int Hi)>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.Lo <= merged[^1].Hi)
                merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, r.Hi));
            else
                merged.Add(r);
        }
        return merged.ToArray();
    }

    /// <summary>The cut text for one line, or null when the line is suppressed (<c>-s</c>, no delimiter).</summary>
    public string? Apply(string line)
    {
        return Mode switch
        {
            CutMode.Fields => ApplyFields(line),
            CutMode.Chars => ApplyUnits(line, bytes: false),
            _ => ApplyUnits(line, bytes: true),
        };
    }

    // Selected position groups for a line of n units (1-based inclusive, clamped to n).
    private void BuildGroups(int n)
    {
        _groups.Clear();
        if (!Complement)
        {
            foreach (var (lo, hi) in _ranges)
            {
                if (lo > n) break;
                _groups.Add((lo, Math.Min(hi, n)));
            }
            return;
        }
        long cur = 1;
        foreach (var (lo, hi) in _ranges)
        {
            if (cur > n) break;
            if (lo > cur) _groups.Add(((int)cur, (int)Math.Min(lo - 1L, n)));
            cur = Math.Max(cur, hi + 1L);
        }
        if (cur <= n) _groups.Add(((int)cur, n));
    }

    private string? ApplyFields(string line)
    {
        if (line.IndexOf(Delimiter, StringComparison.Ordinal) < 0)
            return OnlyDelimited ? null : line;

        string[] fields = line.Split(Delimiter, StringSplitOptions.None);
        BuildGroups(fields.Length);
        string sep = OutputDelimiter ?? Delimiter;
        var sb = new StringBuilder();
        bool first = true;
        foreach (var (lo, hi) in _groups)
        {
            for (int p = lo; p <= hi; p++)
            {
                if (!first) sb.Append(sep);
                sb.Append(fields[p - 1]);
                first = false;
            }
        }
        return sb.ToString();
    }

    private string ApplyUnits(string line, bool bytes)
    {
        if (IsSimple(line))
        {
            BuildGroups(line.Length);
            return JoinGroups(_groups, (lo, len) => line.Substring(lo, len));
        }

        if (bytes)
        {
            byte[] raw = RawBytes.GetBytes(line);
            BuildGroups(raw.Length);
            // One contiguous byte stream per group; decode the whole result so a group that keeps
            // whole characters round-trips exactly (a split character leaves escaped-byte markers, written back as the original bytes).
            var outBytes = new List<byte>(raw.Length);
            byte[]? sepBytes = OutputDelimiter is null ? null : RawBytes.GetBytes(OutputDelimiter);
            bool first = true;
            foreach (var (lo, hi) in _groups)
            {
                if (!first && sepBytes is not null) outBytes.AddRange(sepBytes);
                for (int p = lo; p <= hi; p++) outBytes.Add(raw[p - 1]);
                first = false;
            }
            var arr = outBytes.ToArray();
            return RawBytes.GetString(arr);
        }

        // Characters: index by Unicode scalar so a surrogate pair is never split.
        var scalars = new List<string>(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                scalars.Add(line.Substring(i, 2));
                i++;
            }
            else
            {
                scalars.Add(line[i].ToString());
            }
        }
        BuildGroups(scalars.Count);
        return JoinGroups(_groups, (lo, len) => string.Concat(scalars.GetRange(lo, len)));
    }

    // ASCII-only (and surrogate-free) text: a char index IS a byte index and a scalar index.
    private static bool IsSimple(string line)
    {
        foreach (char c in line)
        {
            if (c >= 0x80) return false;
        }
        return true;
    }

    private string JoinGroups(List<(int Lo, int Hi)> groups, Func<int, int, string> slice)
    {
        if (groups.Count == 0) return string.Empty;
        if (groups.Count == 1 && OutputDelimiter is null)
            return slice(groups[0].Lo - 1, groups[0].Hi - groups[0].Lo + 1);
        var sb = new StringBuilder();
        bool first = true;
        foreach (var (lo, hi) in groups)
        {
            if (!first && OutputDelimiter is not null) sb.Append(OutputDelimiter);
            sb.Append(slice(lo - 1, hi - lo + 1));
            first = false;
        }
        return sb.ToString();
    }
}
