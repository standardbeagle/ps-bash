using System.Collections.Immutable;
using System.Text;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// The pattern operators of a parameter expansion: <c>${v#p} ${v##p} ${v%p} ${v%%p}</c>, <c>${v/p/r}</c> with its
/// <c>// /# /%</c> forms, and <c>${v^p} ${v^^p} ${v,p} ${v,,p}</c>. Each pattern operand is read as a WORD (quoted
/// parts literal, an unquoted expansion's value active) and handed to the runtime as canonical pattern text
/// (<see cref="EmitGlobPatternExpr"/>, the same text every other pattern context gets); the one compiler
/// (<see cref="BashPattern"/>) matches it there. Nothing here builds a regex.
/// </summary>
public static partial class PsEmitter
{
    /// <summary>
    /// The PowerShell expression of a pattern-operator suffix of <paramref name="varRef"/>, or null when
    /// <paramref name="suffix"/> is not one. <paramref name="inDoubleQuote"/>: the expression is spliced into a
    /// PowerShell double-quoted string (a <c>$( )</c> subexpression, literal parens kept out of it).
    /// </summary>
    private static string? TryEmitPatternOperator(string varRef, string suffix, bool inDoubleQuote)
    {
        if (suffix.Length == 0) return null;
        string open = inDoubleQuote ? "$(" : "(";
        char op = suffix[0];
        bool doubled = suffix.Length > 1 && suffix[1] == op;
        string operand = suffix[(doubled ? 2 : 1)..];
        switch (op)
        {
            case '#' or '%':
                return open + PsBuild.PatternRemove(varRef, PatternOperandExpr(operand, inDoubleQuote),
                    suffix: op == '%', longest: doubled) + ")";
            case '/':
            {
                var (find, replace) = SplitPatsubOperands(operand);
                var form = PsBuild.SubstituteForm.All;
                if (!doubled)
                {
                    form = PsBuild.SubstituteForm.First;
                    // `/#` and `/%` anchor only when unquoted and unescaped: `${v/\#x/y}` replaces the text #x.
                    if (find.StartsWith('#')) { form = PsBuild.SubstituteForm.Start; find = find[1..]; }
                    else if (find.StartsWith('%')) { form = PsBuild.SubstituteForm.End; find = find[1..]; }
                }
                return open + PsBuild.PatternSubstitute(varRef, PatternOperandExpr(find, inDoubleQuote),
                    ReplacementTemplateExpr(replace, inDoubleQuote), form) + ")";
            }
            case '^' or ',':
                return open + PsBuild.PatternCaseModify(varRef,
                    operand.Length == 0 ? null : PatternOperandExpr(operand, inDoubleQuote), upper: op == '^', all: doubled) + ")";
        }
        return null;
    }

    /// <summary>The canonical pattern text of the operand word <paramref name="raw"/> (no brace expansion, a leading
    /// <c>~</c> the home directory).</summary>
    private static string PatternOperandExpr(string raw, bool parenSafe)
    {
        if (raw.Length == 0) return "''";
        var word = new CompoundWord(BashParser.DecomposeWord(raw, braceExpansion: false));
        return EmitGlobPatternExpr(word, mapPaths: false, parenSafe: parenSafe);
    }

    /// <summary>
    /// The replacement template of <c>${v/p/r}</c> (<see cref="BashPattern.Substitute"/>): quoted and escaped text
    /// literal (<c>\</c> and <c>&amp;</c> escaped), an unquoted <c>&amp;</c> the match, an unquoted expansion's value
    /// inserted as it is (<c>rep='\&amp;'</c> is an ampersand, <c>rep='&amp;'</c> the match), a quoted one literal.
    /// </summary>
    private static string ReplacementTemplateExpr(string raw, bool parenSafe)
    {
        if (raw.Length == 0) return "''";
        var pieces = new List<string>();
        var lit = new StringBuilder();
        void Flush()
        {
            if (lit.Length == 0) return;
            string q = PsBuild.SingleQuote(lit.ToString());
            pieces.Add(parenSafe ? PsBuild.ParenSafeLiteral(q) : q);
            lit.Clear();
        }
        void Dynamic(string expr)
        {
            Flush();
            pieces.Add(expr);
        }
        foreach (var part in BashParser.DecomposeWord(raw, braceExpansion: false))
        {
            switch (part)
            {
                case WordPart.Literal l:
                    lit.Append(l.Value.Replace("\\", "\\\\"));
                    break;
                case WordPart.GlobPart g:
                    // `[&]` is text with the match in it; a backslash in it escapes the next character.
                    for (int i = 0; i < g.Pattern.Length; i++)
                    {
                        if (g.Pattern[i] == '\\' && i + 1 < g.Pattern.Length) lit.Append(BashPattern.EscapeReplacement(g.Pattern[++i].ToString()));
                        else lit.Append(g.Pattern[i]);
                    }
                    break;
                case WordPart.EscapedLiteral e:
                    lit.Append(BashPattern.EscapeReplacement(e.Value));
                    break;
                case WordPart.SingleQuoted sq:
                    lit.Append(BashPattern.EscapeReplacement(sq.Value));
                    break;
                case WordPart.AnsiCQuoted aq:
                    lit.Append(BashPattern.EscapeReplacement(ExpandAnsiCEscapes(aq.Value)));
                    break;
                case WordPart.DoubleQuoted dq:
                    if (TryGetStaticArgValue(new CompoundWord(ImmutableArray.Create<WordPart>(dq))) is { } dqText)
                        lit.Append(BashPattern.EscapeReplacement(dqText));
                    else
                        Dynamic(PsBuild.LiteralReplacement(EmitDoubleQuoted(dq)));
                    break;
                case WordPart.TildeSub { User: null or "+" or "-" } ts:
                    Dynamic(PsBuild.LiteralReplacement(EmitWordPart(ts)));
                    if (ts.SlashConsumed) lit.Append('/');
                    break;
                case WordPart.TildeSub ts:
                    lit.Append(BashPattern.EscapeReplacement("~" + ts.User));
                    if (ts.SlashConsumed) lit.Append('/');
                    break;
                case WordPart.CommandSub cs:
                    Dynamic(PsBuild.ReplacementText(EmitCommandSubString(cs, nested: false)));
                    break;
                default: // SimpleVarSub, BracedVarSub, ArithSub: unquoted, inserted as they are
                    Dynamic(PsBuild.ReplacementText(EmitWordPart(part)));
                    break;
            }
        }
        Flush();
        if (pieces.Count == 0) return "''";
        if (pieces.Count == 1) return pieces[0];
        return "('' + " + string.Join(" + ", pieces) + ")";
    }
}
