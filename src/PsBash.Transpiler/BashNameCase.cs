using System.Collections.Frozen;

namespace PsBash.Core;

/// <summary>
/// Keeps a bash variable whose name is a non-upper-case spelling of a RESERVED name (<c>path</c>, <c>tmp</c>,
/// <c>Home</c>, <c>os</c>) from being the system variable of that name.
///
/// <para><b>Why.</b> Bash variables live in the process environment (<c>BashVariableStore</c>, emitted as
/// <c>$env:NAME</c>), and on Windows the environment is case-INsensitive: <c>path=abc</c> was <c>PATH=abc</c>, so the
/// next <c>git</c> was "command not found"; <c>tmp=zz</c> sent every temp file to <c>./zz/</c>. bash (case-sensitive
/// environment) keeps them apart. Such a name is stored under a mangled name instead:
/// <c>__psbash_cv_&lt;hexmask&gt;_&lt;UPPER&gt;</c>, where bit i of the mask is set when character i of the bash name is
/// upper-case (<c>path</c> → <c>__psbash_cv_0_PATH</c>, <c>Path</c> → <c>__psbash_cv_1_PATH</c>). The mask keeps
/// <c>path</c>, <c>Path</c>, <c>pATH</c> three variables in a case-insensitive store, and makes the mapping reversible.</para>
///
/// <para><b>Where it is applied.</b> Static names: the AST records that carry a variable name normalise it in their
/// constructor (<c>WordPart.SimpleVarSub</c>, <c>BracedVarSub</c>, <c>Assignment</c>, <c>EnvPair</c>, <c>ForIn</c>,
/// <c>Select</c>, the arithmetic name nodes), so the emitter only ever sees the store name. Run-time names (read,
/// printf -v, unset, declare, getopts, mapfile, ${!ref}, arithmetic text) map at the cmdlet's by-name access and in
/// <c>BashVariableStore</c>. Anything that PRINTS a name (declare -p, env, set, compgen -v, ${!prefix*}, "x: unbound
/// variable") demangles with <see cref="ToBash"/>. A mangled variable is not given to a native child process
/// (<c>BashVariableStore.ApplyTo</c>): Windows cannot hand a child both <c>PATH</c> and <c>path</c>.</para>
///
/// <para>A fixed list, not "whatever the environment holds": the transpiler runs before the script, and a rule that
/// depended on the caller's environment would make the emitted text machine-dependent. Never on Linux/macOS, whose
/// environment is already case-sensitive. Ordinary names (<c>x</c> vs <c>X</c>, <c>http_proxy</c>) are untouched:
/// see <c>docs/specs/intentional-differences.md</c>.</para>
/// </summary>
public static class BashNameCase
{
    /// <summary>Prefix of every mangled store name.</summary>
    public const string Prefix = "__psbash_cv_";

    // Upper-case spellings. Compared ignoring case; only a NON-upper-case spelling is diverted.
    private static readonly string[] ReservedNames =
    [
        // Windows system variables a script commonly reuses as a lowercase name. Clobbering these breaks command
        // lookup (PATH, PATHEXT, COMSPEC), temp files (TMP, TEMP), or the profile paths tools read.
        "PATH", "PATHEXT", "TMP", "TEMP", "COMSPEC", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "USERPROFILE",
        "APPDATA", "LOCALAPPDATA", "PROGRAMFILES", "PROGRAMDATA", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "OS",
        "HOMEDRIVE", "HOMEPATH", "PSMODULEPATH",
        // Shell and POSIX variables that live in the environment (or are read from it: HOSTNAME, PS4 and OSTYPE
        // prefer an env value; IFS/OPTIND/REPLY/OPTARG are plain $env: entries) and that tools read (HOME, TERM,
        // LANG, SHELL, EDITOR, PAGER, COLUMNS, LINES). `lines=$(wc -l <f)` and `hostname=$(hostname)` are common.
        "HOME", "PWD", "OLDPWD", "IFS", "USER", "LOGNAME", "SHELL", "TERM", "LANG", "HOSTNAME", "OSTYPE", "UID",
        "RANDOM", "SECONDS", "LINENO", "OPTIND", "OPTARG", "REPLY", "PS1", "PS2", "PS4", "BASH_VERSION",
        "EDITOR", "PAGER", "COLUMNS", "LINES",
    ];

    private static readonly FrozenSet<string> Reserved =
        ReservedNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

#if NET9_0_OR_GREATER
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> ReservedSpan =
        Reserved.GetAlternateLookup<ReadOnlySpan<char>>();

    private static bool IsReserved(ReadOnlySpan<char> name) => ReservedSpan.Contains(name);
#else
    // net8 has no span lookup on FrozenSet; reached only after the length and first-letter rejects.
    private static bool IsReserved(ReadOnlySpan<char> name) => Reserved.Contains(name.ToString());
#endif

    private static readonly int MinLen = ReservedNames.Min(n => n.Length);
    private static readonly int MaxLen = ReservedNames.Max(n => n.Length);

    // Bit per letter (A..Z): a cheap first-character reject before the set lookup.
    private static readonly uint FirstLetters = ReservedNames.Aggregate(0u, (m, n) => m | 1u << (n[0] - 'A'));

    private static readonly AsyncLocal<bool?> s_override = new();

    /// <summary>True when names are diverted: on Windows, or under <see cref="Override"/>.</summary>
    public static bool Enabled => s_override.Value ?? OperatingSystem.IsWindows();

    /// <summary>
    /// Test seam: force the Windows behaviour on (or off) for the current async flow, on any OS. Dispose to restore.
    /// No environment-variable knob on purpose: the emitted text must not depend on the caller's environment.
    /// </summary>
    public static IDisposable Override(bool windows)
    {
        var prior = s_override.Value;
        s_override.Value = windows;
        return new Restore(prior);
    }

    private sealed class Restore(bool? prior) : IDisposable
    {
        public void Dispose() => s_override.Value = prior;
    }

    /// <summary>
    /// The store name for bash name <paramref name="bashName"/>. Unchanged unless names are diverted
    /// (<see cref="Enabled"/>) and it is a non-upper-case spelling of a reserved name. A trailing subscript is kept
    /// (<c>path[0]</c> → <c>__psbash_cv_0_PATH[0]</c>). Idempotent: a store name maps to itself.
    /// </summary>
    public static string ToStore(string bashName) => ToStore(bashName, Enabled);

    /// <inheritdoc cref="ToStore(string)"/>
    public static string ToStore(string bashName, bool windows)
    {
        if (!windows || bashName is null) return bashName!;
        int len = bashName.Length;
        int bracket = len > MinLen ? bashName.IndexOf('[') : -1;
        int nameLen = bracket > 0 ? bracket : len;
        if (nameLen < MinLen || nameLen > MaxLen) return bashName;
        char c0 = (char)(bashName[0] & ~0x20);
        if (c0 is < 'A' or > 'Z' || (FirstLetters & 1u << (c0 - 'A')) == 0) return bashName;

        uint mask = 0;
        bool anyLower = false;
        for (int i = 0; i < nameLen; i++)
        {
            char ch = bashName[i];
            if (ch is >= 'A' and <= 'Z') mask |= 1u << i;
            else if (ch is >= 'a' and <= 'z') anyLower = true;
            else if (ch is not ('_' or >= '0' and <= '9')) return bashName;
        }
        if (!anyLower) return bashName;
        var span = bashName.AsSpan(0, nameLen);
        if (!IsReserved(span)) return bashName;

        string mangled = Prefix + mask.ToString("x") + "_" + span.ToString().ToUpperInvariant();
        return bracket > 0 ? mangled + bashName[bracket..] : mangled;
    }

    /// <summary>True when <paramref name="storeName"/> is a mangled name (a diverted reserved-name variable).</summary>
    public static bool IsMangled(string? storeName) => storeName is not null && TryDecode(storeName, out _);

    /// <summary>
    /// The bash name for store name <paramref name="storeName"/> (the inverse of <see cref="ToStore(string)"/>);
    /// the input itself when it is not a mangled name. A trailing subscript is kept. Independent of the platform:
    /// a mangled name only exists where mangling is on.
    /// </summary>
    public static string ToBash(string storeName)
        => storeName is not null && TryDecode(storeName, out var bash) ? bash : storeName!;

    private static bool TryDecode(string s, out string bash)
    {
        bash = s;
        if (s.Length <= Prefix.Length + 2 || !s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        int us = s.IndexOf('_', Prefix.Length);
        if (us <= Prefix.Length || us - Prefix.Length > 8) return false;
        if (!uint.TryParse(s.AsSpan(Prefix.Length, us - Prefix.Length), System.Globalization.NumberStyles.AllowHexSpecifier,
                null, out uint mask))
            return false;
        int bracket = s.IndexOf('[', us + 1);
        var upper = bracket > 0 ? s.AsSpan(us + 1, bracket - us - 1) : s.AsSpan(us + 1);
        if (upper.Length == 0 || !IsReserved(upper)) return false;
        var chars = new char[upper.Length];
        bool anyLower = false;
        for (int i = 0; i < upper.Length; i++)
        {
            char u = char.ToUpperInvariant(upper[i]);
            if ((mask & 1u << i) != 0 || u is < 'A' or > 'Z') chars[i] = u;
            else { chars[i] = char.ToLowerInvariant(u); anyLower = true; }
        }
        if (!anyLower || mask >> upper.Length != 0) return false;
        bash = new string(chars) + (bracket > 0 ? s[bracket..] : "");
        return true;
    }
}
