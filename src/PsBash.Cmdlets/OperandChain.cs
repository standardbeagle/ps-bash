namespace PsBash.Cmdlets;

/// <summary>
/// The directory chain of an operand, AS TYPED — what GNU <c>mkdir -p</c>, <c>install -d</c> / <c>-D</c> and
/// <c>rmdir -p</c> walk and name in their messages (<c>mkdir: created directory 'a/b'</c>,
/// <c>rmdir: removing directory, 'a/x'</c>). Pure text: no path is resolved, so the chain can never climb past
/// the operand's own components into the working directory's ancestors (rmdir -p used to walk the ABSOLUTE
/// parents and delete any empty directory above the operand). Guard: <c>OperandChainTests</c>.
/// </summary>
internal static class OperandChain
{
    /// <summary>
    /// gnulib <c>mkancesdirs</c> + the final directory: every prefix of <paramref name="operand"/> ending at a
    /// component, in creation order, the last being the operand itself as typed (<c>a/b/c</c> →
    /// <c>a</c>, <c>a/b</c>, <c>a/b/c</c>; <c>./a//b/</c> → <c>.</c>, <c>./a</c>, <c>./a//b/</c>). A leading root
    /// (<c>/</c>, <c>C:</c>) is not a component of its own.
    /// </summary>
    public static List<string> CreationChain(string operand)
    {
        var chain = new List<string>();
        int i = RootLength(operand);
        while (true)
        {
            while (i < operand.Length && IsSep(operand[i])) i++;
            if (i >= operand.Length) break;
            while (i < operand.Length && !IsSep(operand[i])) i++;
            int next = i;
            while (next < operand.Length && IsSep(operand[next])) next++;
            if (next >= operand.Length) break; // the last component: the operand itself
            chain.Add(operand[..i]);
        }
        chain.Add(operand);
        return chain;
    }

    /// <summary>
    /// GNU <c>rmdir -p</c> <c>remove_parents</c>: the operand's ancestors named by its OWN text, nearest first
    /// (<c>a/x//y/</c> → <c>a/x</c>, <c>a</c>; <c>./z/w</c> → <c>./z</c>, <c>.</c>). Stops before a root
    /// (<c>/</c>, a drive designator) — never a directory the operand did not name.
    /// </summary>
    public static List<string> RemovalParents(string operand)
    {
        var chain = new List<string>();
        int root = RootLength(operand);
        string dir = operand;
        while (dir.Length > root && IsSep(dir[^1])) dir = dir[..^1];
        while (true)
        {
            int slash = LastSep(dir);
            if (slash < root) break;
            int end = slash;
            while (end > root && IsSep(dir[end - 1])) end--;
            if (end <= root) break;
            dir = dir[..end];
            chain.Add(dir);
        }
        return chain;
    }

    /// <summary>A component separator: <c>/</c>, and <c>\</c> on Windows (an operand typed <c>C:\x\y</c>).</summary>
    private static bool IsSep(char c) => c == '/' || (c == '\\' && OperatingSystem.IsWindows());

    private static int LastSep(string s)
    {
        for (int k = s.Length - 1; k >= 0; k--)
            if (IsSep(s[k])) return k;
        return -1;
    }

    /// <summary>Length of the root prefix that is not a component: <c>/</c> (any run), <c>C:</c> / <c>C:/</c> on Windows.</summary>
    private static int RootLength(string s)
    {
        int i = 0;
        if (OperatingSystem.IsWindows() && s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':') i = 2;
        while (i < s.Length && IsSep(s[i])) i++;
        return i;
    }
}
