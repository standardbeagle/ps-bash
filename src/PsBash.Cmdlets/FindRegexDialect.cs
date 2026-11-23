using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Translation of the GNU regex syntaxes <c>find -regextype</c> selects into .NET regular expressions
/// (the match is always against the WHOLE printed path, so the caller anchors it).
/// <list type="bullet">
/// <item><b>emacs</b> (the default, <c>findutils-default</c>): <c>\( \) \|</c> group/alternate, <c>* + ?</c> are operators
/// (a following <c>?</c> makes them non-greedy), <c>\+ \? \{ \}</c> are literals (no intervals), no <c>[[:class:]]</c>
/// (the brackets are an ordinary set, as GNU's RE_SYNTAX_EMACS has no RE_CHAR_CLASSES), <c>\w \W \s \S \b \B \&lt; \&gt; \` \'</c>
/// and back-references <c>\1</c>..<c>\9</c>.</item>
/// <item><b>posix-basic</b> (<c>ed</c>, <c>sed</c>, <c>grep</c>, <c>posix-minimal-basic</c>): <c>\( \) \| \+ \? \{m,n\}</c> are the
/// operators, a bare <c>+ ? | ( ) { }</c> is a literal, <c>*</c> is literal at the start of an expression, classes allowed.</item>
/// <item><b>posix-extended</b> (<c>egrep</c>, <c>posix-egrep</c>, <c>awk</c>, <c>gnu-awk</c>, <c>posix-awk</c>): <c>( ) | + ? * {m,n}</c>
/// are the operators, a backslash makes any other character literal. The awk variants share this mapping (their
/// extra escape sequences are not translated: documented difference).</item>
/// </list>
/// An invalid pattern yields GNU's message text (<c>Unmatched ( or \(</c>, ...).
/// </summary>
internal static class FindRegexDialect
{
    internal enum Dialect { Emacs, Basic, Extended }

    /// <summary>GNU's list of valid names, in the order its error message prints them.</summary>
    internal static readonly string[] TypeNames =
    {
        "findutils-default", "ed", "emacs", "gnu-awk", "grep", "posix-awk", "awk", "posix-basic", "posix-egrep",
        "egrep", "posix-extended", "posix-minimal-basic", "sed",
    };

    internal static string UnknownTypeMessage(string name) =>
        $"Unknown regular expression type ‘{name}’; valid types are " + string.Join(", ", TypeNames.Select(n => $"‘{n}’")) + ".";

    internal static bool TryDialect(string name, out Dialect dialect)
    {
        switch (name)
        {
            case "findutils-default": case "emacs": dialect = Dialect.Emacs; return true;
            case "ed": case "grep": case "posix-basic": case "posix-minimal-basic": case "sed": dialect = Dialect.Basic; return true;
            case "gnu-awk": case "posix-awk": case "awk": case "posix-egrep": case "egrep": case "posix-extended":
                dialect = Dialect.Extended; return true;
            default: dialect = Dialect.Emacs; return false;
        }
    }

    /// <summary>Compile for a whole-path match; <paramref name="error"/> is GNU's wording when the pattern is invalid.</summary>
    internal static bool TryCompile(string pattern, Dialect dialect, bool ignoreCase, out Regex? regex, out string? error)
    {
        regex = null;
        if (!TryTranslate(pattern, dialect, out var net, out error)) return false;
        try
        {
            var opts = RegexOptions.Singleline | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex("^(?:" + net + ")$", opts);
            return true;
        }
        catch (ArgumentException)
        {
            error = "Invalid regular expression";
            return false;
        }
    }

    /// <summary>The pattern in .NET syntax, by the shared GNU translator (<see cref="GnuRegex"/>) with find's flavor.</summary>
    internal static bool TryTranslate(string p, Dialect d, out string net, out string? error)
    {
        var syntax = d switch
        {
            Dialect.Basic => GnuRegexSyntax.FindBasic,
            Dialect.Extended => GnuRegexSyntax.FindExtended,
            _ => GnuRegexSyntax.FindEmacs,
        };
        return GnuRegex.TryTranslate(p, syntax, out net, out error);
    }
}
