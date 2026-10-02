namespace PsBash.Cmdlets;

/// <summary>
/// <c>find -perm MODE</c> (GNU find 4.9): <c>MODE</c> exact, <c>-MODE</c> all of the bits, <c>/MODE</c> any of the bits;
/// MODE is octal (up to 07777) or a chmod symbolic mode (<c>u=rw,g=r</c>, <c>a+x</c>, ...) compiled by
/// <see cref="FileModeSpec"/> against a base of 0 with umask 0, exactly as find does. The obsolete
/// <c>+MODE</c> is gone in 4.9 (<c>invalid mode</c>), and <c>/000</c> means "match everything" with GNU's warning.
/// </summary>
internal readonly record struct FindPerm(char Kind, int Mode)
{
    /// <summary>Kind: <c>'='</c> exact, <c>'-'</c> all bits, <c>'/'</c> any bit.</summary>
    internal static bool TryParse(string arg, out FindPerm perm, out string? warning)
    {
        perm = default; warning = null;
        char kind = '=';
        string body = arg;
        if (arg.Length > 0 && (arg[0] == '-' || arg[0] == '/')) { kind = arg[0]; body = arg.Substring(1); }
        if (body.Length == 0) return false;
        if (!FileModeSpec.TryParse(body, isDirectory: false, umask: 0, baseMode: 0, out int mode)) return false;
        if (kind == '/' && mode == 0)
        {
            warning = $"warning: you have specified a mode pattern {arg} (which is equivalent to /000). The meaning of -perm /000 has now been changed to be consistent with -perm -000; that is, while it used to match no files, it now matches all files.";
        }
        perm = new FindPerm(kind, mode);
        return true;
    }

    internal bool Matches(int fileMode)
    {
        int m = fileMode & 0xFFF;
        return Kind switch
        {
            '-' => (m & Mode) == Mode,
            '/' => Mode == 0 || (m & Mode) != 0,
            _ => m == Mode,
        };
    }
}
