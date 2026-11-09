using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    // bash expands an alias while it READS a command, so the name must be an unquoted literal command word and the
    // alias must have been defined on an EARLIER line (a whole line is read before any of it runs). A script that turns
    // `shopt -s expand_aliases` on gets this expansion at transpile time; the run-time alias function stays as before.
    [ThreadStatic]
    private static List<(string Name, int Line, string? Value)>? _aliasDefs;

    [ThreadStatic]
    private static int _expandAliasesLine;

    /// <summary>True when every use of every alias is expanded at transpile time, so no run-time alias function is needed.</summary>
    [ThreadStatic]
    private static bool _aliasStaticOnly;

    private static bool IsStaticAliasText(string text)
    {
        if (text.Trim().Length == 0) return false;
        try { return BashParser.Parse(text) is Command.Simple { HereDocs.IsDefaultOrEmpty: true }; }
        catch (ParseException) { return false; }
    }

    private static void CollectAliasDefs(Command root, string source)
    {
        var defs = new List<(string, int, string?)>();
        int expandLine = int.MaxValue;
        VisitCommands(root, c =>
        {
            if (c is not Command.Simple { Words.Length: > 0 } s || GetLiteralValue(s.Words[0]) is not { } name) return;
            if (name == "alias")
            {
                foreach (var w in s.Words.Skip(1))
                    if (TryGetStaticArgValue(w) is { } t && !t.StartsWith('-') && t.IndexOf('=') is > 0 and var eq)
                        defs.Add((t[..eq], s.Line, t[(eq + 1)..]));
            }
            else if (name == "unalias")
            {
                foreach (var w in s.Words.Skip(1))
                    if (TryGetStaticArgValue(w) is { } t)
                        defs.Add((t == "-a" ? "*" : t, s.Line, null));
            }
            else if (name == "shopt" && s.Words.Any(w => GetLiteralValue(w) == "expand_aliases") && s.Words.Any(w => GetLiteralValue(w) == "-s"))
                expandLine = Math.Min(expandLine, s.Line);
        });
        _aliasDefs = defs.Count > 0 && expandLine != int.MaxValue ? defs : null;
        // Nested bodies ($( ), ( ), here-docs, eval, sourced text) are transpiled apart from this scan, so a script
        // that has any keeps the run-time function; so does an alias whose text is not one simple command.
        _aliasStaticOnly = _aliasDefs is not null
            && source.IndexOfAny(['$', '`', '(', '<']) < 0
            && !source.Contains("eval", StringComparison.Ordinal) && !source.Contains("source", StringComparison.Ordinal)
            && defs.All(d => d.Item3 is null || IsStaticAliasText(d.Item3));
        _expandAliasesLine = expandLine;
    }

    private static string? AliasLookup(string name, int line)
    {
        var defs = _aliasDefs!;
        for (int i = defs.Count - 1; i >= 0; i--)
        {
            var d = defs[i];
            if (d.Line >= line || (d.Name != name && d.Name != "*")) continue;
            return d.Value;
        }
        return null;
    }

    // An alias value such as `!!!` is one plain word the parser would read as an operator; bash still uses it as the word.
    private static Command.Simple? PlainWordAlias(string text)
    {
        var w = text.Trim();
        if (w.Length == 0) return null;
        foreach (char c in w)
            if (!(char.IsAsciiLetterOrDigit(c) || "!%+,-./:=@^_~".IndexOf(c) >= 0)) return null;
        return new Command.Simple([new CompoundWord([new WordPart.Literal(w)])], ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty);
    }

    private static Command.Simple ExpandAliases(Command.Simple cmd)
    {
        if (_aliasDefs is null || cmd.Words.IsDefaultOrEmpty || cmd.Line <= _expandAliasesLine) return cmd;
        var words = cmd.Words;
        var envPairs = cmd.EnvPairs;
        var redirects = cmd.Redirects;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        int idx = 0;
        bool changed = false;
        for (int guard = 0; guard < 64 && idx < words.Length; guard++)
        {
            if (GetLiteralValue(words[idx]) is not { } name || visited.Contains(name) || AliasLookup(name, cmd.Line) is not { } text) break;
            visited.Add(name);
            if (text.Trim().Length == 0) break;
            Command.Simple? parsed;
            try { parsed = BashParser.Parse(text) as Command.Simple; }
            catch (ParseException) { parsed = PlainWordAlias(text); if (parsed is null) break; }
            if (parsed is null) parsed = PlainWordAlias(text);
            if (parsed is null || parsed.Words.IsDefaultOrEmpty || !parsed.HereDocs.IsDefaultOrEmpty && parsed.HereDocs.Length > 0) break;
            words = words.RemoveRange(idx, 1).InsertRange(idx, parsed.Words);
            if (idx == 0 && !parsed.EnvPairs.IsDefaultOrEmpty) envPairs = parsed.EnvPairs.AddRange(envPairs.IsDefault ? [] : envPairs);
            if (!parsed.Redirects.IsDefaultOrEmpty) redirects = parsed.Redirects.AddRange(redirects.IsDefault ? [] : redirects);
            changed = true;
            // A trailing blank makes the NEXT word an alias candidate too.
            if (char.IsWhiteSpace(text[^1])) { idx += parsed.Words.Length; visited.Clear(); }
        }
        return changed ? cmd with { Words = words, EnvPairs = envPairs, Redirects = redirects } : cmd;
    }
}
