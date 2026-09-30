namespace PsBash.Cmdlets;

/// <summary>
/// Reading and applying Unix permission bits for the copy commands; every member is a no-op /
/// null on Windows, which has no mode bits (its stand-in, the read-only attribute, is handled by
/// the caller). OS-interface seam for <c>cp</c>.
/// </summary>
internal static class PlatformMode
{
    /// <summary>The Unix mode of <paramref name="path"/>, or null on Windows / when unreadable.</summary>
    public static int? TryGet(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return (int)File.GetUnixFileMode(path); }
        catch { return null; }
    }

    /// <summary>
    /// Sets the mode of a finished copy the way GNU <c>cp</c> does. <c>Preserve</c> gives the
    /// destination exactly the source's bits. Otherwise an EXISTING destination keeps the mode it had
    /// (<see cref="File.Copy(string, string, bool)"/> would have replaced it with the source's), and
    /// a NEW one gets the source's bits (<c>Default</c>) or 0666 / 0777 (<c>Clear</c>), both masked
    /// by the umask.
    /// </summary>
    public static void Apply(string src, string dest, bool isDir, CpModePolicy policy, bool destExisted, int? previousMode)
    {
        if (OperatingSystem.IsWindows()) return;

        int umask = FileModeSpec.CurrentUmask();
        if (policy == CpModePolicy.Preserve)
        {
            File.SetUnixFileMode(dest, File.GetUnixFileMode(src));
        }
        else if (destExisted)
        {
            if (previousMode is { } previous) File.SetUnixFileMode(dest, (UnixFileMode)previous);
        }
        else if (policy == CpModePolicy.Clear)
        {
            File.SetUnixFileMode(dest, (UnixFileMode)((isDir ? 0x1FF : 0x1B6) & ~umask));
        }
        else
        {
            File.SetUnixFileMode(dest, (UnixFileMode)((int)File.GetUnixFileMode(src) & ~umask));
        }
    }
}

/// <summary>
/// chmod-style mode strings — the argument of <c>mkdir -m</c> — compiled the way gnulib's
/// <c>mode_compile</c> / <c>mode_adjust</c> do: a bare octal number (at most 07777), or a
/// comma-separated list of symbolic clauses <c>[ugoa]*([-+=][rwxXst]*|[-+=][ugo])+</c> applied in
/// order to a base mode. Pure, so it is unit-tested against a table produced by real GNU coreutils.
/// <para>
/// Rules that matter (all oracle-checked): a clause WITH a who (<c>u=</c>, <c>a+x</c>) acts on exactly
/// those bits and ignores the umask; a clause WITHOUT one (<c>+x</c>, <c>=rx</c>) masks its value with
/// <c>~umask</c> and, for <c>=</c>, clears every bit first. For a directory, setuid/setgid are left
/// alone unless the clause mentions them (<c>g+s</c> / <c>u=rwxs</c>). <c>X</c> is execute only for a
/// directory or when some execute bit is already set.
/// </para>
/// <para>Known gap: GNU skips the final chmod when only special bits are mentioned (<c>+t</c> yields
/// 1755, <c>g-s</c> 755); this computes the full mode (1777, 777).</para>
/// </summary>
internal static class FileModeSpec
{
    private const int SetUid = 0x800;   // 04000
    private const int SetGid = 0x400;   // 02000
    private const int Sticky = 0x200;   // 01000
    private const int AllBits = 0xFFF;  // 07777
    private const int Owner = 0x1C0;    // 0700
    private const int Group = 0x038;    // 0070
    private const int Other = 0x007;    // 0007

    /// <summary>
    /// Compiles <paramref name="spec"/> against <paramref name="baseMode"/> (mkdir: 0777) under
    /// <paramref name="umask"/>. False for anything GNU calls an invalid mode.
    /// </summary>
    public static bool TryParse(string spec, bool isDirectory, int umask, int baseMode, out int mode)
    {
        mode = baseMode & AllBits;
        if (spec.Length == 0) return false;

        if (spec[0] >= '0' && spec[0] <= '7') return TryParseOctal(spec, out mode);

        int result = baseMode & AllBits;
        foreach (var clause in spec.Split(','))
        {
            if (!TryApplyClause(clause, isDirectory, umask, ref result)) { mode = baseMode & AllBits; return false; }
        }
        mode = result;
        return true;
    }

    /// <summary>The owner-write bit — the only part of a mode Windows can express (read-only attribute).</summary>
    public static bool OwnerCanWrite(int mode) => (mode & 0x080) != 0; // 0200

    /// <summary>
    /// The current process umask. Linux exposes it in <c>/proc/self/status</c>; elsewhere (and on
    /// Windows, which has none) the near-universal 022 is used.
    /// </summary>
    public static int CurrentUmask()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines("/proc/self/status"))
                {
                    if (!line.StartsWith("Umask:", StringComparison.Ordinal)) continue;
                    return Convert.ToInt32(line.Substring(6).Trim(), 8);
                }
            }
        }
        catch { /* fall through to the default */ }
        return 0x12; // 0022
    }

    private static bool TryParseOctal(string spec, out int mode)
    {
        mode = 0;
        int value = 0;
        foreach (var c in spec)
        {
            if (c < '0' || c > '7') return false;
            value = value * 8 + (c - '0');
            if (value > AllBits) return false;
        }
        mode = value;
        return true;
    }

    private static bool TryApplyClause(string clause, bool isDir, int umask, ref int mode)
    {
        int i = 0;
        int affected = 0;
        for (; i < clause.Length; i++)
        {
            switch (clause[i])
            {
                case 'u': affected |= SetUid | Owner; continue;
                case 'g': affected |= SetGid | Group; continue;
                case 'o': affected |= Other; continue;
                case 'a': affected |= AllBits; continue;
            }
            break;
        }

        // At least one operation is required ("a" alone, "," and "" are invalid).
        if (i >= clause.Length) return false;

        while (i < clause.Length)
        {
            char op = clause[i++];
            if (op != '+' && op != '-' && op != '=') return false;

            int value = 0;
            bool copy = false, xIfAny = false;
            if (i < clause.Length && (clause[i] == 'u' || clause[i] == 'g' || clause[i] == 'o'))
            {
                // Copy the permissions another class currently has; nothing may follow it but the
                // next operation (`u=ug` is invalid).
                int shift = clause[i] == 'u' ? 6 : clause[i] == 'g' ? 3 : 0;
                int bits = (mode >> shift) & 7;
                value = (bits << 6) | (bits << 3) | bits;
                copy = true;
                i++;
            }
            else
            {
                for (; i < clause.Length; i++)
                {
                    char c = clause[i];
                    if (c == 'r') value |= 0x124;       // 0444
                    else if (c == 'w') value |= 0x092;  // 0222
                    else if (c == 'x') value |= 0x049;  // 0111
                    else if (c == 'X') xIfAny = true;
                    else if (c == 's') value |= SetUid | SetGid;
                    else if (c == 't') value |= Sticky;
                    else break;
                }
            }
            if (xIfAny && (isDir || (mode & 0x049) != 0)) value |= 0x049;
            if (!copy && i < clause.Length && clause[i] != '+' && clause[i] != '-' && clause[i] != '=') return false;
            if (copy && i < clause.Length && clause[i] != '+' && clause[i] != '-' && clause[i] != '=') return false;

            // gnulib mode_adjust: with no who the clause "mentions" exactly the bits of its value.
            int mentioned = affected != 0 ? affected : value;
            int omit = (isDir ? SetUid | SetGid : 0) & ~mentioned;
            value &= affected != 0 ? affected : ~umask;

            switch (op)
            {
                case '=':
                {
                    int preserved = (affected != 0 ? ~affected : 0) | omit;
                    mode = (mode & preserved) | (value & ~omit);
                    break;
                }
                case '+':
                    mode |= value & ~omit;
                    break;
                default: // '-'
                    mode &= ~(value & ~omit);
                    break;
            }
            mode &= AllBits;
        }
        return true;
    }
}
