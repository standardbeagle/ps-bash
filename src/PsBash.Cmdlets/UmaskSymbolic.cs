namespace PsBash.Cmdlets;

/// <summary>
/// Symbolic modes for <c>umask</c> (<c>u=rwx,g+r,o-w</c>, <c>a=</c>). The mask holds the bits that are
/// REMOVED from new files, so the clauses edit its complement, the permissions new files get.
/// bash takes one operator per clause and no permission copy: <c>g=u</c> and <c>u+r+w</c> are errors.
/// </summary>
internal static class UmaskSymbolic
{
    /// <summary>Apply <paramref name="spec"/> to <paramref name="mask"/>; false when it is not a valid symbolic mode.</summary>
    internal static bool TryApply(int mask, string spec, out int newMask)
    {
        newMask = mask;
        if (spec.Length == 0) return false;
        int perm = ~mask & 0x1FF;
        foreach (var clause in spec.Split(','))
        {
            int i = 0, who = 0;
            for (; i < clause.Length && clause[i] is 'u' or 'g' or 'o' or 'a'; i++)
                who |= clause[i] switch { 'u' => 0x1C0, 'g' => 0x38, 'o' => 0x7, _ => 0x1FF };
            if (who == 0) who = 0x1FF;
            if (i == clause.Length) return false;
            char op = clause[i++];
            if (op is not ('+' or '-' or '=')) return false;
            int bits = 0;
            for (; i < clause.Length; i++)
            {
                int b = clause[i] switch { 'r' => 4, 'w' => 2, 'x' => 1, _ => -1 };
                if (b < 0) return false;
                bits |= b << 6 | b << 3 | b;
            }
            bits &= who;
            perm = op switch { '+' => perm | bits, '-' => perm & ~bits, _ => (perm & ~who) | bits };
        }
        newMask = ~perm & 0x1FF;
        return true;
    }
}
