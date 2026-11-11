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

    /// <summary>The <c>alias</c> / <c>unalias</c> definitions with static text, by line, and the first
    /// <c>shopt -s expand_aliases</c> line.</summary>
    private sealed class AliasTable
    {
        public readonly List<(string Name, int Line, string? Value)> Defs = new();
        public int ExpandLine = int.MaxValue;

        public void Add(Command.Simple s, int line)
        {
            if (s.Words.IsDefaultOrEmpty || GetLiteralValue(s.Words[0]) is not { } name) return;
            if (name == "alias")
            {
                foreach (var w in s.Words.Skip(1))
                    if (TryGetStaticArgValue(w) is { } t && !t.StartsWith('-') && t.IndexOf('=') is > 0 and var eq)
                        Defs.Add((t[..eq], line, t[(eq + 1)..]));
            }
            else if (name == "unalias")
            {
                foreach (var w in s.Words.Skip(1))
                    if (TryGetStaticArgValue(w) is { } t)
                        Defs.Add((t == "-a" ? "*" : t, line, null));
            }
            else if (name == "shopt" && s.Words.Any(w => GetLiteralValue(w) == "expand_aliases") && s.Words.Any(w => GetLiteralValue(w) == "-s"))
                ExpandLine = Math.Min(ExpandLine, line);
        }

        /// <summary>The text of alias NAME in effect for a command read on LINE (defined on an earlier line).</summary>
        public string? Lookup(string name, int line)
        {
            for (int i = Defs.Count - 1; i >= 0; i--)
            {
                var d = Defs[i];
                if (d.Line >= line || (d.Name != name && d.Name != "*")) continue;
                return d.Value;
            }
            return null;
        }
    }

    private static void CollectAliasDefs(Command root, string source)
    {
        var table = new AliasTable();
        VisitCommands(root, c => { if (c is Command.Simple s) table.Add(s, s.Line); }, SharesAliases);
        var defs = table.Defs;
        int expandLine = table.ExpandLine;
        _aliasDefs = defs.Count > 0 && expandLine != int.MaxValue ? defs : null;
        // Nested bodies ($( ), ( ), here-docs, eval, sourced text) are transpiled apart from this scan, so a script
        // that has any keeps the run-time function; so does an alias whose text is not one simple command.
        _aliasStaticOnly = _aliasDefs is not null
            && source.IndexOfAny(['$', '`', '(', '<']) < 0
            && !source.Contains("eval", StringComparison.Ordinal) && !source.Contains("source", StringComparison.Ordinal)
            && defs.All(d => d.Item3 is null || IsStaticAliasText(d.Item3));
        _expandAliasesLine = expandLine;
    }

    /// <summary>False for a command whose body runs in a subshell (<c>( )</c>, <c>&amp;</c>, a pipeline element): an
    /// <c>alias</c> or <c>shopt</c> there ends with it, so it must not expand the parent's later lines.</summary>
    private static bool SharesAliases(Command c)
        => c is not (Command.Subshell or Command.Background or Command.Pipeline { Commands.Length: > 1 });

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

    // ---- Alias text that is not a whole command ----
    // bash splices an alias's TEXT into its input and reads on, so an alias can hold part of a compound command
    // (`alias LEFT='{'`, `alias e_='for i in 1 2 3; do echo '`, `alias a=` before `(( … ))`). Such a script does not
    // parse as written; Transpile then parses it again with every alias use replaced by its text.

    /// <summary>Where the reader is: at a command word, or right after an alias whose text ends in a blank (that makes
    /// the next word an alias candidate too).</summary>
    private struct AliasReadState
    {
        public bool CommandPosition;
        public bool AfterBlankAlias;
    }

    /// <summary>Reserved words after which the next word is again a command word.</summary>
    private static readonly HashSet<string> CommandLeadingKeywords = new(StringComparer.Ordinal)
        { "if", "then", "else", "elif", "do", "while", "until", "time", "!", "{" };

    /// <summary>
    /// <paramref name="source"/> with each alias use replaced by the alias text, read the way bash reads it; null when
    /// the script does not turn on <c>expand_aliases</c> or uses no alias.
    /// </summary>
    internal static string? SpliceAliases(string source, out bool partialAlias)
    {
        partialAlias = false;
        if (!source.Contains("expand_aliases", StringComparison.Ordinal)) return null;
        List<BashToken> tokens;
        try { tokens = BashLexer.Tokenize(source); }
        catch (ParseException) { return null; }

        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
            if (source[i] == '\n') lineStarts.Add(i + 1);
        int LineOf(int pos)
        {
            int k = lineStarts.BinarySearch(pos);
            return (k >= 0 ? k : ~k - 1) + 1;
        }

        var table = new AliasTable();
        CollectAliasDefsFromTokens(source, tokens, LineOf, table);
        if (table.Defs.Count == 0 || table.ExpandLine == int.MaxValue) return null;

        var state = new AliasReadState { CommandPosition = true };
        bool changed = false;
        string result = SpliceText(source, tokens, LineOf, table, [], ref state, 0, ref changed, ref partialAlias);
        return changed ? result : null;
    }

    /// <summary>The definitions of the script's <c>alias</c>/<c>unalias</c>/<c>shopt</c> commands, each parsed alone.</summary>
    private static void CollectAliasDefsFromTokens(string source, List<BashToken> tokens, Func<int, int> lineOf, AliasTable table)
    {
        var state = new AliasReadState { CommandPosition = true };
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            bool atCommand = state.CommandPosition && t.Kind == BashTokenKind.Word;
            StepAliasReadState(t, ref state);
            if (!atCommand || t.Value is not ("alias" or "unalias" or "shopt")) continue;
            int end = i + 1;
            while (end < tokens.Count && tokens[end].Kind is BashTokenKind.Word or BashTokenKind.AssignmentWord or BashTokenKind.IoNumber) end++;
            int stop = end < tokens.Count ? tokens[end].Position : source.Length;
            try
            {
                if (BashParser.Parse(source[t.Position..stop]) is Command.Simple s) table.Add(s, lineOf(t.Position));
            }
            catch (ParseException) { }
        }
    }

    /// <summary>Advances the reader past a token that is not an expanded alias.</summary>
    private static void StepAliasReadState(BashToken t, ref AliasReadState state)
    {
        state.AfterBlankAlias = false;
        switch (t.Kind)
        {
            case BashTokenKind.Newline or BashTokenKind.Semi or BashTokenKind.Amp or BashTokenKind.Pipe
                or BashTokenKind.PipeAmp or BashTokenKind.AndIf or BashTokenKind.OrIf or BashTokenKind.LParen
                or BashTokenKind.LBrace or BashTokenKind.Bang:
                state.CommandPosition = true;
                break;
            case BashTokenKind.Word:
                state.CommandPosition = state.CommandPosition && CommandLeadingKeywords.Contains(t.Value);
                break;
            case BashTokenKind.AssignmentWord:
                break; // `X=1 cmd`: still at the command word
            case BashTokenKind.IoNumber or BashTokenKind.Eof:
                break;
            default:
                // A redirect operator: its target is the next word, the command word may still follow.
                if (t.Kind is BashTokenKind.RParen or BashTokenKind.RBrace) state.CommandPosition = false;
                break;
        }
    }

    private static bool IsRedirectOperator(BashTokenKind kind) => kind is BashTokenKind.Less or BashTokenKind.Great
        or BashTokenKind.DLess or BashTokenKind.DGreat or BashTokenKind.LessAnd or BashTokenKind.GreatAnd
        or BashTokenKind.AmpGreat or BashTokenKind.AmpDGreat or BashTokenKind.DLessDash or BashTokenKind.TLess;

    private static bool IsPlainAliasName(string word)
    {
        if (word.Length == 0) return false;
        foreach (char c in word)
            if (!(char.IsAsciiLetterOrDigit(c) || "!%+,-./:@^_~".IndexOf(c) >= 0)) return false;
        return true;
    }

    /// <summary>
    /// <paramref name="text"/> with its alias uses replaced. An alias's own text is read the same way (so an alias
    /// may name another), but never expands an alias already being expanded (<c>alias ls='ls -l'</c>).
    /// </summary>
    private static string SpliceText(string text, List<BashToken> tokens, Func<int, int> lineOf, AliasTable table,
        HashSet<string> expanding, ref AliasReadState state, int depth, ref bool changed, ref bool partial)
    {
        var sb = new System.Text.StringBuilder();
        int copied = 0;
        bool redirectTarget = false;
        foreach (var t in tokens)
        {
            if (t.Kind == BashTokenKind.Eof) break;
            if (redirectTarget) { redirectTarget = false; if (t.Kind == BashTokenKind.Word) continue; }
            if (IsRedirectOperator(t.Kind)) { redirectTarget = true; state.AfterBlankAlias = false; continue; }

            int line = lineOf(t.Position);
            if (t.Kind == BashTokenKind.Word && (state.CommandPosition || state.AfterBlankAlias) && depth < 32
                && line > table.ExpandLine && IsPlainAliasName(t.Value) && !expanding.Contains(t.Value)
                && table.Lookup(t.Value, line) is { } value)
            {
                List<BashToken> inner;
                try { inner = BashLexer.Tokenize(value); }
                catch (ParseException ex)
                {
                    // bash reads an alias's text by itself: a quote it leaves open is a syntax error.
                    throw new ParseException("alias " + t.Value + ": " + ex.Message, line, 0, "alias");
                }
                state.AfterBlankAlias = true; // the first word of the alias text is checked for an alias too
                if (!partial) { try { BashParser.Parse(value); } catch (ParseException) { partial = true; } }
                string expanded = SpliceText(value, inner, _ => line, table, [.. expanding, t.Value], ref state, depth + 1, ref changed, ref partial);
                sb.Append(text, copied, t.Position - copied).Append(expanded);
                copied = t.Position + t.Value.Length;
                changed = true;
                if (value.Length > 0 && value[^1] is ' ' or '\t') state.AfterBlankAlias = true;
                continue;
            }
            StepAliasReadState(t, ref state);
        }
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }
}
