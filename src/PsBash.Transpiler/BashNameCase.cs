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
/// <para><b>Where it is applied</b> (<c>docs/specs/environment-model.md</c> section 12). Static names: the AST records
/// that carry a variable name normalise it in their constructor (<c>WordPart.SimpleVarSub</c>, <c>BracedVarSub</c>,
/// <c>Assignment</c>, <c>EnvPair</c>, <c>ForIn</c>, <c>Select</c>, the arithmetic name nodes), so the emitter only
/// ever sees the store name. Run-time names (read, printf -v, unset, getopts, mapfile, ${!ref}, arithmetic text) map
/// in the by-name primitives themselves: <c>BashVariableStore</c>, <c>BashVariableLookup</c>, <see cref="ShellExports"/>,
/// the nameref table. Every one of them takes a bash name or a store name (the mapping is idempotent), so a caller
/// never maps. Anything that PRINTS a name demangles: a listing (declare -p, env, set, compgen -v, ${!prefix*}) with
/// <see cref="ToBash"/>, a diagnostic ("x: unbound variable", readonly refusals) with <see cref="ToBashText"/> at
/// the diagnostic writers. An exported one reaches a child under its store name, which a nested ps-bash reads back
/// as the bash name: Windows cannot hand a child both <c>PATH</c> and <c>path</c>.</para>
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

    // Exact spellings that are NOT diverted: Windows' own spelling of a variable Git Bash (MSYS2, the Windows reference
    // for bash) keeps under that spelling, so `$ProgramData` / `$PSModulePath` read the system value there too. Every
    // other native spelling (`windir`, `Path`, `SystemRoot`, `ComSpec`) is one MSYS2 imports UPPER-cased, leaving the
    // native spelling unset, which is what diverting it gives.
    private static bool IsExemptSpelling(ReadOnlySpan<char> name)
        => name.SequenceEqual("ProgramData") || name.SequenceEqual("PSModulePath");

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
        if (!IsReserved(span) || IsExemptSpelling(span)) return bashName;

        string mangled = Prefix + mask.ToString("x") + "_" + span.ToString().ToUpperInvariant();
        return bracket > 0 ? mangled + bashName[bracket..] : mangled;
    }

    /// <summary>
    /// The store name of a key read from the PROCESS ENVIRONMENT (an enumeration, not a bash name). Windows keeps a
    /// variable under the spelling it was created with (<c>windir</c>, <c>Path</c>), which is a reserved spelling that
    /// <see cref="ToStore(string)"/> would divert; the environment entry itself is the upper-case variable
    /// (<c>WINDIR</c>, <c>PATH</c>: the environment ignores case). So such a key is returned UPPER-cased, as Git Bash
    /// (MSYS2) imports it: listings show <c>WINDIR=...</c>, and a subshell's restore writes the real variable, never a
    /// diverted one. Any other key (a mangled name, an ordinary name, an exempt spelling) is returned as is.
    /// </summary>
    public static string FromEnvironment(string envKey)
        => envKey is not null && !string.Equals(ToStore(envKey), envKey, StringComparison.Ordinal)
            ? envKey.ToUpperInvariant() : envKey!;

    /// <summary>The name a listing prints for process-environment key <paramref name="envKey"/>:
    /// <see cref="ToBash"/> of <see cref="FromEnvironment"/> (<c>__psbash_cv_0_PATH</c> → <c>path</c>,
    /// <c>windir</c> → <c>WINDIR</c>).</summary>
    public static string ToListed(string envKey) => ToBash(FromEnvironment(envKey));

    /// <summary>
    /// Equality on variable names by STORE name: <c>path</c> and <c>__psbash_cv_0_PATH</c> are one key; <c>path</c> and
    /// <c>PATH</c> are two. For every table the emitter keys by variable name (readonly names, namerefs, a function's
    /// locals, attributes): a name from an AST record is already a store name, one from word text (<c>unset tmp</c>,
    /// <c>local -p path</c>) is not, and both must find the same entry. Ordinal when names are not diverted.
    /// </summary>
    public static IEqualityComparer<string> StoreNameComparer { get; } = new StoreNameEquality();

    private sealed class StoreNameEquality : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
            => ReferenceEquals(x, y) || (x is not null && y is not null && string.Equals(ToStore(x), ToStore(y), StringComparison.Ordinal));

        public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(ToStore(obj));
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

    /// <summary>
    /// <paramref name="text"/> (a diagnostic: <c>__psbash_cv_0_PATH: unbound variable</c>) with every store name in it
    /// written as its bash name (<c>path: unbound variable</c>). A store name counts only as a whole word: one that
    /// follows a letter, digit or <c>_</c> is part of a longer identifier and is left alone. For MESSAGE text only,
    /// never for PowerShell code (a store name there is the variable's real name). The common case, no store name,
    /// returns <paramref name="text"/> itself.
    /// </summary>
    public static string ToBashText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        int at = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int copied = 0;
        while (at >= 0)
        {
            int end = at;
            while (end < text.Length && IsNameChar(text[end])) end++;
            if ((at == 0 || !IsNameChar(text[at - 1])) && TryDecode(text[at..end], out var bash))
            {
                sb.Append(text, copied, at - copied).Append(bash);
                copied = end;
            }
            at = end < text.Length ? text.IndexOf(Prefix, end, StringComparison.OrdinalIgnoreCase) : -1;
        }
        if (copied == 0) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool IsNameChar(char c) => c is '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

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
