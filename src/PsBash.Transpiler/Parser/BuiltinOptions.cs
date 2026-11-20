namespace PsBash.Core.Parser;

/// <summary>
/// bash's <c>internal_getopt</c> check for a builtin's options, the ONE scan every builtin uses, whether the emitter
/// writes it (<c>read</c>, <c>export</c>, <c>local</c>, …: <see cref="EmittedBuiltins"/>) or a cmdlet does
/// (<c>FileSystemHelpers.TryRejectBuiltinOptions</c>). A builtin given an option it does not have reports
/// <c>NAME: OPT: invalid option</c> and its usage line, status 2, and does nothing else.
/// REGRESSION: <c>pwd --bogus</c>, <c>readonly -Q</c>, <c>enable -Q</c> ran (status 0); <c>export -Q</c> and
/// <c>local -Q</c> were "command not found" (127). Guard: <c>Differential.Tests/UnknownOptionGuardTests</c>.
/// </summary>
public static class BuiltinOptions
{
    /// <summary>
    /// The builtins the emitter writes itself, with their option letters (bash 5.2 <c>internal_getopt</c> strings) and
    /// the letters among them that take an argument. A literal option word that is not one of them is rejected at
    /// transpile time as the diagnostic (<c>PsBuild.BuiltinInvalidOption</c>), so the builtin never runs.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Letters, string ValueLetters)> EmittedBuiltins =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["builtin"] = ("", ""),
            ["enable"] = ("adnpsf", "f"),
            ["export"] = ("fnp", ""),
            ["local"] = ("aAfFgiIlnprtux", ""),
            ["read"] = ("ersadinNptu", "adinNptu"),
            ["readonly"] = ("aAfp", ""),
        };

    /// <summary>
    /// The first option word bash would reject in <paramref name="args"/> and its bad letter (<c>'\0'</c> for a
    /// <c>--NAME</c>), or null (pure). Leading option words are scanned up to <c>--</c>, a lone <c>-</c>, the first
    /// operand or a word known only at run time (null); <c>--help</c> is every builtin's help, never an error. A letter in
    /// <paramref name="valueLetters"/> takes the rest of its word, or the next word when it ends the word.
    /// </summary>
    public static (string Word, char Bad)? FindInvalid(IReadOnlyList<string?> args, string letters, string valueLetters = "")
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is null || a == "--" || a.Length < 2 || a[0] != '-') return null;
            if (a == "--help") return null;
            if (a[1] == '-') return (a, '\0');
            for (int k = 1; k < a.Length; k++)
            {
                char c = a[k];
                if (letters.IndexOf(c) < 0) return (a, c);
                if (valueLetters.IndexOf(c) >= 0) { if (k == a.Length - 1) i++; break; }
            }
        }
        return null;
    }

    /// <summary>
    /// How bash's <c>internal_getopt</c> names a bad option word: <c>--</c> for any <c>--NAME</c> (a builtin has no long
    /// options), else <c>-</c> and the first letter it does not know (<paramref name="bad"/>; the word's first letter when
    /// not given).
    /// </summary>
    public static string Spelling(string word, char bad = '\0')
        => word.StartsWith("--", StringComparison.Ordinal) ? "--" : "-" + (bad != '\0' ? bad : word.Length > 1 ? word[1] : '-');
}
