using System.Text;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// A word as the script wrote it, for a diagnostic that names it (<c>$x: ambiguous redirect</c>,
    /// <c>a-{1,2}: ambiguous redirect</c>). Rebuilt from the parts: quoting is shown as written for the common forms; a
    /// command or arithmetic substitution is shown as <c>$(...)</c> / <c>$((...))</c>.
    /// </summary>
    internal static string WordAsWritten(CompoundWord word)
    {
        var sb = new StringBuilder();
        AppendAsWritten(sb, word.Parts);
        return sb.ToString();
    }

    private static void AppendAsWritten(StringBuilder sb, System.Collections.Immutable.ImmutableArray<WordPart> parts)
    {
        if (parts.IsDefaultOrEmpty) return;
        foreach (var part in parts)
        {
            switch (part)
            {
                case WordPart.Literal l: sb.Append(l.Value); break;
                case WordPart.EscapedLiteral e: sb.Append('\\').Append(e.Value); break;
                case WordPart.SingleQuoted s: sb.Append('\'').Append(s.Value).Append('\''); break;
                case WordPart.AnsiCQuoted a: sb.Append("$'").Append(a.Value).Append('\''); break;
                case WordPart.DoubleQuoted d: sb.Append('"'); AppendAsWritten(sb, d.Parts); sb.Append('"'); break;
                case WordPart.SimpleVarSub v: sb.Append('$').Append(v.Name); break;
                case WordPart.BracedVarSub b:
                    sb.Append("${").Append(b.Name).Append(b.Suffix);
                    if (b.ArgWord is { } arg) AppendAsWritten(sb, arg);
                    sb.Append('}');
                    break;
                case WordPart.TildeSub t: sb.Append('~').Append(t.User); break;
                case WordPart.GlobPart g: sb.Append(g.Pattern); break;
                case WordPart.BracedTuple bt: sb.Append('{').Append(string.Join(",", bt.Items)).Append('}'); break;
                case WordPart.BracedRange r:
                    sb.Append('{').Append(r.Start).Append("..").Append(r.End);
                    if (r.Step != 0) sb.Append("..").Append(r.Step);
                    sb.Append('}');
                    break;
                case WordPart.CommandSub: sb.Append("$(...)"); break;
                case WordPart.ArithSub: sb.Append("$((...))"); break;
                case WordPart.ProcessSub p: sb.Append(p.IsInput ? "<(...)" : ">(...)"); break;
            }
        }
    }
}
