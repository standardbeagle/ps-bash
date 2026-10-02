namespace PsBash.Cmdlets;

/// <summary>How <c>split -n</c> divides the input: <c>N</c> / <c>K/N</c> (bytes), <c>l/N</c> (lines), <c>r/N</c> (round robin).</summary>
internal enum SplitChunkKind { Bytes, Lines, RoundRobin }

/// <summary><c>-n</c> argument after parsing. <see cref="K"/> is 0 for "write N files", else "write chunk K to stdout".</summary>
internal readonly record struct SplitChunkSpec(SplitChunkKind Kind, int K, int N);

/// <summary>
/// Pure engine of <c>split -n CHUNKS</c> (GNU coreutils 9.4, oracle-checked against random inputs).
///
/// <para>Byte chunks: the input is cut into N pieces of <c>size / N</c> bytes and the first
/// <c>size % N</c> pieces get one extra byte (10 bytes in 3 chunks = 4,3,3).</para>
/// <para><c>l/N</c>: the same partitions, but a LINE belongs, whole, to the partition holding its first
/// byte, so chunks can be larger, smaller or empty (every one of the N files is still created).</para>
/// <para><c>r/N</c>: line j (0-based) goes to chunk <c>j % N</c>.</para>
/// </summary>
internal static class SplitChunks
{
    /// <summary>Parses the <c>-n</c> value. On failure <paramref name="error"/> is GNU's wording (without the <c>split: </c> prefix).</summary>
    public static bool TryParse(string text, out SplitChunkSpec spec, out string error)
    {
        spec = default;
        error = string.Empty;
        var kind = SplitChunkKind.Bytes;
        string rest = text;
        if (rest.StartsWith("l/", StringComparison.Ordinal)) { kind = SplitChunkKind.Lines; rest = rest[2..]; }
        else if (rest.StartsWith("r/", StringComparison.Ordinal)) { kind = SplitChunkKind.RoundRobin; rest = rest[2..]; }

        int k = 0;
        // GNU reads "K/N" only when the argument starts with a digit; "/3" and "L/3" are bad N.
        int slash = rest.Length > 0 && rest[0] is >= '0' and <= '9' ? rest.IndexOf('/') : -1;
        if (slash >= 0)
        {
            string kText = rest[..slash];
            if (!TryNumber(kText, out long kv) || kv < 1)
            {
                error = $"invalid chunk number: '{kText}'";
                return false;
            }
            rest = rest[(slash + 1)..];
            if (!TryNumber(rest, out long nv0) || nv0 < 1 || nv0 > int.MaxValue)
            {
                error = $"invalid number of chunks: '{rest}'";
                return false;
            }
            if (kv > nv0)
            {
                error = $"invalid chunk number: '{kText}'";
                return false;
            }
            spec = new SplitChunkSpec(kind, (int)kv, (int)nv0);
            return true;
        }

        if (!TryNumber(rest, out long nv) || nv < 1 || nv > int.MaxValue)
        {
            error = $"invalid number of chunks: '{rest}'";
            return false;
        }
        spec = new SplitChunkSpec(kind, k, (int)nv);
        return true;
    }

    // strtoumax: optional '+', then digits only.
    private static bool TryNumber(string s, out long value)
    {
        value = 0;
        int i = s.Length > 0 && s[0] == '+' ? 1 : 0;
        if (i >= s.Length) return false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') return false;
            value = Math.Min(value * 10 + (c - '0'), long.MaxValue / 20);
        }
        return true;
    }

    /// <summary>Minimal suffix length whose <paramref name="radix"/>-ary names cover <paramref name="n"/> files (the "needs to be at least" figure).</summary>
    public static int SuffixLengthNeeded(int n, int radix)
    {
        int len = 1;
        long cap = radix;
        while (cap < n) { cap *= radix; len++; }
        return len;
    }

    /// <summary>Splits <paramref name="data"/> into exactly <paramref name="n"/> chunks.</summary>
    public static byte[][] Partition(byte[] data, SplitChunkKind kind, int n)
    {
        return kind switch
        {
            SplitChunkKind.Bytes => ByBytes(data, n),
            SplitChunkKind.Lines => ByLines(data, n),
            _ => RoundRobin(data, n),
        };
    }

    // end offset (exclusive) of partition i (0-based): first size%n partitions are one byte longer.
    private static long End(long size, int n, int i) => (size / n) * (i + 1) + Math.Min(i + 1, size % n);

    private static byte[][] ByBytes(byte[] data, int n)
    {
        var chunks = new byte[n][];
        long start = 0;
        for (int i = 0; i < n; i++)
        {
            long end = End(data.Length, n, i);
            chunks[i] = data.AsSpan((int)start, (int)(end - start)).ToArray();
            start = end;
        }
        return chunks;
    }

    private static byte[][] ByLines(byte[] data, int n)
    {
        var parts = new MemoryStream[n];
        for (int i = 0; i < n; i++) parts[i] = new MemoryStream();
        int p = 0;
        long size = data.Length;
        long pos = 0;
        while (pos < size)
        {
            int nl = Array.IndexOf(data, (byte)'\n', (int)pos);
            long end = nl < 0 ? size : nl + 1;
            while (p < n - 1 && pos >= End(size, n, p)) p++;
            parts[p].Write(data, (int)pos, (int)(end - pos));
            pos = end;
        }
        var chunks = new byte[n][];
        for (int i = 0; i < n; i++) chunks[i] = parts[i].ToArray();
        return chunks;
    }

    private static byte[][] RoundRobin(byte[] data, int n)
    {
        var parts = new MemoryStream[n];
        for (int i = 0; i < n; i++) parts[i] = new MemoryStream();
        long size = data.Length;
        long pos = 0;
        int j = 0;
        while (pos < size)
        {
            int nl = Array.IndexOf(data, (byte)'\n', (int)pos);
            long end = nl < 0 ? size : nl + 1;
            parts[j % n].Write(data, (int)pos, (int)(end - pos));
            j++;
            pos = end;
        }
        var chunks = new byte[n][];
        for (int i = 0; i < n; i++) chunks[i] = parts[i].ToArray();
        return chunks;
    }
}
